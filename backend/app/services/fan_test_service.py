from __future__ import annotations

import asyncio
import logging
from dataclasses import dataclass
from datetime import datetime, timezone
from threading import Lock
from typing import Literal

from pydantic import BaseModel, Field

from app.models.sensors import SensorReading, SensorType

logger = logging.getLogger(__name__)


class FanTestOptions(BaseModel):
    steps: int = Field(default=10, ge=2, le=20)
    settle_ms: int = Field(default=2500, ge=500, le=10000)
    min_rpm_threshold: float = Field(default=50.0, ge=0.0, le=5000.0)


class FanTestStep(BaseModel):
    speed_pct: float
    rpm: float | None
    spinning: bool


class FanTestResult(BaseModel):
    fan_id: str
    status: Literal["running", "completed", "cancelled", "failed"] = "running"
    started_at: datetime = Field(default_factory=lambda: datetime.now(timezone.utc))
    completed_at: datetime | None = None
    steps: list[FanTestStep] = Field(default_factory=list)
    min_operational_pct: float | None = None
    max_rpm: float | None = None
    options: FanTestOptions = Field(default_factory=FanTestOptions)
    error: str | None = None


class FanTestProgress(BaseModel):
    fan_id: str
    status: str
    steps_done: int
    steps_total: int
    current_pct: float
    current_rpm: float | None
    steps: list[FanTestStep]
    min_operational_pct: float | None


@dataclass
class _TestRun:
    result: FanTestResult
    task: asyncio.Task | None = None
    current_pct: float = 0.0


class FanTestService:
    """Runs per-fan benchmark sweeps and exposes progress/results for API + WS."""

    def __init__(self, backend, sensor_service, fan_service) -> None:
        self._backend = backend
        self._sensor_service = sensor_service
        self._fan_service = fan_service
        self._runs: dict[str, _TestRun] = {}
        self._lock = Lock()

    async def try_start(self, fan_id: str, options: FanTestOptions) -> tuple[bool, str]:
        fan_ids = await self._backend.get_fan_ids()
        if fan_id not in fan_ids:
            return False, f"Fan '{fan_id}' not found"

        with self._lock:
            existing = self._runs.get(fan_id)
            if existing and existing.result.status == "running":
                return False, f"A test is already running for fan '{fan_id}'"

            result = FanTestResult(fan_id=fan_id, options=options)
            run = _TestRun(result=result)
            self._runs[fan_id] = run
            self._fan_service.lock_for_test(fan_id)
            run.task = asyncio.create_task(self._run_sweep(fan_id, run))
        return True, ""

    def get_result(self, fan_id: str) -> FanTestResult | None:
        with self._lock:
            run = self._runs.get(fan_id)
            if run is None:
                return None
            return run.result.model_copy(deep=True)

    def cancel(self, fan_id: str) -> bool:
        with self._lock:
            run = self._runs.get(fan_id)
            if run is None or run.result.status != "running" or run.task is None:
                return False
            run.task.cancel()
            return True

    def get_active_progress(self) -> list[FanTestProgress]:
        progress: list[FanTestProgress] = []
        with self._lock:
            for run in self._runs.values():
                if run.result.status != "running":
                    continue
                current_rpm = run.result.steps[-1].rpm if run.result.steps else None
                progress.append(
                    FanTestProgress(
                        fan_id=run.result.fan_id,
                        status=run.result.status,
                        steps_done=len(run.result.steps),
                        steps_total=run.result.options.steps + 1,
                        current_pct=run.current_pct,
                        current_rpm=current_rpm,
                        steps=[s.model_copy(deep=True) for s in run.result.steps],
                        min_operational_pct=run.result.min_operational_pct,
                    )
                )
        return progress

    async def shutdown(self) -> None:
        tasks: list[asyncio.Task] = []
        with self._lock:
            for run in self._runs.values():
                if run.task and not run.task.done():
                    run.task.cancel()
                    tasks.append(run.task)
        if tasks:
            await asyncio.gather(*tasks, return_exceptions=True)

    async def _run_sweep(self, fan_id: str, run: _TestRun) -> None:
        opts = run.result.options
        step_size = 100.0 / opts.steps
        speeds = [min(100.0, round(i * step_size, 1)) for i in range(opts.steps + 1)]

        try:
            for speed in speeds:
                await self._backend.set_fan_speed(fan_id, speed)
                with self._lock:
                    run.current_pct = speed

                await asyncio.sleep(opts.settle_ms / 1000.0)
                rpm = self._read_rpm(fan_id, self._sensor_service.latest)
                spinning = rpm is not None and rpm >= opts.min_rpm_threshold

                step = FanTestStep(speed_pct=speed, rpm=rpm, spinning=spinning)
                with self._lock:
                    run.result.steps.append(step)
                    if spinning and run.result.min_operational_pct is None:
                        run.result.min_operational_pct = speed
                    if speed >= 100.0 and rpm is not None:
                        run.result.max_rpm = rpm

            with self._lock:
                run.result.status = "completed"
                run.result.completed_at = datetime.now(timezone.utc)

        except asyncio.CancelledError:
            with self._lock:
                run.result.status = "cancelled"
                run.result.completed_at = datetime.now(timezone.utc)
            raise
        except Exception as exc:
            logger.exception("Fan benchmark failed for %s", fan_id)
            with self._lock:
                run.result.status = "failed"
                run.result.error = str(exc)
                run.result.completed_at = datetime.now(timezone.utc)
        finally:
            self._fan_service.unlock_from_test(fan_id)

    @staticmethod
    def _read_rpm(fan_id: str, readings: list[SensorReading]) -> float | None:
        target = f"{fan_id}_rpm"
        for r in readings:
            if r.sensor_type == SensorType.FAN_RPM and (r.id == target or r.id.startswith(fan_id)):
                return r.value
        return None
