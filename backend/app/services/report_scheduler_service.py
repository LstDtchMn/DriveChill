"""Scheduled analytics report service.

Checks every 60 seconds whether any report schedule is due, generates an
HTML analytics summary, and dispatches it via EmailNotificationService.
"""

from __future__ import annotations

import asyncio
import logging
import math
from collections import defaultdict
from datetime import datetime, timedelta, timezone
from typing import TYPE_CHECKING, Any

if TYPE_CHECKING:
    import aiosqlite
    from app.services.email_notification_service import EmailNotificationService

logger = logging.getLogger(__name__)

# ---------------------------------------------------------------------------
# Due-logic helpers
# ---------------------------------------------------------------------------


def _week_start_utc(dt: datetime) -> datetime:
    """Return the Monday 00:00 UTC of the ISO week containing *dt*."""
    return (dt - timedelta(days=dt.weekday())).replace(
        hour=0, minute=0, second=0, microsecond=0, tzinfo=timezone.utc
    )


def _is_due(schedule: dict, now: datetime) -> bool:
    """Return True if *schedule* should fire right now.

    A schedule is due when:
    - Its ``time_utc`` (HH:MM) matches the current UTC hour+minute.
    - It has never been sent, or ``last_sent_at`` is before the current
      reporting period (today for daily, the current ISO week for weekly).
    """
    try:
        hh, mm = schedule["time_utc"].split(":")
        sched_hour = int(hh)
        sched_minute = int(mm)
    except (ValueError, AttributeError):
        return False

    if now.hour != sched_hour or now.minute != sched_minute:
        return False

    last_sent_at = schedule.get("last_sent_at")
    if last_sent_at is None:
        return True

    try:
        last_sent_dt = datetime.fromisoformat(last_sent_at).replace(tzinfo=timezone.utc)
    except (ValueError, TypeError):
        return True

    if schedule["frequency"] == "daily":
        today_start = now.replace(hour=0, minute=0, second=0, microsecond=0)
        return last_sent_dt < today_start
    else:  # weekly
        week_start = _week_start_utc(now)
        return last_sent_dt < week_start


# ---------------------------------------------------------------------------
# Inline analytics helpers (avoid importing request-scoped route functions)
# ---------------------------------------------------------------------------


async def _fetchall(db: "aiosqlite.Connection", sql: str, params: Any = ()) -> list[dict]:
    result: list[dict] = []
    async with db.execute(sql, params) as cursor:
        cols = [d[0] for d in cursor.description]
        result = [dict(zip(cols, row)) for row in await cursor.fetchall()]
    return result


async def _get_stats(db: "aiosqlite.Connection", start_dt: datetime, end_dt: datetime) -> list[dict]:
    fmt = "%Y-%m-%d %H:%M:%S"
    sql = """
        SELECT sensor_id, MAX(sensor_name) AS sensor_name, MAX(sensor_type) AS sensor_type,
               MAX(unit) AS unit,
               MIN(CAST(value AS REAL)) AS min_value,
               MAX(CAST(value AS REAL)) AS max_value,
               AVG(CAST(value AS REAL)) AS avg_value,
               COUNT(*) AS sample_count
        FROM sensor_log
        WHERE timestamp >= ? AND timestamp <= ?
        GROUP BY sensor_id
    """
    return await _fetchall(db, sql, [start_dt.strftime(fmt), end_dt.strftime(fmt)])


