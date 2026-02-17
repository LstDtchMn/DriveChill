from pathlib import Path

from pydantic_settings import BaseSettings


class Settings(BaseSettings):
    app_name: str = "DriveChill"
    app_version: str = "1.0.0"
    host: str = "0.0.0.0"
    port: int = 8085
    cors_origins: list[str] = ["http://localhost:3000", "http://localhost:8085"]

    # Polling
    sensor_poll_interval: float = 1.0  # seconds
    history_retention_hours: int = 24

    # Data
    data_dir: Path = Path("data")
    db_path: Path = Path("data/drivechill.db")

    # Hardware backend: "auto", "lhm", "lm_sensors", "mock"
    hardware_backend: str = "auto"

    # Temperature units: "C" or "F"
    temp_unit: str = "C"

    model_config = {"env_prefix": "DRIVECHILL_"}


settings = Settings()
