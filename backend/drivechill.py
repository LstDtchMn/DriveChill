"""DriveChill packaged entry point.

Used by PyInstaller as the application's main script.
Starts uvicorn in a background thread, then runs the system tray on the
main thread (pystray requires the Win32 message loop on the main thread).

Run directly in development (mock mode):
    cd backend
    python drivechill.py

CLI commands:
    python drivechill.py --backup [--output path]
    python drivechill.py --restore backup.json
    python drivechill.py --restore-db drivechill.db.bak-20260226-143012
    python drivechill.py --install-autostart
    python drivechill.py --remove-autostart

PyInstaller build:
    pyinstaller build/drivechill.spec
"""
from __future__ import annotations

import argparse
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


def _start_server() -> None:
    """Start the DriveChill server (default behavior)."""
    import uvicorn
    from app.config import settings

    ssl_kwargs: dict = {}
    if settings.ssl_certfile and settings.ssl_keyfile:
        ssl_kwargs["ssl_certfile"] = settings.ssl_certfile
        ssl_kwargs["ssl_keyfile"] = settings.ssl_keyfile
    elif settings.ssl_generate_self_signed:
        from app.utils.tls import generate_self_signed_cert
        certfile, keyfile = generate_self_signed_cert(
            settings.data_dir, hostname=settings.host or "localhost",
        )
        ssl_kwargs["ssl_certfile"] = certfile
        ssl_kwargs["ssl_keyfile"] = keyfile

    config = uvicorn.Config(
        app="app.main:app",
        host=settings.host,
        port=settings.port,
        loop="asyncio",      # explicit asyncio (uvloop not available on Windows)
        log_level="warning",
        access_log=False,
        **ssl_kwargs,
    )
    server = uvicorn.Server(config)

    t = threading.Thread(target=_run_uvicorn, args=(server,), daemon=True, name="uvicorn")
    t.start()

    # Main thread: system tray (blocks until Quit)
    from app.tray import run_tray
    run_tray(server)


def _cmd_backup(args: argparse.Namespace) -> None:
    """Export configuration data to a portable JSON file."""
    from app.config import settings
    from app.services.backup_service import export_backup

    output = Path(args.output) if args.output else None
    path = asyncio.run(export_backup(settings.db_path, output))
    print(f"Backup exported to: {path}")


def _cmd_restore(args: argparse.Namespace) -> None:
    """Import configuration data from a JSON backup file."""
    from app.config import settings
    from app.services.backup_service import import_backup

    backup_path = Path(args.backup_file)
    if not backup_path.exists():
        print(f"Error: file not found: {backup_path}", file=sys.stderr)
        sys.exit(1)

    summary = asyncio.run(import_backup(settings.db_path, backup_path))
    print("Backup restored successfully:")
    for key, count in summary.items():
        print(f"  {key}: {count}")


def _cmd_restore_db(args: argparse.Namespace) -> None:
    """Restore a full SQLite database snapshot."""
    from app.config import settings
    from app.services.backup_service import restore_db_snapshot

    snapshot_path = Path(args.snapshot_file)
    try:
        restore_db_snapshot(snapshot_path, settings.db_path)
    except (FileNotFoundError, ValueError) as exc:
        print(f"Error: {exc}", file=sys.stderr)
        sys.exit(1)
    print(f"Database restored from: {snapshot_path}")


def main() -> None:
    parser = argparse.ArgumentParser(
        prog="drivechill",
        description="DriveChill — Temperature-based PC fan speed management",
    )
    group = parser.add_mutually_exclusive_group()
    group.add_argument(
        "--backup",
        action="store_true",
        help="Export profiles, settings, and rules to a JSON backup file",
    )
    group.add_argument(
        "--restore",
        metavar="FILE",
        dest="backup_file",
        help="Restore configuration from a JSON backup file",
    )
    group.add_argument(
        "--restore-db",
        metavar="FILE",
        dest="snapshot_file",
        help="Restore a full SQLite database snapshot (.db.bak file)",
    )
    group.add_argument(
        "--install-autostart",
        action="store_true",
        help="Register DriveChill to start automatically on boot/logon",
    )
    group.add_argument(
        "--remove-autostart",
        action="store_true",
        help="Unregister DriveChill from automatic start",
    )
    parser.add_argument(
        "--output", "-o",
        metavar="PATH",
        help="Output path for --backup (default: drivechill-backup-<timestamp>.json)",
    )

    args = parser.parse_args()

    if args.backup:
        _cmd_backup(args)
    elif args.backup_file:
        _cmd_restore(args)
    elif args.snapshot_file:
        _cmd_restore_db(args)
    elif args.install_autostart:
        from app.services.autostart_service import install_autostart
        print(install_autostart())
    elif args.remove_autostart:
        from app.services.autostart_service import remove_autostart
        print(remove_autostart())
    else:
        _start_server()


if __name__ == "__main__":
    main()
