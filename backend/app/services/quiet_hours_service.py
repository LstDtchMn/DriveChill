"""Quiet hours — automatic profile switching on a time schedule.

Checks the ``quiet_hours`` table every 60 seconds.  When the current
day/time matches a rule, the associated profile is activated — but only
once per boundary transition (not re-applied every minute).

Manual overrides are respected until the next schedule boundary.
"""

from __future__ import annotations

import asyncio
import logging
from datetime import datetime, time, timezone

import aiosqlite

logger = logging.getLogger(__name__)


class QuietHoursService:
    def __init__(self, db: aiosqlite.Connection) -> None:
        self._db = db
        self._task: asyncio.Task | None = None
        self._running = False
        self._manual_override = False
        # Track which (rule_id, profile_id) is currently applied so we
        # don't re-apply every 60 seconds.
        self._active_rule: tuple[int, str] | None = None
        self._activate_profile_fn = None

    def set_activate_fn(self, fn) -> None:
        self._activate_profile_fn = fn

    def notify_manual_override(self) -> None:
        """Called when the user manually activates a profile."""
        self._manual_override = True

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
                logger.exception("Quiet hours check failed")
            await asyncio.sleep(60)

    async def _check_schedule(self) -> None:
        if self._activate_profile_fn is None:
            return

        now = datetime.now(timezone.utc)
        dow = now.weekday()  # 0=Monday
        current_time = now.strftime("%H:%M")

        matched = await self._find_matching_rule(dow, current_time)

        if matched is None:
            # No rule matches.  If we were tracking an active rule,
            # clear it so the next boundary triggers a transition.
            if self._active_rule is not None:
                self._active_rule = None
                self._manual_override = False
            return

        rule_id, profile_id = matched

        # Same rule as last check — nothing to do.
        if self._active_rule == (rule_id, profile_id):
            return

        # New boundary: different rule or first match.
        # Clear manual override — it only applies within a single window.
        # The new rule takes effect immediately.
        self._manual_override = False
        self._active_rule = (rule_id, profile_id)

        try:
            await self._activate_profile_fn(profile_id)
            logger.info("Quiet hours: activated profile %s (rule %d)", profile_id, rule_id)
        except Exception:
            logger.exception("Quiet hours: failed to activate profile %s", profile_id)

    async def _find_matching_rule(self, dow: int, current_time: str) -> tuple[int, str] | None:
        """Find the first enabled rule matching the current day/time.

        Checks both the current weekday's rules AND the previous day's
        rules for overnight ranges that span midnight.
        """
        prev_dow = (dow - 1) % 7

        cursor = await self._db.execute(
            "SELECT id, profile_id, start_time, end_time, day_of_week "
            "FROM quiet_hours "
            "WHERE enabled = 1 AND day_of_week IN (?, ?) "
            "ORDER BY CASE WHEN day_of_week = ? THEN 0 ELSE 1 END, start_time",
            (dow, prev_dow, dow),
        )
        rules = await cursor.fetchall()

        for rule_id, profile_id, start_str, end_str, rule_dow in rules:
            # H-8: catch per-rule parse errors so one corrupt row doesn't
            # prevent all other rules from being evaluated.
            try:
                if rule_dow == dow:
                    # Same-day rule: check normal range
                    if _time_in_range(current_time, start_str, end_str):
                        return (rule_id, profile_id)
                elif rule_dow == prev_dow:
                    # Previous-day rule: only matches if it's an overnight range
                    # and we're in the "after midnight" portion
                    s = _parse_hm(start_str)
                    e = _parse_hm(end_str)
                    if s > e:
                        # Overnight rule (e.g., 23:00 -> 07:00)
                        cur = _parse_hm(current_time)
                        if cur < e:
                            return (rule_id, profile_id)
            except Exception:
                logger.warning(
                    "Skipping rule %d due to invalid time data (start=%r end=%r)",
                    rule_id, start_str, end_str,
                )

        return None

    @staticmethod
    def _time_in_range(current: str, start: str, end: str) -> bool:
        return _time_in_range(current, start, end)


def _time_in_range(current: str, start: str, end: str) -> bool:
    """Check if *current* HH:MM is within [start, end).

    Handles overnight ranges (e.g. 23:00 -> 07:00).
    """
    cur = _parse_hm(current)
    s = _parse_hm(start)
    e = _parse_hm(end)

    if s <= e:
        return s <= cur < e
    else:
        return cur >= s or cur < e


def _parse_hm(hm: str) -> time:
    """Parse HH:MM string to a time object.

    H-8: raises ValueError with a descriptive message on bad input so
    callers can log which rule caused the problem instead of getting a
    bare IndexError or ValueError from deep inside time().
    """
    try:
        parts = hm.split(":")
        return time(int(parts[0]), int(parts[1]))
    except (ValueError, IndexError, AttributeError) as exc:
        raise ValueError(f"Invalid time format: {hm!r}") from exc
