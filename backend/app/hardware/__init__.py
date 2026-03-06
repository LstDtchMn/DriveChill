import platform

from app.config import settings
from app.hardware.base import HardwareBackend


def get_backend() -> HardwareBackend:
    """Auto-detect and return the appropriate hardware backend."""
    backend_type = settings.hardware_backend

    if backend_type == "mock":
        from app.hardware.mock_backend import MockBackend
        return MockBackend()

    if backend_type == "auto":
        system = platform.system()
        if system == "Windows":
            backend_type = "lhm_direct"
        else:
            # Prefer hwmon if writable PWM nodes exist, fall back to lm_sensors
            from pathlib import Path
            hwmon_root = Path("/sys/class/hwmon")
            try:
                has_pwm = hwmon_root.is_dir() and any(
                    p.name.startswith("pwm") and not p.name.endswith("_enable")
                    for d in hwmon_root.iterdir() if d.is_dir()
                    for p in d.iterdir()
                )
            except PermissionError:
                has_pwm = False
            if has_pwm:
                backend_type = "hwmon"
            else:
                backend_type = "lm_sensors"

    if backend_type == "lhm_direct":
        from app.hardware.lhm_direct_backend import LHMDirectBackend
        return LHMDirectBackend()

    if backend_type == "lhm":
        from app.hardware.lhm_backend import LHMBackend
        return LHMBackend()

    if backend_type == "hwmon":
        from app.hardware.hwmon_backend import HwmonBackend
        return HwmonBackend()

    if backend_type == "liquidctl":
        from app.hardware.liquidctl_backend import LiquidctlBackend
        return LiquidctlBackend()

    if backend_type == "lm_sensors":
        from app.hardware.lm_sensors_backend import LmSensorsBackend
        return LmSensorsBackend()

    from app.hardware.mock_backend import MockBackend
    return MockBackend()
