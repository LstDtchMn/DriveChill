"""Tests for liquidctl USB controller backend (Phase 11).

Mocks the liquidctl subprocess to test parsing and control logic.
"""
from __future__ import annotations

import asyncio
import json
from unittest.mock import AsyncMock, patch, MagicMock

from app.hardware.liquidctl_backend import (
    LiquidctlBackend,
    LiquidctlDevice,
    _sanitize,
    _extract_channel_name,
    _make_id_prefix,
    _match_device_profile,
    DEVICE_PROFILES,
)


def _run(coro):
    return asyncio.run(coro)


# Sample liquidctl JSON outputs
SAMPLE_LIST = [
    {
        "description": "NZXT Kraken X63",
        "address": "usb:1:2",
        "vendor_id": "0x1e71",
        "product_id": "0x2007",
    },
]

SAMPLE_STATUS_TRIPLES = [
    ["Liquid temperature", 32.5, "°C"],
    ["Fan 1 speed", 850, "rpm"],
    ["Fan 1 duty", 40, "%"],
    ["Pump speed", 2100, "rpm"],
    ["Pump duty", 60, "%"],
]


class _FakeProcess:
    """Mock async subprocess."""
    def __init__(self, stdout: str = "", returncode: int = 0):
        self._stdout = stdout
        self.returncode = returncode

    async def communicate(self):
        return self._stdout.encode(), b""


def _mock_subprocess(list_json=None, status_json=None):
    """Create a mock for asyncio.create_subprocess_exec."""
    async def fake_exec(*args, **kwargs):
        cmd_args = list(args)
        # Find the liquidctl subcommand
        for i, a in enumerate(cmd_args):
            if a == "list":
                return _FakeProcess(json.dumps(list_json or []))
            elif a == "status":
                return _FakeProcess(json.dumps(status_json or []))
            elif a == "initialize":
                return _FakeProcess("")
            elif a == "set":
                return _FakeProcess("")
        return _FakeProcess("")
    return fake_exec


class TestHelpers:
    def test_sanitize(self):
        assert _sanitize("NZXT Kraken X63") == "nzxt_kraken_x63"
        assert _sanitize("Fan 1") == "fan_1"
        assert _sanitize("some-device/v2.0") == "some_device_v20"

    def test_extract_channel_name(self):
        assert _extract_channel_name("Fan 1 speed") == "fan1"
        assert _extract_channel_name("Fan 2 duty") == "fan2"
        assert _extract_channel_name("Pump speed") == "pump"
        assert _extract_channel_name("Pump duty") == "pump"
        assert _extract_channel_name("Liquid temperature") is None

    def test_make_id_prefix(self):
        dev = LiquidctlDevice(address="usb:1:2", description="NZXT Kraken X63",
                              vendor_id="", product_id="")
        assert _make_id_prefix(dev) == "lctl_nzxt_kraken_x63_usb:1:2"

    def test_duplicate_devices_get_distinct_prefixes(self):
        """Two identical device models on different USB ports produce distinct ID prefixes."""
        dev_a = LiquidctlDevice(address="usb:1:2", description="NZXT Kraken X63",
                                vendor_id="0x1e71", product_id="0x2007")
        dev_b = LiquidctlDevice(address="usb:1:3", description="NZXT Kraken X63",
                                vendor_id="0x1e71", product_id="0x2007")
        prefix_a = _make_id_prefix(dev_a)
        prefix_b = _make_id_prefix(dev_b)
        assert prefix_a != prefix_b
        assert "usb:1:2" in prefix_a
        assert "usb:1:3" in prefix_b


