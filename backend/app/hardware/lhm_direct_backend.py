"""LibreHardwareMonitor direct DLL backend for Windows.

Loads LibreHardwareMonitorLib.dll in-process via pythonnet (clr).
No separate LHM process or HTTP server is required.

Prerequisites
-------------
- Windows (LHM kernel drivers are Windows-only)
- Administrator privileges (required by LHM to load kernel drivers)
- pythonnet >= 3.0.0  (pip install pythonnet)
- LibreHardwareMonitorLib.dll placed in backend/lib/
  Download from: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases
"""

from __future__ import annotations

import asyncio
import ctypes
import logging
import sys
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from typing import Any

from app.hardware.base import HardwareBackend
from app.models.sensors import SensorReading, SensorType

logger = logging.getLogger(__name__)

# ---------------------------------------------------------------------------
# DLL discovery
# ---------------------------------------------------------------------------

_DLL_NAME = "LibreHardwareMonitorLib.dll"

# __file__ = .../backend/app/hardware/lhm_direct_backend.py
# parent.parent.parent = .../backend/
_BACKEND_ROOT = Path(__file__).parent.parent.parent

_DLL_SEARCH_PATHS: list[Path] = [
    _BACKEND_ROOT / "lib" / _DLL_NAME,        # canonical: backend/lib/
    Path(__file__).parent / _DLL_NAME,        # next to this source file
    Path(sys.executable).parent / _DLL_NAME,  # next to python.exe (PyInstaller)
]


def _find_dll() -> Path | None:
    for candidate in _DLL_SEARCH_PATHS:
        if candidate.exists():
            return candidate
    return None


# ---------------------------------------------------------------------------
# Admin elevation check
# ---------------------------------------------------------------------------

def _is_elevated() -> bool:
    if sys.platform != "win32":
        return False
    try:
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except AttributeError:
        return False


# ---------------------------------------------------------------------------
# Sensor classification
# ---------------------------------------------------------------------------

def _classify(hw_type_name: str, lhm_sensor_type_name: str) -> tuple[SensorType | None, str]:
    """Map LHM HardwareType + SensorType names to our SensorType enum + unit."""
    hw = hw_type_name.lower()
    st = lhm_sensor_type_name.lower()

    if st == "temperature":
        if "gpu" in hw:
            return SensorType.GPU_TEMP, "°C"
        if "storage" in hw:
            return SensorType.HDD_TEMP, "°C"
        if "motherboard" in hw or "superio" in hw:
            return SensorType.CASE_TEMP, "°C"
        return SensorType.CPU_TEMP, "°C"

    if st == "load":
        if "gpu" in hw:
            return SensorType.GPU_LOAD, "%"
        return SensorType.CPU_LOAD, "%"

    if st == "fan":
        return SensorType.FAN_RPM, "RPM"

    if st == "control":
        return SensorType.FAN_PERCENT, "%"

    # Power, Voltage, Clock, etc. — not modelled; skip
    return None, ""


# ---------------------------------------------------------------------------
# Stable sensor ID construction
# ---------------------------------------------------------------------------

def _make_sensor_id(hw_identifier: str, sensor_identifier: str) -> str:
    """Build a stable sensor ID from LHM identifier strings.

    LHM identifiers look like: /lpc/nct6798d/fan/0
    Result: lhm_direct_lpc_nct6798d_fan_0
    """
    raw = f"{hw_identifier}{sensor_identifier}"
    return "lhm_direct_" + raw.lstrip("/").replace("/", "_")


# ---------------------------------------------------------------------------
# Synchronous worker functions (always called from the executor thread)
# ---------------------------------------------------------------------------

def _sync_collect_readings(
    computer: Any,
) -> tuple[list[SensorReading], dict[str, Any]]:
    """Update all hardware and return (readings, controls).

    Must be called from the dedicated executor thread — not the event loop.
    """
    readings: list[SensorReading] = []
    controls: dict[str, Any] = {}

    def _process(hw: Any) -> None:
        hw.Update()
        hw_type_name = str(hw.HardwareType)
        hw_identifier = str(hw.Identifier)

        for sensor in hw.Sensors:
            st_name = str(sensor.SensorType)
            s_type, unit = _classify(hw_type_name, st_name)
            if s_type is None:
                continue

            sensor_id = _make_sensor_id(hw_identifier, str(sensor.Identifier))
            raw = sensor.Value
            value = float(raw) if raw is not None else 0.0
            min_val = float(sensor.Min) if sensor.Min is not None else 0.0
            max_val = float(sensor.Max) if sensor.Max is not None else 100.0

            readings.append(
                SensorReading(
                    id=sensor_id,
                    name=str(sensor.Name),
                    sensor_type=s_type,
                    value=value,
                    min_value=min_val,
                    max_value=max_val,
                    unit=unit,
                )
            )

            if st_name.lower() == "control" and sensor.Control is not None:
                controls[sensor_id] = sensor.Control

        for sub in hw.SubHardware:
            _process(sub)

    for hardware in computer.Hardware:
        _process(hardware)

    return readings, controls


def _sync_set_fan_speed(control: Any, speed_percent: float) -> bool:
    try:
        control.SetSoftware(max(0.0, min(100.0, speed_percent)))
        return True
    except Exception as exc:
        logger.error("Failed to set fan speed: %s", exc)
        return False


def _sync_set_fan_auto(control: Any) -> bool:
    try:
        control.SetDefault()
        return True
    except Exception as exc:
        logger.error("Failed to reset fan to auto: %s", exc)
        return False


