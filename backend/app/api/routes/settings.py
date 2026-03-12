from __future__ import annotations

import json
import logging
from datetime import datetime, timezone

from fastapi import APIRouter, Depends, HTTPException, Request

from app.api.dependencies.auth import require_admin, require_auth, require_csrf
from pydantic import BaseModel, field_validator

from app.config import settings as app_config

logger = logging.getLogger(__name__)

router = APIRouter(prefix="/api/settings", tags=["settings"])


class SettingsResponse(BaseModel):
    sensor_poll_interval: float
    history_retention_hours: int
    temp_unit: str
    hardware_backend: str
    backend_name: str
    fan_ramp_rate_pct_per_sec: float


class UpdateSettingsRequest(BaseModel):
    sensor_poll_interval: float | None = None
    history_retention_hours: int | None = None
    temp_unit: str | None = None
    fan_ramp_rate_pct_per_sec: float | None = None

    @field_validator("sensor_poll_interval")
    @classmethod
    def validate_poll_interval(cls, v: float | None) -> float | None:
        # H-6: 0 or negative would create a busy-loop in the sensor service
        if v is not None and v < 0.5:
            raise ValueError("sensor_poll_interval must be at least 0.5 seconds")
        return v

    @field_validator("temp_unit")
    @classmethod
    def validate_temp_unit(cls, v: str | None) -> str | None:
        # H-6: only "C" and "F" are valid - reject anything else silently stored
        if v is not None and v not in ("C", "F"):
            raise ValueError("temp_unit must be 'C' or 'F'")
        return v

    @field_validator("history_retention_hours")
    @classmethod
    def validate_history_retention(cls, v: int | None) -> int | None:
        # Keep retention bounded to the same one-year cap used by history/export APIs.
        if v is not None and not (1 <= v <= 8760):
            raise ValueError("history_retention_hours must be between 1 and 8760")
        return v


@router.get("")
async def get_settings(request: Request):
    """Get current application settings."""
    repo = request.app.state.settings_repo
    return SettingsResponse(
        sensor_poll_interval=await repo.get_float("sensor_poll_interval", 1.0),
        history_retention_hours=await repo.get_int("history_retention_hours", 720),
        temp_unit=(await repo.get("temp_unit")) or "C",
        hardware_backend=app_config.hardware_backend,
        backend_name=request.app.state.backend.get_backend_name(),
        fan_ramp_rate_pct_per_sec=await repo.get_float("fan_ramp_rate_pct_per_sec", 0.0),
    ).model_dump()


@router.put("", dependencies=[Depends(require_csrf)])
async def update_settings(body: UpdateSettingsRequest, request: Request):
    """Update application settings (persisted to SQLite)."""
    repo = request.app.state.settings_repo
    updates: dict[str, str] = {}

    if body.sensor_poll_interval is not None:
        updates["sensor_poll_interval"] = str(body.sensor_poll_interval)
    if body.history_retention_hours is not None:
        updates["history_retention_hours"] = str(body.history_retention_hours)
    if body.temp_unit is not None:
        updates["temp_unit"] = body.temp_unit
    if body.fan_ramp_rate_pct_per_sec is not None:
        updates["fan_ramp_rate_pct_per_sec"] = str(body.fan_ramp_rate_pct_per_sec)

    if updates:
        await repo.set_many(updates)

    # Apply runtime-adjustable settings to running services so changes
    # take effect immediately (no restart required).
    if body.sensor_poll_interval is not None:
        sensor_svc = request.app.state.sensor_service
        sensor_svc.poll_interval = body.sensor_poll_interval

    if body.fan_ramp_rate_pct_per_sec is not None:
        fan_svc = request.app.state.fan_service
        fan_svc.configure_ramp_rate(body.fan_ramp_rate_pct_per_sec)

    return {"success": True, "settings": {
        "sensor_poll_interval": await repo.get_float("sensor_poll_interval", 1.0),
        "history_retention_hours": await repo.get_int("history_retention_hours", 720),
        "temp_unit": (await repo.get("temp_unit")) or "C",
        "fan_ramp_rate_pct_per_sec": await repo.get_float("fan_ramp_rate_pct_per_sec", 0.0),
    }}


# ---------------------------------------------------------------------------
# Config export / import
# ---------------------------------------------------------------------------

