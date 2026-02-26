import asyncio
import json
import logging

from fastapi import APIRouter, WebSocket, WebSocketDisconnect

logger = logging.getLogger(__name__)
router = APIRouter()


@router.websocket("/api/ws")
async def websocket_endpoint(websocket: WebSocket):
    """WebSocket endpoint for real-time sensor data streaming.

    This is a pure observer — fan curve evaluation and alert checking
    happen in FanService's independent control loop.  The WebSocket
    just reads the latest state and forwards it to clients.
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

            # Read last applied speeds from the control loop (not computed here)
            applied_speeds = fan_service.last_applied_speeds

            message = {
                "type": "sensor_update",
                "timestamp": snapshot.timestamp.isoformat(),
                "readings": [r.model_dump() for r in snapshot.readings],
                "applied_speeds": applied_speeds,
                "active_alerts": alert_service.active_alerts,
            }

            await websocket.send_text(json.dumps(message, default=str))

    except WebSocketDisconnect:
        pass
    except Exception:
        # M-1: log unexpected errors so they're visible in server output
        logger.exception("WebSocket error")
    finally:
        sensor_service.unsubscribe(queue)
