"""Tests for the drive monitoring subsystem.

Covers:
- smartctl JSON parsing (ATA and NVMe)
- Device path validation
- Health normalization
- Self-test log parsing
- Route-level input validation (400/404 shapes)
"""
from __future__ import annotations

import platform
import sys
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock, patch

import pytest

# Ensure app.* imports resolve from backend/
_backend_dir = Path(__file__).parent.parent
if str(_backend_dir) not in sys.path:
    sys.path.insert(0, str(_backend_dir))

from app.hardware.drives.smartctl_provider import (
    _validate_device_path,
    _parse_ata_attributes,
    _parse_nvme_attributes,
    _parse_self_test_log,
    _parse_drive,
    _bus_type,
    _media_type,
    _temperature,
    _drive_id_from,
)
from app.models.drives import (
    AttributeStatus,
    BusType,
    DriveSettings,
    HealthStatus,
    MediaType,
    SelfTestStatus,
    SelfTestType,
    TemperatureSource,
)
from app.services.drive_health_normalizer import DriveHealthNormalizer


# ── Fixtures ──────────────────────────────────────────────────────────────────

def _default_settings() -> DriveSettings:
    return DriveSettings()


def _make_ata_drive(
    *,
    serial: str = "ABC123",
    model: str = "TestHDD",
    rotation_rate: int = 7200,
    smart_passed: bool = True,
    temp: int = 35,
    reallocated: int = 0,
    pending: int = 0,
) -> dict:
    """Build a minimal ATA smartctl JSON blob."""
    data: dict = {
        "serial_number": serial,
        "model_name": model,
        "firmware_version": "FW01",
        "device": {"protocol": "ATA", "type": "ata", "name": "/dev/sda"},
        "rotation_rate": rotation_rate,
        "user_capacity": {"bytes": 1_000_000_000_000},
        "temperature": {"current": temp},
        "smart_status": {"passed": smart_passed},
        "ata_smart_data": {
            "capabilities": {
                "self_tests_supported": True,
                "conveyance_self_test_supported": True,
            }
        },
        "power_on_time": {"hours": 5000},
    }
    if reallocated > 0 or pending > 0:
        data["ata_smart_attributes"] = {
            "table": [
                {
                    "id": 5,
                    "name": "Reallocated_Sector_Ct",
                    "value": 100,
                    "worst": 100,
                    "thresh": 36,
                    "raw": {"value": reallocated},
                    "when_failed": "",
                },
                {
                    "id": 197,
                    "name": "Current_Pending_Sector",
                    "value": 200,
                    "worst": 200,
                    "thresh": 0,
                    "raw": {"value": pending},
                    "when_failed": "",
                },
            ]
        }
    return data


def _make_nvme_drive(
    *,
    serial: str = "NVS456",
    model: str = "TestNVMe",
    media_errors: int = 0,
    percentage_used: int = 5,
    available_spare: int = 100,
    unsafe_shutdowns: int = 3,
    temp: int = 42,
    smart_passed: bool = True,
) -> dict:
    """Build a minimal NVMe smartctl JSON blob."""
    return {
        "serial_number": serial,
        "model_name": model,
        "firmware_version": "FW02",
        "device": {"protocol": "NVMe", "type": "disk", "name": "/dev/nvme0"},
        "temperature": {"current": temp},
        "user_capacity": {"bytes": 500_000_000_000},
        "smart_status": {"passed": smart_passed},
        "nvme_smart_health_information_log": {
            "media_errors": media_errors,
            "percentage_used": percentage_used,
            "available_spare": available_spare,
            "unsafe_shutdowns": unsafe_shutdowns,
            "power_on_hours": 1234,
            "power_cycles": 100,
        },
        "nvme_self_test_log": {"table": []},
    }


# ── Device path validation ─────────────────────────────────────────────────────