async def _get_anomalies(db: "aiosqlite.Connection", start_dt: datetime, end_dt: datetime) -> list[dict]:
    fmt = "%Y-%m-%d %H:%M:%S"
    sql = """
        SELECT timestamp, sensor_id, sensor_name, sensor_type,
               CAST(value AS REAL) AS value, unit
        FROM sensor_log
        WHERE timestamp >= ? AND timestamp <= ?
        ORDER BY sensor_id, timestamp ASC
    """
    all_rows = await _fetchall(db, sql, [start_dt.strftime(fmt), end_dt.strftime(fmt)])

    by_sensor: dict[str, list[dict]] = defaultdict(list)
    meta: dict[str, dict] = {}
    for row in all_rows:
        sid = row["sensor_id"]
        by_sensor[sid].append(row)
        if sid not in meta:
            meta[sid] = {"sensor_name": row["sensor_name"], "unit": row["unit"]}

    z_threshold = 3.0
    anomalies: list[dict] = []
    for sid, rows in by_sensor.items():
        if len(rows) < 10:
            continue
        values = [r["value"] for r in rows]
        mean = sum(values) / len(values)
        variance = sum((v - mean) ** 2 for v in values) / len(values)
        stdev = math.sqrt(variance)
        if stdev == 0:
            continue
        for row in rows:
            z = abs(row["value"] - mean) / stdev
            if z > z_threshold:
                anomalies.append({
                    "timestamp_utc": row["timestamp"],
                    "sensor_id": sid,
                    "sensor_name": meta[sid]["sensor_name"],
                    "value": row["value"],
                    "unit": meta[sid]["unit"],
                    "z_score": round(z, 2),
                    "severity": "critical" if z > z_threshold * 1.5 else "warning",
                })
    return anomalies[:50]


# ---------------------------------------------------------------------------
# HTML report builder
# ---------------------------------------------------------------------------

_HTML_TEMPLATE = """\
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<title>DriveChill Analytics Report</title>
<style>
  body {{ font-family: Arial, sans-serif; font-size: 14px; color: #1a1a2e; background: #f4f6fb; margin: 0; padding: 24px; }}
  h1 {{ font-size: 20px; color: #2563eb; margin-bottom: 4px; }}
  h2 {{ font-size: 15px; color: #374151; border-bottom: 1px solid #e2e8f0; padding-bottom: 4px; margin-top: 24px; }}
  .meta {{ color: #6b7280; font-size: 12px; margin-bottom: 20px; }}
  table {{ width: 100%; border-collapse: collapse; font-size: 13px; margin-top: 8px; }}
  th {{ background: #2563eb; color: #fff; text-align: left; padding: 6px 10px; }}
  td {{ padding: 5px 10px; border-bottom: 1px solid #e2e8f0; }}
  tr:nth-child(even) td {{ background: #f0f4ff; }}
  .badge-warn {{ background: #fef3c7; color: #92400e; padding: 1px 6px; border-radius: 4px; font-size: 11px; }}
  .badge-crit {{ background: #fee2e2; color: #991b1b; padding: 1px 6px; border-radius: 4px; font-size: 11px; }}
  .none {{ color: #6b7280; font-style: italic; }}
</style>
</head>
<body>
<h1>DriveChill Analytics Report</h1>
<p class="meta">Generated: {generated_at} &bull; Window: last {window_hours:.0f} hours</p>

<h2>Sensor Statistics</h2>
{stats_table}

<h2>Anomalies Detected</h2>
{anomalies_table}

<p style="color:#9ca3af;font-size:11px;margin-top:32px;">
  This report was sent automatically by DriveChill. To manage schedules, open Settings &rarr; Report Schedules.
</p>
</body>
</html>
"""


