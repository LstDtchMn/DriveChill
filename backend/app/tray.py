"""System tray integration for DriveChill.

Runs the app as a Windows system tray icon with:
- Snowflake icon generated via Pillow (no external image files)
- "Open Dashboard" menu item (opens browser, also the default double-click action)
- "Quit" menu item (stops uvicorn + exits cleanly)
- Auto-opens browser 2 seconds after launch
"""
from __future__ import annotations

import math
import sys
import threading
import time
import webbrowser
from typing import TYPE_CHECKING

import pystray
from PIL import Image, ImageDraw

if TYPE_CHECKING:
    import uvicorn

_PORT = 8085
_DASHBOARD_URL = f"http://localhost:{_PORT}"

# Module-level server reference so menu callbacks can reach it
_server: "uvicorn.Server | None" = None


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


def _quit(_icon: pystray.Icon, _item: pystray.MenuItem) -> None:
    _icon.visible = False
    _icon.stop()
    if _server is not None:
        _server.should_exit = True
    time.sleep(1.5)
    sys.exit(0)


def run_tray(server: "uvicorn.Server") -> None:
    """Start the system tray icon. Blocks the calling thread until Quit is selected.

    Must be called from the main thread — pystray requires the Win32 message loop
    to run on the main thread on Windows.
    """
    global _server
    _server = server

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
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("Quit", _quit),
        ),
    )
    icon.run()  # blocks until _quit calls icon.stop()
