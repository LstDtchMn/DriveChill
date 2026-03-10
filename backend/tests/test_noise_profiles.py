"""Tests for noise profile CRUD endpoints."""

from __future__ import annotations

import asyncio
import json
import sys
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock

import pytest

_backend_dir = Path(__file__).parent.parent
if str(_backend_dir) not in sys.path:
    sys.path.insert(0, str(_backend_dir))

from fastapi import HTTPException

from app.api.routes.noise_profiles import (
    NoiseDataPoint,
    NoiseProfileBody,
    _row_to_dict,
    create_noise_profile,
    delete_noise_profile,
    get_noise_profile,
    list_noise_profiles,
)


# ── Helpers ───────────────────────────────────────────────────────────────────

_SAMPLE_ROW = (
    "np_abc123",
    "fan_1",
    "quick",
    '[{"rpm": 500, "db": 25.0}, {"rpm": 1000, "db": 32.0}]',
    "2026-03-10T00:00:00+00:00",
    "2026-03-10T00:00:00+00:00",
)


def _mock_request(rows=None, rowcount=1):
    """Build a mock Request with a DB that returns the given rows."""
    req = MagicMock()
    db = AsyncMock()

    cursor = AsyncMock()
    cursor.fetchall = AsyncMock(return_value=rows or [])
    cursor.fetchone = AsyncMock(return_value=(rows[0] if rows else None))
    cursor.rowcount = rowcount

    db.execute = AsyncMock(return_value=cursor)
    db.commit = AsyncMock()
    req.app.state.db = db
    return req, db, cursor


# ── _row_to_dict ──────────────────────────────────────────────────────────────


class TestRowToDict:
    def test_parses_data_json(self):
        result = _row_to_dict(_SAMPLE_ROW)
        assert result["id"] == "np_abc123"
        assert result["fan_id"] == "fan_1"
        assert result["mode"] == "quick"
        assert len(result["data"]) == 2
        assert result["data"][0] == {"rpm": 500, "db": 25.0}

    def test_empty_data_json(self):
        row = ("np_x", "fan_2", "precise", "[]", "2026-01-01", "2026-01-01")
        result = _row_to_dict(row)
        assert result["data"] == []

    def test_null_data_json(self):
        row = ("np_x", "fan_2", "precise", None, "2026-01-01", "2026-01-01")
        result = _row_to_dict(row)
        assert result["data"] == []


# ── NoiseProfileBody validation ───────────────────────────────────────────────


class TestNoiseProfileBody:
    def test_valid_quick(self):
        body = NoiseProfileBody(
            fan_id="fan_1",
            mode="quick",
            data=[NoiseDataPoint(rpm=500, db=25.0)],
        )
        assert body.mode == "quick"

    def test_valid_precise(self):
        body = NoiseProfileBody(
            fan_id="fan_1",
            mode="precise",
            data=[NoiseDataPoint(rpm=500, db=25.0)],
        )
        assert body.mode == "precise"

    def test_invalid_mode_raises(self):
        with pytest.raises(Exception):
            NoiseProfileBody(
                fan_id="fan_1",
                mode="invalid",
                data=[NoiseDataPoint(rpm=500, db=25.0)],
            )

    def test_negative_rpm_raises(self):
        with pytest.raises(Exception):
            NoiseDataPoint(rpm=-1, db=25.0)

    def test_negative_db_raises(self):
        with pytest.raises(Exception):
            NoiseDataPoint(rpm=500, db=-1.0)


# ── list_noise_profiles ───────────────────────────────────────────────────────


class TestListNoiseProfiles:
    def test_returns_empty_list(self):
        req, db, cursor = _mock_request(rows=[])
        result = asyncio.run(list_noise_profiles(req))
        assert result == {"profiles": []}

    def test_returns_profiles(self):
        req, db, cursor = _mock_request(rows=[_SAMPLE_ROW])
        cursor.fetchall = AsyncMock(return_value=[_SAMPLE_ROW])
        result = asyncio.run(list_noise_profiles(req))
        assert len(result["profiles"]) == 1
        assert result["profiles"][0]["id"] == "np_abc123"


# ── get_noise_profile ─────────────────────────────────────────────────────────


class TestGetNoiseProfile:
    def test_returns_profile(self):
        req, db, cursor = _mock_request(rows=[_SAMPLE_ROW])
        cursor.fetchone = AsyncMock(return_value=_SAMPLE_ROW)
        result = asyncio.run(get_noise_profile("np_abc123", req))
        assert result["id"] == "np_abc123"
        assert result["mode"] == "quick"

    def test_404_when_not_found(self):
        req, db, cursor = _mock_request(rows=[])
        cursor.fetchone = AsyncMock(return_value=None)
        with pytest.raises(HTTPException) as exc:
            asyncio.run(get_noise_profile("np_missing", req))
        assert exc.value.status_code == 404


# ── create_noise_profile ──────────────────────────────────────────────────────


class TestCreateNoiseProfile:
    def test_creates_and_returns_profile(self):
        req, db, cursor = _mock_request(rows=[_SAMPLE_ROW])
        cursor.fetchone = AsyncMock(return_value=_SAMPLE_ROW)

        body = NoiseProfileBody(
            fan_id="fan_1",
            mode="quick",
            data=[
                NoiseDataPoint(rpm=500, db=25.0),
                NoiseDataPoint(rpm=1000, db=32.0),
            ],
        )
        result = asyncio.run(create_noise_profile(body, req))
        assert result["fan_id"] == "fan_1"
        assert result["mode"] == "quick"
        db.commit.assert_called_once()

    def test_data_serialised_as_json(self):
        req, db, cursor = _mock_request(rows=[_SAMPLE_ROW])
        cursor.fetchone = AsyncMock(return_value=_SAMPLE_ROW)

        body = NoiseProfileBody(
            fan_id="fan_2",
            mode="precise",
            data=[NoiseDataPoint(rpm=750, db=28.5)],
        )
        asyncio.run(create_noise_profile(body, req))
        # Verify INSERT was called with JSON-encoded data
        call_args = db.execute.call_args_list[0]
        insert_sql = call_args[0][0]
        params = call_args[0][1]
        assert "INSERT INTO noise_profiles" in insert_sql
        data = json.loads(params[3])
        assert data[0]["rpm"] == 750
        assert data[0]["db"] == 28.5


# ── delete_noise_profile ──────────────────────────────────────────────────────


class TestDeleteNoiseProfile:
    def test_deletes_successfully(self):
        req, db, cursor = _mock_request()
        cursor.rowcount = 1
        result = asyncio.run(delete_noise_profile("np_abc123", req))
        assert result == {"success": True}
        db.commit.assert_called_once()

    def test_404_when_not_found(self):
        req, db, cursor = _mock_request()
        cursor.rowcount = 0
        with pytest.raises(HTTPException) as exc:
            asyncio.run(delete_noise_profile("np_missing", req))
        assert exc.value.status_code == 404
