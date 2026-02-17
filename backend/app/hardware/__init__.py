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
            backend_type = "lhm"
        else:
            backend_type = "lm_sensors"

    if backend_type == "lhm":
        from app.hardware.lhm_backend import LHMBackend
        return LHMBackend()

    if backend_type == "lm_sensors":
        from app.hardware.lm_sensors_backend import LmSensorsBackend
        return LmSensorsBackend()

    from app.hardware.mock_backend import MockBackend
    return MockBackend()
