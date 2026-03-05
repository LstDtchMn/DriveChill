"""smartctl-based drive data provider."""
from __future__ import annotations

import asyncio
import hashlib
import json
import logging
import platform
import re
import sys
import time
from typing import Any

from app.hardware.drives.base import DriveProvider, ProviderError
from app.models.drives import (
    AttributeSourceKind,
    AttributeStatus,
    BusType,
    DriveCapabilitySet,
    DriveRawAttribute,
    DriveRawData,
    DriveSelfTestRun,
    HealthSource,
    MediaType,
    SelfTestStatus,
    SelfTestType,
    TemperatureSource,
)

logger = logging.getLogger(__name__)

# ── Device path allowlists ─────────────────────────────────────────────────

_WINDOWS_DEVICE_RE = re.compile(r"^(/dev/pd\d+|/dev/nvme\d+)$")
_LINUX_DEVICE_RE = re.compile(
    r"^(/dev/sd[a-z]+|/dev/hd[a-z]+|/dev/nvme\d+n\d+|/dev/disk/by-id/[A-Za-z0-9._:-]+)$"
)


def _validate_device_path(path: str) -> bool:
    if platform.system() == "Windows":
        return bool(_WINDOWS_DEVICE_RE.match(path))
    return bool(_LINUX_DEVICE_RE.match(path))


# ── smartctl output parsing helpers ────────────────────────────────────────

def _sanitize_stderr(raw: bytes, max_len: int = 500) -> str:
    text = raw.decode("utf-8", errors="replace").strip()
    return text[:max_len] if len(text) > max_len else text


def _drive_id_from(serial: str, model: str, bus: BusType, device_path: str) -> str:
    if serial:
        key = f"{serial}|{model}|{bus.value}"
    else:
        key = f"noserial|{model}|{device_path}"
    return hashlib.sha256(key.encode()).hexdigest()[:24]


def _bus_type(data: dict[str, Any]) -> BusType:
    transport = (data.get("device", {}).get("protocol", "") or "").lower()
    type_str = (data.get("device", {}).get("type", "") or "").lower()
    name = (data.get("device", {}).get("name", "") or "").lower()
    if "nvme" in transport or "nvme" in type_str or "nvme" in name:
        return BusType.NVME
    if "sata" in transport or "ata" in type_str:
        return BusType.SATA
    if "usb" in transport:
        return BusType.USB
    return BusType.UNKNOWN


def _media_type(data: dict[str, Any], bus: BusType) -> MediaType:
    if bus == BusType.NVME:
        return MediaType.NVME
    rpm = data.get("rotation_rate")
    if rpm == 0:
        return MediaType.SSD
    if isinstance(rpm, int) and rpm > 0:
        return MediaType.HDD
    return MediaType.UNKNOWN


def _capacity_bytes(data: dict[str, Any]) -> int:
    user_cap = data.get("user_capacity", {})
    if isinstance(user_cap, dict):
        return int(user_cap.get("bytes", 0))
    return 0


def _temperature(data: dict[str, Any]) -> float | None:
    temp = data.get("temperature", {})
    if isinstance(temp, dict):
        val = temp.get("current")
        if val is not None:
            try:
                return float(val)
            except (TypeError, ValueError):
                pass
    return None


def _parse_ata_attributes(data: dict[str, Any]) -> list[DriveRawAttribute]:
    ata_smart = data.get("ata_smart_attributes", {})
    table = ata_smart.get("table", [])
    attrs: list[DriveRawAttribute] = []
    for row in table:
        try:
            raw_str = str(row.get("raw", {}).get("value", ""))
            val = row.get("value")
            worst = row.get("worst")
            thresh = row.get("thresh")
            failed = row.get("when_failed", "")
            # Determine status
            if failed == "now":
                status = AttributeStatus.CRITICAL
            elif failed in ("past", "always_failing"):
                status = AttributeStatus.WARNING
            elif val is not None and thresh is not None and int(val) <= int(thresh):
                status = AttributeStatus.CRITICAL
            else:
                status = AttributeStatus.OK

            attrs.append(
                DriveRawAttribute(
                    key=str(row.get("id", "")),
                    name=str(row.get("name", "")),
                    normalized_value=int(val) if val is not None else None,
                    worst_value=int(worst) if worst is not None else None,
                    threshold=int(thresh) if thresh is not None else None,
                    raw_value=raw_str,
                    status=status,
                    source_kind=AttributeSourceKind.ATA_SMART,
                )
            )
        except (TypeError, ValueError, KeyError):
            continue
    return attrs


