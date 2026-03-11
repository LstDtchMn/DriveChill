from __future__ import annotations

import csv
import io
import json
import math
from collections import defaultdict
from datetime import datetime, timedelta, timezone
from typing import Any

from fastapi import APIRouter, HTTPException, Query, Request
from starlette.responses import Response

router = APIRouter(prefix="/api/analytics", tags=["analytics"])


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _clamp(value: float, lo: float, hi: float) -> float:
    return max(lo, min(hi, value))


async def _fetchall_as_dicts(db: Any, sql: str, params: Any = ()) -> list:
    """Execute a query and return rows as dicts keyed by column name.

    Uses cursor.description rather than mutating the shared connection's
    row_factory, which is not safe under concurrent requests.
    """
    result: list = []
    async with db.execute(sql, params) as cursor:
        cols = [d[0] for d in cursor.description]
        result = [dict(zip(cols, row)) for row in await cursor.fetchall()]
    return result


def _auto_bucket_seconds(duration_seconds: float) -> int:
    """Choose a sensible bucket size given a query duration."""
    if duration_seconds <= 3_600:
        return 30        # ≤1 h → 30 s
    if duration_seconds <= 86_400:
        return 300       # ≤24 h → 5 min
    if duration_seconds <= 604_800:
        return 1_800     # ≤7 d → 30 min
    return 7_200         # >7 d → 2 h


def _parse_sensor_ids(
    sensor_id: str | None,
    sensor_ids: str | None,
) -> list[str] | None:
    """Parse sensor-ID params into a list, or None for all.

    Raises HTTP 400 if both sensor_id and sensor_ids are provided.
    """
    if sensor_id and sensor_ids:
        raise HTTPException(
            status_code=400,
            detail="Use sensor_id or sensor_ids, not both",
        )
    ids: list[str] = []
    if sensor_ids:
        ids.extend(s.strip() for s in sensor_ids.split(",") if s.strip())
    if sensor_id:
        ids.append(sensor_id)
    return ids if ids else None


def _parse_utc(value: str) -> datetime:
    """Parse an ISO 8601 timestamp and normalise it to UTC.

    - Trailing ``Z`` is treated as UTC.
    - Naive datetimes (no offset) are assumed UTC.
    - Offset-aware datetimes are *converted* to UTC (not relabelled).
    """
    cleaned = value.strip()
    if cleaned.endswith("Z") or cleaned.endswith("z"):
        cleaned = cleaned[:-1] + "+00:00"
    dt = datetime.fromisoformat(cleaned)
    if dt.tzinfo is None:
        return dt.replace(tzinfo=timezone.utc)
    return dt.astimezone(timezone.utc)


def _resolve_range(
    hours: float | None,
    start: str | None,
    end: str | None,
) -> tuple[datetime, datetime]:
    """Return (start_dt, end_dt) as UTC-aware datetimes.

    Custom range requires **both** ``start`` and ``end`` to be present and
    valid (start < end).  A one-sided or invalid pair falls back to the
    preset ``hours`` window anchored to now.
    """
    now = datetime.now(timezone.utc)
    if isinstance(start, str) and isinstance(end, str):
        try:
            start_dt = _parse_utc(start)
            end_dt = _parse_utc(end)
            if start_dt < end_dt:
                return start_dt, end_dt
        except ValueError:
            pass
    h = _clamp(hours if hours is not None else 24.0, 0.1, 8760.0)
    return now - timedelta(hours=h), now


def _range_meta(start_dt: datetime, end_dt: datetime) -> dict[str, str]:
    return {"start": start_dt.isoformat(), "end": end_dt.isoformat()}


async def _retention_hours(request: Request) -> int:
    repo = getattr(request.app.state, "settings_repo", None)
    if repo is None:
        return 720
    return await repo.get_int("history_retention_hours", 720)


def _sensor_in_clause(ids: list[str]) -> tuple[str, list]:
    """Return (sql_fragment, positional_params) for a sensor_id IN clause."""
    placeholders = ",".join("?" * len(ids))
    return f"sensor_id IN ({placeholders})", list(ids)


# ---------------------------------------------------------------------------
# GET /api/analytics/history
# ---------------------------------------------------------------------------