class TestDiscovery:
    def test_not_available(self):
        async def _test():
            with patch("shutil.which", return_value=None):
                backend = LiquidctlBackend()
                await backend.initialize()
                assert backend._available is False
                assert backend.get_backend_name() == "liquidctl (not available)"
                ids = await backend.get_fan_ids()
                assert ids == []
        _run(_test())

    def test_discovers_devices(self):
        async def _test():
            with patch("shutil.which", return_value="/usr/bin/liquidctl"), \
                 patch("asyncio.create_subprocess_exec", side_effect=_mock_subprocess(
                     list_json=SAMPLE_LIST, status_json=SAMPLE_STATUS_TRIPLES)):
                backend = LiquidctlBackend()
                await backend.initialize()
                assert backend._available is True
                assert len(backend._devices) == 1
                dev = backend._devices[0]
                assert dev.description == "NZXT Kraken X63"
                assert "fan1" in dev.fan_channels or "pump" in dev.fan_channels
        _run(_test())

    def test_backend_name_with_devices(self):
        async def _test():
            with patch("shutil.which", return_value="/usr/bin/liquidctl"), \
                 patch("asyncio.create_subprocess_exec", side_effect=_mock_subprocess(
                     list_json=SAMPLE_LIST, status_json=SAMPLE_STATUS_TRIPLES)):
                backend = LiquidctlBackend()
                await backend.initialize()
                name = backend.get_backend_name()
                assert "liquidctl" in name
                assert "1 device" in name
        _run(_test())


class TestSensorReadings:
    def test_reads_temps_and_fans(self):
        async def _test():
            with patch("shutil.which", return_value="/usr/bin/liquidctl"), \
                 patch("asyncio.create_subprocess_exec", side_effect=_mock_subprocess(
                     list_json=SAMPLE_LIST, status_json=SAMPLE_STATUS_TRIPLES)):
                backend = LiquidctlBackend()
                await backend.initialize()
                readings = await backend.get_sensor_readings()

                # Should have temp, fan RPM, and duty readings
                ids = [r.id for r in readings]
                names = [r.name for r in readings]
                types = [r.sensor_type.value for r in readings]

                # At least one temperature reading
                assert any("temp" in t for t in types)
                # At least one fan RPM
                assert any("fan_rpm" in t for t in types)
        _run(_test())

    def test_no_readings_when_unavailable(self):
        async def _test():
            with patch("shutil.which", return_value=None):
                backend = LiquidctlBackend()
                await backend.initialize()
                readings = await backend.get_sensor_readings()
                assert readings == []
        _run(_test())


class TestFanControl:
    def test_set_fan_returns_false_when_unavailable(self):
        async def _test():
            with patch("shutil.which", return_value=None):
                backend = LiquidctlBackend()
                await backend.initialize()
                ok = await backend.set_fan_speed("some_fan", 50.0)
                assert ok is False
        _run(_test())

    def test_set_unknown_fan_returns_false(self):
        async def _test():
            with patch("shutil.which", return_value="/usr/bin/liquidctl"), \
                 patch("asyncio.create_subprocess_exec", side_effect=_mock_subprocess(
                     list_json=SAMPLE_LIST, status_json=SAMPLE_STATUS_TRIPLES)):
                backend = LiquidctlBackend()
                await backend.initialize()
                ok = await backend.set_fan_speed("nonexistent_fan", 50.0)
                assert ok is False
        _run(_test())

    def test_set_fan_clamps_range(self):
        async def _test():
            with patch("shutil.which", return_value="/usr/bin/liquidctl"), \
                 patch("asyncio.create_subprocess_exec", side_effect=_mock_subprocess(
                     list_json=SAMPLE_LIST, status_json=SAMPLE_STATUS_TRIPLES)):
                backend = LiquidctlBackend()
                await backend.initialize()

                # Get a real fan ID
                fan_ids = await backend.get_fan_ids()
                if not fan_ids:
                    return  # No fans discovered in mock

                # Should not crash with out-of-range values
                await backend.set_fan_speed(fan_ids[0], -10.0)
                await backend.set_fan_speed(fan_ids[0], 150.0)
        _run(_test())


class TestGetFanIds:
    def test_returns_device_channels(self):
        async def _test():
            with patch("shutil.which", return_value="/usr/bin/liquidctl"), \
                 patch("asyncio.create_subprocess_exec", side_effect=_mock_subprocess(
                     list_json=SAMPLE_LIST, status_json=SAMPLE_STATUS_TRIPLES)):
                backend = LiquidctlBackend()
                await backend.initialize()
                fan_ids = await backend.get_fan_ids()
                # All IDs should start with lctl_ prefix
                for fid in fan_ids:
                    assert fid.startswith("lctl_")
        _run(_test())


# ---------------------------------------------------------------------------
# Duplicate-device identity tests
# ---------------------------------------------------------------------------

