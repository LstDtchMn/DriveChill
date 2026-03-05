"""Temperature target service — proportional fan control based on drive temperatures."""

from __future__ import annotations

import logging
import threading
from typing import TYPE_CHECKING

from app.models.temperature_targets import TemperatureTarget

if TYPE_CHECKING:
    from app.db.repositories.temperature_target_repo import TemperatureTargetRepo

logger = logging.getLogger(__name__)


def compute_proportional_speed(
    temp: float,
    target_temp_c: float,
    tolerance_c: float,
    min_fan_speed: float,
) -> float:
    """Compute fan speed using proportional control within the tolerance band.

    Returns a speed in [min_fan_speed, 100.0].
    """
    if tolerance_c <= 0:
        # Zero or negative tolerance: any deviation triggers max speed.
        return 100.0

    low = target_temp_c - tolerance_c
    high = target_temp_c + tolerance_c

    if temp <= low:
        return min_fan_speed
    if temp >= high:
        return 100.0

    t = (temp - low) / (2 * tolerance_c)
    return min_fan_speed + t * (100.0 - min_fan_speed)


class TemperatureTargetService:
    """Manages temperature targets and evaluates proportional fan speeds.

    Locking strategy:
    - evaluate() is called from the sync fan-control loop; it reads _targets
      under a short threading.Lock.
    - Write methods (add/update/remove/set_enabled) persist to DB first (async),
      then update the in-memory cache under the same lock.  No awaits inside
      the lock.  On DB failure, the cache is not mutated.
    """

    def __init__(self, repo: TemperatureTargetRepo) -> None:
        self._repo = repo
        self._targets: list[TemperatureTarget] = []
        self._lock = threading.Lock()

    async def load(self) -> None:
        """Load all targets from DB into memory. Called at startup."""
        targets = await self._repo.list_all()
        with self._lock:
            self._targets = targets
        logger.info("Loaded %d temperature target(s)", len(targets))

    def evaluate(self, sensor_map: dict[str, float]) -> dict[str, float]:
        """Evaluate all enabled targets and return {fan_id: required_speed}.

        Called from the sync fan-control loop.  Returns the maximum speed
        required for each fan across all targets pointing to it.
        """
        result: dict[str, float] = {}
        with self._lock:
            targets = list(self._targets)

        for t in targets:
            if not t.enabled:
                continue
            temp = sensor_map.get(t.sensor_id)
            if temp is None:
                continue
            speed = compute_proportional_speed(
                temp, t.target_temp_c, t.tolerance_c, t.min_fan_speed,
            )
            for fan_id in t.fan_ids:
                result[fan_id] = max(result.get(fan_id, 0.0), speed)

        return result

    @property
    def targets(self) -> list[TemperatureTarget]:
        with self._lock:
            return list(self._targets)

    async def add(self, target: TemperatureTarget) -> TemperatureTarget:
        """Persist a new target to DB, then update in-memory cache."""
        created = await self._repo.create(target)
        with self._lock:
            self._targets.append(created)
        return created

    async def update(self, target_id: str, **fields) -> TemperatureTarget | None:
        """Persist updates to DB, then update in-memory cache."""
        updated = await self._repo.update(target_id, **fields)
        if updated is None:
            return None
        with self._lock:
            self._targets = [
                updated if t.id == target_id else t for t in self._targets
            ]
        return updated

    async def remove(self, target_id: str) -> bool:
        """Delete from DB, then remove from in-memory cache."""
        deleted = await self._repo.delete(target_id)
        if not deleted:
            return False
        with self._lock:
            self._targets = [t for t in self._targets if t.id != target_id]
        return True

    async def set_enabled(self, target_id: str, enabled: bool) -> TemperatureTarget | None:
        """Toggle enabled state on a target."""
        return await self.update(target_id, enabled=enabled)
