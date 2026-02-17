import asyncio
import json

from fastapi import APIRouter, WebSocket, WebSocketDisconnect

router = APIRouter()


@router.websocket("/api/ws")
async def websocket_endpoint(websocket: WebSocket):
    """WebSocket endpoint for real-time sensor data streaming."""
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
                # Send heartbeat
                await websocket.send_json({"type": "heartbeat"})
                continue

            # Check for alerts
            new_alerts = alert_service.check(snapshot.readings)

            # Apply fan curves
            applied_speeds = await fan_service.apply_curves(snapshot.readings)

            # Send data
            message = {
                "type": "sensor_update",
                "timestamp": snapshot.timestamp.isoformat(),
                "readings": [r.model_dump() for r in snapshot.readings],
                "applied_speeds": applied_speeds,
                "alerts": [a.model_dump() for a in new_alerts] if new_alerts else [],
                "active_alerts": alert_service.active_alerts,
            }

            await websocket.send_text(json.dumps(message, default=str))

    except WebSocketDisconnect:
        pass
    except Exception:
        pass
    finally:
        sensor_service.unsubscribe(queue)
