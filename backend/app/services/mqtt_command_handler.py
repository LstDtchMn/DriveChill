"""MQTT command handler — subscribes to command topics and dispatches actions.

Allows external systems (Home Assistant, Node-RED) to control DriveChill
via MQTT: set fan speeds, activate profiles, release fan override.

Runs as a background coroutine started from main.py lifespan.
"""
from __future__ import annotations

import asyncio
import json
import logging
import time
from typing import TYPE_CHECKING
from urllib.parse import urlparse

if TYPE_CHECKING:
    from app.services.notification_channel_service import NotificationChannelService
    from app.services.fan_service import FanService
    from app.db.repositories.profile_repo import ProfileRepo
    from app.hardware.base import HardwareBackend

logger = logging.getLogger(__name__)

MAX_COMMANDS_PER_SECOND = 10


async def _dispatch_command(
    topic: str,
    payload: bytes | bytearray | None,
    prefix: str,
    backend: "HardwareBackend",
    fan_service: "FanService",
    profile_repo: "ProfileRepo",
) -> None:
    """Parse and dispatch a single MQTT command."""
    # Strip the prefix to get the relative command path
    expected_start = f"{prefix}/"
    if not topic.startswith(expected_start):
        logger.debug("MQTT command topic does not match prefix: %s", topic)
        return

    suffix = topic[len(expected_start):]

    if not payload:
        # commands/fans/release needs no payload
        if suffix == "commands/fans/release":
            await fan_service.release_fan_control()
            logger.info("MQTT: released fan control")
            return
        logger.debug("Empty payload for %s", topic)
        return

    try:
        data = json.loads(payload)
    except (json.JSONDecodeError, ValueError):
        logger.warning("Malformed JSON on MQTT command topic %s", topic)
        return

    # commands/fans/{fan_id}/speed
    if suffix.startswith("commands/fans/") and suffix.endswith("/speed"):
        parts = suffix.split("/")
        if len(parts) == 4:  # commands / fans / {fan_id} / speed
            fan_id = parts[2]
            percent = data.get("percent")
            if (
                percent is not None
                and isinstance(percent, (int, float))
                and 0 <= percent <= 100
            ):
                success = await backend.set_fan_speed(fan_id, percent)
                logger.info("MQTT: set fan %s to %s%% (success=%s)", fan_id, percent, success)
            else:
                logger.warning("Invalid percent in MQTT fan speed command: %s", data)
        else:
            logger.debug("Unknown MQTT fan command structure: %s", suffix)

    # commands/profiles/activate
    elif suffix == "commands/profiles/activate":
        profile_id = data.get("profile_id")
        if profile_id and isinstance(profile_id, str):
            result = await profile_repo.activate(profile_id)
            if result:
                # Also apply the profile to the fan service so curves take effect
                profile = await profile_repo.get(profile_id)
                if profile:
                    await fan_service.apply_profile(profile)
                logger.info("MQTT: activated profile %s", profile_id)
            else:
                logger.warning("MQTT: profile %s not found", profile_id)
        else:
            logger.warning("Invalid profile_id in MQTT activate command: %s", data)

    # commands/fans/release (with payload — also accepted)
    elif suffix == "commands/fans/release":
        await fan_service.release_fan_control()
        logger.info("MQTT: released fan control")

    else:
        logger.debug("Unknown MQTT command topic: %s", topic)


