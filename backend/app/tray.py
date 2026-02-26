"""System tray integration for DriveChill.

Runs the app as a Windows system tray icon with:
- Snowflake icon generated via Pillow (no external image files)
- "Open Dashboard" menu item (opens browser, also the default double-click action)
- "Release Fan Control" item (sets all fans to BIOS/auto mode instantly)
- "Switch Profile" submenu (one-click profile activation)
- "Quit" menu item (stops uvicorn + exits cleanly)
- Auto-opens browser 2 seconds after launch
"""
from __future__ import annotations

import json
import logging
import math
import sys
import threading
import time
import urllib.parse
import urllib.request
import webbrowser
from typing import TYPE_CHECKING

import pystray
from PIL import Image, ImageDraw

if TYPE_CHECKING:
    import uvicorn

from app.config import settings

logger = logging.getLogger(__name__)

_PORT = 8085
_DASHBOARD_URL = f"http://localhost:{_PORT}"

# Module-level server reference so menu callbacks can reach it
_server: "uvicorn.Server | None" = None

# Cached profiles for the tray submenu (refreshed on a background timer)
_profiles_cache: list[dict] = []
_profiles_lock = threading.Lock()


def _internal_headers() -> dict[str, str]:
    """Return headers that include the per-process internal auth token."""
    return {
        "Content-Type": "application/json",
        "X-DriveChill-Internal": settings.internal_token,
    }


def _generate_icon(size: int = 64) -> Image.Image:
    """Generate a snowflake icon with Pillow. No external image files required."""
    img = Image.new("RGBA", (size, size), (18, 20, 30, 255))
    draw = ImageDraw.Draw(img)

    cx, cy = size / 2, size / 2
    arm = size / 2 - size / 8
    branch = arm / 3
    lw = max(1, size // 32)

    for i in range(6):
        angle = math.radians(i * 60)
        ex = cx + arm * math.cos(angle)
        ey = cy + arm * math.sin(angle)
        draw.line([(cx, cy), (ex, ey)], fill=(120, 200, 255, 255), width=lw)

        for frac in (0.35, 0.65):
            bx = cx + arm * frac * math.cos(angle)
            by = cy + arm * frac * math.sin(angle)
            for side in (+1, -1):
                ba = angle + side * math.radians(60)
                ex2 = bx + branch * math.cos(ba)
                ey2 = by + branch * math.sin(ba)
                draw.line([(bx, by), (ex2, ey2)], fill=(120, 200, 255, 255), width=max(1, lw // 2))

    r = max(2, size // 20)
    draw.ellipse([(cx - r, cy - r), (cx + r, cy + r)], fill=(200, 230, 255, 255))
    return img


def _open_dashboard(_icon: pystray.Icon, _item: pystray.MenuItem) -> None:
    webbrowser.open(_DASHBOARD_URL)


def _refresh_profiles() -> None:
    """Fetch the current profile list from the local API."""
    global _profiles_cache
    try:
        req = urllib.request.Request(
            f"{_DASHBOARD_URL}/api/profiles",
            headers={"X-DriveChill-Internal": settings.internal_token},
        )
        with urllib.request.urlopen(req, timeout=3) as resp:
            data = json.loads(resp.read())
            with _profiles_lock:
                _profiles_cache = data.get("profiles", [])
    except Exception:
        logger.debug("Could not fetch profiles for tray menu", exc_info=True)


def _start_profile_refresh_timer() -> None:
    """Periodically refresh the profile cache on a background thread."""
    def _loop() -> None:
        # Initial delay to let uvicorn start
        time.sleep(3.0)
        while True:
            _refresh_profiles()
            time.sleep(15)

    threading.Thread(target=_loop, daemon=True, name="tray-profile-refresh").start()


def _activate_profile(profile_id: str) -> None:
    """Activate a profile via the local API."""
    safe_id = urllib.parse.quote(profile_id, safe="")
    try:
        req = urllib.request.Request(
            f"{_DASHBOARD_URL}/api/profiles/{safe_id}/activate",
            method="PUT",
            headers=_internal_headers(),
            data=b"{}",
        )
        with urllib.request.urlopen(req, timeout=5) as resp:
            if resp.status == 200:
                logger.info("Profile %s activated via tray menu", profile_id)
                _refresh_profiles()
    except Exception:
        logger.warning("Failed to activate profile %s from tray menu", profile_id, exc_info=True)


def _make_profile_action(profile_id: str):
    """Create a callback bound to a specific profile ID."""
    def _action(_icon: pystray.Icon, _item: pystray.MenuItem) -> None:
        threading.Thread(
            target=_activate_profile,
            args=(profile_id,),
            daemon=True,
            name="tray-activate",
        ).start()
    return _action


def _profile_menu_items():
    """Dynamically generate profile submenu items.

    Reads from the cached profile list (refreshed on a background timer)
    to avoid blocking the Win32 message-pump thread.
    """
    with _profiles_lock:
        profiles = list(_profiles_cache)
    if not profiles:
        yield pystray.MenuItem("(no profiles)", None, enabled=False)
        return
    for p in profiles:
        pid = p.get("id", "")
        name = p.get("name", "Unknown")
        active = p.get("is_active", False)
        yield pystray.MenuItem(
            name,
            _make_profile_action(pid),
            checked=lambda item, _active=active: _active,
        )


def _release_fan_control(_icon: pystray.Icon, _item: pystray.MenuItem) -> None:
    """Call the local API to release all fans to BIOS/auto mode."""
    try:
        req = urllib.request.Request(
            f"{_DASHBOARD_URL}/api/fans/release",
            method="POST",
            headers=_internal_headers(),
            data=b"{}",
        )
        with urllib.request.urlopen(req, timeout=5) as resp:
            if resp.status == 200:
                logger.info("Fan control released via tray menu")
    except Exception:
        logger.warning("Failed to release fan control from tray menu", exc_info=True)


def _quit(_icon: pystray.Icon, _item: pystray.MenuItem) -> None:
    _icon.visible = False
    _icon.stop()
    if _server is not None:
        _server.should_exit = True
    time.sleep(1.5)
    # sys.exit() only raises SystemExit in the calling thread — on pystray's
    # worker thread this may not terminate the process.  os._exit() is a hard
    # exit that works from any thread and ensures the process actually ends.
    import os
    os._exit(0)


def run_tray(server: "uvicorn.Server") -> None:
    """Start the system tray icon. Blocks the calling thread until Quit is selected.

    Must be called from the main thread — pystray requires the Win32 message loop
    to run on the main thread on Windows.
    """
    global _server
    _server = server

    # Start background profile cache refresher
    _start_profile_refresh_timer()

    # Auto-open browser after a short delay so uvicorn has time to bind
    def _auto_open() -> None:
        time.sleep(2.0)
        webbrowser.open(_DASHBOARD_URL)

    threading.Thread(target=_auto_open, daemon=True).start()

    icon = pystray.Icon(
        name="DriveChill",
        icon=_generate_icon(64),
        title="DriveChill \u2014 Fan Controller",
        menu=pystray.Menu(
            pystray.MenuItem("DriveChill", None, enabled=False),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("Open Dashboard", _open_dashboard, default=True),
            pystray.MenuItem("Release Fan Control", _release_fan_control),
            pystray.MenuItem(
                "Switch Profile",
                pystray.Menu(_profile_menu_items),
            ),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("Quit", _quit),
        ),
    )
    icon.run()  # blocks until _quit calls icon.stop()
