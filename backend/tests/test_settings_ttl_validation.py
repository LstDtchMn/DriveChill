from __future__ import annotations

import pytest
from pydantic import ValidationError

from app.api.routes.settings import UpdateSettingsRequest
from app.config import Settings


def test_history_retention_valid_bounds() -> None:
    body = UpdateSettingsRequest(history_retention_hours=24)
    assert body.history_retention_hours == 24


@pytest.mark.parametrize("value", [0, -1, 8761])
def test_history_retention_rejects_out_of_bounds(value: int) -> None:
    with pytest.raises(ValidationError):
        UpdateSettingsRequest(history_retention_hours=value)


@pytest.mark.parametrize(
    ("raw_ttl", "expected_seconds"),
    [
        ("15m", 900),
        ("8h", 28800),
        ("7d", 604800),
        ("120", 120),
        ("0h", 28800),
        ("-1d", 28800),
        ("0", 28800),
        ("-30", 28800),
        ("abc", 28800),
    ],
)
def test_session_ttl_seconds_parsing(raw_ttl: str, expected_seconds: int) -> None:
    s = Settings(session_ttl=raw_ttl)
    assert s.session_ttl_seconds == expected_seconds