# ---------------------------------------------------------------------------
# Backend class
# ---------------------------------------------------------------------------

class LHMDirectBackend(HardwareBackend):
    """Hardware backend using LibreHardwareMonitorLib.dll directly via pythonnet.

    A single-thread ThreadPoolExecutor is used for all .NET calls to ensure
    the Computer object (which is not thread-safe) is only ever touched from
    one thread.
    """

    def __init__(self, dll_path: Path | None = None) -> None:
        self._dll_path = dll_path or _find_dll()
        self._executor: ThreadPoolExecutor | None = None
        self._computer: Any = None
        # sensor_id → LHM IControl object
        self._controls: dict[str, Any] = {}
        self._initialized = False

    # ------------------------------------------------------------------
    # Lifecycle
    # ------------------------------------------------------------------

    async def initialize(self) -> None:
        if not _is_elevated():
            raise PermissionError(
                "LibreHardwareMonitor requires administrator privileges to access "
                "hardware sensors and fan controllers. "
                "Right-click DriveChill and choose 'Run as administrator', or launch "
                "from an elevated Command Prompt / PowerShell."
            )

        if self._dll_path is None or not self._dll_path.exists():
            searched = "\n  ".join(str(p) for p in _DLL_SEARCH_PATHS)
            raise FileNotFoundError(
                f"{_DLL_NAME} not found. Searched:\n  {searched}\n\n"
                "Download the DLL from:\n"
                "  https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases\n"
                "Extract the ZIP and copy LibreHardwareMonitorLib.dll to:\n"
                f"  {_BACKEND_ROOT / 'lib' / _DLL_NAME}"
            )

        try:
            import clr  # noqa: F401
        except ImportError as exc:
            raise ImportError(
                "pythonnet is not installed. Install it with:\n"
                "  pip install pythonnet>=3.0.0"
            ) from exc

        self._executor = ThreadPoolExecutor(
            max_workers=1, thread_name_prefix="lhm_direct"
        )

        loop = asyncio.get_event_loop()
        await loop.run_in_executor(self._executor, self._sync_open)
        self._initialized = True
        logger.info("LHMDirectBackend initialized — DLL: %s", self._dll_path)

    def _sync_open(self) -> None:
        """Open the LHM Computer object. Runs on the executor thread."""
        import clr

        clr.AddReference(str(self._dll_path))
        from LibreHardwareMonitor.Hardware import Computer  # type: ignore[import]

        computer = Computer()
        computer.IsCpuEnabled = True
        computer.IsGpuEnabled = True
        computer.IsStorageEnabled = True
        computer.IsMotherboardEnabled = True   # needed for fan headers on most boards
        computer.IsControllerEnabled = True    # USB/HID fan controllers
        computer.IsNetworkEnabled = False
        computer.IsMemoryEnabled = True
        computer.Open()
        self._computer = computer

    async def shutdown(self) -> None:
        if not self._initialized:
            return
        loop = asyncio.get_event_loop()
        if self._executor and self._computer is not None:
            try:
                await loop.run_in_executor(self._executor, self._computer.Close)
            except Exception as exc:
                logger.warning("Error closing LHM Computer: %s", exc)
        if self._executor:
            self._executor.shutdown(wait=True)
        self._computer = None
        self._executor = None
        self._initialized = False
        logger.info("LHMDirectBackend shut down")

    # ------------------------------------------------------------------
    # Sensor readings
    # ------------------------------------------------------------------

    async def get_sensor_readings(self) -> list[SensorReading]:
        if not self._initialized or self._computer is None:
            return []
        loop = asyncio.get_event_loop()
        readings, controls = await loop.run_in_executor(
            self._executor,
            _sync_collect_readings,
            self._computer,
        )
        # Merge so fans that temporarily disappear remain addressable
        self._controls.update(controls)
        return readings

    # ------------------------------------------------------------------
    # Fan control
    # ------------------------------------------------------------------

    async def set_fan_speed(self, fan_id: str, speed_percent: float) -> bool:
        control = self._controls.get(fan_id)
        if control is None:
            logger.warning(
                "set_fan_speed: unknown fan_id '%s'. Known: %s",
                fan_id,
                list(self._controls.keys()),
            )
            return False
        loop = asyncio.get_event_loop()
        return await loop.run_in_executor(
            self._executor, _sync_set_fan_speed, control, speed_percent
        )

    async def set_fan_auto(self, fan_id: str) -> bool:
        """Return a fan to automatic (BIOS/firmware) control."""
        control = self._controls.get(fan_id)
        if control is None:
            return False
        loop = asyncio.get_event_loop()
        return await loop.run_in_executor(
            self._executor, _sync_set_fan_auto, control
        )

    async def release_fan_control(self) -> None:
        """Return all controlled fans to BIOS/firmware automatic mode."""
        loop = asyncio.get_event_loop()
        for fan_id, control in list(self._controls.items()):
            try:
                await loop.run_in_executor(
                    self._executor, _sync_set_fan_auto, control
                )
            except Exception as exc:
                logger.warning("Failed to release fan %s to auto: %s", fan_id, exc)

    async def get_fan_ids(self) -> list[str]:
        return list(self._controls.keys())

    # ------------------------------------------------------------------
    # Metadata
    # ------------------------------------------------------------------

    def get_backend_name(self) -> str:
        return "LibreHardwareMonitor (direct DLL)"
