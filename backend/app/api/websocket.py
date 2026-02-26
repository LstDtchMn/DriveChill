import asyncio
import json
import logging
from datetime import datetime, timezone

from fastapi import APIRouter, WebSocket, WebSocketDisconnect

logger = logging.getLogger(__name__)
router = APIRouter()


@router.websocket("/api/ws")
async def websocket_endpoint(websocket: WebSocket):
    """WebSocket endpoint for real-time sensor data streaming.

    This is a pure observer — fan curve evaluation and alert checking
    happen in FanService's independent control loop.  The WebSocket
    just reads the latest state and forwards it to clients.

    A ``None`` sentinel from the sensor queue means the sensor service
    has exceeded its consecutive failure limit.  The WebSocket sends an
    update with empty readings and ``safe_mode.active=true`` so the
    frontend can show an alert banner.
    """
    await websocket.accept()

    sensor_service = websocket.app.state.sensor_service
    alert_service = websocket.app.state.alert_service
    fan_service = websocket.app.state.fan_service
    queue = sensor_service.subscribe()

    try:
        while True:
            try:
                snapshot = await asyncio.wait_for(queue.get(), timeout=5.0)
            except asyncio.TimeoutError:
                await websocket.send_json({"type": "heartbeat"})
                continue

            # Read last applied speeds and safe-mode status from the control loop.
            applied_speeds = fan_service.last_applied_speeds
            safe_mode = fan_service.safe_mode_status

            # None sentinel: sensor failures exceeded limit — send empty readings.
            if snapshot is None:
                failure_msg = {
                    "type": "sensor_update",
                    "timestamp": datetime.now(timezone.utc).isoformat(),
                    "readings": [],
                    "applied_speeds": applied_speeds,
                    "active_alerts": alert_service.active_alerts,
                    "safe_mode": safe_mode,
                }
                await websocket.send_text(json.dumps(failure_msg, default=str))
                continue

            # snapshot is a real SensorSnapshot from this point forward.
            assert snapshot is not None  # None case handled above; narrowing hint
            message = {
                "type": "sensor_update",
                "timestamp": snapshot.timestamp.isoformat(),
                "readings": [r.model_dump() for r in snapshot.readings],
                "applied_speeds": applied_speeds,
                "active_alerts": alert_service.active_alerts,
                "safe_mode": safe_mode,
            }

            await websocket.send_text(json.dumps(message, default=str))

    except WebSocketDisconnect:
        pass
    except Exception:
        # M-1: log unexpected errors so they're visible in server output
        logger.exception("WebSocket error")
    finally:
        sensor_service.unsubscribe(queue)
