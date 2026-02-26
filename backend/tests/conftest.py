"""Shared test fixtures for DriveChill backend tests."""

from __future__ import annotations

import sys
from pathlib import Path

import pytest

# Ensure 'app.*' imports resolve from the backend/ directory.
_backend_dir = Path(__file__).parent.parent
if str(_backend_dir) not in sys.path:
    sys.path.insert(0, str(_backend_dir))


@pytest.fixture
def tmp_db(tmp_path: Path) -> Path:
    """Return a path for a temporary SQLite database file."""
    return tmp_path / "test.db"
