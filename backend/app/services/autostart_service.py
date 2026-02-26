"""Auto-start on boot: register/unregister DriveChill for automatic launch.

Supports:
  - Windows: Task Scheduler (schtasks) — runs at logon with highest privileges
  - Linux: systemd user service — runs via systemctl --user

Works with both PyInstaller frozen builds and development (python drivechill.py).
"""
from __future__ import annotations

import logging
import shutil
import subprocess
import sys
from pathlib import Path

logger = logging.getLogger(__name__)

TASK_NAME = "DriveChill"
SERVICE_NAME = "drivechill"


def _get_executable_command() -> list[str]:
    """Return the command to start DriveChill, adapting for frozen vs dev mode."""
    if getattr(sys, "frozen", False):
        return [sys.executable]
    else:
        return [sys.executable, str(Path(__file__).parent.parent.parent / "drivechill.py")]


# ---------------------------------------------------------------------------
# Windows — Task Scheduler
# ---------------------------------------------------------------------------

def _quote(path: str) -> str:
    """Wrap a path in double quotes if it contains spaces."""
    if " " in path and not path.startswith('"'):
        return f'"{path}"'
    return path


def _win_install(elevated: bool = False) -> str:
    """Register a Windows Task Scheduler task that starts DriveChill at logon.

    By default, creates the task at normal privilege level to avoid a UAC
    prompt on every login.  Pass ``elevated=True`` only if the hardware
    backend requires administrator access (e.g., LHM Direct).
    """
    cmd = _get_executable_command()
    # Quote every component that may contain spaces (e.g. "C:\Program Files\...")
    quoted_parts = [_quote(part) for part in cmd]
    tr_value = " ".join(quoted_parts)

    schtasks_cmd = [
        "schtasks", "/Create",
        "/TN", TASK_NAME,
        "/SC", "ONLOGON",
        "/DELAY", "0000:10",
        "/TR", tr_value,
        "/F",  # force overwrite if already exists
    ]
    if elevated:
        schtasks_cmd.extend(["/RL", "HIGHEST"])

    result = subprocess.run(schtasks_cmd, capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(f"schtasks /Create failed: {result.stderr.strip()}")
    level = "elevated" if elevated else "normal"
    return f"Task Scheduler task '{TASK_NAME}' created (runs at logon, {level} privilege, 10s delay)"


def _win_remove() -> str:
    """Remove the Windows Task Scheduler task."""
    result = subprocess.run(
        ["schtasks", "/Delete", "/TN", TASK_NAME, "/F"],
        capture_output=True, text=True,
    )
    if result.returncode != 0:
        stderr = result.stderr.strip()
        if "cannot find" in stderr.lower() or "does not exist" in stderr.lower():
            return f"Task '{TASK_NAME}' was not registered — nothing to remove"
        raise RuntimeError(f"schtasks /Delete failed: {stderr}")
    return f"Task Scheduler task '{TASK_NAME}' removed"


def _win_status() -> bool:
    """Check if the Windows task exists."""
    result = subprocess.run(
        ["schtasks", "/Query", "/TN", TASK_NAME],
        capture_output=True, text=True,
    )
    return result.returncode == 0


# ---------------------------------------------------------------------------
# Linux — systemd user service
# ---------------------------------------------------------------------------

def _systemd_unit_path() -> Path:
    return Path.home() / ".config" / "systemd" / "user" / f"{SERVICE_NAME}.service"


def _linux_install() -> str:
    """Create and enable a systemd user service for DriveChill."""
    import shlex
    cmd = _get_executable_command()
    # shlex.join properly escapes/quotes paths with spaces for shell use
    exec_start = shlex.join(cmd)

    unit = f"""[Unit]
Description=DriveChill — Temperature-based PC fan speed management
After=default.target

[Service]
Type=simple
ExecStart={exec_start}
Restart=on-failure
RestartSec=5

[Install]
WantedBy=default.target
"""
    unit_path = _systemd_unit_path()
    unit_path.parent.mkdir(parents=True, exist_ok=True)
    unit_path.write_text(unit)

    # Reload, enable, and start
    subprocess.run(["systemctl", "--user", "daemon-reload"], check=True)
    subprocess.run(["systemctl", "--user", "enable", SERVICE_NAME], check=True)
    subprocess.run(["systemctl", "--user", "start", SERVICE_NAME], check=False)

    # Enable lingering so the service runs without an active login session.
    # Without linger, the service stops when the user logs out.
    if shutil.which("loginctl"):
        linger_result = subprocess.run(
            ["loginctl", "enable-linger"], capture_output=True, text=True,
        )
        if linger_result.returncode != 0:
            logger.warning(
                "loginctl enable-linger failed (service may stop on logout): %s",
                linger_result.stderr.strip(),
            )
    else:
        logger.warning(
            "loginctl not found — cannot enable linger. "
            "Service may stop when user logs out."
        )

    return f"systemd user service '{SERVICE_NAME}' installed and enabled"


def _linux_remove() -> str:
    """Disable and remove the systemd user service."""
    subprocess.run(
        ["systemctl", "--user", "stop", SERVICE_NAME],
        capture_output=True,
    )
    subprocess.run(
        ["systemctl", "--user", "disable", SERVICE_NAME],
        capture_output=True,
    )

    unit_path = _systemd_unit_path()
    if unit_path.exists():
        unit_path.unlink()
        subprocess.run(["systemctl", "--user", "daemon-reload"], check=False)
        return f"systemd user service '{SERVICE_NAME}' removed"
    return f"Service '{SERVICE_NAME}' was not installed — nothing to remove"


def _linux_status() -> bool:
    """Check if the systemd user service is enabled."""
    result = subprocess.run(
        ["systemctl", "--user", "is-enabled", SERVICE_NAME],
        capture_output=True, text=True,
    )
    return result.stdout.strip() == "enabled"


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def install_autostart(*, elevated: bool = False) -> str:
    """Register DriveChill to start automatically on boot/logon.

    Args:
        elevated: Windows only — create task with HIGHEST privilege level.
                  Required for hardware backends needing admin access.
    """
    if sys.platform == "win32":
        return _win_install(elevated=elevated)
    else:
        return _linux_install()


def remove_autostart() -> str:
    """Unregister DriveChill from starting automatically."""
    if sys.platform == "win32":
        return _win_remove()
    else:
        return _linux_remove()


def autostart_status() -> bool:
    """Return True if auto-start is currently registered."""
    if sys.platform == "win32":
        return _win_status()
    else:
        return _linux_status()