async def _subscribe_loop(
    channel_config: dict,
    channel_name: str,
    backend: "HardwareBackend",
    fan_service: "FanService",
    profile_repo: "ProfileRepo",
) -> None:
    """Subscribe to one MQTT channel's command topics and dispatch actions."""
    try:
        import aiomqtt
    except ImportError:
        logger.warning(
            "aiomqtt is not installed — MQTT command subscription unavailable. "
            "Install it with: pip install aiomqtt"
        )
        return

    broker_url = channel_config.get("broker_url", "")
    if not broker_url:
        return

    parsed = urlparse(broker_url)
    hostname = parsed.hostname or "localhost"
    port = parsed.port or (8883 if parsed.scheme == "mqtts" else 1883)
    use_tls = parsed.scheme in ("mqtts", "ssl")
    username = channel_config.get("username") or None
    password = channel_config.get("password") or None
    topic_prefix = channel_config.get("topic_prefix", "drivechill")
    client_id = f"drivechill-sub-{channel_name[:8]}"

    tls_params = aiomqtt.TLSParameters() if use_tls else None

    rate_tracker: list[float] = []
    backoff = 5  # seconds, doubles on consecutive failures, caps at 60

    while True:
        try:
            async with aiomqtt.Client(
                hostname=hostname,
                port=port,
                username=username,
                password=password,
                identifier=client_id,
                tls_params=tls_params,
            ) as client:
                await client.subscribe(f"{topic_prefix}/commands/#")
                logger.info(
                    "MQTT subscribed to %s/commands/# for channel %s",
                    topic_prefix,
                    channel_name,
                )
                backoff = 5  # reset on successful connect

                async for message in client.messages:
                    # Rate limit: sliding window of 1 second
                    now = time.monotonic()
                    rate_tracker[:] = [t for t in rate_tracker if now - t < 1.0]
                    if len(rate_tracker) >= MAX_COMMANDS_PER_SECOND:
                        logger.warning(
                            "MQTT command rate limit exceeded for channel %s, dropping message",
                            channel_name,
                        )
                        continue
                    rate_tracker.append(now)

                    topic = str(message.topic)
                    try:
                        await _dispatch_command(
                            topic, message.payload, topic_prefix,
                            backend, fan_service, profile_repo,
                        )
                    except Exception:
                        logger.debug("MQTT command dispatch error", exc_info=True)
        except asyncio.CancelledError:
            raise  # let the supervisor cancel us cleanly
        except Exception:
            logger.warning(
                "MQTT subscribe loop error for channel %s, retrying in %ds",
                channel_name, backoff, exc_info=True,
            )
            await asyncio.sleep(backoff)
            backoff = min(backoff * 2, 60)


async def create_mqtt_command_handler(
    channel_svc: "NotificationChannelService",
    backend: "HardwareBackend",
    fan_service: "FanService",
    profile_repo: "ProfileRepo",
) -> None:
    """Main loop: watch for mqtt_subscribe-enabled channels, subscribe to each.

    Periodically re-checks channel list. Creates a separate aiomqtt client
    per channel (subscribe clients should be separate from publish clients).
    Cancelled tasks for removed/disabled channels are cleaned up automatically.
    """
    active_tasks: dict[str, asyncio.Task] = {}  # channel_id -> task

    try:
        while True:
            try:
                channels = await channel_svc.list_channels()
            except Exception:
                logger.debug("MQTT command handler: failed to list channels", exc_info=True)
                await asyncio.sleep(30)
                continue

            current_ids: set[str] = set()

            for ch in channels:
                if not ch.enabled or ch.type != "mqtt":
                    continue
                if not ch.config.get("mqtt_subscribe", False):
                    continue

                current_ids.add(ch.id)

                # Start or restart task if needed
                if ch.id not in active_tasks or active_tasks[ch.id].done():
                    task = asyncio.create_task(
                        _subscribe_loop(
                            ch.config, ch.name,
                            backend, fan_service, profile_repo,
                        )
                    )
                    active_tasks[ch.id] = task

            # Cancel tasks for removed/disabled channels
            for cid in list(active_tasks):
                if cid not in current_ids:
                    active_tasks[cid].cancel()
                    del active_tasks[cid]

            await asyncio.sleep(30)  # Re-check channels every 30s
    except asyncio.CancelledError:
        for task in active_tasks.values():
            task.cancel()
        await asyncio.gather(*active_tasks.values(), return_exceptions=True)
