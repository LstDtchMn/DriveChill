"""MQTT telemetry publisher — background task that subscribes to sensor
snapshots and publishes them to MQTT channels with publish_telemetry enabled.

Runs as a standalone coroutine started from main.py lifespan, not inside
SensorService (separation of concerns).
"""
import asyncio
import logging
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from app.services.sensor_service import SensorService
    from app.services.notification_channel_service import NotificationChannelService

logger = logging.getLogger(__name__)


async def _do_publish(
    channel_svc: "NotificationChannelService",
    snapshot,
) -> None:
    """Publish a single snapshot's readings to MQTT channels."""
    readings = [
        {
            "sensor_id": r.id,
            "sensor_name": r.name,
            "sensor_type": r.sensor_type,
            "value": r.value,
            "unit": r.unit,
        }
        for r in snapshot.readings
    ]
    await channel_svc.publish_telemetry(readings)


async def create_telemetry_publisher(
    sensor_svc: "SensorService",
    channel_svc: "NotificationChannelService",
) -> None:
    """Subscribe to sensor snapshots and publish telemetry to MQTT.

    Uses single-flight pattern: publish is launched as a fire-and-forget task.
    If the previous publish task is still running when the next snapshot arrives,
    the snapshot is dropped rather than queued.
    """
    queue = sensor_svc.subscribe()
    publish_task: asyncio.Task | None = None

    try:
        while True:
            snapshot = await queue.get()
            if snapshot is None:
                continue

            if publish_task is not None and not publish_task.done():
                continue

            publish_task = asyncio.create_task(_do_publish(channel_svc, snapshot))
    except asyncio.CancelledError:
        if publish_task is not None and not publish_task.done():
            try:
                await asyncio.wait_for(publish_task, timeout=1.0)
            except (asyncio.CancelledError, asyncio.TimeoutError, Exception):
                pass
    finally:
        sensor_svc.unsubscribe(queue)