@router.get("/history")
async def get_history(
    request: Request,
    hours: float | None = Query(default=None, ge=0.1, le=8760.0),
    start: str | None = Query(default=None),
    end: str | None = Query(default=None),
    sensor_id: str | None = Query(default=None),
    sensor_ids: str | None = Query(default=None),
    bucket_seconds: int | None = Query(default=None, ge=10, le=86400),
):
    start_dt, end_dt = _resolve_range(hours, start, end)
    duration = (end_dt - start_dt).total_seconds()
    bucket = bucket_seconds if bucket_seconds is not None else _auto_bucket_seconds(duration)

    # Retention gate
    ret_hours = await _retention_hours(request)
    retention_limit = datetime.now(timezone.utc) - timedelta(hours=float(ret_hours))
    retention_limited = start_dt < retention_limit
    effective_start = max(start_dt, retention_limit)

    ids = _parse_sensor_ids(sensor_id, sensor_ids)
    fmt = "%Y-%m-%d %H:%M:%S"

    base_params: list = [bucket, effective_start.strftime(fmt), end_dt.strftime(fmt)]
    sensor_clause = ""
    if ids:
        clause, extra = _sensor_in_clause(ids)
        sensor_clause = f"AND {clause}"
        base_params.extend(extra)

    sql = f"""
        SELECT
          sensor_id,
          MAX(sensor_name) AS sensor_name,
          MAX(sensor_type) AS sensor_type,
          MAX(unit) AS unit,
          CAST(strftime('%s', timestamp) AS INTEGER) / ? AS bucket_epoch,
          AVG(CAST(value AS REAL)) AS avg_value,
          MIN(CAST(value AS REAL)) AS min_value,
          MAX(CAST(value AS REAL)) AS max_value,
          COUNT(*) AS sample_count
        FROM sensor_log
        WHERE timestamp >= ? AND timestamp <= ?
          {sensor_clause}
        GROUP BY sensor_id, bucket_epoch
        ORDER BY sensor_id, bucket_epoch ASC
    """

    db = request.app.state.db
    rows = await _fetchall_as_dicts(db, sql, base_params)

    # buckets: flat list (legacy v1 shape — retained for backwards compat)
    buckets: list[dict] = []
    # series: dict grouped by sensor_id (v2.0 shape)
    series: dict[str, list[dict]] = {}
    all_ts: list[str] = []

    for row in rows:
        ts = datetime.fromtimestamp(
            row["bucket_epoch"] * bucket, tz=timezone.utc
        ).isoformat()
        all_ts.append(ts)
        flat_point = {
            "sensor_id": row["sensor_id"],
            "sensor_name": row["sensor_name"],
            "sensor_type": row["sensor_type"],
            "unit": row["unit"],
            "timestamp_utc": ts,
            "avg_value": row["avg_value"],
            "min_value": row["min_value"],
            "max_value": row["max_value"],
            "sample_count": row["sample_count"],
        }
        buckets.append(flat_point)
        sid = row["sensor_id"]
        if sid not in series:
            series[sid] = []
        series[sid].append({
            "timestamp": ts,
            "avg": row["avg_value"],
            "min": row["min_value"],
            "max": row["max_value"],
            "count": row["sample_count"],
        })

    # Actual returned range from data timestamps
    if all_ts:
        returned_start = datetime.fromisoformat(min(all_ts))
        returned_end = datetime.fromisoformat(max(all_ts))
    else:
        returned_start, returned_end = effective_start, end_dt

    return {
        "buckets": buckets,        # legacy key — retained for v1 consumers
        "series": series,          # v2.0 key: {sensor_id: [{timestamp, avg, min, max, count}]}
        "bucket_seconds": bucket,
        "requested_range": _range_meta(start_dt, end_dt),
        "returned_range": _range_meta(returned_start, returned_end),
        "retention_limited": retention_limited,
    }


# ---------------------------------------------------------------------------
# GET /api/analytics/stats
# ---------------------------------------------------------------------------