@router.get("/export", dependencies=[Depends(require_auth), Depends(require_admin)])
async def export_config(request: Request):
    """Export the full application configuration as a portable JSON object."""
    db = request.app.state.db

    # Profiles (with curves)
    profile_repo = request.app.state.profile_repo
    profiles_raw = await profile_repo.list_all()
    profiles = [
        {
            "id": p.id,
            "name": p.name,
            "preset": p.preset.value,
            "is_active": p.is_active,
            "curves": [c.model_dump() for c in p.curves],
        }
        for p in profiles_raw
    ]

    # Alert rules
    alert_service = request.app.state.alert_service
    alert_rules = [r.model_dump() for r in alert_service.rules]

    # Temperature targets
    temp_target_svc = request.app.state.temperature_target_service
    temperature_targets = [t.model_dump() for t in temp_target_svc.targets]

    # Quiet hours
    cursor = await db.execute(
        "SELECT id, day_of_week, start_time, end_time, profile_id, enabled "
        "FROM quiet_hours ORDER BY day_of_week, start_time"
    )
    rows = await cursor.fetchall()
    quiet_hours = [
        {
            "id": r[0], "day_of_week": r[1], "start_time": r[2],
            "end_time": r[3], "profile_id": r[4], "enabled": bool(r[5]),
        }
        for r in rows
    ]

    # Webhook config
    webhook_svc = request.app.state.webhook_service
    webhook_config = await webhook_svc.get_config()

    # Sensor labels
    cursor = await db.execute("SELECT sensor_id, label FROM sensor_labels")
    rows = await cursor.fetchall()
    sensor_labels = {r[0]: r[1] for r in rows}

    # Settings
    settings_repo = request.app.state.settings_repo
    settings_data = {
        "sensor_poll_interval": await settings_repo.get_float("sensor_poll_interval", 1.0),
        "history_retention_hours": await settings_repo.get_int("history_retention_hours", 720),
        "temp_unit": (await settings_repo.get("temp_unit")) or "C",
        "fan_ramp_rate_pct_per_sec": await settings_repo.get_float("fan_ramp_rate_pct_per_sec", 0.0),
    }

    return {
        "export_version": 1,
        "exported_at": datetime.now(timezone.utc).isoformat(),
        "profiles": profiles,
        "alert_rules": alert_rules,
        "temperature_targets": temperature_targets,
        "quiet_hours": quiet_hours,
        "webhook_config": webhook_config,
        "sensor_labels": sensor_labels,
        "settings": settings_data,
    }


