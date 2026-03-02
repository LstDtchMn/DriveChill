from __future__ import annotations

import math
from collections import defaultdict
from datetime import datetime, timezone

from fastapi import APIRouter, Query, Request

router = APIRouter(prefix="/api/analytics", tags=["analytics"])


def _clamp(value: float, lo: float, hi: float) -> float:
    return max(lo, min(hi, value))


# ---------------------------------------------------------------------------
# GET /api/analytics/history
# ---------------------------------------------------------------------------

@router.get("/history")
async def get_history(
    request: Request,
    hours: float = Query(default=1.0, ge=0.1, le=8760.0),
    sensor_id: str | None = Query(default=None),
    bucket_seconds: int = Query(default=60, ge=10, le=86400),
):
    hours = _clamp(hours, 0.1, 8760.0)
    bucket_seconds = int(_clamp(bucket_seconds, 10, 86400))
    offset = f"-{hours} hours"

    sql = """
        SELECT
          sensor_id,
          MAX(sensor_name) AS sensor_name,
          MAX(sensor_type) AS sensor_type,
          MAX(unit) AS unit,
          CAST(strftime('%s', timestamp) AS INTEGER) / :bucket AS bucket_epoch,
          AVG(CAST(value AS REAL)) AS avg_value,
          MIN(CAST(value AS REAL)) AS min_value,
          MAX(CAST(value AS REAL)) AS max_value,
          COUNT(*) AS sample_count
        FROM sensor_log
        WHERE timestamp >= datetime('now', :offset)
          AND (:sensor_id IS NULL OR sensor_id = :sensor_id)
        GROUP BY sensor_id, bucket_epoch
        ORDER BY bucket_epoch ASC
    """
    db = request.app.state.db
    db.row_factory = __import__("aiosqlite").Row
    async with db.execute(
        sql,
        {"bucket": bucket_seconds, "offset": offset, "sensor_id": sensor_id},
    ) as cursor:
        rows = await cursor.fetchall()

    buckets = []
    for row in rows:
        ts = datetime.fromtimestamp(
            row["bucket_epoch"] * bucket_seconds, tz=timezone.utc
        ).isoformat()
        buckets.append(
            {
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
        )
    return {"buckets": buckets}


# ---------------------------------------------------------------------------
# GET /api/analytics/stats
# ---------------------------------------------------------------------------

@router.get("/stats")
async def get_stats(
    request: Request,
    hours: float = Query(default=24.0, ge=0.1, le=8760.0),
    sensor_id: str | None = Query(default=None),
):
    hours = _clamp(hours, 0.1, 8760.0)
    offset = f"-{hours} hours"

    agg_sql = """
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
        WHERE timestamp >= datetime('now', :offset)
          AND (:sensor_id IS NULL OR sensor_id = :sensor_id)
        GROUP BY sensor_id
    """
    db = request.app.state.db
    db.row_factory = __import__("aiosqlite").Row
    async with db.execute(
        agg_sql, {"offset": offset, "sensor_id": sensor_id}
    ) as cursor:
        agg_rows = await cursor.fetchall()

    stats = []
    for row in agg_rows:
        sid = row["sensor_id"]
        count = row["sample_count"]

        # Fetch sorted values for p95
        p95_sql = """
            SELECT CAST(value AS REAL) AS v
            FROM sensor_log
            WHERE sensor_id = :sid
              AND timestamp >= datetime('now', :offset)
            ORDER BY v ASC
        """
        async with db.execute(p95_sql, {"sid": sid, "offset": offset}) as p95_cursor:
            p95_rows = await p95_cursor.fetchall()

        p95_value: float | None = None
        if p95_rows:
            idx = int(len(p95_rows) * 0.95)
            idx = min(idx, len(p95_rows) - 1)
            p95_value = p95_rows[idx]["v"]

        stats.append(
            {
                "sensor_id": sid,
                "sensor_name": row["sensor_name"],
                "sensor_type": row["sensor_type"],
                "unit": row["unit"],
                "min_value": row["min_value"],
                "max_value": row["max_value"],
                "avg_value": row["avg_value"],
                "p95_value": p95_value,
                "sample_count": count,
            }
        )
    return {"stats": stats}


# ---------------------------------------------------------------------------
# GET /api/analytics/anomalies
# ---------------------------------------------------------------------------

@router.get("/anomalies")
async def get_anomalies(
    request: Request,
    hours: float = Query(default=24.0, ge=0.1, le=720.0),
    z_score_threshold: float = Query(default=3.0, ge=1.0, le=10.0),
):
    hours = _clamp(hours, 0.1, 720.0)
    z_score_threshold = _clamp(z_score_threshold, 1.0, 10.0)
    offset = f"-{hours} hours"

    sql = """
        SELECT timestamp, sensor_id, sensor_name, sensor_type, CAST(value AS REAL) AS value, unit
        FROM sensor_log
        WHERE timestamp >= datetime('now', :offset)
        ORDER BY sensor_id, timestamp ASC
    """
    db = request.app.state.db
    db.row_factory = __import__("aiosqlite").Row
    async with db.execute(sql, {"offset": offset}) as cursor:
        all_rows = await cursor.fetchall()

    # Group by sensor_id
    by_sensor: dict[str, list] = defaultdict(list)
    meta: dict[str, dict] = {}
    for row in all_rows:
        sid = row["sensor_id"]
        by_sensor[sid].append(row)
        if sid not in meta:
            meta[sid] = {
                "sensor_name": row["sensor_name"],
                "sensor_type": row["sensor_type"],
                "unit": row["unit"],
            }

    anomalies = []
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
            if z > z_score_threshold:
                anomalies.append(
                    {
                        "timestamp_utc": row["timestamp"],
                        "sensor_id": sid,
                        "sensor_name": meta[sid]["sensor_name"],
                        "value": row["value"],
                        "unit": meta[sid]["unit"],
                        "z_score": round(z, 4),
                        "mean": round(mean, 4),
                        "stdev": round(stdev, 4),
                    }
                )

    return {"anomalies": anomalies}


# ---------------------------------------------------------------------------
# GET /api/analytics/report
# ---------------------------------------------------------------------------

@router.get("/report")
async def get_report(
    request: Request,
    hours: float = Query(default=24.0, ge=0.1, le=720.0),
):
    hours = _clamp(hours, 0.1, 720.0)

    # Reuse stats and anomalies logic by calling their helpers directly
    from fastapi import Request as _Request  # already imported; just alias
    stats_response = await get_stats(request, hours=hours, sensor_id=None)
    anomalies_response = await get_anomalies(
        request, hours=hours, z_score_threshold=3.0
    )

    stats = stats_response["stats"]
    anomalies = anomalies_response["anomalies"]

    # Count anomalies per sensor
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
        "window_hours": hours,
        "stats": stats,
        "anomalies": anomalies,
        "top_anomalous_sensors": top_anomalous,
    }