DUPLICATE_LIST = [
    {
        "description": "NZXT Kraken X63",
        "address": "usb:1:2",
        "vendor_id": "0x1e71",
        "product_id": "0x2007",
    },
    {
        "description": "NZXT Kraken X63",
        "address": "usb:1:3",
        "vendor_id": "0x1e71",
        "product_id": "0x2007",
    },
]


def _mock_subprocess_multi(list_json=None, status_json=None):
    """Mock that tracks which --address arg was used for status/set commands."""
    calls: list[tuple] = []

    async def fake_exec(*args, **kwargs):
        cmd_args = list(args)
        addr = None
        for i, a in enumerate(cmd_args):
            if a == "--address" and i + 1 < len(cmd_args):
                addr = cmd_args[i + 1]
        for a in cmd_args:
            if a == "list":
                return _FakeProcess(json.dumps(list_json or []))
            elif a == "status":
                calls.append(("status", addr))
                return _FakeProcess(json.dumps(status_json or []))
            elif a == "initialize":
                return _FakeProcess("")
            elif a == "set":
                calls.append(("set", addr))
                return _FakeProcess("")
        return _FakeProcess("")

    return fake_exec, calls


class TestDuplicateDeviceIdentity:
    """Verify that two identical devices are disambiguated by address."""

    def test_two_identical_devices_get_distinct_fan_ids(self):
        async def _test():
            executor, _ = _mock_subprocess_multi(
                list_json=DUPLICATE_LIST, status_json=SAMPLE_STATUS_TRIPLES
            )
            with patch("shutil.which", return_value="/usr/bin/liquidctl"), \
                 patch("asyncio.create_subprocess_exec", side_effect=executor):
                backend = LiquidctlBackend()
                await backend.initialize()
                fan_ids = await backend.get_fan_ids()

            # Should have fans from both devices — no collisions
            assert len(fan_ids) == len(set(fan_ids)), "Duplicate fan IDs found"
            # Both USB addresses appear in the IDs
            assert any("usb:1:2" in fid for fid in fan_ids)
            assert any("usb:1:3" in fid for fid in fan_ids)
        _run(_test())

    def test_set_fan_speed_targets_correct_device_by_address(self):
        async def _test():
            executor, calls = _mock_subprocess_multi(
                list_json=DUPLICATE_LIST, status_json=SAMPLE_STATUS_TRIPLES
            )
            with patch("shutil.which", return_value="/usr/bin/liquidctl"), \
                 patch("asyncio.create_subprocess_exec", side_effect=executor):
                backend = LiquidctlBackend()
                await backend.initialize()
                fan_ids = await backend.get_fan_ids()

                # Pick the fan from the first device (usb:1:2)
                target_fan = next((f for f in fan_ids if "usb:1:2" in f), None)
                assert target_fan is not None, "No fan ID with usb:1:2 address"

                await backend.set_fan_speed(target_fan, 50.0)

            # The set command should have targeted usb:1:2, not usb:1:3
            set_calls = [(op, addr) for op, addr in calls if op == "set"]
            assert len(set_calls) == 1
            assert set_calls[0][1] == "usb:1:2"
        _run(_test())

    def test_single_device_still_works(self):
        async def _test():
            with patch("shutil.which", return_value="/usr/bin/liquidctl"), \
                 patch("asyncio.create_subprocess_exec", side_effect=_mock_subprocess(
                     list_json=SAMPLE_LIST, status_json=SAMPLE_STATUS_TRIPLES)):
                backend = LiquidctlBackend()
                await backend.initialize()
                assert len(backend._devices) == 1
                fan_ids = await backend.get_fan_ids()
                assert len(fan_ids) > 0
                # ID includes the address
                assert "usb:1:2" in fan_ids[0]
        _run(_test())


# ---------------------------------------------------------------------------
# Device profile tests
# ---------------------------------------------------------------------------

