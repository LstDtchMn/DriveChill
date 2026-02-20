import asyncio
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware
from fastapi.staticfiles import StaticFiles

from app.config import settings
from app.hardware import get_backend
from app.services.sensor_service import SensorService
from app.services.fan_service import FanService
from app.services.alert_service import AlertService
from app.services.logging_service import LoggingService
from app.api.routes import sensors, fans, profiles, alerts, settings as settings_route
from app.api.websocket import router as ws_router


@asynccontextmanager
async def lifespan(app: FastAPI):
    # Startup
    backend = get_backend()
    await backend.initialize()
    app.state.backend = backend

    sensor_service = SensorService(backend, poll_interval=settings.sensor_poll_interval)
    app.state.sensor_service = sensor_service

    fan_service = FanService(backend)
    app.state.fan_service = fan_service

    alert_service = AlertService()
    app.state.alert_service = alert_service

    logging_service = LoggingService(settings.db_path)
    await logging_service.initialize()
    app.state.logging_service = logging_service

    await sensor_service.start()

    # Background task: log sensor data every 10 seconds
    async def log_loop():
        while True:
            await asyncio.sleep(10)
            readings = sensor_service.latest
            if readings:
                from app.models.sensors import SensorSnapshot
                from datetime import datetime
                snapshot = SensorSnapshot(timestamp=datetime.now(), readings=readings)
                await logging_service.log_snapshot(snapshot)

    log_task = asyncio.create_task(log_loop())

    print(f"  DriveChill v{settings.app_version} started")
    print(f"  Backend: {backend.get_backend_name()}")
    print(f"  Dashboard: http://localhost:{settings.port}")

    yield

    # Shutdown
    log_task.cancel()
    try:
        await log_task
    except asyncio.CancelledError:
        pass

    await sensor_service.stop()
    await logging_service.shutdown()
    await backend.shutdown()


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
app.include_router(ws_router)


# Serve frontend static files (built Next.js export)
# In a PyInstaller bundle, files land in sys._MEIPASS/frontend_out/
import sys as _sys
if getattr(_sys, "frozen", False):
    frontend_dist = Path(_sys._MEIPASS) / "frontend_out"
else:
    frontend_dist = Path(__file__).parent.parent.parent / "frontend" / "out"

if frontend_dist.exists():
    app.mount("/", StaticFiles(directory=str(frontend_dist), html=True), name="frontend")


@app.get("/api/health")
async def health():
    return {
        "status": "ok",
        "app": settings.app_name,
        "version": settings.app_version,
        "backend": app.state.backend.get_backend_name() if hasattr(app.state, "backend") else "unknown",
    }
