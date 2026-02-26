import asyncio
import logging
import signal
import sys
from contextlib import asynccontextmanager
from pathlib import Path

import aiosqlite
from fastapi import APIRouter, FastAPI
from fastapi.middleware.cors import CORSMiddleware
from fastapi.staticfiles import StaticFiles

from app.config import settings
from app.db.migration_runner import run_migrations
from app.db.repositories.profile_repo import ProfileRepo
from app.db.repositories.settings_repo import SettingsRepo
from app.hardware import get_backend
from app.services.sensor_service import SensorService
from app.services.fan_service import FanService
from app.services.fan_test_service import FanTestService
from app.services.alert_service import AlertService
from app.services.logging_service import LoggingService
from app.services.quiet_hours_service import QuietHoursService
from app.api.routes import sensors, fans, profiles, alerts, settings as settings_route
from app.api.routes.quiet_hours import router as quiet_hours_router
from app.api.websocket import router as ws_router

logger = logging.getLogger(__name__)


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
    await settings_repo.seed_defaults()
    app.state.profile_repo = profile_repo
    app.state.settings_repo = settings_repo

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
    app.state.fan_service = fan_service

    alert_service = AlertService()
    app.state.alert_service = alert_service

    fan_test_service = FanTestService(backend, sensor_service, fan_service)
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
    await fan_service.start(sensor_service, alert_service)

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

    print(f"  DriveChill v{settings.app_version} started")
    print(f"  Backend: {backend.get_backend_name()}")
    print(f"  Dashboard: http://localhost:{settings.port}")

    yield

    # ------------------------------------------------------------------
    # Shutdown
    # ------------------------------------------------------------------
    log_task.cancel()
    try:
        await log_task
    except asyncio.CancelledError:
        pass

    await fan_service.stop()
    await fan_test_service.shutdown()
    await quiet_hours_service.stop()
    await sensor_service.stop()
    await logging_service.shutdown()
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

# API routes
app.include_router(sensors.router)
app.include_router(fans.router)
app.include_router(profiles.router)
app.include_router(alerts.router)
app.include_router(settings_route.router)
app.include_router(quiet_hours_router)
app.include_router(ws_router)

# Health endpoint — must be registered via include_router (not @app.get)
# so it takes priority over the catch-all static files mount below.
_health_router = APIRouter()


@_health_router.get("/api/health")
async def health():
    return {
        "status": "ok",
        "app": settings.app_name,
        "version": settings.app_version,
        "backend": app.state.backend.get_backend_name() if hasattr(app.state, "backend") else "unknown",
    }


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
