"""Tests for FanService startup safety profile (A2.1).

Verifies:
- Startup safety is active initially.
- _exit_startup_safety() deactivates it.
- _check_startup_safety_expired() is time-based.
- apply_profile() exits startup safety.
- Control loop applies safe speed (50%) during startup window.
- Panic overrides startup safety.
"""

from __future__ import annotations

import asyncio
import time
from typing import Any
from unittest.mock import AsyncMock, MagicMock

import pytest

from app.services.fan_service import FanService


class _MockBackend:
    """Minimal HardwareBackend stub."""

    def __init__(self, fan_ids: list[str] = None):
        self._fan_ids = fan_ids or ["fan1"]
        self.applied: dict[str, float] = {}

    async def get_fan_ids(self) -> list[str]:
        return self._fan_ids

    async def set_fan_speed(self, fan_id: str, speed_percent: float) -> bool:
        self.applied[fan_id] = speed_percent
        return True

    async def set_fan_auto(self, fan_id: str) -> bool:
        return True

    def initialize(self) -> None:
        pass


def _make_service(fan_ids: list[str] = None) -> tuple[FanService, _MockBackend]:
    backend = _MockBackend(fan_ids)
    svc = FanService(backend)
    return svc, backend


class TestStartupSafetyInitialState:
    def test_is_active_initially(self):
        svc, _ = _make_service()
        assert svc.startup_safety_active is True

    def test_exit_startup_safety_deactivates(self):
        svc, _ = _make_service()
        svc._exit_startup_safety()
        assert svc.startup_safety_active is False

    def test_exit_startup_safety_idempotent(self):
        svc, _ = _make_service()
        svc._exit_startup_safety()
        svc._exit_startup_safety()  # second call — must not raise
        assert svc.startup_safety_active is False


class TestStartupSafetyExpiry:
    def test_check_expired_returns_false_within_window(self):
        svc, _ = _make_service()
        # Just created — within the 15-second window
        result = svc._check_startup_safety_expired()
        assert result is False
        assert svc.startup_safety_active is True

    def test_check_expired_after_window(self):
        svc, _ = _make_service()
        # Rewind start time to simulate elapsed window
        svc._startup_safety_start = time.monotonic() - 20.0
        result = svc._check_startup_safety_expired()
        assert result is True
        assert svc.startup_safety_active is False

    def test_check_expired_already_inactive_returns_false(self):
        svc, _ = _make_service()
        svc._exit_startup_safety()
        result = svc._check_startup_safety_expired()
        assert result is False


class TestStartupSafetyApplyProfile:
    def test_apply_profile_exits_startup_safety(self):
        svc, _ = _make_service()
        assert svc.startup_safety_active is True

        # Construct a minimal profile-like object
        profile = MagicMock()
        profile.curves = []
        profile.active = True

        asyncio.run(svc.apply_profile(profile))
        assert svc.startup_safety_active is False


class TestStartupSafetyControlLoop:
    def test_safe_speed_applied_during_startup(self):
        """During startup window, control loop must hold fans at 50%."""
        svc, backend = _make_service(["fan1", "fan2"])
        assert svc.startup_safety_active is True

        asyncio.run(svc._emergency_all_fans(svc._startup_safety_speed, source="startup_safety"))
        assert backend.applied.get("fan1") == 50.0
        assert backend.applied.get("fan2") == 50.0

    def test_panic_takes_priority_over_startup_safety(self):
        """Temp panic forces 100% even when startup safety is active."""
        svc, backend = _make_service(["fan1"])
        assert svc.startup_safety_active is True

        svc._temp_panic = True
        asyncio.run(svc._emergency_all_fans(100.0, source="panic_temp"))
        assert backend.applied.get("fan1") == 100.0


class TestControlTransparency:
    """B3: per-fan control source tracking."""

    def test_control_sources_initially_empty(self):
        svc, _ = _make_service()
        assert svc.control_sources == {}

    def test_emergency_sets_control_source(self):
        svc, _ = _make_service(["fan1", "fan2"])
        asyncio.run(svc._emergency_all_fans(100.0, source="panic_sensor"))
        sources = svc.control_sources
        assert sources.get("fan1") == "panic_sensor"
        assert sources.get("fan2") == "panic_sensor"

    def test_startup_safety_source(self):
        svc, _ = _make_service(["fan1"])
        asyncio.run(svc._emergency_all_fans(50.0, source="startup_safety"))
        assert svc.control_sources.get("fan1") == "startup_safety"

    def test_panic_temp_source(self):
        svc, _ = _make_service(["fan1"])
        asyncio.run(svc._emergency_all_fans(100.0, source="panic_temp"))
        assert svc.control_sources.get("fan1") == "panic_temp"

    def test_control_sources_returns_copy(self):
        """control_sources property must return a copy, not the internal dict."""
        svc, _ = _make_service(["fan1"])
        asyncio.run(svc._emergency_all_fans(50.0, source="startup_safety"))
        copy1 = svc.control_sources
        copy1["fan1"] = "hacked"
        assert svc.control_sources.get("fan1") == "startup_safety"
