import csv
import io
import aiosqlite
from datetime import datetime, timedelta, timezone
from pathlib import Path

from app.models.sensors import SensorReading, SensorSnapshot


class LoggingService:
    """Stores historical sensor data in SQLite for charting and export."""

    def __init__(self, db_path: Path) -> None:
        self._db_path = db_path
        self._db: aiosqlite.Connection | None = None

    async def initialize(self) -> None:
        self._db_path.parent.mkdir(parents=True, exist_ok=True)
        self._db = await aiosqlite.connect(str(self._db_path))
        # Table and indexes are created by the migration framework
        # (001_initial_schema.sql).  WAL mode for better concurrent reads.
        await self._db.execute("PRAGMA journal_mode=WAL")

    async def shutdown(self) -> None:
        if self._db:
            await self._db.close()

    async def log_snapshot(self, snapshot: SensorSnapshot) -> None:
        """Store a sensor snapshot in the database."""
        if not self._db:
            return

        # M-4: normalise to UTC so ISO string comparisons work correctly
        # across DST boundary changes.
        ts = snapshot.timestamp.astimezone(timezone.utc).isoformat()
        rows = [
            (ts, r.id, r.name, r.sensor_type.value, r.value, r.unit)
            for r in snapshot.readings
        ]
        await self._db.executemany(
            "INSERT INTO sensor_log (timestamp, sensor_id, sensor_name, sensor_type, value, unit) "
            "VALUES (?, ?, ?, ?, ?, ?)",
            rows,
        )
        await self._db.commit()

    async def get_history(
        self,
        sensor_id: str | None = None,
        hours: int = 1,
        limit: int = 5000,
    ) -> list[dict]:
        """Retrieve historical data."""
        if not self._db:
            return []

        # M-4: use UTC for cutoff so comparisons with UTC-stored timestamps
        # remain correct across DST changes.
        cutoff_iso = (datetime.now(timezone.utc) - timedelta(hours=hours)).isoformat()

        if sensor_id:
            cursor = await self._db.execute(
                "SELECT timestamp, sensor_id, sensor_name, sensor_type, value, unit "
                "FROM sensor_log WHERE sensor_id = ? AND timestamp >= ? "
                "ORDER BY timestamp DESC LIMIT ?",
                (sensor_id, cutoff_iso, limit),
            )
        else:
            cursor = await self._db.execute(
                "SELECT timestamp, sensor_id, sensor_name, sensor_type, value, unit "
                "FROM sensor_log WHERE timestamp >= ? "
                "ORDER BY timestamp DESC LIMIT ?",
                (cutoff_iso, limit),
            )

        rows = await cursor.fetchall()
        return [
            {
                "timestamp": row[0],
                "sensor_id": row[1],
                "sensor_name": row[2],
                "sensor_type": row[3],
                "value": row[4],
                "unit": row[5],
            }
            for row in rows
        ]

    async def prune(self, retention_hours: int) -> int:
        """Delete records older than retention_hours. Returns count deleted."""
        if not self._db:
            return 0

        cutoff_iso = (datetime.now(timezone.utc) - timedelta(hours=retention_hours)).isoformat()

        cursor = await self._db.execute(
            "DELETE FROM sensor_log WHERE timestamp < ?", (cutoff_iso,)
        )
        await self._db.commit()
        return cursor.rowcount

    async def export_csv(self, sensor_id: str | None = None, hours: int = 24) -> str:
        """Export history as CSV string.

        C-4: uses csv.DictWriter so field values with commas/newlines are
        properly quoted rather than corrupting the output.
        """
        rows = await self.get_history(sensor_id=sensor_id, hours=hours, limit=100000)
        buf = io.StringIO()
        fieldnames = ["timestamp", "sensor_id", "sensor_name", "sensor_type", "value", "unit"]
        writer = csv.DictWriter(buf, fieldnames=fieldnames, lineterminator="\n")
        writer.writeheader()
        writer.writerows(rows)
        return buf.getvalue()