def _parse_nvme_attributes(data: dict[str, Any]) -> list[DriveRawAttribute]:
    health = data.get("nvme_smart_health_information_log", {})
    attrs: list[DriveRawAttribute] = []
    _field_map = {
        "critical_warning": "Critical Warning",
        "temperature": "Temperature",
        "available_spare": "Available Spare",
        "available_spare_threshold": "Available Spare Threshold",
        "percentage_used": "Percentage Used",
        "data_units_read": "Data Units Read",
        "data_units_written": "Data Units Written",
        "host_reads": "Host Reads",
        "host_writes": "Host Writes",
        "controller_busy_time": "Controller Busy Time",
        "power_cycles": "Power Cycles",
        "power_on_hours": "Power On Hours",
        "unsafe_shutdowns": "Unsafe Shutdowns",
        "media_errors": "Media Errors",
        "num_err_log_entries": "Error Log Entries",
    }
    for key, name in _field_map.items():
        val = health.get(key)
        if val is None:
            continue
        try:
            attrs.append(
                DriveRawAttribute(
                    key=key,
                    name=name,
                    raw_value=str(val),
                    status=AttributeStatus.OK,
                    source_kind=AttributeSourceKind.NVME_SMART,
                )
            )
        except (TypeError, ValueError):
            continue
    return attrs


def _parse_self_test_log(data: dict[str, Any], drive_id: str) -> DriveSelfTestRun | None:
    """Extract the most recent self-test entry from the log."""
    # ATA
    ata_log = data.get("ata_smart_self_test_log", {})
    table = (ata_log.get("standard", {}) or {}).get("table", []) or []
    # NVMe
    nvme_log = data.get("nvme_self_test_log", {})
    nvme_table = nvme_log.get("table", []) or []
    table = table + nvme_table

    if not table:
        return None

    latest = table[0]
    try:
        test_type_str = str(latest.get("type", {}).get("string", "") or "").lower()
        if "short" in test_type_str:
            test_type = SelfTestType.SHORT
        elif "extended" in test_type_str or "long" in test_type_str:
            test_type = SelfTestType.EXTENDED
        elif "conveyance" in test_type_str:
            test_type = SelfTestType.CONVEYANCE
        else:
            test_type = SelfTestType.SHORT

        status_str = str(latest.get("status", {}).get("string", "") or "").lower()
        if "in progress" in status_str or "running" in status_str:
            test_status = SelfTestStatus.RUNNING
        elif "completed without error" in status_str or "passed" in status_str:
            test_status = SelfTestStatus.PASSED
        elif "aborted" in status_str or "interrupted" in status_str:
            test_status = SelfTestStatus.ABORTED
        elif "failed" in status_str or "error" in status_str:
            test_status = SelfTestStatus.FAILED
        else:
            test_status = SelfTestStatus.UNKNOWN

        # Try to extract remaining percent
        remaining = latest.get("remaining_percent")
        progress = None
        if test_status == SelfTestStatus.RUNNING and remaining is not None:
            try:
                progress = 100.0 - float(remaining)
            except (TypeError, ValueError):
                pass

        return DriveSelfTestRun(
            id=f"smartctl_{drive_id}_{latest.get('num', 0)}",
            drive_id=drive_id,
            type=test_type,
            status=test_status,
            progress_percent=progress,
            started_at="unknown",
            finished_at=None if test_status == SelfTestStatus.RUNNING else "unknown",
            failure_message=status_str if test_status == SelfTestStatus.FAILED else None,
        )
    except (TypeError, ValueError, KeyError):
        return None


def _capabilities(data: dict[str, Any], bus: BusType) -> DriveCapabilitySet:
    has_self_test = False
    has_abort = False
    has_conveyance = False

    ata_caps = data.get("ata_smart_data", {}).get("capabilities", {})
    if ata_caps.get("self_tests_supported"):
        has_self_test = True
    if ata_caps.get("conveyance_self_test_supported"):
        has_conveyance = True

    if bus == BusType.NVME:
        # NVMe standard self-test is optional — check if the log exists
        if data.get("nvme_self_test_log") is not None:
            has_self_test = True

    # Abort is supported when self-tests are supported on ATA
    # NVMe self-test abort may be inferred from nvme_self_test_log presence
    if has_self_test:
        has_abort = True

    smart_status = data.get("smart_status", {})
    smart_read = smart_status.get("passed") is not None

    return DriveCapabilitySet(
        smart_read=smart_read,
        smart_self_test_short=has_self_test,
        smart_self_test_extended=has_self_test,
        smart_self_test_conveyance=has_conveyance,
        smart_self_test_abort=has_abort,
        temperature_source=TemperatureSource.SMARTCTL if _temperature(data) is not None else TemperatureSource.NONE,
        health_source=HealthSource.SMARTCTL if smart_read else HealthSource.NONE,
    )