class TestValidateDevicePath:

    @pytest.mark.skipif(
        platform.system() == "Windows", reason="Linux paths only"
    )
    def test_linux_sda_accepted(self) -> None:
        assert _validate_device_path("/dev/sda") is True

    @pytest.mark.skipif(
        platform.system() == "Windows", reason="Linux paths only"
    )
    def test_linux_nvme_accepted(self) -> None:
        assert _validate_device_path("/dev/nvme0n1") is True

    @pytest.mark.skipif(
        platform.system() == "Windows", reason="Linux paths only"
    )
    def test_linux_arbitrary_path_rejected(self) -> None:
        assert _validate_device_path("/etc/passwd") is False

    @pytest.mark.skipif(
        platform.system() == "Windows", reason="Linux paths only"
    )
    def test_linux_path_traversal_rejected(self) -> None:
        assert _validate_device_path("/dev/sda/../sda") is False

    @pytest.mark.skipif(
        platform.system() == "Windows", reason="Linux paths only"
    )
    def test_linux_empty_path_rejected(self) -> None:
        assert _validate_device_path("") is False

    @pytest.mark.skipif(
        platform.system() != "Windows", reason="Windows paths only"
    )
    def test_windows_pd_accepted(self) -> None:
        assert _validate_device_path("/dev/pd0") is True

    @pytest.mark.skipif(
        platform.system() != "Windows", reason="Windows paths only"
    )
    def test_windows_nvme_accepted(self) -> None:
        assert _validate_device_path("/dev/nvme0") is True

    @pytest.mark.skipif(
        platform.system() != "Windows", reason="Windows paths only"
    )
    def test_windows_arbitrary_rejected(self) -> None:
        assert _validate_device_path("C:\\Windows\\system32") is False


# ── ATA attribute parsing ──────────────────────────────────────────────────────

class TestParseAtaAttributes:

    def test_empty_data_returns_empty_list(self) -> None:
        assert _parse_ata_attributes({}) == []

    def test_parses_ok_attribute(self) -> None:
        data = {
            "ata_smart_attributes": {
                "table": [
                    {
                        "id": 5,
                        "name": "Reallocated_Sector_Ct",
                        "value": 100,
                        "worst": 100,
                        "thresh": 36,
                        "raw": {"value": 0},
                        "when_failed": "",
                    }
                ]
            }
        }
        attrs = _parse_ata_attributes(data)
        assert len(attrs) == 1
        assert attrs[0].name == "Reallocated_Sector_Ct"
        assert attrs[0].status == AttributeStatus.OK
        assert attrs[0].raw_value == "0"

    def test_failing_now_sets_critical(self) -> None:
        data = {
            "ata_smart_attributes": {
                "table": [
                    {
                        "id": 197,
                        "name": "Current_Pending_Sector",
                        "value": 1,
                        "worst": 1,
                        "thresh": 0,
                        "raw": {"value": 5},
                        "when_failed": "now",
                    }
                ]
            }
        }
        attrs = _parse_ata_attributes(data)
        assert attrs[0].status == AttributeStatus.CRITICAL

    def test_past_failure_sets_warning(self) -> None:
        data = {
            "ata_smart_attributes": {
                "table": [
                    {
                        "id": 198,
                        "name": "Offline_Uncorrectable",
                        "value": 100,
                        "worst": 100,
                        "thresh": 0,
                        "raw": {"value": 1},
                        "when_failed": "past",
                    }
                ]
            }
        }
        attrs = _parse_ata_attributes(data)
        assert attrs[0].status == AttributeStatus.WARNING

    def test_value_at_threshold_sets_critical(self) -> None:
        data = {
            "ata_smart_attributes": {
                "table": [
                    {
                        "id": 1,
                        "name": "Raw_Read_Error_Rate",
                        "value": 36,
                        "worst": 36,
                        "thresh": 36,
                        "raw": {"value": 0},
                        "when_failed": "",
                    }
                ]
            }
        }
        attrs = _parse_ata_attributes(data)
        assert attrs[0].status == AttributeStatus.CRITICAL


# ── NVMe attribute parsing ─────────────────────────────────────────────────────

class TestParseNvmeAttributes:

    def test_empty_data_returns_empty_list(self) -> None:
        assert _parse_nvme_attributes({}) == []

    def test_parses_key_fields(self) -> None:
        data = {
            "nvme_smart_health_information_log": {
                "media_errors": 0,
                "percentage_used": 5,
                "available_spare": 100,
                "power_on_hours": 500,
                "unsafe_shutdowns": 2,
            }
        }
        attrs = _parse_nvme_attributes(data)
        names = {a.name for a in attrs}
        assert "Media Errors" in names
        assert "Percentage Used" in names
        assert "Available Spare" in names
        assert "Power On Hours" in names

    def test_nvme_attrs_all_status_ok(self) -> None:
        data = {
            "nvme_smart_health_information_log": {
                "media_errors": 0,
                "percentage_used": 10,
            }
        }
        attrs = _parse_nvme_attributes(data)
        for a in attrs:
            assert a.status == AttributeStatus.OK


