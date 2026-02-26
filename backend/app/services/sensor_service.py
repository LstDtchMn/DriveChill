import asyncio
import logging
from collections import deque
from datetime import datetime, timezone

from app.hardware.base import HardwareBackend
from app.models.sensors import SensorReading, SensorSnapshot

logger = logging.getLogger(__name__)

# Sentinel value pushed to subscriber queues when consecutive sensor failures
# exceed the configured limit.  FanService handles None by entering panic mode.
_FAILURE_SENTINEL = None


class SensorService:
    """Periodically polls hardware sensors and maintains a history buffer."""

    def __init__(
        self,
        backend: HardwareBackend,
        poll_interval: float = 1.0,
        failure_limit: int = 3,
    ) -> None:
        self._backend = backend
        self._poll_interval = poll_interval
        self._failure_limit = failure_limit
        self._latest: list[SensorReading] = []
        self._history: deque[SensorSnapshot] = deque(maxlen=3600)  # 1 hour at 1s
        self._running = False
        self._task: asyncio.Task | None = None
        self._listeners: list[asyncio.Queue] = []
        self._consecutive_failures: int = 0

    @property
    def poll_interval(self) -> float:
        return self._poll_interval

    @poll_interval.setter
    def poll_interval(self, value: float) -> None:
        self._poll_interval = max(0.5, value)

    @property
    def failure_limit(self) -> int:
        return self._failure_limit

    @failure_limit.setter
    def failure_limit(self, value: int) -> None:
        self._failure_limit = max(1, value)

    @property
    def latest(self) -> list[SensorReading]:
        return list(self._latest)

    @property
    def history(self) -> list[SensorSnapshot]:
        return list(self._history)

    @property
    def consecutive_failures(self) -> int:
        return self._consecutive_failures

    def subscribe(self) -> asyncio.Queue:
        """Create a new subscription queue for real-time updates."""
        queue: asyncio.Queue = asyncio.Queue(maxsize=10)
        self._listeners.append(queue)
        return queue

    def unsubscribe(self, queue: asyncio.Queue) -> None:
        """Remove a subscription queue."""
        if queue in self._listeners:
            self._listeners.remove(queue)

    async def start(self) -> None:
        """Start the polling loop."""
        self._running = True
        self._task = asyncio.create_task(self._poll_loop())

    async def stop(self) -> None:
        """Stop the polling loop."""
        self._running = False
        if self._task:
            self._task.cancel()
            try:
                await self._task
            except asyncio.CancelledError:
                pass

    def _notify_listeners(self, value) -> None:
        """Push a value (snapshot or None sentinel) to all subscriber queues."""
        for queue in list(self._listeners):
            try:
                queue.put_nowait(value)
            except asyncio.QueueFull:
                # Drop old data if consumer is slow
                try:
                    queue.get_nowait()
                except asyncio.QueueEmpty:
                    pass
                try:
                    queue.put_nowait(value)
                except asyncio.QueueFull:
                    pass

    async def _poll_loop(self) -> None:
        while self._running:
            try:
                readings = await self._backend.get_sensor_readings()
                self._latest = readings
                self._consecutive_failures = 0

                snapshot = SensorSnapshot(timestamp=datetime.now(timezone.utc), readings=readings)
                self._history.append(snapshot)
                self._notify_listeners(snapshot)

            except Exception:
                # H-3: log the failure so hardware errors are visible; the
                # loop continues so fans keep running on the last known state.
                logger.exception("Sensor poll failed")
                self._consecutive_failures += 1

                if self._consecutive_failures > self._failure_limit:
                    # Escalation: push None sentinel to notify FanService and
                    # WebSocket that readings are unavailable (PRD safe mode).
                    logger.error(
                        "Sensor failures: %d consecutive (limit=%d) — escalating to fan panic mode",
                        self._consecutive_failures,
                        self._failure_limit,
                    )
                    self._notify_listeners(_FAILURE_SENTINEL)

            await asyncio.sleep(self._poll_interval)
