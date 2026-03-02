import atexit
import asyncio
import logging
import re
import signal
import sys
from contextlib import asynccontextmanager
from pathlib import Path

import aiosqlite
from fastapi import APIRouter, Depends, FastAPI
from fastapi.responses import PlainTextResponse
from fastapi.middleware.cors import CORSMiddleware
from fastapi.staticfiles import StaticFiles
from starlette.middleware.base import BaseHTTPMiddleware
from starlette.requests import Request as StarletteRequest
from starlette.responses import Response as StarletteResponse

from app.config import settings
from app.db.migration_runner import run_migrations
from app.db.repositories.profile_repo import ProfileRepo
from app.db.repositories.settings_repo import SettingsRepo
from app.db.repositories.fan_settings_repo import FanSettingsRepo
from app.db.repositories.machine_repo import MachineRepo
from app.hardware import get_backend
from app.services.sensor_service import SensorService
from app.services.fan_service import FanService
from app.services.fan_test_service import FanTestService
from app.services.alert_service import AlertService
from app.services.auth_service import AuthService
from app.services.machine_monitor_service import MachineMonitorService
from app.services.logging_service import LoggingService
from app.services.quiet_hours_service import QuietHoursService
from app.services.webhook_service import WebhookService
from app.api.dependencies.auth import require_auth
from app.db.repositories.push_subscription_repo import PushSubscriptionRepo
from app.db.repositories.email_notification_repo import EmailNotificationRepo
from app.services.push_notification_service import PushNotificationService
from app.services.email_notification_service import EmailNotificationService
from app.api.routes import sensors, fans, profiles, alerts, settings as settings_route, machines, webhooks
from app.api.routes import analytics as analytics_route
from app.api.routes.auth import router as auth_router
from app.api.routes.quiet_hours import router as quiet_hours_router
from app.api.routes.notifications import router as notifications_router
from app.api.websocket import router as ws_router

logger = logging.getLogger(__name__)
_PROM_LABEL_SAFE_RE = re.compile(r"[^a-zA-Z0-9_.-]")


def _sanitize_metric_label(value: object) -> str:
    """Sanitize a dynamic label value for Prometheus text exposition."""
    return _PROM_LABEL_SAFE_RE.sub("_", str(value))


