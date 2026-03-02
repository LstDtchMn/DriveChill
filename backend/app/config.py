import os
import secrets
from pathlib import Path

from pydantic import model_validator
from pydantic_settings import BaseSettings

# Use %APPDATA%\DriveChill when running on Windows (packaged or dev),
# fall back to a local 'data/' directory on Linux/Docker.
_appdata = os.environ.get("APPDATA")
_default_data_dir = Path(_appdata) / "DriveChill" if _appdata else Path("data")

# Internal token: auto-generated per process, NOT a pydantic-settings field.
# Stored at module level so DRIVECHILL_INTERNAL_TOKEN env var cannot override it.
# Shared between tray and auth deps for local API calls.
_INTERNAL_TOKEN: str = secrets.token_hex(32)


class Settings(BaseSettings):
    app_name: str = "DriveChill"
    app_version: str = "1.5.0"
    host: str = "127.0.0.1"
    port: int = 8085
    cors_origins: list[str] = ["http://localhost:3000", "http://localhost:8085"]

    # Polling
    sensor_poll_interval: float = 1.0  # seconds
    history_retention_hours: int = 24

    # Data - defaults to %APPDATA%\DriveChill on Windows, data/ on Linux
    data_dir: Path = _default_data_dir
    db_path: Path = _default_data_dir / "drivechill.db"

    # Hardware backend: "auto", "lhm", "lm_sensors", "mock"
    hardware_backend: str = "auto"

    # Temperature units: "C" or "F"
    temp_unit: str = "C"

    # TLS / HTTPS
    ssl_certfile: str | None = None  # DRIVECHILL_SSL_CERTFILE - path to PEM certificate
    ssl_keyfile: str | None = None  # DRIVECHILL_SSL_KEYFILE - path to PEM private key
    ssl_generate_self_signed: bool = False  # DRIVECHILL_SSL_GENERATE_SELF_SIGNED

    # Authentication
    password: str | None = None  # DRIVECHILL_PASSWORD - used for auto-setup
    session_ttl: str = "8h"  # DRIVECHILL_SESSION_TTL - "15m", "8h", "7d", etc.
    force_auth: bool = False  # DRIVECHILL_FORCE_AUTH - force auth even on localhost
    allow_private_outbound_targets: bool = False  # DRIVECHILL_ALLOW_PRIVATE_OUTBOUND_TARGETS

    model_config = {"env_prefix": "DRIVECHILL_"}

    @model_validator(mode="after")
    def _sync_db_path_with_data_dir(self) -> "Settings":
        """Derive db_path from data_dir when db_path is still at its default.

        This means setting DRIVECHILL_DATA_DIR=/app/data automatically moves
        both the database and TLS certs to that directory — one env var for Docker.
        """
        if self.db_path == _default_data_dir / "drivechill.db":
            self.db_path = self.data_dir / "drivechill.db"
        return self

    @property
    def internal_token(self) -> str:
        """Per-process internal token (not env-overridable)."""
        return _INTERNAL_TOKEN

    @property
    def session_ttl_seconds(self) -> int:
        """Parse session_ttl string (e.g., '8h', '30m', '7d') to seconds."""
        s = self.session_ttl.strip().lower()
        try:
            if s.endswith("d"):
                seconds = int(s[:-1]) * 86400
            elif s.endswith("h"):
                seconds = int(s[:-1]) * 3600
            elif s.endswith("m"):
                seconds = int(s[:-1]) * 60
            else:
                seconds = int(s)
            return seconds if seconds > 0 else 28800
        except (ValueError, TypeError):
            return 28800  # Default: 8 hours

    _LOCALHOST_ADDRS = frozenset({"127.0.0.1", "localhost", "::1"})

    @property
    def auth_required(self) -> bool:
        """Auth is required when not binding to localhost, or when force_auth is set.

        Empty string and 0.0.0.0 both bind all interfaces - auth is required.
        Only explicit localhost addresses bypass auth.
        """
        if self.force_auth:
            return True
        # Empty host binds all interfaces in uvicorn - treat as non-localhost.
        if not self.host:
            return True
        return self.host not in self._LOCALHOST_ADDRS


settings = Settings()
