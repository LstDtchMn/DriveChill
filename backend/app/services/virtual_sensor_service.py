"""Virtual sensor resolution service.

Resolves virtual sensor IDs into computed values based on their type
(max, min, avg, weighted, delta, moving_avg) from real sensor readings.
"""

from __future__ import annotations

import math
import time
from collections import deque
from dataclasses import dataclass, field


# Supported virtual sensor types
VIRTUAL_SENSOR_TYPES = {"max", "min", "avg", "weighted", "delta", "moving_avg"}


@dataclass
class VirtualSensorDef:
    id: str
    name: str
    type: str  # max | min | avg | weighted | delta | moving_avg
    source_ids: list[str]
    weights: list[float] | None = None
    window_seconds: float | None = None
    offset: float = 0.0
    enabled: bool = True


@dataclass
class _MovingAvgState:
    """Exponential moving average state for a virtual sensor."""
    value: float = float("nan")
    last_update: float = 0.0


class VirtualSensorService:
    """Resolves virtual sensors from real sensor values."""

    def __init__(self) -> None:
        self._defs: dict[str, VirtualSensorDef] = {}
        self._ema_state: dict[str, _MovingAvgState] = {}

    def load(self, defs: list[VirtualSensorDef]) -> None:
        """Replace all definitions (called at startup and on CRUD changes)."""
        self._defs = {d.id: d for d in defs}
        # Prune stale EMA state
        self._ema_state = {
            k: v for k, v in self._ema_state.items() if k in self._defs
        }

    @property
    def definitions(self) -> list[VirtualSensorDef]:
        return list(self._defs.values())

    def resolve_all(
        self,
        sensor_values: dict[str, float],
    ) -> dict[str, float]:
        """Compute all enabled virtual sensor values and merge into sensor_values.

        Returns a new dict that contains both real and virtual sensor values.
        Virtual sensors can reference other virtual sensors as long as there
        are no circular dependencies (evaluated in definition order).
        """
        result = dict(sensor_values)
        for vs in self._defs.values():
            if not vs.enabled:
                continue
            val = self._compute(vs, result)
            if val is not None and math.isfinite(val):
                result[vs.id] = val
        return result

    def _compute(
        self,
        vs: VirtualSensorDef,
        values: dict[str, float],
    ) -> float | None:
        """Compute a single virtual sensor value."""
        sources = [
            values[sid]
            for sid in vs.source_ids
            if sid in values and math.isfinite(values[sid])
        ]

        if vs.type == "delta":
            # delta: source_ids[0] - source_ids[1]
            if len(vs.source_ids) < 2:
                return None
            v0 = values.get(vs.source_ids[0])
            v1 = values.get(vs.source_ids[1])
            if v0 is None or v1 is None:
                return None
            if not math.isfinite(v0) or not math.isfinite(v1):
                return None
            return (v0 - v1) + vs.offset

        if not sources:
            return None

        raw: float
        if vs.type == "max":
            raw = max(sources)
        elif vs.type == "min":
            raw = min(sources)
        elif vs.type == "avg":
            raw = sum(sources) / len(sources)
        elif vs.type == "weighted":
            weights = vs.weights or []
            if len(weights) != len(vs.source_ids):
                # Fallback to equal weights
                raw = sum(sources) / len(sources)
            else:
                # Use only available sources with their corresponding weights
                w_sum = 0.0
                v_sum = 0.0
                for sid, w in zip(vs.source_ids, weights):
                    if sid in values and math.isfinite(values[sid]):
                        v_sum += values[sid] * w
                        w_sum += w
                if w_sum == 0:
                    return None
                raw = v_sum / w_sum
        elif vs.type == "moving_avg":
            instant = sum(sources) / len(sources)
            raw = self._update_ema(vs, instant)
        else:
            return None

        return raw + vs.offset

    def _update_ema(self, vs: VirtualSensorDef, instant: float) -> float:
        """Update exponential moving average for a virtual sensor."""
        now = time.monotonic()
        state = self._ema_state.get(vs.id)
        window = vs.window_seconds or 30.0

        if state is None or not math.isfinite(state.value):
            state = _MovingAvgState(value=instant, last_update=now)
            self._ema_state[vs.id] = state
            return instant

        dt = now - state.last_update
        if dt <= 0:
            return state.value

        # EMA smoothing: alpha = 1 - exp(-dt / window)
        alpha = 1.0 - math.exp(-dt / window) if window > 0 else 1.0
        state.value = state.value + alpha * (instant - state.value)
        state.last_update = now
        return state.value