@asynccontextmanager
async def lifespan(app: FastAPI):
    # ------------------------------------------------------------------
    # Database: migrations then open a shared connection
    # ------------------------------------------------------------------
    settings.db_path.parent.mkdir(parents=True, exist_ok=True)
    applied = await run_migrations(settings.db_path)
    if applied:
        logger.info("Applied %d migration(s)", applied)

    db = await aiosqlite.connect(str(settings.db_path))
    await db.execute("PRAGMA journal_mode=WAL")
    await db.execute("PRAGMA foreign_keys=ON")
    app.state.db = db

    # Repositories
    profile_repo = ProfileRepo(db)
    settings_repo = SettingsRepo(db)
    await profile_repo.seed_defaults()
    await profile_repo.seed_missing_presets()
    await settings_repo.seed_defaults()
    fan_settings_repo = FanSettingsRepo(db)
    machine_repo = MachineRepo(db)
    app.state.profile_repo = profile_repo
    app.state.settings_repo = settings_repo
    app.state.fan_settings_repo = fan_settings_repo
    app.state.machine_repo = machine_repo

    # ------------------------------------------------------------------
    # Hardware backend
    # ------------------------------------------------------------------
    backend = get_backend()
    await backend.initialize()
    app.state.backend = backend

    # Read persisted poll interval and failure limit for sensor service
    poll_interval = await settings_repo.get_float(
        "sensor_poll_interval", settings.sensor_poll_interval
    )
    failure_limit = await settings_repo.get_int("sensor_failure_limit", 3)
    sensor_service = SensorService(
        backend, poll_interval=poll_interval, failure_limit=failure_limit
    )
    app.state.sensor_service = sensor_service

    panic_cpu = await settings_repo.get_float("panic_cpu_temp_c", 95.0)
    panic_gpu = await settings_repo.get_float("panic_gpu_temp_c", 90.0)
    panic_hyst = await settings_repo.get_float("panic_hysteresis_c", 5.0)
    fan_service = FanService(
        backend,
        panic_cpu_temp=panic_cpu,
        panic_gpu_temp=panic_gpu,
        panic_hysteresis=panic_hyst,
    )
    await fan_service.load_fan_settings(fan_settings_repo)
    app.state.fan_service = fan_service

    # ------------------------------------------------------------------
    # Push + Email notification services — created before AlertService so
    # they can be injected via the constructor (avoids private-attr patching).
    # ------------------------------------------------------------------
    push_sub_repo = PushSubscriptionRepo(db)
    app.state.push_subscription_repo = push_sub_repo
    vapid_claims = {"sub": f"mailto:{settings.vapid_contact_email}"}
    push_svc = PushNotificationService(
        repo=push_sub_repo,
        vapid_private_key=settings.vapid_private_key,
        vapid_claims=vapid_claims,
    )
    app.state.push_notification_service = push_svc

    email_repo = EmailNotificationRepo(db, secret_key=settings.secret_key)
    app.state.email_notification_repo = email_repo
    email_svc = EmailNotificationService(repo=email_repo)
    app.state.email_notification_service = email_svc

    alert_service = AlertService(db, push_notification_service=push_svc, email_svc=email_svc)
    await alert_service.load_rules()
    app.state.alert_service = alert_service
    webhook_service = WebhookService(db)
    await webhook_service.start()
    app.state.webhook_service = webhook_service

    # ------------------------------------------------------------------
    # Auth service
    # ------------------------------------------------------------------
    auth_service = AuthService(db, session_ttl_seconds=settings.session_ttl_seconds)
    app.state.auth_service = auth_service

    if settings.auth_required:
        has_user = await auth_service.user_exists()
        if not has_user and not settings.password:
            raise RuntimeError(
                "Session auth required for non-localhost binding "
                f"(host={settings.host}). Set DRIVECHILL_PASSWORD environment "
                "variable before starting so the admin user can be created "
                "automatically."
            )
        if not has_user and settings.password:
            await auth_service.create_user("admin", settings.password)
            logger.info("Created admin user from DRIVECHILL_PASSWORD env var")
            await auth_service._log_auth_event(
                "user_created", "localhost", "admin", "success",
                "Auto-created from DRIVECHILL_PASSWORD env var",
            )

    fan_test_service = FanTestService(backend, sensor_service, fan_service,
                                      fan_settings_repo=fan_settings_repo)
    app.state.fan_test_service = fan_test_service

    # LoggingService shares the same DB file but keeps its own connection
    # (sensor writes are high-frequency; isolate from app queries).
    logging_service = LoggingService(settings.db_path)
    await logging_service.initialize()
    app.state.logging_service = logging_service

    await sensor_service.start()

    # Start the independent fan control loop BEFORE profile restore so that
    # fan_service._sensor_service is set when apply_profile() runs.
    # M-3: apply_profile() is the single canonical implementation.
    await fan_service.start(sensor_service, alert_service, webhook_service)

    # ------------------------------------------------------------------
    # Restore active profile on startup
    # ------------------------------------------------------------------
    active_profile = await profile_repo.get_active()
    if active_profile:
        await fan_service.apply_profile(active_profile)
        logger.info("Restored active profile: %s", active_profile.name)

    # ------------------------------------------------------------------
    # Quiet hours service
    # ------------------------------------------------------------------
    quiet_hours_service = QuietHoursService(db)
    app.state.quiet_hours_service = quiet_hours_service
    machine_monitor_service = MachineMonitorService(machine_repo)
    app.state.machine_monitor_service = machine_monitor_service

    async def _activate_profile_by_id(profile_id: str) -> None:
        """Callback for quiet hours to activate a profile.

        M-3: delegates to fan_service.apply_profile() instead of
        duplicating the preset-expansion logic.
        """
        profile = await profile_repo.get(profile_id)
        if not profile:
            return
        await profile_repo.activate(profile_id)
        await fan_service.apply_profile(profile)

    quiet_hours_service.set_activate_fn(_activate_profile_by_id)
    await quiet_hours_service.start()
    await machine_monitor_service.start()

    # ------------------------------------------------------------------
    # Background task: log sensor data every 10 seconds
    # ------------------------------------------------------------------
    async def log_loop():
        from datetime import datetime, timezone
        from app.models.sensors import SensorSnapshot
        while True:
            await asyncio.sleep(10)
            try:
                readings = sensor_service.latest
                if readings:
                    snapshot = SensorSnapshot(
                        timestamp=datetime.now(timezone.utc),
                        readings=readings,
                    )
                    await logging_service.log_snapshot(snapshot)
            except asyncio.CancelledError:
                raise
            except Exception:
                # H-2: log failures so sensor recording stops visibly, not silently
                logger.exception("Sensor log write failed")

    log_task = asyncio.create_task(log_loop())

    # ------------------------------------------------------------------
    # Background task: auth cleanup (expired sessions + old auth logs)
    # ------------------------------------------------------------------
    async def auth_cleanup_loop():
        while True:
            await asyncio.sleep(3600)  # every hour
            try:
                expired = await auth_service.cleanup_expired_sessions()
                old_logs = await auth_service.cleanup_old_auth_logs()
                pruned_webhooks = await webhook_service.prune_delivery_log()
                if expired or old_logs:
                    logger.info(
                        "Auth cleanup: %d expired sessions, %d old auth logs",
                        expired, old_logs,
                    )
                if pruned_webhooks:
                    logger.info("Webhook cleanup: pruned %d old delivery rows", pruned_webhooks)
            except asyncio.CancelledError:
                raise
            except Exception:
                logger.exception("Auth cleanup failed")

    auth_cleanup_task = asyncio.create_task(auth_cleanup_loop())

    # ------------------------------------------------------------------
    # Background task: prune old sensor log data based on retention setting
    # ------------------------------------------------------------------
    async def retention_prune_loop():
        while True:
            await asyncio.sleep(3600)  # every hour
            try:
                retention_hours = await settings_repo.get_int(
                    "history_retention_hours", settings.history_retention_hours
                )
                pruned = await logging_service.prune(retention_hours)
                if pruned:
                    logger.info("Retention prune: deleted %d old sensor log rows", pruned)
            except asyncio.CancelledError:
                raise
            except Exception:
                logger.exception("Retention prune failed")

    retention_prune_task = asyncio.create_task(retention_prune_loop())

    # ------------------------------------------------------------------
    # Signal handlers: release fan control on SIGTERM / SIGINT so fans
    # return to BIOS auto mode instead of staying at the last set speed.
    # (v1.0 release gate v1.0-7: graceful fan restore on process exit)
    # ------------------------------------------------------------------
    loop = asyncio.get_event_loop()

    def _on_signal(sig_name: str) -> None:
        logger.info("Received %s — releasing fan control before shutdown", sig_name)
        asyncio.ensure_future(fan_service.release_fan_control())

    # SIGINT / SIGTERM are the two signals uvicorn uses for graceful shutdown.
    # Windows only supports SIGINT and SIGTERM via signal.signal (not add_signal_handler).
    if sys.platform == "win32":
        for sig in (signal.SIGINT, signal.SIGTERM):
            original = signal.getsignal(sig)

            def _make_handler(s=sig, orig=original, sname=sig.name):
                def handler(signum, frame):
                    _on_signal(sname)
                    if callable(orig):
                        orig(signum, frame)
                return handler

            signal.signal(sig, _make_handler())
    else:
        for sig in (signal.SIGINT, signal.SIGTERM):
            loop.add_signal_handler(sig, lambda s=sig: _on_signal(s.name))

    # atexit handler: best-effort fan restore for paths where signal handlers
    # don't fire (unhandled exceptions, sys.exit from non-main threads, etc.).
    # Task Manager "End Task" on Windows sends no signal and also skips atexit,
    # but this covers more exit paths than signal handlers alone.
    _atexit_called = False

    def _atexit_release_fans(*_args: object, **_kwargs: object) -> None:
        nonlocal _atexit_called
        if _atexit_called:
            return
        _atexit_called = True
        try:
            backend.release_fan_control_sync()
            logger.info("atexit: released fan control to BIOS/auto mode")
        except AttributeError:
            # Backend doesn't have a sync release method — skip silently
            pass
        except Exception:
            logger.debug("atexit: fan release failed (best-effort)", exc_info=True)

    atexit.register(_atexit_release_fans)

    print(f"  DriveChill v{settings.app_version} started")
    print(f"  Backend: {backend.get_backend_name()}")
    print(f"  Dashboard: http://localhost:{settings.port}")

    yield

    # ------------------------------------------------------------------
    # Shutdown
    # ------------------------------------------------------------------
    log_task.cancel()
    auth_cleanup_task.cancel()
    retention_prune_task.cancel()
    try:
        await log_task
    except asyncio.CancelledError:
        pass
    try:
        await auth_cleanup_task
    except asyncio.CancelledError:
        pass
    try:
        await retention_prune_task
    except asyncio.CancelledError:
        pass

    await fan_service.stop()
    await fan_test_service.shutdown()
    await quiet_hours_service.stop()
    await machine_monitor_service.stop()
    await sensor_service.stop()
    await logging_service.shutdown()
    await webhook_service.stop()
    await backend.shutdown()
    await db.close()