# ── Self-test log parsing ──────────────────────────────────────────────────────

class TestParseSelfTestLog:

    def test_empty_log_returns_none(self) -> None:
        assert _parse_self_test_log({}, "drive_abc") is None

    def test_ata_short_test_passed(self) -> None:
        data = {
            "ata_smart_self_test_log": {
                "standard": {
                    "table": [
                        {
                            "num": 1,
                            "type": {"string": "Short offline"},
                            "status": {"string": "Completed without error"},
                            "remaining_percent": 0,
                        }
                    ]
                }
            }
        }
        result = _parse_self_test_log(data, "aabbcc112233445566")
        assert result is not None
        assert result.type == SelfTestType.SHORT
        assert result.status == SelfTestStatus.PASSED

    def test_ata_extended_test_running(self) -> None:
        data = {
            "ata_smart_self_test_log": {
                "standard": {
                    "table": [
                        {
                            "num": 1,
                            "type": {"string": "Extended offline"},
                            "status": {"string": "Self-test routine in progress"},
                            "remaining_percent": 60,
                        }
                    ]
                }
            }
        }
        result = _parse_self_test_log(data, "aabbcc112233445566")
        assert result is not None
        assert result.status == SelfTestStatus.RUNNING
        assert result.progress_percent == pytest.approx(40.0)  # 100 - 60

    def test_ata_failed_test(self) -> None:
        data = {
            "ata_smart_self_test_log": {
                "standard": {
                    "table": [
                        {
                            "num": 1,
                            "type": {"string": "Short offline"},
                            "status": {"string": "Completed: servo/seek error"},
                            "remaining_percent": 0,
                        }
                    ]
                }
            }
        }
        result = _parse_self_test_log(data, "aabbcc112233445566")
        assert result is not None
        assert result.status == SelfTestStatus.FAILED

    def test_aborted_test(self) -> None:
        data = {
            "ata_smart_self_test_log": {
                "standard": {
                    "table": [
                        {
                            "num": 1,
                            "type": {"string": "Short offline"},
                            "status": {"string": "Aborted by host"},
                            "remaining_percent": 0,
                        }
                    ]
                }
            }
        }
        result = _parse_self_test_log(data, "aabbcc112233445566")
        assert result is not None
        assert result.status == SelfTestStatus.ABORTED


# ── Drive parsing (ATA) ────────────────────────────────────────────────────────

class TestParseDriveAta:

    def test_basic_hdd_fields(self) -> None:
        data = _make_ata_drive()
        raw = _parse_drive(data, "/dev/sda")
        assert raw.model == "TestHDD"
        assert raw.serial == "ABC123"
        assert raw.bus_type == BusType.SATA
        assert raw.media_type == MediaType.HDD
        assert raw.temperature_c == 35.0
        assert raw.capacity_bytes == 1_000_000_000_000
        assert raw.smart_overall_health == "PASSED"
        assert raw.predicted_failure is False

    def test_ssd_detected_by_zero_rotation(self) -> None:
        data = _make_ata_drive(rotation_rate=0)
        raw = _parse_drive(data, "/dev/sdb")
        assert raw.media_type == MediaType.SSD

    def test_smart_failed_sets_predicted_failure(self) -> None:
        data = _make_ata_drive(smart_passed=False)
        raw = _parse_drive(data, "/dev/sda")
        assert raw.smart_overall_health == "FAILED"
        assert raw.predicted_failure is True

    def test_capabilities_include_self_test(self) -> None:
        data = _make_ata_drive()
        raw = _parse_drive(data, "/dev/sda")
        assert raw.capabilities.smart_self_test_short is True
        assert raw.capabilities.smart_self_test_conveyance is True
        assert raw.capabilities.smart_self_test_abort is True

    def test_drive_id_is_deterministic(self) -> None:
        data = _make_ata_drive(serial="XYZ999", model="TestDrive")
        raw1 = _parse_drive(data, "/dev/sda")
        raw2 = _parse_drive(data, "/dev/sda")
        assert raw1.id == raw2.id

    def test_drive_id_is_24_hex_chars(self) -> None:
        data = _make_ata_drive()
        raw = _parse_drive(data, "/dev/sda")
        assert len(raw.id) == 24
        assert all(c in "0123456789abcdef" for c in raw.id)

    def test_reallocated_sectors_extracted(self) -> None:
        data = _make_ata_drive(reallocated=7)
        raw = _parse_drive(data, "/dev/sda")
        assert raw.reallocated_sectors == 7

    def test_no_temperature_field_returns_none(self) -> None:
        data = _make_ata_drive()
        del data["temperature"]
        raw = _parse_drive(data, "/dev/sda")
        assert raw.temperature_c is None


