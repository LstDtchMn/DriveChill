"""Normalize raw drive data into health assessments."""
from __future__ import annotations

from app.models.drives import (
    DriveRawData,
    DriveSettings,
    HealthStatus,
    MediaType,
)


class DriveHealthNormalizer:
    """
    Converts raw provider data + global settings into a normalized health state.

    Normalization rules (in priority order):
    1. SMART overall_health == FAILED → CRITICAL, predicted_failure = True
    2. Reallocated sectors > 0 → WARNING (threshold-free)
    3. Pending sectors > 0 → WARNING
    4. Uncorrectable / media errors > 0 → CRITICAL
    5. Wear >= critical threshold → CRITICAL
    6. Wear >= warning threshold → WARNING
    7. Temperature >= critical threshold → CRITICAL
    8. Temperature >= warning threshold → WARNING
    9. No negative indicators → GOOD
    """

    def __init__(self, settings: DriveSettings) -> None:
        self._s = settings

    def _temp_thresholds(
        self, media: MediaType
    ) -> tuple[float, float]:
        """Return (warning_c, critical_c) for the given media type."""
        if media == MediaType.NVME:
            return self._s.nvme_temp_warning_c, self._s.nvme_temp_critical_c
        if media == MediaType.SSD:
            return self._s.ssd_temp_warning_c, self._s.ssd_temp_critical_c
        # HDD or unknown
        return self._s.hdd_temp_warning_c, self._s.hdd_temp_critical_c

    def health_status(self, raw: DriveRawData) -> HealthStatus:
        if raw.smart_overall_health == "FAILED":
            return HealthStatus.CRITICAL
        if raw.predicted_failure:
            return HealthStatus.CRITICAL

        # NVMe critical_warning bitmask: any non-zero value = active controller warning
        if raw.nvme_critical_warning:
            return HealthStatus.CRITICAL

        if (raw.uncorrectable_errors or 0) > 0:
            return HealthStatus.CRITICAL
        if (raw.media_errors or 0) > 0:
            return HealthStatus.CRITICAL

        wear = raw.wear_percent_used
        if wear is not None:
            if wear >= self._s.wear_critical_percent_used:
                return HealthStatus.CRITICAL
            if wear >= self._s.wear_warning_percent_used:
                return HealthStatus.WARNING

        spare = raw.available_spare_percent
        if spare is not None and spare < 10.0:
            return HealthStatus.CRITICAL

        if (raw.reallocated_sectors or 0) > 0:
            return HealthStatus.WARNING
        if (raw.pending_sectors or 0) > 0:
            return HealthStatus.WARNING

        warn_c, crit_c = self._temp_thresholds(raw.media_type)
        temp = raw.temperature_c
        if temp is not None:
            if temp >= crit_c:
                return HealthStatus.CRITICAL
            if temp >= warn_c:
                return HealthStatus.WARNING

        if raw.capabilities.smart_read or raw.capabilities.health_source.value != "none":
            return HealthStatus.HEALTHY

        return HealthStatus.UNKNOWN

    def health_percent(self, raw: DriveRawData) -> float | None:
        """
        Estimate a 0-100% health score from available signals.
        Returns None when no useful signals are available.
        """
        available = (
            raw.capabilities.smart_read
            or raw.capabilities.health_source.value != "none"
            or raw.wear_percent_used is not None
        )
        if not available:
            return None

        score = 100.0

        # Wear dominates for NVMe/SSD
        if raw.wear_percent_used is not None:
            score = min(score, max(0.0, 100.0 - raw.wear_percent_used))

        if raw.smart_overall_health == "FAILED":
            score = min(score, 0.0)
        elif raw.predicted_failure:
            score = min(score, 5.0)

        if raw.nvme_critical_warning:
            score = min(score, 5.0)
        if (raw.uncorrectable_errors or 0) > 0:
            score = min(score, 10.0)
        if (raw.media_errors or 0) > 0:
            score = min(score, 20.0)
        if raw.available_spare_percent is not None and raw.available_spare_percent < 10.0:
            score = min(score, 10.0)
        if (raw.reallocated_sectors or 0) > 0:
            score = min(score, 60.0)
        if (raw.pending_sectors or 0) > 0:
            score = min(score, 70.0)

        return round(score, 1)

    def temp_warning_c(self, raw: DriveRawData) -> float:
        return self._temp_thresholds(raw.media_type)[0]

    def temp_critical_c(self, raw: DriveRawData) -> float:
        return self._temp_thresholds(raw.media_type)[1]
