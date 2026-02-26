import asyncio
import logging
from collections import deque
from datetime import datetime, timezone

from app.hardware.base import HardwareBackend
from app.models.sensors import SensorReading, SensorSnapshot

logger = logging.getLogger(__name__)


class SensorService:
    """Periodically polls hardware sensors and maintains a history buffer."""

    def __init__(self, backend: HardwareBackend, poll_interval: float = 1.0) -> None:
        self._backend = backend
        self._poll_interval = poll_interval
        self._latest: list[SensorReading] = []
        self._history: deque[SensorSnapshot] = deque(maxlen=3600)  # 1 hour at 1s
        self._running = False
        self._task: asyncio.Task | None = None
        self._listeners: list[asyncio.Queue] = []

    @property
    def latest(self) -> list[SensorReading]:
        return list(self._latest)

    @property
    def history(self) -> list[SensorSnapshot]:
        return list(self._history)

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

    async def _poll_loop(self) -> None:
        while self._running:
            try:
                readings = await self._backend.get_sensor_readings()
                self._latest = readings

                snapshot = SensorSnapshot(timestamp=datetime.now(timezone.utc), readings=readings)
                self._history.append(snapshot)

                # Notify all listeners
                for queue in list(self._listeners):
                    try:
                        queue.put_nowait(snapshot)
                    except asyncio.QueueFull:
                        # Drop old data if consumer is slow
                        try:
                            queue.get_nowait()
                        except asyncio.QueueEmpty:
                            pass
                        try:
                            queue.put_nowait(snapshot)
                        except asyncio.QueueFull:
                            pass

            except Exception:
                # H-3: log the failure so hardware errors are visible; the
                # loop continues so fans keep running on the last known state.
                logger.exception("Sensor poll failed")

            await asyncio.sleep(self._poll_interval)