app = FastAPI(
    title="DriveChill",
    description="PC Fan Controller — Temperature-based fan speed management",
    version=settings.app_version,
    lifespan=lifespan,
)

# CORS
app.add_middleware(
    CORSMiddleware,
    allow_origins=settings.cors_origins,
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)


# Security response headers
class _SecurityHeadersMiddleware(BaseHTTPMiddleware):
    async def dispatch(self, request: StarletteRequest, call_next):
        response: StarletteResponse = await call_next(request)
        response.headers["X-Content-Type-Options"] = "nosniff"
        response.headers["X-Frame-Options"] = "DENY"
        response.headers["Referrer-Policy"] = "strict-origin-when-cross-origin"
        # Explicitly allow ws:// and wss:// to localhost so the WebSocket
        # connection works even in browsers where 'self' alone does not
        # cover the ws/wss scheme mapping.  Use the server-configured port
        # (not the request Host header, which is attacker-controlled).
        ws_host = f"localhost:{settings.port}"
        response.headers["Content-Security-Policy"] = (
            "default-src 'self'; "
            # Next.js static export injects inline bootstrap scripts.
            "script-src 'self' 'unsafe-inline'; "
            # Google Fonts stylesheet loaded by the Next.js layout.
            "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; "
            "img-src 'self' data:; "
            f"connect-src 'self' ws://{ws_host} wss://{ws_host}; "
            # Google Fonts font files.
            "font-src 'self' https://fonts.gstatic.com; "
            "frame-ancestors 'none'"
        )
        return response


