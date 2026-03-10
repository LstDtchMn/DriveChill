"""Tests for MQTT telemetry publishing wiring."""
import asyncio
import pytest
from unittest.mock import AsyncMock, MagicMock


@pytest.mark.anyio
async def test_telemetry_publisher_calls_publish_on_snapshot():
    """The telemetry publisher should call publish_telemetry when it receives a snapshot."""
    from app.services.sensor_service import SensorService

    channel_svc = AsyncMock()
    channel_svc.publish_telemetry = AsyncMock(return_value=1)

    sensor_svc = MagicMock(spec=SensorService)
    queue = asyncio.Queue(maxsize=10)
    sensor_svc.subscribe.return_value = queue
    sensor_svc.unsubscribe = MagicMock()

    from app.mqtt_telemetry import create_telemetry_publisher

    task = asyncio.create_task(create_telemetry_publisher(sensor_svc, channel_svc))

    mock_snapshot = MagicMock()
    mock_snapshot.readings = [
        MagicMock(id="cpu_temp_0", name="CPU", sensor_type="temp", value=55.0, unit="°C"),
    ]
    await queue.put(mock_snapshot)
    await asyncio.sleep(0.1)

    channel_svc.publish_telemetry.assert_called_once()

    task.cancel()
    try:
        await task
    except asyncio.CancelledError:
        pass
    sensor_svc.unsubscribe.assert_called_once_with(queue)


@pytest.mark.anyio
async def test_telemetry_publisher_single_flight():
    """If a publish is already in-flight, the next snapshot should be dropped."""
    from app.services.sensor_service import SensorService

    publish_call_count = 0
    publish_started = asyncio.Event()
    publish_continue = asyncio.Event()

    async def slow_publish(readings):
        nonlocal publish_call_count
        publish_call_count += 1
        publish_started.set()
        await publish_continue.wait()
        return 1

    channel_svc = AsyncMock()
    channel_svc.publish_telemetry = slow_publish

    sensor_svc = MagicMock(spec=SensorService)
    queue = asyncio.Queue(maxsize=10)
    sensor_svc.subscribe.return_value = queue
    sensor_svc.unsubscribe = MagicMock()

    from app.mqtt_telemetry import create_telemetry_publisher

    task = asyncio.create_task(create_telemetry_publisher(sensor_svc, channel_svc))

    mock_snapshot = MagicMock()
    mock_snapshot.readings = [MagicMock(id="s1", name="S1", sensor_type="temp", value=50, unit="C")]
    await queue.put(mock_snapshot)
    await publish_started.wait()

    await queue.put(mock_snapshot)
    await asyncio.sleep(0.05)

    await queue.put(mock_snapshot)
    await asyncio.sleep(0.05)

    publish_continue.set()
    await asyncio.sleep(0.1)

    assert publish_call_count == 1

    task.cancel()
    try:
        await task
    except asyncio.CancelledError:
        pass