def _parse_drive(data: dict[str, Any], device_path: str) -> DriveRawData:
    serial = str(data.get("serial_number") or "").strip()
    model = str(data.get("model_name") or data.get("model_family") or "").strip()
    firmware = str(data.get("firmware_version") or "").strip()

    bus = _bus_type(data)
    media = _media_type(data, bus)
    drive_id = _drive_id_from(serial, model, bus, device_path)

    capacity = _capacity_bytes(data)
    temp = _temperature(data)

    # Gather NVMe health log values
    nvme_health = data.get("nvme_smart_health_information_log", {})
    media_errors = nvme_health.get("media_errors")
    unsafe_shutdowns = nvme_health.get("unsafe_shutdowns")
    percentage_used = nvme_health.get("percentage_used")
    available_spare = nvme_health.get("available_spare")
    nvme_critical_warning = nvme_health.get("critical_warning")
    power_on_hours = (
        nvme_health.get("power_on_hours") or data.get("power_on_time", {}).get("hours")
    )
    power_cycle_count = (
        nvme_health.get("power_cycles") or data.get("power_cycle_count")
    )

    # ATA-specific counters from attributes
    ata_attrs = _parse_ata_attributes(data)
    nvme_attrs = _parse_nvme_attributes(data)
    raw_attrs = ata_attrs + nvme_attrs

    def _attr_raw_int(name_fragment: str) -> int | None:
        for a in ata_attrs:
            if name_fragment.lower() in a.name.lower():
                try:
                    return int(a.raw_value.split()[0])
                except (ValueError, IndexError):
                    pass
        return None

    reallocated = _attr_raw_int("reallocated_sector") or _attr_raw_int("reallocated sector")
    pending = _attr_raw_int("current_pending") or _attr_raw_int("current pending")
    uncorrectable = _attr_raw_int("uncorrectable") or _attr_raw_int("offline uncorrectable")

    # SMART health assessment
    smart_status = data.get("smart_status", {})
    overall_health = None
    if smart_status.get("passed") is True:
        overall_health = "PASSED"
    elif smart_status.get("passed") is False:
        overall_health = "FAILED"

    predicted_failure = overall_health == "FAILED"

    caps = _capabilities(data, bus)

    interface_speed = None
    ata_ver = data.get("ata_version", {})
    if isinstance(ata_ver, dict):
        interface_speed = ata_ver.get("string")

    # Rotation rate
    rotation_rate = data.get("rotation_rate")
    rotation_rate_rpm = int(rotation_rate) if isinstance(rotation_rate, int) and rotation_rate > 0 else None

    name = model or f"Drive {device_path}"

    return DriveRawData(
        id=drive_id,
        name=name,
        model=model,
        serial=serial,
        device_path=device_path,
        bus_type=bus,
        media_type=media,
        capacity_bytes=capacity,
        firmware_version=firmware,
        interface_speed=interface_speed,
        rotation_rate_rpm=rotation_rate_rpm,
        temperature_c=temp,
        power_on_hours=int(power_on_hours) if power_on_hours is not None else None,
        power_cycle_count=int(power_cycle_count) if power_cycle_count is not None else None,
        unsafe_shutdowns=int(unsafe_shutdowns) if unsafe_shutdowns is not None else None,
        wear_percent_used=float(percentage_used) if percentage_used is not None else None,
        available_spare_percent=float(available_spare) if available_spare is not None else None,
        reallocated_sectors=reallocated,
        pending_sectors=pending,
        uncorrectable_errors=uncorrectable,
        media_errors=int(media_errors) if media_errors is not None else None,
        predicted_failure=predicted_failure,
        smart_overall_health=overall_health,
        nvme_critical_warning=int(nvme_critical_warning) if nvme_critical_warning is not None else None,
        capabilities=caps,
        raw_attributes=raw_attrs,
        raw_smartctl=data,
    )


# ── SmartctlProvider ────────────────────────────────────────────────────────