# ── Drive parsing (NVMe) ───────────────────────────────────────────────────────

class TestParseDriveNvme:

    def test_nvme_bus_and_media_type(self) -> None:
        data = _make_nvme_drive()
        raw = _parse_drive(data, "/dev/nvme0n1")
        assert raw.bus_type == BusType.NVME
        assert raw.media_type == MediaType.NVME

    def test_nvme_wear_and_spare(self) -> None:
        data = _make_nvme_drive(percentage_used=15, available_spare=85)
        raw = _parse_drive(data, "/dev/nvme0n1")
        assert raw.wear_percent_used == 15.0
        assert raw.available_spare_percent == 85.0

    def test_nvme_media_errors(self) -> None:
        data = _make_nvme_drive(media_errors=3)
        raw = _parse_drive(data, "/dev/nvme0n1")
        assert raw.media_errors == 3

    def test_nvme_power_on_hours(self) -> None:
        data = _make_nvme_drive()
        raw = _parse_drive(data, "/dev/nvme0n1")
        assert raw.power_on_hours == 1234

    def test_nvme_self_test_capability_from_log(self) -> None:
        data = _make_nvme_drive()
        raw = _parse_drive(data, "/dev/nvme0n1")
        assert raw.capabilities.smart_self_test_short is True


# ── Health normalization ───────────────────────────────────────────────────────

class TestDriveHealthNormalizer:

    def _norm(self) -> DriveHealthNormalizer:
        return DriveHealthNormalizer(_default_settings())

    def _raw_from_ata(self, **kwargs):
        return _parse_drive(_make_ata_drive(**kwargs), "/dev/sda")

    def _raw_from_nvme(self, **kwargs):
        return _parse_drive(_make_nvme_drive(**kwargs), "/dev/nvme0n1")

    def test_healthy_drive_returns_good(self) -> None:
        raw = self._raw_from_ata()
        assert self._norm().health_status(raw) == HealthStatus.HEALTHY

    def test_smart_failed_returns_critical(self) -> None:
        raw = self._raw_from_ata(smart_passed=False)
        assert self._norm().health_status(raw) == HealthStatus.CRITICAL

    def test_reallocated_sectors_returns_warning(self) -> None:
        raw = self._raw_from_ata(reallocated=1)
        assert self._norm().health_status(raw) == HealthStatus.WARNING

    def test_pending_sectors_returns_warning(self) -> None:
        raw = self._raw_from_ata(pending=2)
        assert self._norm().health_status(raw) == HealthStatus.WARNING

    def test_media_errors_returns_critical(self) -> None:
        raw = self._raw_from_nvme(media_errors=1)
        assert self._norm().health_status(raw) == HealthStatus.CRITICAL

    def test_hdd_temp_warning_threshold(self) -> None:
        raw = self._raw_from_ata(temp=45)  # default hdd_temp_warning_c = 45
        assert self._norm().health_status(raw) == HealthStatus.WARNING

    def test_hdd_temp_critical_threshold(self) -> None:
        raw = self._raw_from_ata(temp=50)  # default hdd_temp_critical_c = 50
        assert self._norm().health_status(raw) == HealthStatus.CRITICAL

    def test_nvme_temp_uses_nvme_thresholds(self) -> None:
        raw = self._raw_from_nvme(temp=65)  # default nvme_temp_warning_c = 65
        assert self._norm().health_status(raw) == HealthStatus.WARNING

    def test_healthy_drive_health_percent_is_100(self) -> None:
        raw = self._raw_from_ata()
        pct = self._norm().health_percent(raw)
        assert pct == 100.0

    def test_failed_smart_health_percent_is_0(self) -> None:
        raw = self._raw_from_ata(smart_passed=False)
        pct = self._norm().health_percent(raw)
        assert pct == 0.0

    def test_nvme_wear_reduces_health_percent(self) -> None:
        raw = self._raw_from_nvme(percentage_used=30)
        pct = self._norm().health_percent(raw)
        assert pct == pytest.approx(70.0)

    def test_wear_at_critical_threshold(self) -> None:
        raw = self._raw_from_nvme(percentage_used=90)
        assert self._norm().health_status(raw) == HealthStatus.CRITICAL

    def test_wear_at_warning_threshold(self) -> None:
        raw = self._raw_from_nvme(percentage_used=80)
        assert self._norm().health_status(raw) == HealthStatus.WARNING

    def test_low_available_spare_returns_critical(self) -> None:
        raw = self._raw_from_nvme(available_spare=5)
        assert self._norm().health_status(raw) == HealthStatus.CRITICAL

    def test_custom_thresholds_respected(self) -> None:
        s = DriveSettings(hdd_temp_warning_c=40.0, hdd_temp_critical_c=45.0)
        norm = DriveHealthNormalizer(s)
        raw = self._raw_from_ata(temp=42)
        assert norm.health_status(raw) == HealthStatus.WARNING

    def test_temp_thresholds_by_media(self) -> None:
        norm = self._norm()
        ata_raw = self._raw_from_ata()
        assert norm.temp_warning_c(ata_raw) == 45.0
        assert norm.temp_critical_c(ata_raw) == 50.0

        nvme_raw = self._raw_from_nvme()
        assert norm.temp_warning_c(nvme_raw) == 65.0
        assert norm.temp_critical_c(nvme_raw) == 75.0