@router.get("/stats")
async def get_stats(
    request: Request,
    hours: float | None = Query(default=None, ge=0.1, le=8760.0),
    start: str | None = Query(default=None),
    end: str | None = Query(default=None),
    sensor_id: str | None = Query(default=None),
    sensor_ids: str | None = Query(default=None),
):
    start_dt, end_dt = _resolve_range(hours, start, end)
    ret_hours = await _retention_hours(request)
    retention_limit = datetime.now(timezone.utc) - timedelta(hours=float(ret_hours))
    start_dt = max(start_dt, retention_limit)
    ids = _parse_sensor_ids(sensor_id, sensor_ids)
    fmt = "%Y-%m-%d %H:%M:%S"

    base_params: list = [start_dt.strftime(fmt), end_dt.strftime(fmt)]
    sensor_clause = ""
    if ids:
        clause, extra = _sensor_in_clause(ids)
        sensor_clause = f"AND {clause}"
        base_params.extend(extra)

    agg_sql = f"""
        SELECT
          sensor_id,
          MAX(sensor_name) AS sensor_name,
          MAX(sensor_type) AS sensor_type,
          MAX(unit) AS unit,
          MIN(CAST(value AS REAL)) AS min_value,
          MAX(CAST(value AS REAL)) AS max_value,
          AVG(CAST(value AS REAL)) AS avg_value,
          COUNT(*) AS sample_count
        FROM sensor_log
        WHERE timestamp >= ? AND timestamp <= ?
          {sensor_clause}
        GROUP BY sensor_id
    """
    p95_sql = f"""
        SELECT sensor_id, CAST(value AS REAL) AS v
        FROM sensor_log
        WHERE timestamp >= ? AND timestamp <= ?
          {sensor_clause}
        ORDER BY sensor_id, v ASC
    """

    db = request.app.state.db
    agg_rows = await _fetchall_as_dicts(db, agg_sql, base_params)

    # p95 needs the same params
    sorted_vals: dict[str, list[float]] = defaultdict(list)
    async with db.execute(p95_sql, base_params) as cursor:
        async for row in cursor:
            sorted_vals[row[0]].append(row[1])

    stats = []
    for row in agg_rows:
        sid = row["sensor_id"]
        vals = sorted_vals.get(sid, [])
        p95_value: float | None = None
        if vals:
            idx = min(int(len(vals) * 0.95), len(vals) - 1)
            p95_value = vals[idx]
        stats.append({
            "sensor_id": sid,
            "sensor_name": row["sensor_name"],
            "sensor_type": row["sensor_type"],
            "unit": row["unit"],
            "min_value": row["min_value"],
            "max_value": row["max_value"],
            "avg_value": row["avg_value"],
            "p95_value": p95_value,
            "sample_count": row["sample_count"],
        })

    # Returned range: actual data extent
    first_row_ts_sql = f"""
        SELECT MIN(timestamp), MAX(timestamp)
        FROM sensor_log
        WHERE timestamp >= ? AND timestamp <= ?
          {sensor_clause}
    """
    extent_row = (await _fetchall_as_dicts(db, first_row_ts_sql, base_params))
    returned_start = start_dt
    returned_end = end_dt
    if extent_row and extent_row[0]["MIN(timestamp)"]:
        try:
            returned_start = datetime.fromisoformat(extent_row[0]["MIN(timestamp)"]).replace(tzinfo=timezone.utc)
            returned_end = datetime.fromisoformat(extent_row[0]["MAX(timestamp)"]).replace(tzinfo=timezone.utc)
        except (ValueError, KeyError):
            pass

    return {
        "stats": stats,
        "requested_range": _range_meta(start_dt, end_dt),
        "returned_range": _range_meta(returned_start, returned_end),
    }


# ---------------------------------------------------------------------------
# GET /api/analytics/anomalies
# ---------------------------------------------------------------------------

