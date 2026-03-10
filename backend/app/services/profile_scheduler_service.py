"""Profile scheduler — automatic profile switching based on time-of-day rules.

Checks the ``profile_schedules`` table every 60 seconds.  When the current
day/time matches a schedule, the associated profile is activated — but only
if the system is NOT in panic mode, NOT in an alert-triggered profile switch,
and NOT in quiet hours.

Priority order (highest to lowest):
  1. Panic mode (always wins)
  2. Alert-triggered profile (safety)
  3. Quiet hours (explicit user silence preference)
  4. Profile schedule (automation convenience)
  5. Manual profile selection (default)
"""

from __future__ import annotations

import asyncio
import logging
from datetime import datetime, time, timezone

import aiosqlite

logger = logging.getLogger(__name__)


class ProfileSchedulerService:
    def __init__(self, db: aiosqlite.Connection) -> None:
        self._db = db
        self._task: asyncio.Task | None = None
        self._running = False
        # Track which schedule is currently applied so we don't re-activate
        # every 60 seconds.
        self._active_schedule_id: str | None = None
        self._activate_profile_fn = None
        # Callbacks to check higher-priority overrides
        self._is_panic_fn = None
        self._is_alert_active_fn = None
        self._is_quiet_hours_fn = None

    def set_activate_fn(self, fn) -> None:
        """Set the callback used to activate a profile by ID."""
        self._activate_profile_fn = fn

    def set_panic_check_fn(self, fn) -> None:
        """Set callback that returns True if system is in panic mode."""
        self._is_panic_fn = fn

    def set_alert_active_check_fn(self, fn) -> None:
        """Set callback that returns True if an alert-triggered profile is active."""
        self._is_alert_active_fn = fn

    def set_quiet_hours_check_fn(self, fn) -> None:
        """Set callback that returns True if quiet hours are currently active."""
        self._is_quiet_hours_fn = fn

    async def start(self) -> None:
        self._running = True
        self._task = asyncio.create_task(self._loop())

    async def stop(self) -> None:
        self._running = False
        if self._task:
            self._task.cancel()
            try:
                await self._task
            except asyncio.CancelledError:
                pass

    async def _loop(self) -> None:
        while self._running:
            try:
                await self._check_schedule()
            except asyncio.CancelledError:
                raise
            except Exception:
                logger.exception("Profile schedule check failed")
            await asyncio.sleep(60)

    async def _check_schedule(self) -> None:
        if self._activate_profile_fn is None:
            return

        # Check higher-priority overrides
        if self._is_panic_fn and self._is_panic_fn():
            return
        if self._is_alert_active_fn and self._is_alert_active_fn():
            return
        if self._is_quiet_hours_fn and self._is_quiet_hours_fn():
            return

        schedules = await self._load_schedules()
        matched = _find_active_schedule(schedules)

        if matched is None:
            # No schedule matches — clear tracking
            if self._active_schedule_id is not None:
                self._active_schedule_id = None
            return

        schedule_id = matched["id"]
        profile_id = matched["profile_id"]

        # Already on this schedule — nothing to do
        if self._active_schedule_id == schedule_id:
            return

        self._active_schedule_id = schedule_id

        try:
            await self._activate_profile_fn(profile_id)
            logger.info(
                "Profile schedule: activated profile %s (schedule %s)",
                profile_id, schedule_id,
            )
        except Exception:
            logger.exception(
                "Profile schedule: failed to activate profile %s", profile_id
            )

    async def _load_schedules(self) -> list[dict]:
        cursor = await self._db.execute(
            "SELECT id, profile_id, start_time, end_time, days_of_week, "
            "timezone, enabled, created_at "
            "FROM profile_schedules WHERE enabled = 1"
        )
        rows = await cursor.fetchall()
        return [
            {
                "id": r[0],
                "profile_id": r[1],
                "start_time": r[2],
                "end_time": r[3],
                "days_of_week": r[4],
                "timezone": r[5],
                "enabled": bool(r[6]),
                "created_at": r[7],
            }
            for r in rows
        ]


def _find_active_schedule(
    schedules: list[dict],
    now: datetime | None = None,
) -> dict | None:
    """Find the best matching schedule for the current local time.

    For overlapping schedules: most specific (fewest days) wins.
    If tied, most recently created wins.
    """
    if now is None:
        now = datetime.now(timezone.utc)

    current_day = now.weekday()  # 0=Monday
    current_time = now.strftime("%H:%M")

    matching = []
    for schedule in schedules:
        if not schedule.get("enabled", True):
            continue
        days = [int(d) for d in schedule["days_of_week"].split(",") if d.strip()]
        if current_day not in days:
            continue

        start = schedule["start_time"]
        end = schedule["end_time"]

        # Handle overnight spans (e.g., 22:00 to 06:00)
        if start <= end:
            if start <= current_time < end:
                matching.append(schedule)
        else:  # overnight
            if current_time >= start or current_time < end:
                matching.append(schedule)

    if not matching:
        return None

    # Most specific (fewest days) wins, then most recently created
    matching.sort(
        key=lambda s: (len(s["days_of_week"].split(",")), s.get("created_at", ""))
    )
    return matching[0]
