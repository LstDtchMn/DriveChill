"""Drive self-test lifecycle service."""
from __future__ import annotations

import asyncio
import logging
from typing import TYPE_CHECKING

from app.db.repositories.drive_repo import DriveRepo
from app.hardware.drives.base import ProviderError
from app.models.drives import SelfTestType

if TYPE_CHECKING:
    from app.services.drive_monitor_service import DriveMonitorService

logger = logging.getLogger(__name__)

_POLL_INTERVAL = 30.0  # seconds between status checks while a test is running


class DriveSelfTestService:
    """Manages self-test start/abort and polling for active runs."""

    def __init__(self, drive_repo: DriveRepo, monitor: DriveMonitorService) -> None:
        self._repo = drive_repo
        self._monitor = monitor
        self._running = False
        self._poll_task: asyncio.Task | None = None

    async def start(self) -> None:
        self._running = True
        self._poll_task = asyncio.create_task(self._poll_loop())

    async def stop(self) -> None:
        self._running = False
        if self._poll_task:
            self._poll_task.cancel()
            try:
                await self._poll_task
            except asyncio.CancelledError:
                pass

    async def start_test(self, drive_id: str, test_type: SelfTestType) -> dict:
        """Start a self-test. Returns the created run dict."""
        raw = self._monitor.get_drive(drive_id)
        if raw is None:
            raise ValueError(f"Drive not found: {drive_id}")
        if not raw.capabilities.smart_self_test_short:
            raise ValueError("Self-test not supported for this drive")

        # Reject if a test is already in progress for this drive
        running = await self._repo.get_running_self_tests()
        if any(r["drive_id"] == drive_id for r in running):
            raise ValueError("A self-test is already in progress for this drive")

        provider = self._monitor._provider
        if provider is None:
            raise ValueError("Drive provider not available")

        try:
            ref = await provider.start_self_test(raw.device_path, test_type)
        except ProviderError as exc:
            raise ValueError(f"Self-test start failed: {exc.message}") from exc

        run_id = await self._repo.create_self_test_run(
            drive_id=drive_id,
            test_type=test_type.value,
            provider_run_ref=ref,
        )
        return await self._repo.get_self_test_run(run_id) or {}

    async def abort_test(self, drive_id: str, run_id: str) -> bool:
        """Abort a running self-test. Returns True on success."""
        run = await self._repo.get_self_test_run(run_id)
        if run is None:
            return False
        if run["drive_id"] != drive_id:
            return False

        raw = self._monitor.get_drive(drive_id)
        if raw is None:
            return False

        provider = self._monitor._provider
        if provider is None:
            return False

        aborted = await provider.abort_self_test(raw.device_path)
        if aborted:
            await self._repo.update_self_test_run(run_id, status="aborted")
        return aborted

    async def _poll_loop(self) -> None:
        """Poll status for all running self-tests every 30s."""
        while self._running:
            await asyncio.sleep(_POLL_INTERVAL)
            try:
                await self._poll_active_tests()
            except asyncio.CancelledError:
                raise
            except Exception:
                logger.exception("Self-test status poll failed")

    async def _poll_active_tests(self) -> None:
        running = await self._repo.get_running_self_tests()
        if not running:
            return

        provider = self._monitor._provider
        if provider is None:
            return

        for row in running:
            drive_id = row["drive_id"]
            run_id = row["id"]
            raw = self._monitor.get_drive(drive_id)
            if raw is None:
                await self._repo.update_self_test_run(
                    run_id, status="aborted", failure_message="Drive no longer present"
                )
                continue

            try:
                result = await provider.get_self_test_status(raw.device_path)
            except Exception as exc:
                logger.warning("Self-test status check failed for %s: %s", drive_id, exc)
                continue

            if result is None:
                continue

            if result.status.value in ("passed", "failed", "aborted"):
                await self._repo.update_self_test_run(
                    run_id,
                    status=result.status.value,
                    progress_percent=result.progress_percent,
                    failure_message=result.failure_message,
                )
            elif result.status.value == "running":
                await self._repo.update_self_test_run(
                    run_id,
                    status="running",
                    progress_percent=result.progress_percent,
                )