@router.get("/anomalies")
async def get_anomalies(
    request: Request,
    hours: float | None = Query(default=None, ge=0.1, le=720.0),
    start: str | None = Query(default=None),
    end: str | None = Query(default=None),
    sensor_id: str | None = Query(default=None),
    sensor_ids: str | None = Query(default=None),
    z_score_threshold: float = Query(default=3.0, ge=1.0, le=10.0),
):
    if hours is None and start is None:
        hours = 24.0
    start_dt, end_dt = _resolve_range(hours, start, end)
    ret_hours = await _retention_hours(request)
    retention_limit = datetime.now(timezone.utc) - timedelta(hours=float(ret_hours))
    start_dt = max(start_dt, retention_limit)
    z_score_threshold = _clamp(z_score_threshold, 1.0, 10.0)
    ids = _parse_sensor_ids(sensor_id, sensor_ids)
    fmt = "%Y-%m-%d %H:%M:%S"

    base_params: list = [start_dt.strftime(fmt), end_dt.strftime(fmt)]
    sensor_clause = ""
    if ids:
        clause, extra = _sensor_in_clause(ids)
        sensor_clause = f"AND {clause}"
        base_params.extend(extra)

    db = request.app.state.db

    # Step 1: Compute per-sensor mean and stddev in SQL (avoids loading all rows).
    stats_sql = f"""
        SELECT sensor_id,
               MAX(sensor_name) AS sensor_name,
               MAX(sensor_type) AS sensor_type,
               MAX(unit) AS unit,
               AVG(CAST(value AS REAL)) AS mean,
               COUNT(*) AS cnt,
               CASE WHEN COUNT(*) > 1 THEN
                 SQRT(MAX(0, AVG(CAST(value AS REAL) * CAST(value AS REAL))
                            - AVG(CAST(value AS REAL)) * AVG(CAST(value AS REAL))))
               ELSE 0 END AS stddev
        FROM sensor_log
        WHERE timestamp >= ? AND timestamp <= ?
          {sensor_clause}
        GROUP BY sensor_id
    """
    sensor_stats = await _fetchall_as_dicts(db, stats_sql, base_params)

    # Build lookup of sensors with enough samples and non-zero stddev.
    stats_map: dict[str, dict] = {}
    for s in sensor_stats:
        if s["cnt"] >= 10 and s["stddev"] > 0:
            stats_map[s["sensor_id"]] = s

    anomalies: list[dict[str, Any]] = []

    if stats_map:
        # Step 2: For each qualifying sensor, find individual anomalous readings in SQL.
        # We query each sensor separately so we can apply per-sensor mean/stddev thresholds.
        for sid, st in stats_map.items():
            mean = st["mean"]
            stddev = st["stddev"]
            deviation_threshold = z_score_threshold * stddev

            anomaly_sql = f"""
                SELECT timestamp, sensor_id, sensor_name,
                       CAST(value AS REAL) AS value, unit
                FROM sensor_log
                WHERE timestamp >= ? AND timestamp <= ?
                  AND sensor_id = ?
                  AND ABS(CAST(value AS REAL) - ?) > ?
                ORDER BY ABS(CAST(value AS REAL) - ?) DESC
                LIMIT 100
            """
            anomaly_params = [
                base_params[0], base_params[1],
                sid, mean, deviation_threshold, mean,
            ]
            anom_rows = await _fetchall_as_dicts(db, anomaly_sql, anomaly_params)

            for row in anom_rows:
                z = abs(row["value"] - mean) / stddev
                severity = "critical" if z > z_score_threshold * 1.5 else "warning"
                anomalies.append({
                    "timestamp_utc": row["timestamp"],
                    "sensor_id": sid,
                    "sensor_name": row["sensor_name"],
                    "value": row["value"],
                    "unit": row["unit"],
                    "z_score": round(z, 4),
                    "mean": round(mean, 4),
                    "stdev": round(stddev, 4),
                    "severity": severity,
                })

    # Cap result size to prevent enormous payloads
    if len(anomalies) > 500:
        anomalies = anomalies[:500]

    # Returned range reflects the actual extent of data considered, not merely the request window.
    extent_sql = f"""
        SELECT MIN(timestamp) AS min_ts, MAX(timestamp) AS max_ts
        FROM sensor_log
        WHERE timestamp >= ? AND timestamp <= ?
          {sensor_clause}
    """
    extent_rows = await _fetchall_as_dicts(db, extent_sql, base_params)
    if extent_rows and extent_rows[0]["min_ts"]:
        returned_start = _parse_utc(extent_rows[0]["min_ts"])
        returned_end = _parse_utc(extent_rows[0]["max_ts"])
    else:
        returned_start, returned_end = start_dt, end_dt

    return {
        "anomalies": anomalies,
        "z_score_threshold": z_score_threshold,
        "requested_range": _range_meta(start_dt, end_dt),
        "returned_range":  _range_meta(returned_start, returned_end),
    }


# ---------------------------------------------------------------------------
# GET /api/analytics/regression
# ---------------------------------------------------------------------------

