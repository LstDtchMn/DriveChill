"""Tests for Linux hwmon fan-write backend (Phase 10).

Uses a temporary directory to simulate /sys/class/hwmon structure.
"""
from __future__ import annotations

import asyncio
import os
from pathlib import Path

from app.hardware.hwmon_backend import HwmonBackend, HwmonFanNode


def _run(coro):
    return asyncio.run(coro)


def _make_hwmon_tree(tmp_path: Path, chip_name: str = "nct6775",
                     num_fans: int = 2, writable: bool = True) -> Path:
    """Create a mock /sys/class/hwmon/hwmon0 directory with pwm nodes."""
    hwmon_root = tmp_path / "hwmon"
    hwmon_dir = hwmon_root / "hwmon0"
    hwmon_dir.mkdir(parents=True)

    # Write chip name
    (hwmon_dir / "name").write_text(chip_name)

    for i in range(1, num_fans + 1):
        pwm_path = hwmon_dir / f"pwm{i}"
        enable_path = hwmon_dir / f"pwm{i}_enable"

        pwm_path.write_text("128")  # ~50% duty
        enable_path.write_text("2")  # auto mode

        if not writable:
            pwm_path.chmod(0o444)

    return hwmon_root


class TestDiscovery:
    def test_discovers_writable_fans(self, tmp_path):
        hwmon_root = _make_hwmon_tree(tmp_path, num_fans=3)

        async def _test():
            backend = HwmonBackend(hwmon_root=hwmon_root)
            await backend.initialize()
            assert backend.fan_write_supported is True
            fan_ids = await backend.get_fan_ids()
            assert len(fan_ids) >= 3
            assert "hwmon_nct6775_pwm1" in fan_ids
            assert "hwmon_nct6775_pwm2" in fan_ids
            assert "hwmon_nct6775_pwm3" in fan_ids
        _run(_test())

    def test_no_hwmon_root(self, tmp_path):
        missing_root = tmp_path / "nonexistent"

        async def _test():
            backend = HwmonBackend(hwmon_root=missing_root)
            await backend.initialize()
            assert backend.fan_write_supported is False
            fan_ids = await backend.get_fan_ids()
            # Should still return lm-sensors fans (may be empty in test env)
            assert isinstance(fan_ids, list)
        _run(_test())

    def test_no_pwm_nodes(self, tmp_path):
        hwmon_root = tmp_path / "hwmon"
        hwmon_dir = hwmon_root / "hwmon0"
        hwmon_dir.mkdir(parents=True)
        (hwmon_dir / "name").write_text("some_chip")
        # No pwm files at all

        async def _test():
            backend = HwmonBackend(hwmon_root=hwmon_root)
            await backend.initialize()
            assert backend.fan_write_supported is False
        _run(_test())

    def test_read_only_pwm_not_discovered(self, tmp_path):
        hwmon_root = _make_hwmon_tree(tmp_path, num_fans=1, writable=False)

        async def _test():
            backend = HwmonBackend(hwmon_root=hwmon_root)
            await backend.initialize()
            assert backend.fan_write_supported is False
        _run(_test())

    def test_multiple_chips(self, tmp_path):
        hwmon_root = tmp_path / "hwmon"

        # Chip 1
        dir1 = hwmon_root / "hwmon0"
        dir1.mkdir(parents=True)
        (dir1 / "name").write_text("chip_a")
        (dir1 / "pwm1").write_text("100")
        (dir1 / "pwm1_enable").write_text("2")

        # Chip 2
        dir2 = hwmon_root / "hwmon1"
        dir2.mkdir(parents=True)
        (dir2 / "name").write_text("chip_b")
        (dir2 / "pwm1").write_text("200")
        (dir2 / "pwm1_enable").write_text("2")

        async def _test():
            backend = HwmonBackend(hwmon_root=hwmon_root)
            await backend.initialize()
            assert backend.fan_write_supported is True
            fan_ids = await backend.get_fan_ids()
            assert "hwmon_chip_a_pwm1" in fan_ids
            assert "hwmon_chip_b_pwm1" in fan_ids
        _run(_test())