def _build_report_html(
    stats: list[dict],
    anomalies: list[dict],
    window_hours: float,
) -> str:
    generated_at = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M UTC")

    # Stats table
    if stats:
        rows_html = "".join(
            f"<tr><td>{s['sensor_name']}</td><td>{s['sensor_type']}</td>"
            f"<td>{s['avg_value']:.1f} {s['unit']}</td>"
            f"<td>{s['min_value']:.1f}</td><td>{s['max_value']:.1f}</td>"
            f"<td>{s['sample_count']}</td></tr>"
            for s in stats
        )
        stats_table = (
            "<table><thead><tr>"
            "<th>Sensor</th><th>Type</th><th>Avg</th><th>Min</th><th>Max</th><th>Samples</th>"
            "</tr></thead><tbody>"
            + rows_html
            + "</tbody></table>"
        )
    else:
        stats_table = '<p class="none">No sensor data in this window.</p>'

    # Anomalies table
    if anomalies:
        rows_html = "".join(
            f"<tr><td>{a['timestamp_utc']}</td><td>{a['sensor_name']}</td>"
            f"<td>{a['value']:.1f} {a['unit']}</td>"
            f"<td>{a['z_score']:.2f}</td>"
            f"<td><span class=\"{'badge-crit' if a['severity'] == 'critical' else 'badge-warn'}\">"
            f"{a['severity']}</span></td></tr>"
            for a in anomalies
        )
        anomalies_table = (
            "<table><thead><tr>"
            "<th>Time</th><th>Sensor</th><th>Value</th><th>Z-score</th><th>Severity</th>"
            "</tr></thead><tbody>"
            + rows_html
            + "</tbody></table>"
        )
    else:
        anomalies_table = '<p class="none">No anomalies detected.</p>'

    return _HTML_TEMPLATE.format(
        generated_at=generated_at,
        window_hours=window_hours,
        stats_table=stats_table,
        anomalies_table=anomalies_table,
    )


# ---------------------------------------------------------------------------
# Service
# ---------------------------------------------------------------------------


class ReportSchedulerService:
    """Background service that fires scheduled analytics email reports."""

    def __init__(
        self,
        db: "aiosqlite.Connection",
        email_svc: "EmailNotificationService",
    ) -> None:
        self._db = db
        self._email_svc = email_svc
        self._task: asyncio.Task | None = None

    async def start(self) -> None:
        self._task = asyncio.create_task(self._loop())
        logger.info("ReportSchedulerService started")

    async def stop(self) -> None:
        if self._task:
            self._task.cancel()
            try:
                await self._task
            except asyncio.CancelledError:
                pass
        logger.info("ReportSchedulerService stopped")

    async def _loop(self) -> None:
        while True:
            await asyncio.sleep(60)
            try:
                await self._check_and_send()
            except asyncio.CancelledError:
                raise
            except Exception:
                logger.exception("ReportSchedulerService: unexpected error in check loop")

    async def _check_and_send(self) -> None:
        now = datetime.now(timezone.utc)
        cursor = await self._db.execute(
            "SELECT id, frequency, time_utc, timezone, enabled, last_sent_at, created_at "
            "FROM report_schedules WHERE enabled=1"
        )
        rows = await cursor.fetchall()
        schedules = [
            {
                "id": r[0],
                "frequency": r[1],
                "time_utc": r[2],
                "timezone": r[3],
                "enabled": bool(r[4]),
                "last_sent_at": r[5],
                "created_at": r[6],
            }
            for r in rows
        ]

        for schedule in schedules:
            if not _is_due(schedule, now):
                continue
            logger.info(
                "Report schedule %s is due (freq=%s, time_utc=%s) — generating report",
                schedule["id"],
                schedule["frequency"],
                schedule["time_utc"],
            )
            try:
                await self._send_report(schedule, now)
            except Exception:
                logger.exception("Failed to send scheduled report %s", schedule["id"])

    async def _send_report(self, schedule: dict, now: datetime) -> None:
        window_hours = 24.0 if schedule["frequency"] == "daily" else 168.0
        start_dt = now - timedelta(hours=window_hours)

        stats = await _get_stats(self._db, start_dt, now)
        anomalies = await _get_anomalies(self._db, start_dt, now)

        html_body = _build_report_html(stats, anomalies, window_hours)

        freq_label = "Daily" if schedule["frequency"] == "daily" else "Weekly"
        subject = f"DriveChill {freq_label} Report — {now.strftime('%Y-%m-%d')}"

        sent = await self._email_svc._send_html(subject, html_body)
        if sent:
            await self._db.execute(
                "UPDATE report_schedules SET last_sent_at=? WHERE id=?",
                (now.isoformat(), schedule["id"]),
            )
            await self._db.commit()
            logger.info("Scheduled report %s sent successfully", schedule["id"])
        else:
            logger.warning("Scheduled report %s: email send returned 0 (check email config)", schedule["id"])
