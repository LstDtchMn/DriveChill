"""DriveChill packaged entry point.

Used by PyInstaller as the application's main script.
Starts uvicorn in a background thread, then runs the system tray on the
main thread (pystray requires the Win32 message loop on the main thread).

Run directly in development (mock mode):
    cd backend
    python drivechill.py

PyInstaller build:
    pyinstaller build/drivechill.spec
"""
from __future__ import annotations

import asyncio
import sys
import threading
from pathlib import Path

# ---------------------------------------------------------------------------
# Path fixup: ensure 'app.*' imports work whether run from repo root,
# from backend/, or from inside the PyInstaller bundle.
# ---------------------------------------------------------------------------
_here = Path(__file__).parent  # backend/ in source tree; bundle root in frozen app
if str(_here) not in sys.path:
    sys.path.insert(0, str(_here))


def _run_uvicorn(server: "uvicorn.Server") -> None:  # type: ignore[name-defined]
    """Target function for the uvicorn background thread."""
    asyncio.run(server.serve())


def main() -> None:
    import uvicorn

    config = uvicorn.Config(
        app="app.main:app",
        host="127.0.0.1",    # local only — no firewall prompt
        port=8085,
        loop="asyncio",      # explicit asyncio (uvloop not available on Windows)
        log_level="warning",
        access_log=False,
    )
    server = uvicorn.Server(config)

    t = threading.Thread(target=_run_uvicorn, args=(server,), daemon=True, name="uvicorn")
    t.start()

    # Main thread: system tray (blocks until Quit)
    from app.tray import run_tray
    run_tray(server)


if __name__ == "__main__":
    main()