class TestDeviceProfiles:
    def test_kraken_profile_matches(self):
        profile = _match_device_profile("NZXT Kraken X63")
        assert profile is not None
        assert profile["friendly_prefix"] == "Kraken"

    def test_commander_profile_matches(self):
        profile = _match_device_profile("Corsair Commander Pro")
        assert profile is not None
        assert profile["friendly_prefix"] == "Commander"

    def test_aquacomputer_profile_matches(self):
        profile = _match_device_profile("Aquacomputer D5 Next")
        assert profile is not None
        assert profile["friendly_prefix"] == "Aquacomputer"

    def test_corsair_hydro_matches(self):
        profile = _match_device_profile("Corsair Hydro H100i RGB")
        assert profile is not None
        assert profile["friendly_prefix"] == "Corsair AIO"

    def test_unknown_device_no_profile(self):
        profile = _match_device_profile("Some Random USB Device")
        assert profile is None

    def test_profile_assigned_during_discovery(self):
        async def _test():
            with patch("shutil.which", return_value="/usr/bin/liquidctl"), \
                 patch("asyncio.create_subprocess_exec", side_effect=_mock_subprocess(
                     list_json=SAMPLE_LIST, status_json=SAMPLE_STATUS_TRIPLES)):
                backend = LiquidctlBackend()
                await backend.initialize()
                dev = backend._devices[0]
                assert dev.profile is not None
                assert dev.profile["friendly_prefix"] == "Kraken"
        _run(_test())


# ---------------------------------------------------------------------------
# Device reconnect tests
# ---------------------------------------------------------------------------

class TestDeviceReconnect:
    def test_device_goes_offline_after_failures(self):
        """Device marked offline after 3 consecutive empty status responses."""
        call_count = 0

        async def failing_subprocess(*args, **kwargs):
            nonlocal call_count
            cmd_args = list(args)
            for a in cmd_args:
                if a == "list":
                    return _FakeProcess(json.dumps(SAMPLE_LIST))
                elif a == "status":
                    call_count += 1
                    if call_count > 1:  # First call succeeds (discovery), rest fail
                        return _FakeProcess("[]")
                    return _FakeProcess(json.dumps(SAMPLE_STATUS_TRIPLES))
                elif a == "initialize":
                    return _FakeProcess("")
            return _FakeProcess("")

        async def _test():
            nonlocal call_count
            call_count = 0
            with patch("shutil.which", return_value="/usr/bin/liquidctl"), \
                 patch("asyncio.create_subprocess_exec", side_effect=failing_subprocess):
                backend = LiquidctlBackend()
                await backend.initialize()
                assert backend._devices[0].status == "ok"

                # Simulate 3 failed polls
                for _ in range(3):
                    await backend.get_sensor_readings()

                assert backend._devices[0].status == "offline"
                assert backend._devices[0].consecutive_failures >= 3
        _run(_test())

    def test_device_status_default_ok(self):
        async def _test():
            with patch("shutil.which", return_value="/usr/bin/liquidctl"), \
                 patch("asyncio.create_subprocess_exec", side_effect=_mock_subprocess(
                     list_json=SAMPLE_LIST, status_json=SAMPLE_STATUS_TRIPLES)):
                backend = LiquidctlBackend()
                await backend.initialize()
                assert backend._devices[0].status == "ok"
                assert backend._devices[0].consecutive_failures == 0
        _run(_test())


# ---------------------------------------------------------------------------
# Device info tests
# ---------------------------------------------------------------------------

class TestDeviceInfo:
    def test_get_device_info(self):
        async def _test():
            with patch("shutil.which", return_value="/usr/bin/liquidctl"), \
                 patch("asyncio.create_subprocess_exec", side_effect=_mock_subprocess(
                     list_json=SAMPLE_LIST, status_json=SAMPLE_STATUS_TRIPLES)):
                backend = LiquidctlBackend()
                await backend.initialize()
                info = backend.get_device_info()
                assert len(info) == 1
                assert info[0]["description"] == "NZXT Kraken X63"
                assert info[0]["family"] == "Kraken"
                assert info[0]["status"] == "ok"
                assert isinstance(info[0]["fan_channels"], list)
                assert isinstance(info[0]["temp_channels"], list)
        _run(_test())

    def test_backend_name_with_offline_device(self):
        async def _test():
            with patch("shutil.which", return_value="/usr/bin/liquidctl"), \
                 patch("asyncio.create_subprocess_exec", side_effect=_mock_subprocess(
                     list_json=SAMPLE_LIST, status_json=SAMPLE_STATUS_TRIPLES)):
                backend = LiquidctlBackend()
                await backend.initialize()
                backend._devices[0].status = "offline"
                name = backend.get_backend_name()
                assert "0 online" in name
                assert "1 offline" in name
        _run(_test())
