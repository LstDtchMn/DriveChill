"""Update check and one-click update endpoints."""

from __future__ import annotations

import asyncio
import logging
import os
import re
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

import httpx
from fastapi import APIRouter, Depends, HTTPException, Request

from app.api.dependencies.auth import require_csrf
from app.config import settings

router = APIRouter(prefix="/api/update", tags=["update"])
logger = logging.getLogger(__name__)

_GITHUB_REPO  = "LstDtchMn/DriveChill"
_CACHE_TTL    = timedelta(hours=1)

# Strict semver guard — prevents command injection when version is interpolated
# into PowerShell arguments launched with UAC elevation.
_SEMVER_RE = re.compile(r'^\d+\.\d+\.\d+(?:[-+][a-zA-Z0-9._-]+)?$')

# Simple in-memory cache: shared across requests; safe for single-process FastAPI
_cache_lock   = asyncio.Lock()
_cached_at:  datetime | None = None
_cached_data: dict | None    = None

# Cached deployment type — never changes at runtime; evaluated once.
_deployment_type_cache: str | None = None


async def _deployment_type() -> str:
    global _deployment_type_cache
    if _deployment_type_cache is not None:
        return _deployment_type_cache

    if os.path.exists("/.dockerenv"):
        _deployment_type_cache = "docker"
        return _deployment_type_cache

    if sys.platform == "win32":
        try:
            proc = await asyncio.create_subprocess_exec(
                "sc.exe", "query", "DriveChill",
                stdout=asyncio.subprocess.DEVNULL,
                stderr=asyncio.subprocess.DEVNULL,
            )
            await asyncio.wait_for(proc.communicate(), timeout=5.0)
            _deployment_type_cache = (
                "windows_service" if proc.returncode == 0 else "windows_standalone"
            )
        except Exception:
            _deployment_type_cache = "windows_standalone"
        return _deployment_type_cache

    _deployment_type_cache = "other"
    return _deployment_type_cache


async def _fetch_latest() -> dict:
    """Query GitHub releases API and return normalised check result."""
    global _cached_at, _cached_data

    async with _cache_lock:
        now = datetime.now(timezone.utc)
        if _cached_at and _cached_data and (now - _cached_at) < _CACHE_TTL:
            return _cached_data

        url = f"https://api.github.com/repos/{_GITHUB_REPO}/releases/latest"
        try:
            async with httpx.AsyncClient(timeout=10.0) as client:
                resp = await client.get(
                    url, headers={"User-Agent": f"DriveChill/{settings.app_version}"}
                )
                resp.raise_for_status()
                data = resp.json()
        except httpx.HTTPError as exc:
            raise HTTPException(
                status_code=503,
                detail=f"Could not reach GitHub releases API: {exc}",
            )

        latest_tag = data.get("tag_name", "").lstrip("v")
        release_url = data.get("html_url", "")

        def _ver_tuple(v: str):
            try:
                parts = (v.split("-")[0].split(".") + ["0", "0", "0"])[:3]
                return tuple(int(x) for x in parts)
            except ValueError:
                return (0, 0, 0)

        update_available = _ver_tuple(latest_tag) > _ver_tuple(settings.app_version)

        _cached_data = {
            "current":          settings.app_version,
            "latest":           latest_tag,
            "update_available": update_available,
            "release_url":      release_url,
            "deployment":       await _deployment_type(),
        }
        _cached_at = now
        return _cached_data


@router.get("/check")
async def check_update():
    """Return current vs latest version from GitHub Releases (cached 1 h)."""
    return await _fetch_latest()


@router.post("/apply", dependencies=[Depends(require_csrf)])
async def apply_update(request: Request):
    """
    Trigger an in-place update.

    - Windows: launches update_windows.ps1 via UAC (ShellExecute runas).
      The service will stop, files will be replaced, and the service restarted.
      Expect the connection to drop within a few seconds.
    - Docker / Linux: returns instructions — the container manages its own lifecycle.
    """
    info    = await _fetch_latest()
    version = info["latest"]
    deploy  = info["deployment"]

    if deploy == "docker":
        return {
            "status":  "manual_required",
            "message": "Docker containers update via image pull.",
            "command": "docker compose pull && docker compose up -d",
        }

    if sys.platform != "win32":
        return {
            "status":  "manual_required",
            "message": "Automated update is only supported on Windows.",
        }

    # Guard against command injection: version must be strict semver.
    if not _SEMVER_RE.fullmatch(version):
        logger.error("GitHub returned unexpected version string: %r", version)
        raise HTTPException(status_code=500, detail="Unexpected version string from GitHub.")

    # Locate update_windows.ps1 relative to this backend
    scripts_dir = Path(__file__).resolve().parents[4] / "scripts"
    ps_script   = scripts_dir / "update_windows.ps1"
    if not ps_script.exists():
        logger.error("Update script not found at %s", ps_script)
        raise HTTPException(status_code=500, detail="Update script not found. Check server logs.")

    # Launch PowerShell with UAC elevation (ShellExecute runas).
    # Version is validated as semver above and double-quoted in the argument string.
    import ctypes
    args = f'-NoProfile -ExecutionPolicy Bypass -File "{ps_script}" -Version "{version}" -Artifact python'
    result = ctypes.windll.shell32.ShellExecuteW(
        None, "runas", "powershell.exe", args, None, 1
    )
    if result <= 32:
        raise HTTPException(
            status_code=500,
            detail=f"ShellExecute failed with code {result}. UAC may have been denied.",
        )

    logger.info("Update to v%s triggered via update_windows.ps1", version)
    return {
        "status":  "update_started",
        "version": version,
        "message": "Update is running. The service will restart automatically. "
                   "Reconnect to the dashboard in ~30 seconds.",
    }