class SmartctlProvider(DriveProvider):
    """Drives data provider backed by smartctl."""

    def __init__(self, smartctl_path: str = "smartctl") -> None:
        self._smartctl_path = smartctl_path
        self._available: bool | None = None  # cached after first check

    @property
    def provider_name(self) -> str:
        return "smartctl"

    async def is_available(self) -> bool:
        if self._available is not None:
            return self._available
        try:
            proc = await asyncio.create_subprocess_exec(
                self._smartctl_path,
                "--version",
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE,
            )
            await asyncio.wait_for(proc.communicate(), timeout=5.0)
            self._available = proc.returncode == 0
        except (FileNotFoundError, OSError, asyncio.TimeoutError):
            self._available = False
        return self._available

    async def _run_smartctl(
        self,
        args: list[str],
        timeout: float = 10.0,
    ) -> dict[str, Any]:
        """Run smartctl and return parsed JSON output."""
        cmd = [self._smartctl_path] + args
        try:
            proc = await asyncio.create_subprocess_exec(
                *cmd,
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE,
            )
            stdout, stderr = await asyncio.wait_for(proc.communicate(), timeout=timeout)
        except FileNotFoundError:
            self._available = False
            raise ProviderError(
                ProviderError.SMARTCTL_UNAVAILABLE,
                "smartctl not found",
            )
        except asyncio.TimeoutError:
            try:
                proc.kill()
                await proc.wait()
            except Exception:
                pass
            raise ProviderError(
                ProviderError.PROVIDER_TIMEOUT,
                f"smartctl timed out after {timeout}s",
            )

        if stderr:
            sanitized = _sanitize_stderr(stderr)
            logger.debug("smartctl stderr: %s", sanitized)

        # smartctl uses a bitmask exit code — bits 2-7 indicate SMART findings,
        # not command failures. We should still parse JSON for those cases.
        # Bit 0 (0x01): command line error — no usable output.
        # Bit 1 (0x02): device open failed (permission denied / not found).
        # Bits 2-7: SMART-level findings — JSON may still be present and valid.
        rc = proc.returncode
        assert rc is not None  # communicate() guarantees returncode is set
        if rc & 0x01:
            raise ProviderError(
                ProviderError.DRIVE_NOT_FOUND,
                "smartctl: command line error or device not found",
            )
        if (rc & 0x02) and not stdout:
            raise ProviderError(
                ProviderError.PERMISSION_DENIED,
                "smartctl: device open failed (permission denied or not found)",
            )

        if not stdout:
            raise ProviderError(
                ProviderError.PARSE_ERROR,
                "smartctl produced no output",
            )
        try:
            return json.loads(stdout.decode("utf-8", errors="replace"))
        except json.JSONDecodeError as exc:
            raise ProviderError(
                ProviderError.PARSE_ERROR,
                f"smartctl JSON parse failed: {exc}",
            )

    async def discover(self) -> list[DriveRawData]:
        if not await self.is_available():
            return []
        try:
            scan_data = await self._run_smartctl(
                ["--scan-open", "--json"], timeout=10.0
            )
        except ProviderError as exc:
            logger.warning("smartctl scan failed: %s", exc.message)
            return []

        devices = scan_data.get("devices", [])
        drives: list[DriveRawData] = []
        for dev in devices:
            device_path = str(dev.get("name") or "").strip()
            if not device_path or not _validate_device_path(device_path):
                continue
            try:
                detail = await self._run_smartctl(
                    ["-i", "-H", "-A", "-l", "selftest", "-j", device_path],
                    timeout=10.0,
                )
                drives.append(_parse_drive(detail, device_path))
            except ProviderError as exc:
                logger.warning("smartctl detail failed for %s: %s", device_path, exc.message)
        return drives

    async def refresh(self, device_path: str) -> DriveRawData | None:
        if not _validate_device_path(device_path):
            raise ProviderError(
                ProviderError.DRIVE_NOT_FOUND,
                f"Rejected device path: {device_path}",
            )
        if not await self.is_available():
            return None
        try:
            data = await self._run_smartctl(
                ["-a", "-j", device_path], timeout=10.0
            )
            return _parse_drive(data, device_path)
        except ProviderError:
            return None

    async def start_self_test(self, device_path: str, test_type: SelfTestType) -> str | None:
        if not _validate_device_path(device_path):
            raise ProviderError(
                ProviderError.DRIVE_NOT_FOUND,
                f"Rejected device path: {device_path}",
            )
        type_flag = {
            SelfTestType.SHORT: "short",
            SelfTestType.EXTENDED: "long",
            SelfTestType.CONVEYANCE: "conveyance",
        }[test_type]
        try:
            await self._run_smartctl(
                ["-t", type_flag, "-j", device_path], timeout=15.0
            )
            return f"smartctl_{device_path}_{type_flag}_{int(time.time())}"
        except ProviderError as exc:
            if exc.code == ProviderError.UNSUPPORTED_OPERATION:
                return None
            raise

    async def get_self_test_status(self, device_path: str) -> DriveSelfTestRun | None:
        if not _validate_device_path(device_path):
            return None
        if not await self.is_available():
            return None
        try:
            data = await self._run_smartctl(
                ["-l", "selftest", "-j", device_path], timeout=10.0
            )
            serial = str(data.get("serial_number") or "").strip()
            model = str(data.get("model_name") or "").strip()
            bus = _bus_type(data)
            drive_id = _drive_id_from(serial, model, bus, device_path)
            return _parse_self_test_log(data, drive_id)
        except ProviderError:
            return None

    async def abort_self_test(self, device_path: str) -> bool:
        if not _validate_device_path(device_path):
            return False
        if not await self.is_available():
            return False
        try:
            await self._run_smartctl(["-X", "-j", device_path], timeout=15.0)
            return True
        except ProviderError:
            return False
