"""Drive monitoring service: polling, sensor injection."""
from __future__ import annotations

import asyncio
import logging
from typing import TYPE_CHECKING

import aiosqlite

from app.db.repositories.drive_repo import DriveRepo
from app.db.repositories.settings_repo import SettingsRepo
from app.hardware.drives.composite_provider import CompositeDriveProvider
from app.hardware.drives.native_provider import NativeDriveProvider
from app.hardware.drives.smartctl_provider import SmartctlProvider
from app.models.drives import (
    DriveRawData,
    DriveSettings,
    HealthStatus,
)
from app.models.sensors import SensorReading, SensorType
from app.services.drive_health_normalizer import DriveHealthNormalizer

if TYPE_CHECKING:
    from app.services.sensor_service import SensorService
    from app.services.smart_trend_service import SmartTrendService

logger = logging.getLogger(__name__)


class DriveMonitorService:
    """
    Coordinates drive discovery, polling, health normalization, and
    hdd_temp sensor injection into the main sensor pipeline.

    Polling schedule:
    - Rescan: default 900s — full inventory refresh
    - Health poll: default 300s — health, wear, counters
    - Temp poll: default 15s — temperature only (fast path)

    Drive temperatures are published as hdd_temp SensorReading objects
    and injected into SensorService so they ride the existing sensor/fan-curve
    / WebSocket / alert pipeline without any additional infrastructure.
    """

    def __init__(
        self,
        db: aiosqlite.Connection,
        settings_repo: SettingsRepo,
    ) -> None:
        self._db = db
        self._settings_repo = settings_repo
        self._repo = DriveRepo(db)

        # Built after loading settings
        self._provider: CompositeDriveProvider | None = None
        self._normalizer: DriveHealthNormalizer | None = None

        # In-memory drive cache: drive_id → latest DriveRawData
        self._drives: dict[str, DriveRawData] = {}

        self._sensor_service: SensorService | None = None
        self._smart_trend_svc: SmartTrendService | None = None

        self._running = False
        self._tasks: list[asyncio.Task] = []

    # ── Wiring ──────────────────────────────────────────────────────────────

    def set_sensor_service(self, svc: SensorService) -> None:
        self._sensor_service = svc

    def set_smart_trend_service(self, svc: SmartTrendService) -> None:
        self._smart_trend_svc = svc

    # ── Settings loading ─────────────────────────────────────────────────────

    async def _load_settings(self) -> DriveSettings:
        r = self._settings_repo
        return DriveSettings(
            enabled=bool(int(await r.get("drive_monitoring_enabled") or "1")),
            native_provider_enabled=bool(int(await r.get("drive_native_provider_enabled") or "1")),
            smartctl_provider_enabled=bool(int(await r.get("drive_smartctl_provider_enabled") or "1")),
            smartctl_path=await r.get("drive_smartctl_path") or "smartctl",
            fast_poll_seconds=int(await r.get("drive_fast_poll_seconds") or "15"),
            health_poll_seconds=int(await r.get("drive_health_poll_seconds") or "300"),
            rescan_poll_seconds=int(await r.get("drive_rescan_poll_seconds") or "900"),
            hdd_temp_warning_c=float(await r.get("drive_hdd_temp_warning_c") or "45"),
            hdd_temp_critical_c=float(await r.get("drive_hdd_temp_critical_c") or "50"),
            ssd_temp_warning_c=float(await r.get("drive_ssd_temp_warning_c") or "55"),
            ssd_temp_critical_c=float(await r.get("drive_ssd_temp_critical_c") or "65"),
            nvme_temp_warning_c=float(await r.get("drive_nvme_temp_warning_c") or "65"),
            nvme_temp_critical_c=float(await r.get("drive_nvme_temp_critical_c") or "75"),
            wear_warning_percent_used=float(await r.get("drive_wear_warning_percent_used") or "80"),
            wear_critical_percent_used=float(await r.get("drive_wear_critical_percent_used") or "90"),
        )

    # ── Startup / shutdown ───────────────────────────────────────────────────

    async def start(self) -> None:
        settings = await self._load_settings()
        if not settings.enabled:
            logger.info("Drive monitoring disabled by settings")
            return

        native = NativeDriveProvider()
        smartctl = SmartctlProvider(smartctl_path=settings.smartctl_path)
        self._provider = CompositeDriveProvider(
            native=native,
            smartctl=smartctl,
            prefer_smartctl=settings.smartctl_provider_enabled,
        )
        self._normalizer = DriveHealthNormalizer(settings)

        self._running = True

        # Initial inventory scan before starting loops (blocking is fine here)
        try:
            await self._rescan(settings)
        except Exception:
            logger.exception("Drive initial scan failed")

        # Restart recovery: reconcile any self-tests still marked 'running'
        reconcile_task = asyncio.create_task(self._reconcile_running_tests())

        # Polling loops
        self._tasks = [reconcile_task,
            asyncio.create_task(self._temp_loop(settings)),
            asyncio.create_task(self._health_loop(settings)),
            asyncio.create_task(self._rescan_loop(settings)),
        ]

    async def stop(self) -> None:
        self._running = False
        for t in self._tasks:
            t.cancel()
        for t in self._tasks:
            try:
                await t
            except asyncio.CancelledError:
                pass
        self._tasks.clear()

    # ── Drive state accessors ────────────────────────────────────────────────

    def get_all_drives(self) -> list[DriveRawData]:
        return list(self._drives.values())

    def get_drive(self, drive_id: str) -> DriveRawData | None:
        return self._drives.get(drive_id)

    async def get_settings(self) -> DriveSettings:
        return await self._load_settings()

    async def is_smartctl_available(self) -> bool:
        if self._provider is None:
            return False
        return await self._provider.smartctl_available()

    # ── Polling loops ────────────────────────────────────────────────────────

    async def _temp_loop(self, settings: DriveSettings) -> None:
        while self._running:
            await asyncio.sleep(settings.fast_poll_seconds)
            try:
                await self._poll_temps(settings)
            except asyncio.CancelledError:
                raise
            except Exception:
                logger.exception("Drive temp poll failed")

    async def _health_loop(self, settings: DriveSettings) -> None:
        while self._running:
            await asyncio.sleep(settings.health_poll_seconds)
            try:
                await self._poll_health(settings)
            except asyncio.CancelledError:
                raise
            except Exception:
                logger.exception("Drive health poll failed")

    async def _rescan_loop(self, settings: DriveSettings) -> None:
        while self._running:
            await asyncio.sleep(settings.rescan_poll_seconds)
            try:
                await self._rescan(settings)
            except asyncio.CancelledError:
                raise
            except Exception:
                logger.exception("Drive rescan failed")

    # ── Polling implementations ──────────────────────────────────────────────

    async def _rescan(self, settings: DriveSettings) -> None:
        if self._provider is None:
            return
        drives = await self._provider.discover()
        smartctl_ok = await self._provider.smartctl_available()
        native_ok = await self._provider.native_available()

        for raw in drives:
            self._drives[raw.id] = raw
            await self._repo.upsert_drive(
                id=raw.id,
                name=raw.name,
                model=raw.model,
                serial_full=raw.serial,
                device_path=raw.device_path,
                bus_type=raw.bus_type.value,
                media_type=raw.media_type.value,
                capacity_bytes=raw.capacity_bytes,
                firmware_version=raw.firmware_version,
                smart_available=raw.capabilities.smart_read,
                native_available=native_ok,
                supports_self_test=raw.capabilities.smart_self_test_short,
                supports_abort=raw.capabilities.smart_self_test_abort,
            )
            if raw.raw_attributes:
                await self._repo.upsert_attributes(raw.id, raw.raw_attributes)

        await self._publish_drive_sensors(settings)
        logger.debug("Drive rescan complete: %d drives found", len(drives))

    async def _poll_temps(self, settings: DriveSettings) -> None:
        if self._provider is None:
            return
        updated = False
        for drive_id, raw in list(self._drives.items()):
            refreshed = await self._provider.refresh(raw.device_path)
            if refreshed is not None:
                self._drives[drive_id] = refreshed
                if refreshed.temperature_c is not None:
                    updated = True
        if updated:
            await self._publish_drive_sensors(settings)

    async def _poll_health(self, settings: DriveSettings) -> None:
        if self._provider is None or self._normalizer is None:
            return
        for drive_id, raw in list(self._drives.items()):
            refreshed = await self._provider.refresh(raw.device_path)
            if refreshed is None:
                continue
            self._drives[drive_id] = refreshed

            health = self._normalizer.health_status(refreshed)
            health_pct = self._normalizer.health_percent(refreshed)

            await self._repo.insert_health_snapshot(
                drive_id=drive_id,
                temperature_c=refreshed.temperature_c,
                health_status=health.value,
                health_percent=health_pct,
                predicted_failure=refreshed.predicted_failure,
                wear_percent_used=refreshed.wear_percent_used,
                available_spare_percent=refreshed.available_spare_percent,
                reallocated_sectors=refreshed.reallocated_sectors,
                pending_sectors=refreshed.pending_sectors,
                uncorrectable_errors=refreshed.uncorrectable_errors,
                media_errors=refreshed.media_errors,
                power_on_hours=refreshed.power_on_hours,
                unsafe_shutdowns=refreshed.unsafe_shutdowns,
            )
            if refreshed.raw_attributes:
                await self._repo.upsert_attributes(drive_id, refreshed.raw_attributes)

            # SMART trend detection
            if self._smart_trend_svc:
                self._smart_trend_svc.check_drive(
                    drive_id=drive_id,
                    drive_name=refreshed.name,
                    reallocated_sectors=refreshed.reallocated_sectors,
                    wear_percent_used=refreshed.wear_percent_used,
                    power_on_hours=refreshed.power_on_hours,
                )

        await self._publish_drive_sensors(settings)

    # ── Sensor injection ─────────────────────────────────────────────────────

    async def _publish_drive_sensors(self, settings: DriveSettings) -> None:
        if self._sensor_service is None:
            return
        readings: list[SensorReading] = []
        for raw in self._drives.values():
            if raw.temperature_c is None:
                continue
            warn_c = self._normalizer.temp_warning_c(raw) if self._normalizer else 45.0
            crit_c = self._normalizer.temp_critical_c(raw) if self._normalizer else 50.0
            sensor_id = f"hdd_temp_{raw.id}"
            readings.append(
                SensorReading(
                    id=sensor_id,
                    name=raw.name,
                    sensor_type=SensorType.HDD_TEMP,
                    value=raw.temperature_c,
                    min_value=0.0,
                    max_value=crit_c + 20.0,
                    unit="°C",
                    drive_id=raw.id,
                    entity_name=raw.name,
                    source_kind=(
                        "smartctl" if raw.capabilities.temperature_source.value == "smartctl"
                        else "native"
                    ),
                )
            )
        self._sensor_service.update_drive_readings(readings)

    # ── Manual actions (called from routes) ─────────────────────────────────

    async def rescan_now(self) -> int:
        """Trigger an immediate rescan and return the count of discovered drives."""
        settings = await self._load_settings()
        await self._rescan(settings)
        return len(self._drives)

    async def refresh_drive(self, drive_id: str) -> DriveRawData | None:
        raw = self._drives.get(drive_id)
        if raw is None:
            return None
        if self._provider is None:
            return raw
        refreshed = await self._provider.refresh(raw.device_path)
        if refreshed is not None:
            self._drives[drive_id] = refreshed
            settings = await self._load_settings()
            await self._publish_drive_sensors(settings)
        return refreshed or raw

    # ── Self-test restart recovery ────────────────────────────────────────────

    async def _reconcile_running_tests(self) -> None:
        """On startup, reconcile self-test rows marked 'running' with provider state."""
        if self._provider is None:
            return
        running = await self._repo.get_running_self_tests()
        for row in running:
            drive_id = row["drive_id"]
            run_id = row["id"]
            raw = self._drives.get(drive_id)
            if raw is None:
                await self._repo.update_self_test_run(
                    run_id, status="aborted", failure_message="Drive not found on restart"
                )
                continue
            result = await self._provider.get_self_test_status(raw.device_path)
            if result is None:
                await self._repo.update_self_test_run(
                    run_id, status="aborted", failure_message="Status unavailable on restart"
                )
            elif result.status.value in ("passed", "failed", "aborted"):
                await self._repo.update_self_test_run(
                    run_id,
                    status=result.status.value,
                    progress_percent=result.progress_percent,
                    failure_message=result.failure_message,
                )
            # else still running — leave as-is and the self-test service will poll