app.add_middleware(_SecurityHeadersMiddleware)

# Auth routes (unprotected — login/setup must be accessible without a session)
app.include_router(auth_router)

# API routes (auth-protected when auth is enabled)
_auth_deps = [Depends(require_auth)]
app.include_router(sensors.router, dependencies=_auth_deps)
app.include_router(fans.router, dependencies=_auth_deps)
app.include_router(profiles.router, dependencies=_auth_deps)
app.include_router(alerts.router, dependencies=_auth_deps)
app.include_router(settings_route.router, dependencies=_auth_deps)
app.include_router(machines.router, dependencies=_auth_deps)
app.include_router(webhooks.router, dependencies=_auth_deps)
app.include_router(notifications_router, dependencies=_auth_deps)
app.include_router(quiet_hours_router, dependencies=_auth_deps)
app.include_router(analytics_route.router, dependencies=_auth_deps)
# WebSocket auth is handled inside the endpoint (require_ws_auth) because
# router-level Depends(require_auth) injects Request, which fails for WS.
app.include_router(ws_router)

# Health endpoint — must be registered via include_router (not @app.get)
# so it takes priority over the catch-all static files mount below.
_health_router = APIRouter()


@_health_router.get("/api/health")
async def health():
    return {
        "status": "ok",
        "app": settings.app_name,
        "api_version": "v1",
        "capabilities": [
            "api_keys",
            "webhooks",
            "machine_registry",
            "composite_curves",
            "fan_settings",
        ],
        "version": settings.app_version,
        "backend": app.state.backend.get_backend_name() if hasattr(app.state, "backend") else "unknown",
    }


@_health_router.get("/metrics", response_class=PlainTextResponse)
async def metrics():
    """Prometheus text exposition for core temperatures and fan RPM."""
    readings = []
    if hasattr(app.state, "sensor_service"):
        readings = app.state.sensor_service.latest
    lines = [
        "# HELP drivechill_temperature_c Temperature reading in Celsius",
        "# TYPE drivechill_temperature_c gauge",
        "# HELP drivechill_fan_rpm Fan speed in RPM",
        "# TYPE drivechill_fan_rpm gauge",
    ]
    for r in readings:
        sid = _sanitize_metric_label(r.id)
        if r.sensor_type.value in {"cpu_temp", "gpu_temp", "hdd_temp", "case_temp"}:
            lines.append(f'drivechill_temperature_c{{sensor_id="{sid}",sensor_type="{r.sensor_type.value}"}} {float(r.value):.3f}')
        elif r.sensor_type.value == "fan_rpm":
            lines.append(f'drivechill_fan_rpm{{sensor_id="{sid}"}} {float(r.value):.3f}')
    return "\n".join(lines) + "\n"


app.include_router(_health_router)

# Serve frontend static files (built Next.js export)
# Must be mounted AFTER all API routes — catch-all "/" mount intercepts everything.
# In a PyInstaller bundle, files land in sys._MEIPASS/frontend_out/
import sys as _sys
if getattr(_sys, "frozen", False):
    frontend_dist = Path(_sys._MEIPASS) / "frontend_out"
else:
    frontend_dist = Path(__file__).parent.parent.parent / "frontend" / "out"

if frontend_dist.exists():
    app.mount("/", StaticFiles(directory=str(frontend_dist), html=True), name="frontend")
