"""Tests for Home Assistant MQTT discovery message publishing."""
import json
import pytest
from unittest.mock import AsyncMock, MagicMock, call

from app.services.notification_channel_service import (
    NotificationChannel,
    NotificationChannelService,
)


def _make_channel(*, ha_discovery=True, ha_prefix="homeassistant",
                  topic_prefix="drivechill", channel_id="ch1") -> NotificationChannel:
    config = {
        "broker_url": "mqtt://localhost:1883",
        "publish_telemetry": True,
        "topic_prefix": topic_prefix,
        "ha_discovery": ha_discovery,
        "ha_discovery_prefix": ha_prefix,
    }
    return NotificationChannel(
        id=channel_id, type="mqtt", name="Test MQTT",
        enabled=True, config=config,
        created_at="", updated_at="",
    )


def _make_readings():
    return [
        {"id": "cpu_temp_0", "name": "CPU Core #0", "sensor_type": "temperature",
         "value": 55.0, "unit": "\u00b0C"},
        {"id": "fan_1", "name": "System Fan #1", "sensor_type": "fan_speed",
         "value": 1200, "unit": "RPM"},
    ]


@pytest.mark.anyio
async def test_ha_discovery_publishes_sensor_and_fan():
    """Discovery should publish sensor config for temp and fan config for fan readings."""
    svc = NotificationChannelService.__new__(NotificationChannelService)
    svc._ha_advertised = {}

    client = AsyncMock()
    channel = _make_channel()
    readings = _make_readings()

    await svc._publish_ha_discovery(channel, client, readings)

    assert client.publish.call_count == 2

    # Collect published topics and payloads
    published = {}
    for c in client.publish.call_args_list:
        topic = c.args[0] if c.args else c.kwargs.get("topic")
        payload_bytes = c.kwargs.get("payload", b"")
        published[topic] = json.loads(payload_bytes.decode()) if payload_bytes else None

    # Sensor (temperature)
    sensor_topic = "homeassistant/sensor/drivechill_cpu_temp_0/config"
    assert sensor_topic in published
    sensor_cfg = published[sensor_topic]
    assert sensor_cfg["name"] == "DriveChill CPU Core #0"
    assert sensor_cfg["unique_id"] == "drivechill_cpu_temp_0"
    assert sensor_cfg["state_topic"] == "drivechill/sensors/cpu_temp_0"
    assert sensor_cfg["value_template"] == "{{ value_json.value }}"
    assert sensor_cfg["unit_of_measurement"] == "\u00b0C"
    assert sensor_cfg["device"]["identifiers"] == ["drivechill"]
    assert "command_topic" not in sensor_cfg

    # Fan
    fan_topic = "homeassistant/fan/drivechill_fan_1/config"
    assert fan_topic in published
    fan_cfg = published[fan_topic]
    assert fan_cfg["name"] == "DriveChill System Fan #1"
    assert fan_cfg["unique_id"] == "drivechill_fan_1"
    assert fan_cfg["command_topic"] == "drivechill/commands/fans/fan_1/speed"
    assert fan_cfg["percentage_command_topic"] == "drivechill/commands/fans/fan_1/speed"
    assert fan_cfg["device"]["name"] == "DriveChill"


@pytest.mark.anyio
async def test_ha_discovery_skipped_when_disabled():
    """When ha_discovery is False, no messages should be published."""
    svc = NotificationChannelService.__new__(NotificationChannelService)
    svc._ha_advertised = {}

    client = AsyncMock()
    channel = _make_channel(ha_discovery=False)

    await svc._publish_ha_discovery(channel, client, _make_readings())

    client.publish.assert_not_called()


@pytest.mark.anyio
async def test_ha_discovery_custom_prefix():
    """Custom ha_discovery_prefix should be used in discovery topics."""
    svc = NotificationChannelService.__new__(NotificationChannelService)
    svc._ha_advertised = {}

    client = AsyncMock()
    channel = _make_channel(ha_prefix="my_ha", topic_prefix="mypc")
    readings = [{"id": "t1", "name": "Temp", "sensor_type": "temperature",
                 "value": 40.0, "unit": "C"}]

    await svc._publish_ha_discovery(channel, client, readings)

    topic = client.publish.call_args_list[0].args[0]
    assert topic == "my_ha/sensor/drivechill_t1/config"

    payload = json.loads(client.publish.call_args_list[0].kwargs["payload"].decode())
    assert payload["state_topic"] == "mypc/sensors/t1"


@pytest.mark.anyio
async def test_ha_discovery_removes_disappeared_sensors():
    """When a sensor disappears, an empty payload should be published to remove it."""
    svc = NotificationChannelService.__new__(NotificationChannelService)
    svc._ha_advertised = {"ch1": {"cpu_temp_0", "old_sensor"}}

    client = AsyncMock()
    channel = _make_channel()
    # Only cpu_temp_0 remains; old_sensor disappeared
    readings = [{"id": "cpu_temp_0", "name": "CPU", "sensor_type": "temperature",
                 "value": 50.0, "unit": "C"}]

    await svc._publish_ha_discovery(channel, client, readings)

    # 1 discovery publish for cpu_temp_0 + 2 removal publishes for old_sensor (sensor + fan)
    assert client.publish.call_count == 3

    # Find the removal calls (empty payload)
    removal_calls = [c for c in client.publish.call_args_list
                     if c.kwargs.get("payload") == b""]
    assert len(removal_calls) == 2

    removal_topics = {c.args[0] for c in removal_calls}
    assert "homeassistant/sensor/drivechill_old_sensor/config" in removal_topics
    assert "homeassistant/fan/drivechill_old_sensor/config" in removal_topics

    # After publish, only cpu_temp_0 should be tracked
    assert svc._ha_advertised["ch1"] == {"cpu_temp_0"}


@pytest.mark.anyio
async def test_ha_discovery_retain_and_qos():
    """Discovery messages must be published with retain=True and qos=1."""
    svc = NotificationChannelService.__new__(NotificationChannelService)
    svc._ha_advertised = {}

    client = AsyncMock()
    channel = _make_channel()
    readings = [{"id": "s1", "name": "S1", "sensor_type": "temperature",
                 "value": 30.0, "unit": "C"}]

    await svc._publish_ha_discovery(channel, client, readings)

    c = client.publish.call_args_list[0]
    assert c.kwargs["retain"] is True
    assert c.kwargs["qos"] == 1
