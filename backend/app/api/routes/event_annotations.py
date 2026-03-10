"""Event annotation CRUD — text markers on trend charts."""

import uuid
from datetime import datetime, timezone

from fastapi import APIRouter, Depends, HTTPException, Request
from pydantic import BaseModel, Field

from app.api.dependencies.auth import require_csrf

router = APIRouter(prefix="/api/annotations", tags=["annotations"])


class AnnotationBody(BaseModel):
    timestamp_utc: str = Field(min_length=1)
    label: str = Field(min_length=1, max_length=200)
    description: str | None = None


def _row_to_dict(row) -> dict:
    return {
        "id": row[0],
        "timestamp_utc": row[2],
        "label": row[3],
        "description": row[4],
        "created_at": row[6],
    }


@router.get("")
async def list_annotations(request: Request, start: str | None = None, end: str | None = None):
    """List annotations, optionally filtered by time range."""
    db = request.app.state.db
    clauses = ["event_type = 'annotation'"]
    params: list[str] = []
    if start:
        clauses.append("timestamp_utc >= ?")
        params.append(start)
    if end:
        clauses.append("timestamp_utc <= ?")
        params.append(end)
    where = " AND ".join(clauses)
    cursor = await db.execute(
        f"SELECT id, event_type, timestamp_utc, label, description, metadata_json, created_at "
        f"FROM event_log WHERE {where} ORDER BY timestamp_utc DESC",
        params,
    )
    rows = await cursor.fetchall()
    return [_row_to_dict(r) for r in rows]


@router.post("", dependencies=[Depends(require_csrf)])
async def create_annotation(body: AnnotationBody, request: Request):
    """Create a new annotation."""
    db = request.app.state.db
    annotation_id = f"ann_{uuid.uuid4().hex[:12]}"
    now = datetime.now(timezone.utc).isoformat()
    await db.execute(
        "INSERT INTO event_log (id, event_type, timestamp_utc, label, description, created_at) "
        "VALUES (?, 'annotation', ?, ?, ?, ?)",
        (annotation_id, body.timestamp_utc, body.label, body.description, now),
    )
    await db.commit()
    cursor = await db.execute(
        "SELECT id, event_type, timestamp_utc, label, description, metadata_json, created_at "
        "FROM event_log WHERE id = ?",
        (annotation_id,),
    )
    row = await cursor.fetchone()
    return _row_to_dict(row)


@router.delete("/{annotation_id}", dependencies=[Depends(require_csrf)], status_code=204)
async def delete_annotation(annotation_id: str, request: Request):
    """Delete an annotation by id. Only deletes event_type='annotation'."""
    db = request.app.state.db
    cursor = await db.execute(
        "DELETE FROM event_log WHERE id = ? AND event_type = 'annotation'",
        (annotation_id,),
    )
    await db.commit()
    if cursor.rowcount == 0:
        raise HTTPException(status_code=404, detail="Annotation not found")
    return None