class TestSetFanSpeed:
    def test_set_speed_writes_pwm(self, tmp_path):
        hwmon_root = _make_hwmon_tree(tmp_path, num_fans=1)

        async def _test():
            backend = HwmonBackend(hwmon_root=hwmon_root)
            await backend.initialize()

            ok = await backend.set_fan_speed("hwmon_nct6775_pwm1", 75.0)
            assert ok is True

            # Verify pwm value written (75% of 255 = 191)
            pwm_val = (hwmon_root / "hwmon0" / "pwm1").read_text().strip()
            assert pwm_val == "191"

            # Verify enable set to manual (1)
            enable_val = (hwmon_root / "hwmon0" / "pwm1_enable").read_text().strip()
            assert enable_val == "1"
        _run(_test())

    def test_set_speed_clamps_to_range(self, tmp_path):
        hwmon_root = _make_hwmon_tree(tmp_path, num_fans=1)

        async def _test():
            backend = HwmonBackend(hwmon_root=hwmon_root)
            await backend.initialize()

            # 0% -> pwm 0
            await backend.set_fan_speed("hwmon_nct6775_pwm1", 0.0)
            assert (hwmon_root / "hwmon0" / "pwm1").read_text().strip() == "0"

            # 100% -> pwm 255
            await backend.set_fan_speed("hwmon_nct6775_pwm1", 100.0)
            assert (hwmon_root / "hwmon0" / "pwm1").read_text().strip() == "255"

            # >100 clamped to 100
            await backend.set_fan_speed("hwmon_nct6775_pwm1", 150.0)
            assert (hwmon_root / "hwmon0" / "pwm1").read_text().strip() == "255"

            # <0 clamped to 0
            await backend.set_fan_speed("hwmon_nct6775_pwm1", -10.0)
            assert (hwmon_root / "hwmon0" / "pwm1").read_text().strip() == "0"
        _run(_test())

    def test_set_speed_unknown_fan(self, tmp_path):
        hwmon_root = _make_hwmon_tree(tmp_path, num_fans=1)

        async def _test():
            backend = HwmonBackend(hwmon_root=hwmon_root)
            await backend.initialize()
            ok = await backend.set_fan_speed("nonexistent_fan", 50.0)
            assert ok is False
        _run(_test())


class TestReleaseControl:
    def test_release_restores_original_enable(self, tmp_path):
        hwmon_root = _make_hwmon_tree(tmp_path, num_fans=1)

        async def _test():
            backend = HwmonBackend(hwmon_root=hwmon_root)
            await backend.initialize()

            # Set to manual
            await backend.set_fan_speed("hwmon_nct6775_pwm1", 50.0)
            assert (hwmon_root / "hwmon0" / "pwm1_enable").read_text().strip() == "1"

            # Release should restore to original value (2 = auto)
            await backend.release_fan_control()
            assert (hwmon_root / "hwmon0" / "pwm1_enable").read_text().strip() == "2"
        _run(_test())

    def test_release_sync(self, tmp_path):
        hwmon_root = _make_hwmon_tree(tmp_path, num_fans=1)

        async def _test():
            backend = HwmonBackend(hwmon_root=hwmon_root)
            await backend.initialize()
            await backend.set_fan_speed("hwmon_nct6775_pwm1", 50.0)
            # Sync release (used in atexit)
            backend.release_fan_control_sync()
            assert (hwmon_root / "hwmon0" / "pwm1_enable").read_text().strip() == "2"
        _run(_test())


class TestBackendName:
    def test_name_with_fans(self, tmp_path):
        hwmon_root = _make_hwmon_tree(tmp_path, num_fans=2)

        async def _test():
            backend = HwmonBackend(hwmon_root=hwmon_root)
            await backend.initialize()
            name = backend.get_backend_name()
            assert "hwmon" in name
            assert "2 writable fans" in name
        _run(_test())

    def test_name_without_fans(self, tmp_path):
        missing_root = tmp_path / "nonexistent"

        async def _test():
            backend = HwmonBackend(hwmon_root=missing_root)
            await backend.initialize()
            name = backend.get_backend_name()
            assert "read-only" in name
        _run(_test())