@router.get("/regression")
async def get_regression(
    request: Request,
    baseline_days: int = Query(default=30, ge=7, le=90),
    recent_hours: float = Query(default=24.0, ge=1.0, le=168.0),
    threshold_delta: float = Query(default=5.0, ge=1.0, le=50.0),
    start: str | None = Query(default=None),
    end: str | None = Query(default=None),
    sensor_id: str | None = Query(default=None),
    sensor_ids: str | None = Query(default=None),
):
    """Compare recent sensor averages against a rolling baseline to detect thermal regression.

    When ``start`` and ``end`` are supplied they define the *recent* comparison
    window; ``baseline_days`` is the lookback period immediately before ``start``.
    Otherwise ``recent_hours`` defines the recent window ending at now.

    When cpu_load/gpu_load data is present the comparison is done within
    matching coarse load bands (low <25 %, medium 25-75 %, high ≥75 %),
    making the delta immune to workload changes. Falls back to a simple
    whole-period average when no load data is available.
    """
    baseline_days = int(_clamp(baseline_days, 7, 90))
    recent_hours = _clamp(recent_hours, 1.0, 168.0)
    threshold_delta = _clamp(threshold_delta, 1.0, 50.0)
    ids = _parse_sensor_ids(sensor_id, sensor_ids)
    ret_hours = await _retention_hours(request)
    retention_limit = datetime.now(timezone.utc) - timedelta(hours=float(ret_hours))

    sensor_clause = ""
    sensor_params: list = []
    if ids:
        clause, extra = _sensor_in_clause(ids)
        sensor_clause = f"AND {clause}"
        sensor_params = extra

    fmt = "%Y-%m-%d %H:%M:%S"
    now = datetime.now(timezone.utc)

    # If explicit start/end supplied, use them as the recent window and
    # place the baseline immediately before start.
    if isinstance(start, str) and isinstance(end, str):
        try:
            recent_start = _parse_utc(start)
            recent_end = _parse_utc(end)
            if recent_start < recent_end:
                baseline_since = (recent_start - timedelta(days=baseline_days)).strftime(fmt)
                recent_since = recent_start.strftime(fmt)
                recent_hours = (recent_end - recent_start).total_seconds() / 3600
            else:
                baseline_since = (now - timedelta(days=baseline_days)).strftime(fmt)
                recent_since = (now - timedelta(hours=recent_hours)).strftime(fmt)
        except ValueError:
            baseline_since = (now - timedelta(days=baseline_days)).strftime(fmt)
            recent_since = (now - timedelta(hours=recent_hours)).strftime(fmt)
    else:
        baseline_since = (now - timedelta(days=baseline_days)).strftime(fmt)
        recent_since = (now - timedelta(hours=recent_hours)).strftime(fmt)

    # Clamp baseline to retention limit
    baseline_since = max(baseline_since, retention_limit.strftime(fmt))

    db = request.app.state.db

    # Check whether load sensors exist in the baseline period
    load_check = await _fetchall_as_dicts(
        db,
        "SELECT COUNT(*) AS cnt FROM sensor_log WHERE timestamp >= ? AND sensor_type IN ('cpu_load', 'gpu_load')",
        [baseline_since],
    )
    load_band_aware = bool(load_check and load_check[0].get("cnt", 0) > 0)

    regressions: list = []

    if load_band_aware:
        # Per-minute temps bucketed by concurrent load band.
        # Both CTEs use the same $since so band assignment reflects actual
        # load during that minute.
        band_sql = f"""
            WITH minute_load AS (
                SELECT strftime('%Y-%m-%d %H:%M', timestamp) AS minute,
                       AVG(CAST(value AS REAL)) AS avg_load
                FROM sensor_log
                WHERE timestamp >= ?
                  AND sensor_type IN ('cpu_load', 'gpu_load')
                GROUP BY minute
            ),
            banded_temps AS (
                SELECT sl.sensor_id,
                       MAX(sl.sensor_name) AS sensor_name,
                       strftime('%Y-%m-%d %H:%M', sl.timestamp) AS minute,
                       AVG(CAST(sl.value AS REAL)) AS avg_temp,
                       CASE
                           WHEN ml.avg_load < 25 THEN 'low'
                           WHEN ml.avg_load < 75 THEN 'medium'
                           ELSE 'high'
                       END AS load_band
                FROM sensor_log sl
                JOIN minute_load ml
                  ON strftime('%Y-%m-%d %H:%M', sl.timestamp) = ml.minute
                WHERE sl.timestamp >= ?
                  AND sl.sensor_type IN ('cpu_temp', 'gpu_temp', 'hdd_temp', 'case_temp')
                  {sensor_clause}
                GROUP BY sl.sensor_id, minute
            )
            SELECT sensor_id, MAX(sensor_name) AS sensor_name, load_band,
                   AVG(avg_temp) AS avg_value, COUNT(*) AS samples
            FROM banded_temps
            GROUP BY sensor_id, load_band
        """
        baseline_rows = await _fetchall_as_dicts(
            db, band_sql, [baseline_since, baseline_since] + sensor_params
        )
        recent_rows = await _fetchall_as_dicts(
            db, band_sql, [recent_since, recent_since] + sensor_params
        )

        baseline_map = {(r["sensor_id"], r["load_band"]): r for r in baseline_rows}

        for recent in recent_rows:
            key = (recent["sensor_id"], recent["load_band"])
            baseline = baseline_map.get(key)
            if not baseline or baseline["samples"] < 10:
                continue
            delta = recent["avg_value"] - baseline["avg_value"]
            if delta >= threshold_delta:
                severity = "critical" if delta >= threshold_delta * 2 else "warning"
                regressions.append({
                    "sensor_id": recent["sensor_id"],
                    "sensor_name": recent["sensor_name"],
                    "baseline_avg": round(baseline["avg_value"], 1),
                    "recent_avg": round(recent["avg_value"], 1),
                    "delta": round(delta, 1),
                    "severity": severity,
                    "load_band": recent["load_band"],
                    "message": (
                        f"{recent['sensor_name']} is {delta:.1f}°C hotter "
                        f"than its {baseline_days}-day {recent['load_band']}-load average"
                    ),
                })
    else:
        # Fallback: simple whole-period average comparison (no load data)
        simple_sql = f"""
            SELECT sensor_id, MAX(sensor_name) AS sensor_name,
                   AVG(CAST(value AS REAL)) AS avg_value, COUNT(*) AS samples
            FROM sensor_log
            WHERE timestamp >= ?
              AND sensor_type IN ('cpu_temp', 'gpu_temp', 'hdd_temp', 'case_temp')
              {sensor_clause}
            GROUP BY sensor_id
        """
        baseline_rows = await _fetchall_as_dicts(db, simple_sql, [baseline_since] + sensor_params)
        recent_rows = await _fetchall_as_dicts(db, simple_sql, [recent_since] + sensor_params)

        baseline_map = {r["sensor_id"]: r for r in baseline_rows}

        for recent in recent_rows:
            sid = recent["sensor_id"]
            baseline = baseline_map.get(sid)
            if not baseline or baseline["samples"] < 10:
                continue
            delta = recent["avg_value"] - baseline["avg_value"]
            if delta >= threshold_delta:
                severity = "critical" if delta >= threshold_delta * 2 else "warning"
                regressions.append({
                    "sensor_id": sid,
                    "sensor_name": recent["sensor_name"],
                    "baseline_avg": round(baseline["avg_value"], 1),
                    "recent_avg": round(recent["avg_value"], 1),
                    "delta": round(delta, 1),
                    "severity": severity,
                    "message": (
                        f"{recent['sensor_name']} is {delta:.1f}°C hotter "
                        f"than its {baseline_days}-day average"
                    ),
                })

    regressions.sort(key=lambda r: r["delta"], reverse=True)

    return {
        "regressions": regressions,
        "baseline_period_days": baseline_days,
        "recent_period_hours": recent_hours,
        "threshold_delta": threshold_delta,
        "load_band_aware": load_band_aware,
    }


