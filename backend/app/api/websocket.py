import asyncio
import json
import logging
from datetime import datetime, timezone
from http.cookies import SimpleCookie

from fastapi import APIRouter, WebSocket, WebSocketDisconnect

from app.api.dependencies.auth import require_ws_auth, _auth_enabled

logger = logging.getLogger(__name__)
router = APIRouter()

# Revalidate the WS session token every ~60 messages (~1 minute at 1s polling).
_SESSION_REVALIDATE_INTERVAL = 60


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
    # Authenticate before accepting — closes with 1008 if auth fails.
    # require_ws_auth returns None both when auth is disabled (proceed)
    # and when auth fails (websocket already closed). Check client state
    # to distinguish: if the WS was closed, bail out.
    await require_ws_auth(websocket)
    if websocket.client_state.name == "DISCONNECTED":
        return

    await websocket.accept()

    sensor_service = websocket.app.state.sensor_service
    alert_service = websocket.app.state.alert_service
    fan_service = websocket.app.state.fan_service
    fan_test_service = getattr(websocket.app.state, "fan_test_service", None)
    queue = sensor_service.subscribe()

    # Track alert events already sent to this connection so we only push deltas.
    last_event_count = len(alert_service.events)

    # Extract session token for periodic revalidation (long-lived WS connections
    # could outlive a session otherwise).
    ws_session_token: str | None = None
    if _auth_enabled():
        cookie_header = websocket.headers.get("cookie", "")
        cookies = SimpleCookie(cookie_header)
        morsel = cookies.get("drivechill_session")
        ws_session_token = morsel.value if morsel else None
    msg_count = 0

    try:
        while True:
            try:
                snapshot = await asyncio.wait_for(queue.get(), timeout=5.0)
            except asyncio.TimeoutError:
                await websocket.send_json({"type": "heartbeat"})
                continue

            # Periodic session revalidation: close socket if session expired
            # or was invalidated (e.g., user logged out from another tab).
            msg_count += 1
            if (
                ws_session_token
                and msg_count % _SESSION_REVALIDATE_INTERVAL == 0
            ):
                auth_service = websocket.app.state.auth_service
                session = await auth_service.validate_session(ws_session_token)
                if session is None:
                    await websocket.close(
                        code=1008, reason="Session expired"
                    )
                    return

            # Read last applied speeds and safe-mode status from the control loop.
            applied_speeds = fan_service.last_applied_speeds
            safe_mode = fan_service.safe_mode_status
            fan_test = fan_test_service.get_active_progress() if fan_test_service else []

            # Collect new alert events since last send.
            all_events = alert_service.events
            current_event_count = len(all_events)
            new_alert_events: list[dict] = []
            if current_event_count > last_event_count:
                new_alert_events = [
                    e.model_dump(mode="json") for e in all_events[last_event_count:]
                ]
            elif current_event_count < last_event_count:
                # Events were cleared — reset counter
                pass
            last_event_count = current_event_count

            # None sentinel: sensor failures exceeded limit — send empty readings.
            if snapshot is None:
                failure_msg = {
                    "type": "sensor_update",
                    "timestamp": datetime.now(timezone.utc).isoformat(),
                    "readings": [],
                    "applied_speeds": applied_speeds,
                    "alerts": new_alert_events,
                    "active_alerts": alert_service.active_alerts,
                    "safe_mode": safe_mode,
                    "fan_test": [p.model_dump(mode="json") for p in fan_test],
                }
                await websocket.send_text(json.dumps(failure_msg, default=str))
                continue

            # snapshot is a real SensorSnapshot from this point forward
            # (None case handled above and continues the loop).
            if snapshot is None:  # pragma: no cover — unreachable; narrows type
                continue
            message = {
                "type": "sensor_update",
                "timestamp": snapshot.timestamp.isoformat(),
                "readings": [r.model_dump() for r in snapshot.readings],
                "applied_speeds": applied_speeds,
                "alerts": new_alert_events,
                "active_alerts": alert_service.active_alerts,
                "safe_mode": safe_mode,
                "fan_test": [p.model_dump(mode="json") for p in fan_test],
            }

            await websocket.send_text(json.dumps(message, default=str))

    except WebSocketDisconnect:
        pass
    except Exception:
        # M-1: log unexpected errors so they're visible in server output
        logger.exception("WebSocket error")
    finally:
        sensor_service.unsubscribe(queue)
