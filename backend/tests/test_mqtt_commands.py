"""Tests for MQTT command handler dispatch logic."""
import asyncio
import json

import pytest

from app.services.mqtt_command_handler import _dispatch_command, MAX_COMMANDS_PER_SECOND


# ---------------------------------------------------------------------------
# Fake collaborators
# ---------------------------------------------------------------------------

class FakeBackend:
    """Records set_fan_speed calls."""

    def __init__(self):
        self.calls: list[tuple[str, float]] = []

    async def set_fan_speed(self, fan_id: str, speed: float) -> bool:
        self.calls.append((fan_id, speed))
        return True


class FakeFanService:
    """Records release_fan_control and apply_profile calls."""

    def __init__(self):
        self.released = False
        self.applied_profiles: list = []

    async def release_fan_control(self):
        self.released = True

    async def apply_profile(self, profile):
        self.applied_profiles.append(profile)


class FakeProfile:
    def __init__(self, id: str, name: str = "Test"):
        self.id = id
        self.name = name


class FakeProfileRepo:
    """Mimics ProfileRepo.activate() and .get()."""

    def __init__(self, profiles: dict | None = None):
        self.profiles = profiles or {}
        self.activated: list[str] = []

    async def activate(self, profile_id: str) -> bool:
        if profile_id in self.profiles:
            self.activated.append(profile_id)
            return True
        return False

    async def get(self, profile_id: str):
        return self.profiles.get(profile_id)


def _run(coro):
    """Helper to run an async function synchronously."""
    return asyncio.run(coro)


def _dispatch(topic, payload, prefix, backend, fan_svc, repo):
    """Helper to run _dispatch_command synchronously."""
    return _run(_dispatch_command(topic, payload, prefix, backend, fan_svc, repo))


# ---------------------------------------------------------------------------
# Fan speed command tests
# ---------------------------------------------------------------------------

def test_fan_speed_valid():
    backend = FakeBackend()
    fan_svc = FakeFanService()
    repo = FakeProfileRepo()

    payload = json.dumps({"percent": 75}).encode()
    _dispatch("drivechill/commands/fans/fan_1/speed", payload, "drivechill",
              backend, fan_svc, repo)

    assert len(backend.calls) == 1
    assert backend.calls[0] == ("fan_1", 75)


def test_fan_speed_zero():
    backend = FakeBackend()
    fan_svc = FakeFanService()
    repo = FakeProfileRepo()

    payload = json.dumps({"percent": 0}).encode()
    _dispatch("drivechill/commands/fans/fan_1/speed", payload, "drivechill",
              backend, fan_svc, repo)

    assert backend.calls[0] == ("fan_1", 0)


def test_fan_speed_hundred():
    backend = FakeBackend()
    fan_svc = FakeFanService()
    repo = FakeProfileRepo()

    payload = json.dumps({"percent": 100}).encode()
    _dispatch("drivechill/commands/fans/fan_1/speed", payload, "drivechill",
              backend, fan_svc, repo)

    assert backend.calls[0] == ("fan_1", 100)


def test_fan_speed_float():
    backend = FakeBackend()
    fan_svc = FakeFanService()
    repo = FakeProfileRepo()

    payload = json.dumps({"percent": 42.5}).encode()
    _dispatch("drivechill/commands/fans/fan_1/speed", payload, "drivechill",
              backend, fan_svc, repo)

    assert backend.calls[0] == ("fan_1", 42.5)


def test_fan_speed_invalid_negative():
    """Negative percent should be rejected."""
    backend = FakeBackend()
    fan_svc = FakeFanService()
    repo = FakeProfileRepo()

    payload = json.dumps({"percent": -10}).encode()
    _dispatch("drivechill/commands/fans/fan_1/speed", payload, "drivechill",
              backend, fan_svc, repo)

    assert len(backend.calls) == 0


def test_fan_speed_invalid_over_100():
    """Percent > 100 should be rejected."""
    backend = FakeBackend()
    fan_svc = FakeFanService()
    repo = FakeProfileRepo()

    payload = json.dumps({"percent": 150}).encode()
    _dispatch("drivechill/commands/fans/fan_1/speed", payload, "drivechill",
              backend, fan_svc, repo)

    assert len(backend.calls) == 0


def test_fan_speed_missing_percent():
    """Missing percent field should be rejected."""
    backend = FakeBackend()
    fan_svc = FakeFanService()
    repo = FakeProfileRepo()

    payload = json.dumps({"speed": 50}).encode()
    _dispatch("drivechill/commands/fans/fan_1/speed", payload, "drivechill",
              backend, fan_svc, repo)

    assert len(backend.calls) == 0


def test_fan_speed_percent_string():
    """String percent should be rejected."""
    backend = FakeBackend()
    fan_svc = FakeFanService()
    repo = FakeProfileRepo()

    payload = json.dumps({"percent": "fifty"}).encode()
    _dispatch("drivechill/commands/fans/fan_1/speed", payload, "drivechill",
              backend, fan_svc, repo)

    assert len(backend.calls) == 0