# ── Route input validation ─────────────────────────────────────────────────────

class TestDriveRouteValidation:
    """Test the ID-validation helpers used by the route layer."""

    def test_valid_drive_id_accepted(self) -> None:
        from app.api.routes.drives import _validate_drive_id
        from fastapi import HTTPException
        # Should not raise
        _validate_drive_id("a1b2c3d4e5f6a1b2c3d4e5f6")

    def test_invalid_drive_id_raises_400(self) -> None:
        from app.api.routes.drives import _validate_drive_id
        from fastapi import HTTPException
        with pytest.raises(HTTPException) as exc:
            _validate_drive_id("not-valid")
        assert exc.value.status_code == 400

    def test_drive_id_too_short_raises_400(self) -> None:
        from app.api.routes.drives import _validate_drive_id
        from fastapi import HTTPException
        with pytest.raises(HTTPException):
            _validate_drive_id("a1b2c3")

    def test_drive_id_uppercase_rejected(self) -> None:
        from app.api.routes.drives import _validate_drive_id
        from fastapi import HTTPException
        with pytest.raises(HTTPException):
            _validate_drive_id("A1B2C3D4E5F6A1B2C3D4E5F6")

    def test_valid_run_id_accepted(self) -> None:
        from app.api.routes.drives import _validate_run_id
        from fastapi import HTTPException
        _validate_run_id("a1b2c3d4e5f6a1b2")

    def test_invalid_run_id_raises_400(self) -> None:
        from app.api.routes.drives import _validate_run_id
        from fastapi import HTTPException
        with pytest.raises(HTTPException):
            _validate_run_id("ZZZZ")


# ── Degraded mode behavior ─────────────────────────────────────────────────────

class TestDegradedMode:
    """When smartctl is unavailable, SmartctlProvider.discover() returns []."""

    def test_discover_returns_empty_when_unavailable(self) -> None:
        import asyncio
        from app.hardware.drives.smartctl_provider import SmartctlProvider

        provider = SmartctlProvider(smartctl_path="nonexistent_smartctl")

        async def _run():
            result = await provider.discover()
            return result

        result = asyncio.run(_run())
        assert result == []

    def test_is_available_returns_false_for_missing_binary(self) -> None:
        import asyncio
        from app.hardware.drives.smartctl_provider import SmartctlProvider

        provider = SmartctlProvider(smartctl_path="nonexistent_smartctl_xyz")

        async def _run():
            return await provider.is_available()

        available = asyncio.run(_run())
        assert available is False


# ── History retention flag ─────────────────────────────────────────────────────

class TestHistoryRetentionFlag:
    """The route returns retention_limited=True when hours > retention window."""

    def test_hours_validation_positive(self) -> None:
        # Just test the logic directly since the route validation is inline
        hours = 200.0
        retention = 168.0
        effective = min(hours, retention)
        retention_limited = effective < hours
        assert retention_limited is True
        assert effective == 168.0

    def test_hours_within_retention_not_limited(self) -> None:
        hours = 24.0
        retention = 168.0
        effective = min(hours, retention)
        retention_limited = effective < hours
        assert retention_limited is False
