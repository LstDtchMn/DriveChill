import aiosqlite
from datetime import datetime
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
        await self._db.execute("""
            CREATE TABLE IF NOT EXISTS sensor_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                sensor_id TEXT NOT NULL,
                sensor_name TEXT NOT NULL,
                sensor_type TEXT NOT NULL,
                value REAL NOT NULL,
                unit TEXT
            )
        """)
        await self._db.execute("""
            CREATE INDEX IF NOT EXISTS idx_sensor_log_ts
            ON sensor_log (timestamp)
        """)
        await self._db.execute("""
            CREATE INDEX IF NOT EXISTS idx_sensor_log_sensor
            ON sensor_log (sensor_id, timestamp)
        """)
        await self._db.commit()

    async def shutdown(self) -> None:
        if self._db:
            await self._db.close()

    async def log_snapshot(self, snapshot: SensorSnapshot) -> None:
        """Store a sensor snapshot in the database."""
        if not self._db:
            return

        ts = snapshot.timestamp.isoformat()
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

        cutoff = datetime.now().timestamp() - hours * 3600
        cutoff_iso = datetime.fromtimestamp(cutoff).isoformat()

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

        cutoff = datetime.now().timestamp() - retention_hours * 3600
        cutoff_iso = datetime.fromtimestamp(cutoff).isoformat()

        cursor = await self._db.execute(
            "DELETE FROM sensor_log WHERE timestamp < ?", (cutoff_iso,)
        )
        await self._db.commit()
        return cursor.rowcount

    async def export_csv(self, sensor_id: str | None = None, hours: int = 24) -> str:
        """Export history as CSV string."""
        rows = await self.get_history(sensor_id=sensor_id, hours=hours, limit=100000)
        lines = ["timestamp,sensor_id,sensor_name,sensor_type,value,unit"]
        for r in rows:
            lines.append(
                f"{r['timestamp']},{r['sensor_id']},{r['sensor_name']},"
                f"{r['sensor_type']},{r['value']},{r['unit']}"
            )
        return "\n".join(lines)