# ---------------------------------------------------------------------------
# GET /api/analytics/correlation
# ---------------------------------------------------------------------------

@router.get("/correlation")
async def get_correlation(
    request: Request,
    x_sensor_id: str | None = Query(default=None),   # preferred
    y_sensor_id: str | None = Query(default=None),   # preferred
    sensor_x: str | None = Query(default=None),      # alias
    sensor_y: str | None = Query(default=None),      # alias
    hours: float | None = Query(default=None, ge=0.1, le=720.0),
    start: str | None = Query(default=None),
    end: str | None = Query(default=None),
):
    """Compute Pearson correlation coefficient between two sensor time series.

    Accepts x_sensor_id/y_sensor_id (preferred) or sensor_x/sensor_y (legacy aliases).
    Pairs are matched by nearest-timestamp bucketing (1-minute buckets).
    """
    sx = x_sensor_id or sensor_x
    sy = y_sensor_id or sensor_y
    if not sx or not sy:
        raise HTTPException(
            status_code=400,
            detail="x_sensor_id and y_sensor_id are required",
        )

    if hours is None and start is None:
        hours = 24.0
    start_dt, end_dt = _resolve_range(hours, start, end)
    fmt = "%Y-%m-%d %H:%M:%S"

    # Sample both sensors in 1-minute buckets to align by time
    sql = """
        SELECT
          sensor_id,
          CAST(strftime('%s', timestamp) AS INTEGER) / 60 AS minute_epoch,
          AVG(CAST(value AS REAL)) AS avg_value
        FROM sensor_log
        WHERE timestamp >= ? AND timestamp <= ?
          AND sensor_id IN (?, ?)
        GROUP BY sensor_id, minute_epoch
        ORDER BY minute_epoch ASC
    """

    db = request.app.state.db
    rows = await _fetchall_as_dicts(
        db, sql, [start_dt.strftime(fmt), end_dt.strftime(fmt), sx, sy]
    )

    x_by_epoch: dict[int, float] = {}
    y_by_epoch: dict[int, float] = {}
    for row in rows:
        epoch = row["minute_epoch"]
        if row["sensor_id"] == sx:
            x_by_epoch[epoch] = row["avg_value"]
        elif row["sensor_id"] == sy:
            y_by_epoch[epoch] = row["avg_value"]

    # Pair on matching epochs
    common_epochs = sorted(set(x_by_epoch) & set(y_by_epoch))
    xs = [x_by_epoch[e] for e in common_epochs]
    ys = [y_by_epoch[e] for e in common_epochs]

    correlation: float | None = None
    if len(xs) >= 2:
        n = len(xs)
        x_mean = sum(xs) / n
        y_mean = sum(ys) / n
        cov = sum((xs[i] - x_mean) * (ys[i] - y_mean) for i in range(n))
        x_std = math.sqrt(sum((v - x_mean) ** 2 for v in xs))
        y_std = math.sqrt(sum((v - y_mean) ** 2 for v in ys))
        if x_std > 0 and y_std > 0:
            correlation = round(cov / (x_std * y_std), 4)

    # Build sampled pairs (max 200 for the response)
    step = max(1, len(common_epochs) // 200)
    samples = [
        {
            "epoch": common_epochs[i],
            "x": round(xs[i], 2),
            "y": round(ys[i], 2),
        }
        for i in range(0, len(common_epochs), step)
    ]

    return {
        "x_sensor_id": sx,
        "y_sensor_id": sy,
        "correlation_coefficient": correlation,
        "sample_count": len(common_epochs),
        "samples": samples,
        "requested_range": _range_meta(start_dt, end_dt),
    }


# ---------------------------------------------------------------------------
# GET /api/analytics/report
# ---------------------------------------------------------------------------

@router.get("/report")
async def get_report(
    request: Request,
    hours: float | None = Query(default=None, ge=0.1, le=720.0),
    start: str | None = Query(default=None),
    end: str | None = Query(default=None),
    sensor_id: str | None = Query(default=None),
    sensor_ids: str | None = Query(default=None),
):
    if hours is None and start is None:
        hours = 24.0
    start_dt, end_dt = _resolve_range(hours, start, end)
    window_hours = (end_dt - start_dt).total_seconds() / 3600
    recent_hours = _clamp(window_hours, 1.0, 168.0)
    start_iso = start_dt.isoformat()
    end_iso = end_dt.isoformat()

    stats_response = await get_stats(
        request, hours=None, start=start_iso, end=end_iso,
        sensor_id=sensor_id, sensor_ids=sensor_ids,
    )
    anomalies_response = await get_anomalies(
        request, hours=None, start=start_iso, end=end_iso,
        sensor_id=sensor_id, sensor_ids=sensor_ids, z_score_threshold=3.0,
    )
    regression_response = await get_regression(
        request, baseline_days=30, recent_hours=recent_hours, threshold_delta=5.0,
        start=start_iso, end=end_iso,
        sensor_id=sensor_id, sensor_ids=sensor_ids,
    )

    stats = stats_response["stats"]
    anomalies = anomalies_response["anomalies"]

    anomaly_counts: dict[str, dict] = {}
    for a in anomalies:
        sid = a["sensor_id"]
        if sid not in anomaly_counts:
            anomaly_counts[sid] = {"sensor_id": sid, "sensor_name": a["sensor_name"], "count": 0}
        anomaly_counts[sid]["count"] += 1

    top_anomalous = sorted(
        anomaly_counts.values(), key=lambda x: x["count"], reverse=True
    )[:3]

    return {
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "window_hours": window_hours,
        "requested_range": _range_meta(start_dt, end_dt),
        "returned_range": stats_response.get("returned_range", _range_meta(start_dt, end_dt)),
        "stats": stats,
        "anomalies": anomalies,
        "top_anomalous_sensors": top_anomalous,
        "regressions": regression_response["regressions"],
    }


# ---------------------------------------------------------------------------
# GET /api/analytics/export
# ---------------------------------------------------------------------------

@router.get("/export")
async def export_data(
    request: Request,
    format: str = Query(default="csv", pattern="^(csv|json)$"),
    hours: float | None = Query(default=None, ge=0.1, le=8760.0),
    start: str | None = Query(default=None),
    end: str | None = Query(default=None),
    sensor_id: str | None = Query(default=None),
    sensor_ids: str | None = Query(default=None),
):
    """Export analytics history data as CSV or JSON for download."""
    start_dt, end_dt = _resolve_range(hours, start, end)

    # Retention gate
    ret_hours = await _retention_hours(request)
    retention_limit = datetime.now(timezone.utc) - timedelta(hours=float(ret_hours))
    effective_start = max(start_dt, retention_limit)

    ids = _parse_sensor_ids(sensor_id, sensor_ids)
    fmt_str = "%Y-%m-%d %H:%M:%S"
    duration = (end_dt - effective_start).total_seconds()
    bucket = _auto_bucket_seconds(duration)

    base_params: list = [bucket, effective_start.strftime(fmt_str), end_dt.strftime(fmt_str)]
    sensor_clause = ""
    if ids:
        clause, extra = _sensor_in_clause(ids)
        sensor_clause = f"AND {clause}"
        base_params.extend(extra)

    sql = f"""
        SELECT
          sensor_id,
          MAX(sensor_name) AS sensor_name,
          MAX(sensor_type) AS sensor_type,
          MAX(unit) AS unit,
          CAST(strftime('%s', timestamp) AS INTEGER) / ? AS bucket_epoch,
          AVG(CAST(value AS REAL)) AS avg_value,
          MIN(CAST(value AS REAL)) AS min_value,
          MAX(CAST(value AS REAL)) AS max_value,
          COUNT(*) AS sample_count
        FROM sensor_log
        WHERE timestamp >= ? AND timestamp <= ?
          {sensor_clause}
        GROUP BY sensor_id, bucket_epoch
        ORDER BY sensor_id, bucket_epoch ASC
    """

    db = request.app.state.db
    rows = await _fetchall_as_dicts(db, sql, base_params)

    # Build flat records
    records: list[dict] = []
    for row in rows:
        ts = datetime.fromtimestamp(
            row["bucket_epoch"] * bucket, tz=timezone.utc
        ).isoformat()
        records.append({
            "timestamp_utc": ts,
            "sensor_id": row["sensor_id"],
            "sensor_name": row["sensor_name"],
            "sensor_type": row["sensor_type"],
            "unit": row["unit"],
            "avg_value": round(row["avg_value"], 4),
            "min_value": round(row["min_value"], 4),
            "max_value": round(row["max_value"], 4),
            "sample_count": row["sample_count"],
        })

    now_tag = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")

    if format == "json":
        content = json.dumps(records, indent=2)
        return Response(
            content=content,
            media_type="application/json",
            headers={
                "Content-Disposition": f'attachment; filename="drivechill-export-{now_tag}.json"',
            },
        )

    # CSV format
    columns = [
        "timestamp_utc", "sensor_id", "sensor_name", "sensor_type",
        "unit", "avg_value", "min_value", "max_value", "sample_count",
    ]
    buf = io.StringIO()
    writer = csv.DictWriter(buf, fieldnames=columns)
    writer.writeheader()
    writer.writerows(records)

    return Response(
        content=buf.getvalue(),
        media_type="text/csv",
        headers={
            "Content-Disposition": f'attachment; filename="drivechill-export-{now_tag}.csv"',
        },
    )