@router.post("/import", dependencies=[Depends(require_auth), Depends(require_admin), Depends(require_csrf)])
async def import_config(request: Request):
    """Import application configuration from a previously exported JSON object."""
    body = await request.json()

    if body.get("export_version") != 1:
        raise HTTPException(status_code=422, detail="Unsupported export_version (expected 1)")

    db = request.app.state.db
    imported: dict[str, int] = {}
    skipped: list[str] = []

    # --- Profiles ---
    if "profiles" in body:
        profile_repo = request.app.state.profile_repo
        count = 0
        for p in body["profiles"]:
            from app.models.profiles import ProfilePreset
            from app.models.fan_curves import FanCurve
            pid = p.get("id")
            if not pid:
                skipped.append(f"profile missing 'id': {p.get('name', '?')}")
                continue
            try:
                curves = [FanCurve(**c) for c in p.get("curves", [])]
            except Exception as exc:
                skipped.append(f"profile '{pid}' invalid curve: {exc}")
                continue
            existing = await profile_repo.get(pid)
            if existing:
                # Update: replace curves
                await profile_repo.set_curves(existing.id, curves)
            else:
                # Create with the same ID
                now = datetime.now(timezone.utc).isoformat()
                preset_val = p.get("preset", "custom")
                try:
                    preset = ProfilePreset(preset_val)
                except ValueError:
                    preset = ProfilePreset.CUSTOM
                await db.execute(
                    "INSERT OR REPLACE INTO profiles (id, name, preset, is_active, created_at, updated_at) "
                    "VALUES (?, ?, ?, ?, ?, ?)",
                    (pid, p.get("name", "Imported"), preset.value,
                     int(p.get("is_active", False)), now, now),
                )
                for curve in curves:
                    points_json = json.dumps([pt.model_dump() for pt in curve.points])
                    sensor_ids_json = json.dumps(curve.sensor_ids)
                    await db.execute(
                        "INSERT OR REPLACE INTO fan_curves "
                        "(id, profile_id, name, sensor_id, fan_id, enabled, points_json, sensor_ids_json) "
                        "VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                        (curve.id, pid, curve.name, curve.sensor_id,
                         curve.fan_id, int(curve.enabled), points_json, sensor_ids_json),
                    )
                await db.commit()
            count += 1
        imported["profiles"] = count

    # --- Alert rules ---
    if "alert_rules" in body:
        from app.services.alert_service import AlertRule
        alert_service = request.app.state.alert_service
        # Clear existing rules
        for rule in list(alert_service.rules):
            await alert_service.remove_rule(rule.id)
        count = 0
        for r in body["alert_rules"]:
            rule = AlertRule(**r)
            await alert_service.add_rule(rule)
            count += 1
        imported["alert_rules"] = count

    # --- Temperature targets ---
    if "temperature_targets" in body:
        from app.models.temperature_targets import TemperatureTarget
        temp_repo = request.app.state.temperature_target_service._repo
        # Clear existing
        existing_targets = await temp_repo.list_all()
        for t in existing_targets:
            await temp_repo.delete(t.id)
        count = 0
        for t in body["temperature_targets"]:
            target = TemperatureTarget(**t)
            await temp_repo.create(target)
            count += 1
        # Reload in-memory state
        await request.app.state.temperature_target_service.load()
        imported["temperature_targets"] = count

    # --- Quiet hours ---
    if "quiet_hours" in body:
        await db.execute("DELETE FROM quiet_hours")
        count = 0
        for qh in body["quiet_hours"]:
            await db.execute(
                "INSERT INTO quiet_hours (day_of_week, start_time, end_time, profile_id, enabled) "
                "VALUES (?, ?, ?, ?, ?)",
                (qh["day_of_week"], qh["start_time"], qh["end_time"],
                 qh["profile_id"], int(qh.get("enabled", True))),
            )
            count += 1
        await db.commit()
        imported["quiet_hours"] = count

    # --- Webhook config ---
    if "webhook_config" in body:
        wh = body["webhook_config"]
        webhook_svc = request.app.state.webhook_service
        from app.services.webhook_service import _KEEP_SECRET
        await webhook_svc.update_config(
            enabled=wh.get("enabled", False),
            target_url=wh.get("target_url", ""),
            signing_secret=_KEEP_SECRET,  # never import signing secrets
            timeout_seconds=wh.get("timeout_seconds", 3.0),
            max_retries=wh.get("max_retries", 2),
            retry_backoff_seconds=wh.get("retry_backoff_seconds", 1.0),
        )
        imported["webhook_config"] = 1

    # --- Sensor labels ---
    if "sensor_labels" in body:
        now = datetime.now(timezone.utc).isoformat()
        count = 0
        for sensor_id, label in body["sensor_labels"].items():
            await db.execute(
                "INSERT OR REPLACE INTO sensor_labels (sensor_id, label, updated_at) "
                "VALUES (?, ?, ?)",
                (sensor_id, label, now),
            )
            count += 1
        await db.commit()
        imported["sensor_labels"] = count

    # --- Settings ---
    if "settings" in body:
        settings_repo = request.app.state.settings_repo
        s = body["settings"]
        updates: dict[str, str] = {}
        if "sensor_poll_interval" in s:
            updates["sensor_poll_interval"] = str(s["sensor_poll_interval"])
        if "history_retention_hours" in s:
            updates["history_retention_hours"] = str(s["history_retention_hours"])
        if "temp_unit" in s:
            updates["temp_unit"] = str(s["temp_unit"])
        if "fan_ramp_rate_pct_per_sec" in s:
            updates["fan_ramp_rate_pct_per_sec"] = str(s["fan_ramp_rate_pct_per_sec"])
        if updates:
            await settings_repo.set_many(updates)
        imported["settings"] = len(updates)

    logger.info("Config import completed: %s (skipped: %s)", imported, skipped)
    result: dict = {"success": True, "imported": imported}
    if skipped:
        result["skipped"] = skipped
    return result
