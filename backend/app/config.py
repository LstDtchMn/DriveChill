import os
from pathlib import Path

from pydantic_settings import BaseSettings

# Use %APPDATA%\DriveChill when running on Windows (packaged or dev),
# fall back to a local 'data/' directory on Linux/Docker.
_appdata = os.environ.get("APPDATA")
_default_data_dir = Path(_appdata) / "DriveChill" if _appdata else Path("data")


class Settings(BaseSettings):
    app_name: str = "DriveChill"
    app_version: str = "1.0.0"
    host: str = "127.0.0.1"
    port: int = 8085
    cors_origins: list[str] = ["http://localhost:3000", "http://localhost:8085"]

    # Polling
    sensor_poll_interval: float = 1.0  # seconds
    history_retention_hours: int = 24

    # Data — defaults to %APPDATA%\DriveChill on Windows, data/ on Linux
    data_dir: Path = _default_data_dir
    db_path: Path = _default_data_dir / "drivechill.db"

    # Hardware backend: "auto", "lhm", "lm_sensors", "mock"
    hardware_backend: str = "auto"

    # Temperature units: "C" or "F"
    temp_unit: str = "C"

    model_config = {"env_prefix": "DRIVECHILL_"}


settings = Settings()