# ---------------------------------------------------------------------------
# Profile activate command tests
# ---------------------------------------------------------------------------

def test_profile_activate_valid():
    backend = FakeBackend()
    fan_svc = FakeFanService()
    profile = FakeProfile("prof_1")
    repo = FakeProfileRepo({"prof_1": profile})

    payload = json.dumps({"profile_id": "prof_1"}).encode()
    _dispatch("drivechill/commands/profiles/activate", payload, "drivechill",
              backend, fan_svc, repo)

    assert "prof_1" in repo.activated
    assert len(fan_svc.applied_profiles) == 1


def test_profile_activate_not_found():
    backend = FakeBackend()
    fan_svc = FakeFanService()
    repo = FakeProfileRepo()

    payload = json.dumps({"profile_id": "nonexistent"}).encode()
    _dispatch("drivechill/commands/profiles/activate", payload, "drivechill",
              backend, fan_svc, repo)

    assert len(repo.activated) == 0
    assert len(fan_svc.applied_profiles) == 0


def test_profile_activate_missing_field():
    backend = FakeBackend()
    fan_svc = FakeFanService()
    repo = FakeProfileRepo()

    payload = json.dumps({"name": "silent"}).encode()
    _dispatch("drivechill/commands/profiles/activate", payload, "drivechill",
              backend, fan_svc, repo)

    assert len(repo.activated) == 0


def test_profile_activate_numeric_id():
    """Numeric profile_id should be rejected (must be string)."""
    backend = FakeBackend()
    fan_svc = FakeFanService()
    repo = FakeProfileRepo()

    payload = json.dumps({"profile_id": 123}).encode()
    _dispatch("drivechill/commands/profiles/activate", payload, "drivechill",
              backend, fan_svc, repo)

    assert len(repo.activated) == 0


# ---------------------------------------------------------------------------
# Fan release command tests
# ---------------------------------------------------------------------------

def test_fan_release_no_payload():
    backend = FakeBackend()
    fan_svc = FakeFanService()
    repo = FakeProfileRepo()

    _dispatch("drivechill/commands/fans/release", None, "drivechill",
              backend, fan_svc, repo)

    assert fan_svc.released is True


def test_fan_release_with_payload():
    backend = FakeBackend()
    fan_svc = FakeFanService()
    repo = FakeProfileRepo()

    payload = json.dumps({}).encode()
    _dispatch("drivechill/commands/fans/release", payload, "drivechill",
              backend, fan_svc, repo)

    assert fan_svc.released is True


# ---------------------------------------------------------------------------
# Malformed / edge cases
# ---------------------------------------------------------------------------

def test_malformed_json():
    """Malformed JSON should be logged and dropped, not raise."""
    backend = FakeBackend()
    fan_svc = FakeFanService()
    repo = FakeProfileRepo()

    _dispatch("drivechill/commands/fans/fan_1/speed", b"not-json", "drivechill",
              backend, fan_svc, repo)

    assert len(backend.calls) == 0


def test_empty_payload_non_release():
    """Empty payload on a non-release topic should be dropped."""
    backend = FakeBackend()
    fan_svc = FakeFanService()
    repo = FakeProfileRepo()

    _dispatch("drivechill/commands/fans/fan_1/speed", b"", "drivechill",
              backend, fan_svc, repo)

    assert len(backend.calls) == 0


def test_unknown_command_topic():
    """Unknown command sub-topic should be silently ignored."""
    backend = FakeBackend()
    fan_svc = FakeFanService()
    repo = FakeProfileRepo()

    payload = json.dumps({"key": "value"}).encode()
    _dispatch("drivechill/commands/unknown/action", payload, "drivechill",
              backend, fan_svc, repo)

    assert len(backend.calls) == 0
    assert not fan_svc.released


def test_wrong_prefix():
    """Topic that doesn't match prefix should be ignored."""
    backend = FakeBackend()
    fan_svc = FakeFanService()
    repo = FakeProfileRepo()

    payload = json.dumps({"percent": 50}).encode()
    _dispatch("other/commands/fans/fan_1/speed", payload, "drivechill",
              backend, fan_svc, repo)

    assert len(backend.calls) == 0


def test_custom_prefix():
    """Custom topic prefix should work."""
    backend = FakeBackend()
    fan_svc = FakeFanService()
    repo = FakeProfileRepo()

    payload = json.dumps({"percent": 60}).encode()
    _dispatch("mypc/commands/fans/fan_1/speed", payload, "mypc",
              backend, fan_svc, repo)

    assert len(backend.calls) == 1
    assert backend.calls[0] == ("fan_1", 60)


def test_rate_limit_constant():
    """Verify the rate limit constant is set correctly."""
    assert MAX_COMMANDS_PER_SECOND == 10
