from abc import ABC, abstractmethod

from app.models.sensors import SensorReading


class HardwareBackend(ABC):
    """Abstract base class for hardware monitoring backends."""

    @abstractmethod
    async def initialize(self) -> None:
        """Initialize the hardware backend."""

    @abstractmethod
    async def shutdown(self) -> None:
        """Clean up resources."""

    @abstractmethod
    async def get_sensor_readings(self) -> list[SensorReading]:
        """Read all available sensor data."""

    @abstractmethod
    async def set_fan_speed(self, fan_id: str, speed_percent: float) -> bool:
        """Set a fan's speed (0-100%). Returns True on success."""

    @abstractmethod
    async def get_fan_ids(self) -> list[str]:
        """Get list of controllable fan IDs."""

    @abstractmethod
    def get_backend_name(self) -> str:
        """Return the name of this backend."""
