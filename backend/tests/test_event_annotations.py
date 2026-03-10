"""Tests for event annotation CRUD endpoints."""

from __future__ import annotations

import asyncio
import sys
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock

import pytest

_backend_dir = Path(__file__).parent.parent
if str(_backend_dir) not in sys.path:
    sys.path.insert(0, str(_backend_dir))

from fastapi import HTTPException

from app.api.routes.event_annotations import (
    AnnotationBody,
    _row_to_dict,
    create_annotation,
    delete_annotation,
    list_annotations,
)


# ── Helpers ───────────────────────────────────────────────────────────────────

_SAMPLE_ROW = (
    "ann_abc123def456",
    "annotation",
    "2026-03-10T12:00:00+00:00",
    "Repasted CPU",
    "Applied Noctua NT-H2",
    None,
    "2026-03-10T12:01:00+00:00",
)

_NON_ANNOTATION_ROW = (
    "evt_xyz789",
    "system_event",
    "2026-03-10T10:00:00+00:00",
    "System reboot",
    None,
    None,
    "2026-03-10T10:00:00+00:00",
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
    def test_maps_fields_correctly(self):
        result = _row_to_dict(_SAMPLE_ROW)
        assert result["id"] == "ann_abc123def456"
        assert result["timestamp_utc"] == "2026-03-10T12:00:00+00:00"
        assert result["label"] == "Repasted CPU"
        assert result["description"] == "Applied Noctua NT-H2"
        assert result["created_at"] == "2026-03-10T12:01:00+00:00"

    def test_null_description(self):
        row = ("ann_x", "annotation", "2026-01-01T00:00:00Z", "Test", None, None, "2026-01-01T00:00:00Z")
        result = _row_to_dict(row)
        assert result["description"] is None


# ── AnnotationBody validation ─────────────────────────────────────────────────


class TestAnnotationBody:
    def test_valid_full(self):
        body = AnnotationBody(
            timestamp_utc="2026-03-10T12:00:00Z",
            label="New cooler",
            description="Installed Noctua NH-D15",
        )
        assert body.label == "New cooler"
        assert body.description == "Installed Noctua NH-D15"

    def test_valid_without_description(self):
        body = AnnotationBody(
            timestamp_utc="2026-03-10T12:00:00Z",
            label="Repasted",
        )
        assert body.description is None

    def test_empty_label_raises(self):
        with pytest.raises(Exception):
            AnnotationBody(timestamp_utc="2026-03-10T12:00:00Z", label="")

    def test_empty_timestamp_raises(self):
        with pytest.raises(Exception):
            AnnotationBody(timestamp_utc="", label="Test")


# ── list_annotations ──────────────────────────────────────────────────────────


class TestListAnnotations:
    def test_returns_empty_list(self):
        req, db, cursor = _mock_request(rows=[])
        result = asyncio.run(list_annotations(req))
        assert result == []

    def test_returns_annotations(self):
        req, db, cursor = _mock_request(rows=[_SAMPLE_ROW])
        cursor.fetchall = AsyncMock(return_value=[_SAMPLE_ROW])
        result = asyncio.run(list_annotations(req))
        assert len(result) == 1
        assert result[0]["id"] == "ann_abc123def456"
        assert result[0]["label"] == "Repasted CPU"

    def test_filters_by_start(self):
        req, db, cursor = _mock_request(rows=[_SAMPLE_ROW])
        cursor.fetchall = AsyncMock(return_value=[_SAMPLE_ROW])
        asyncio.run(list_annotations(req, start="2026-03-10T00:00:00Z"))
        call_args = db.execute.call_args
        sql = call_args[0][0]
        params = call_args[0][1]
        assert "timestamp_utc >= ?" in sql
        assert "2026-03-10T00:00:00Z" in params

    def test_filters_by_start_and_end(self):
        req, db, cursor = _mock_request(rows=[])
        asyncio.run(list_annotations(req, start="2026-03-01T00:00:00Z", end="2026-03-10T23:59:59Z"))
        call_args = db.execute.call_args
        sql = call_args[0][0]
        params = call_args[0][1]
        assert "timestamp_utc >= ?" in sql
        assert "timestamp_utc <= ?" in sql
        assert len(params) == 2


# ── create_annotation ─────────────────────────────────────────────────────────


class TestCreateAnnotation:
    def test_creates_and_returns_annotation(self):
        req, db, cursor = _mock_request(rows=[_SAMPLE_ROW])
        cursor.fetchone = AsyncMock(return_value=_SAMPLE_ROW)

        body = AnnotationBody(
            timestamp_utc="2026-03-10T12:00:00+00:00",
            label="Repasted CPU",
            description="Applied Noctua NT-H2",
        )
        result = asyncio.run(create_annotation(body, req))
        assert result["label"] == "Repasted CPU"
        assert result["description"] == "Applied Noctua NT-H2"
        db.commit.assert_called_once()

    def test_insert_uses_annotation_event_type(self):
        req, db, cursor = _mock_request(rows=[_SAMPLE_ROW])
        cursor.fetchone = AsyncMock(return_value=_SAMPLE_ROW)

        body = AnnotationBody(
            timestamp_utc="2026-03-10T12:00:00Z",
            label="Test",
        )
        asyncio.run(create_annotation(body, req))
        insert_call = db.execute.call_args_list[0]
        sql = insert_call[0][0]
        assert "'annotation'" in sql


# ── delete_annotation ─────────────────────────────────────────────────────────


class TestDeleteAnnotation:
    def test_deletes_successfully(self):
        req, db, cursor = _mock_request()
        cursor.rowcount = 1
        result = asyncio.run(delete_annotation("ann_abc123def456", req))
        assert result is None
        db.commit.assert_called_once()

    def test_only_deletes_annotation_type(self):
        req, db, cursor = _mock_request()
        cursor.rowcount = 1
        asyncio.run(delete_annotation("ann_abc123def456", req))
        call_args = db.execute.call_args
        sql = call_args[0][0]
        assert "event_type = 'annotation'" in sql

    def test_404_when_not_found(self):
        req, db, cursor = _mock_request()
        cursor.rowcount = 0
        with pytest.raises(HTTPException) as exc:
            asyncio.run(delete_annotation("ann_missing", req))
        assert exc.value.status_code == 404

    def test_404_for_non_annotation_event(self):
        """Delete should fail for events that aren't annotations."""
        req, db, cursor = _mock_request()
        cursor.rowcount = 0  # simulates WHERE event_type='annotation' not matching
        with pytest.raises(HTTPException) as exc:
            asyncio.run(delete_annotation("evt_xyz789", req))
        assert exc.value.status_code == 404
