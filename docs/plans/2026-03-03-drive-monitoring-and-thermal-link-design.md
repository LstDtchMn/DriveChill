# DriveChill Drive Monitoring + Thermal Link Design

**Date:** 2026-03-03  
**Status:** Draft  
**Authors:** Product + Engineering  
**Scope:** CrystalDiskInfo-like drive monitoring integrated with DriveChill thermal management

---

## 1. Purpose

This document defines the design and implementation framework for adding drive monitoring to DriveChill.

The feature is intended to:
- Provide a CrystalDiskInfo-like storage monitoring experience for HDD, SATA SSD, and NVMe drives.
- Surface drive temperatures, health state, SMART/NVMe details, and safe diagnostic actions.
- Integrate storage telemetry directly into DriveChill's existing thermal control model.
- Reuse the current alerting, analytics, notification, and fan-curve systems where possible.

This is a single integrated feature release with full Python and C# backend parity.

---

## 2. Product Positioning

DriveChill should not become a generic disk utility. The feature should reinforce the existing product thesis:

**Storage health + thermal response + automation in one system**

### Why this matters
- CrystalDiskInfo-class tools show health well, but they do not connect that health to cooling policy.
- DriveChill already has fan control, alerts, analytics, webhooks, push, email, and remote monitoring patterns.
- Storage monitoring becomes more valuable when it is directly tied to thermal response and maintenance guidance.

### Competitive intent
- Match the visibility users expect from disk health tools.
- Differentiate by linking drive temperature into fan control and existing automation channels.
- Avoid scope drift into destructive disk maintenance tools.

---

## 3. Goals and Non-Goals

### Goals
- Show inventory and health for local drives.
- Support HDD, SATA SSD, and NVMe drives.
- Expose current drive temperatures as normal DriveChill temperature sensors.
- Allow users to select drive temperatures in existing fan curves.
- Add storage-specific alerts and history.
- Support safe SMART self-tests and abort where available.
- Degrade gracefully if advanced tooling is unavailable.
- Keep Python and C# API contracts identical.

### Non-Goals
- Secure erase
- Firmware updates
- APM/AAM changes
- Manual spindown or standby writes
- TRIM or other destructive maintenance commands
- New fan-control modes just for storage
- Using SMART health scores or raw attributes directly as fan-curve inputs

---

## 4. Core Decisions

### 4.1 Reuse existing `hdd_temp` sensors

Drive temperatures must be surfaced as normal `hdd_temp` sensors rather than introducing a new curve input mode.

This keeps:
- Existing fan curve evaluation intact
- Existing composite curve logic intact
- Existing sensor history intact
- Existing WebSocket sensor updates intact
- Existing alert and analytics paths reusable

The only additive change is metadata that links an `hdd_temp` reading to a canonical `drive_id`.

### 4.2 Hybrid provider strategy

Use a hybrid source model:
1. Native provider first where cheap and reliable
2. `smartctl` as fallback and advanced authority
3. Graceful degraded mode when `smartctl` is unavailable

### 4.3 Full parity in first release

Both backends must ship:
- The same routes
- The same request/response shapes
- The same error semantics
- The same health classification behavior

This is intentionally higher scope but keeps the shared frontend honest from day one.

### 4.4 Safe diagnostics only

The first release includes read-only monitoring plus safe SMART self-tests.

It does not include destructive drive operations.

---

## 5. Provider Architecture

## 5.1 Composite provider model

The drive collection subsystem should use two provider families:
- Native provider
- `smartctl` provider

The composite provider resolves fields in this order:
1. Native data if present and reliable
2. `smartctl` fills missing fields
3. `smartctl` is authoritative for advanced SMART/NVMe health and self-tests

## 5.2 Native provider responsibilities

The native provider may return partial data only.

It should be used for:
- Basic inventory where available
- Current drive temperature where the platform already exposes it
- Low-cost metadata that does not require elevated tooling

## 5.3 `smartctl` responsibilities

`smartctl` is used for:
- SMART and NVMe health details
- Raw attributes and SMART log access
- Temperature fallback if native data is missing
- Self-test execution
- Self-test status polling
- Capability detection

## 5.4 Degraded mode

If `smartctl` is missing or blocked:
- The feature still loads
- Native data is used if available
- SMART details are marked unavailable
- Self-test controls are hidden or disabled
- The UI shows clear remediation guidance

---

## 6. Security Model For `smartctl`

This is the highest-risk implementation detail and must be implemented exactly.

## 6.1 Shell execution rules

### Python
- Use `asyncio.create_subprocess_exec(...)` or equivalent
- Never use `shell=True`
- Never pass user-controlled strings through a shell

### C#
- Use `ProcessStartInfo`
- `UseShellExecute = false`
- `RedirectStandardOutput = true`
- `RedirectStandardError = true`
- Never invoke through `cmd.exe` or a shell wrapper

## 6.2 Allowed command patterns

Only these commands are allowed in v1:

### Inventory
- `smartctl --scan-open --json`
- `smartctl -i -H -A -l selftest -j <validated_device_path>`
- `smartctl -a -j <validated_device_path>`

### Self-tests
- `smartctl -t short -j <validated_device_path>`
- `smartctl -t long -j <validated_device_path>`
- `smartctl -t conveyance -j <validated_device_path>`

### Abort
- `smartctl -X -j <validated_device_path>`

No other flags are permitted.

## 6.3 Device path validation

The client never sends a device path directly.

All mutating or diagnostic actions must:
1. Accept `drive_id`
2. Resolve that `drive_id` to the server-trusted stored drive record
3. Read the provider-known `device_path`
4. Validate the `device_path`
5. Execute only if the path is still present in the latest discovered inventory

### Windows accepted patterns
Prefer storing and reusing the exact path returned by `smartctl --scan-open`.

Accepted forms:
- `^/dev/pd\\d+$`
- `^/dev/nvme\\d+$`

Any additional Windows pattern must be explicitly added to the allowlist later. Do not infer or transform device paths from user-facing labels.

### Linux accepted patterns
- `^/dev/sd[a-z]+$`
- `^/dev/hd[a-z]+$`
- `^/dev/nvme\\d+n\\d+$`
- `^/dev/disk/by-id/[A-Za-z0-9._:-]+$`

Reject everything else.

## 6.4 Timeouts

- Inventory scan: `10s`
- Drive refresh/detail read: `10s`
- Self-test start/abort: `15s`
- Self-test status poll: `10s`

On timeout:
- Return a normalized provider timeout error
- Do not block the fan control loop
- Mark provider status degraded if timeouts repeat

## 6.5 Output handling

- Parse JSON output only
- Do not expose raw stderr to the client
- Sanitize and truncate stderr before logging
- Normalize API errors:
  - `smartctl_unavailable`
  - `permission_denied`
  - `unsupported_operation`
  - `drive_not_found`
  - `provider_timeout`

---

## 7. Polling and Runtime Model

Drive monitoring should not run on the same cadence as CPU/GPU control sensors.

### 7.1 Temperature poll
- Default: `15s`
- Purpose: current drive temperature for UI and fan-curve participation

### 7.2 Health poll
- Default: `300s`
- Purpose: health status, wear, counters, SMART/NVMe state

### 7.3 Rescan poll
- Default: `900s`
- Purpose: inventory refresh and capability refresh

### 7.4 Self-test status poll
- Poll every `30s` while any self-test is running

### 7.5 Restart recovery

On startup:
1. Load any self-test rows marked `running`
2. Poll current status from the provider
3. Reconcile each run to `running`, `passed`, `failed`, `aborted`, or `unknown`
4. Resume polling only for runs still actually active

---

## 8. Identity and Data Model

## 8.1 Canonical `drive_id`

Generate `drive_id` using:
1. Serial + model + bus + stable device identifier hash when serial exists
2. Device-path-based fallback hash if serial is absent

The ID must remain stable across restarts whenever the drive remains identifiable.

## 8.2 Serial number policy

This is fixed:
- Store full serial in the database
- Mask serial in list APIs
- Expose full serial only in authenticated detail APIs

This supports stable identity and avoids ambiguous "optional serial storage" behavior.

## 8.3 Public types

### `DriveSummary`
- `id`
- `name`
- `model`
- `serial_masked`
- `device_path_masked`
- `bus_type`
- `media_type`
- `capacity_bytes`
- `temperature_c`
- `health_status`
- `health_percent`
- `smart_available`
- `native_available`
- `supports_self_test`
- `supports_abort`
- `last_updated_at`

### `DriveDetail`
Includes all `DriveSummary` fields plus:
- `serial_full`
- `device_path`
- `firmware_version`
- `interface_speed`
- `rotation_rate_rpm`
- `power_on_hours`
- `power_cycle_count`
- `unsafe_shutdowns`
- `wear_percent_used`
- `available_spare_percent`
- `reallocated_sectors`
- `pending_sectors`
- `uncorrectable_errors`
- `media_errors`
- `predicted_failure`
- `temperature_warning_c`
- `temperature_critical_c`
- `capabilities`
- `warnings`
- `last_self_test`
- `raw_attributes`
- `history_retention_hours_effective`

### `DriveCapabilitySet`
- `smart_read`
- `smart_self_test_short`
- `smart_self_test_extended`
- `smart_self_test_conveyance`
- `smart_self_test_abort`
- `temperature_source`
- `health_source`

### `DriveRawAttribute`
- `key`
- `name`
- `normalized_value`
- `worst_value`
- `threshold`
- `raw_value`
- `status`
- `source_kind`

### `DriveSelfTestRun`
- `id`
- `drive_id`
- `type`
- `status`
- `progress_percent`
- `started_at`
- `finished_at`
- `failure_message`

## 8.4 Additive sensor metadata

Existing `hdd_temp` sensors gain optional metadata:
- `drive_id`
- `entity_name`
- `source_kind`

This keeps the current sensor model backward compatible.

---

## 9. Database Schema

## 9.1 Migration

Add a new Python migration:
- `008_drive_monitoring.sql`

The C# backend must mirror the same schema in its DB initialization logic.

## 9.2 New tables

### `drives`
Latest known inventory and resolved state:
- `id`
- `name`
- `model`
- `serial_full`
- `device_path`
- `bus_type`
- `media_type`
- `capacity_bytes`
- `firmware_version`
- `smart_available`
- `native_available`
- `supports_self_test`
- `supports_abort`
- `last_seen_at`
- `last_updated_at`

### `drive_health_snapshots`
Used for trend analysis and delta detection:
- `id`
- `drive_id`
- `recorded_at`
- `temperature_c`
- `health_status`
- `health_percent`
- `predicted_failure`
- `wear_percent_used`
- `available_spare_percent`
- `reallocated_sectors`
- `pending_sectors`
- `uncorrectable_errors`
- `media_errors`
- `power_on_hours`
- `unsafe_shutdowns`

### `drive_attributes_latest`
Store only the latest raw attribute payload:
- `drive_id`
- `captured_at`
- `attributes_json`

### `drive_self_test_runs`
- `id`
- `drive_id`
- `type`
- `status`
- `progress_percent`
- `started_at`
- `finished_at`
- `failure_message`
- `provider_run_ref`

## 9.3 Capability refresh semantics

The following fields are persisted as the latest known state, not immutable hardware facts:
- `smart_available`
- `supports_self_test`
- `supports_abort`

They must be refreshed:
- On startup
- On scheduled rescan
- On explicit rescan
- On explicit drive refresh

## 9.4 Delta detection source

Counters that need "increase" detection must be compared using `drive_health_snapshots`, not `drive_attributes_latest`.

This is specifically required for alerts such as:
- `drive_media_error_increase`

---

## 10. Health Classification

Use a deterministic cross-drive health model.

### `critical`
Any of:
- SMART explicitly reports failure or prefail
- NVMe critical warning indicates a serious condition
- A self-test fails
- Pending sectors are non-zero on an HDD
- Uncorrectable or media errors are non-zero and worsening
- Temperature is at or above the critical threshold
- Available spare is critically low
- Wear has crossed the critical threshold

### `warning`
Any of:
- Temperature is at or above the warning threshold
- Reallocated sectors are non-zero
- Wear is approaching the critical threshold
- Available spare is low
- Attributes are degrading but not failed

### `good`
- No warning or critical conditions

### `unknown`
- Not enough data to classify

### Default thermal thresholds
- HDD: warning `45C`, critical `50C`
- SATA SSD: warning `55C`, critical `65C`
- NVMe: warning `65C`, critical `75C`

Thresholds must support:
- Global defaults
- Per-drive overrides

---

## 11. API Contract

All endpoints below must exist in both Python and C# with identical request and response shapes.

## 11.1 Inventory and detail

### `GET /api/drives`
Returns:
- `drives: DriveSummary[]`

### `POST /api/drives/rescan`
Returns:
- `success`
- `discovered_count`

### `GET /api/drives/{drive_id}`
Returns:
- `drive: DriveDetail`

### `GET /api/drives/{drive_id}/attributes`
Returns:
- `drive_id`
- `captured_at`
- `attributes: DriveRawAttribute[]`

### `GET /api/drives/{drive_id}/history`
Supports:
- `hours`
- or `start` / `end`

Returns:
- `drive_id`
- `requested_range`
- `returned_range`
- `retention_limited`
- `history_retention_hours_effective`
- `snapshots`
- `temperature_series`

### `POST /api/drives/{drive_id}/refresh`
Returns:
- `success`
- `drive`

## 11.2 Self-tests

### `POST /api/drives/{drive_id}/self-tests`
Request:
- `type: "short" | "extended" | "conveyance"`

Returns:
- `success`
- `run: DriveSelfTestRun`

### `GET /api/drives/{drive_id}/self-tests`
Returns:
- `runs: DriveSelfTestRun[]`

### `POST /api/drives/{drive_id}/self-tests/{run_id}/abort`
Returns:
- `success`
- `run`

## 11.3 Global settings

### `GET /api/drives/settings`
Returns:
- `enabled`
- `native_provider_enabled`
- `smartctl_provider_enabled`
- `smartctl_path`
- `fast_poll_seconds`
- `health_poll_seconds`
- `rescan_poll_seconds`
- `hdd_temp_warning_c`
- `hdd_temp_critical_c`
- `ssd_temp_warning_c`
- `ssd_temp_critical_c`
- `nvme_temp_warning_c`
- `nvme_temp_critical_c`
- `wear_warning_percent_used`
- `wear_critical_percent_used`

### `PUT /api/drives/settings`
Accepts the same fields with validation.

## 11.4 Per-drive settings

### `GET /api/drives/{drive_id}/settings`
Returns effective and overridden settings.

### `PUT /api/drives/{drive_id}/settings`
Allows per-drive override of:
- Temperature warning and critical thresholds
- Alert enable/disable
- Whether the drive temp is surfaced in the curve picker

---

## 12. Auth, API Keys, and WebSocket Behavior

## 12.1 API key scope additions

Add a new scope domain:
- `drives`

Required scopes:
- `read:drives`
- `write:drives`

Path mapping:
- `/api/drives` must map to the `drives` scope domain in both backends

## 12.2 Browser/session auth

Browser auth remains aligned with the current model. Drive actions use the same admin-level auth requirements as other mutations.

## 12.3 WebSocket behavior

Drive temperatures flow through the existing sensor snapshot path because they are normal `hdd_temp` sensors.

Do not add a new drive-specific WebSocket message type in v1.

Drive health transitions should be surfaced via the existing alert paths:
- `alerts`
- `active_alerts`

This preserves the current WebSocket contract.

---

## 13. Thermal Integration

## 13.1 Direct thermal linkage

The direct thermal linkage is:
- drive temperature enters the existing sensor system
- the curve editor can select drive temperature sensors
- existing temperature curve logic remains unchanged

Do not use SMART health or wear metrics as direct curve inputs in v1.

## 13.2 Curve editor behavior

Group selectable temperature sensors by category:
- CPU
- GPU
- Motherboard
- Storage

Storage entries should show:
- Human-readable drive name
- Current temperature

## 13.3 "Use for cooling" flow

From drive detail:
1. User clicks `Use for cooling`
2. App switches to the existing `curves` page
3. App stores a transient `preselectedCurveSensorId`
4. The curve editor reads that state and preselects the drive sensor
5. The transient state clears after use or page exit

Do not add a URL-based deep-link mechanism in v1.

## 13.4 "Create storage cooling preset" flow

Because a drive does not imply a target fan, this flow must require fan selection:
1. User clicks `Create storage cooling preset`
2. App prompts for a target controllable fan
3. User selects a fan
4. App opens the curve editor with:
   - the target fan selected
   - the drive temp sensor preselected
   - unsaved suggested points loaded
5. The user must manually save

Suggested default curve:
- `30C -> 25%`
- `40C -> 40%`
- `50C -> 65%`
- `60C -> 100%`

---

## 14. Frontend UX Scope

The frontend currently uses a single-page navigation model with a `Page` union. This feature must follow that pattern.

## 14.1 New page

Add a new top-level page:
- `drives`

Update:
- Page union/type definitions
- Navigation/sidebar
- App shell render switch

Do not introduce a new routing paradigm.

## 14.2 Drives list page

The drives page should show:
- Name
- Model
- Media type
- Capacity
- Current temperature
- Health badge
- SMART availability
- Last self-test state

Support:
- Sort by temperature, health, or name
- Filter by health state
- Filter by media type
- Rescan and refresh actions

## 14.3 Drive detail surface

Use a drill-in panel or in-page detail section consistent with the current UI structure.

Sections:
- Overview
- Health
- SMART / Attributes
- Temperature history
- Self-tests
- Cooling link

## 14.4 Settings integration

Add a Storage Monitoring section to the existing Settings page:
- Enable/disable
- Provider status
- `smartctl` path override
- Polling intervals
- Global thresholds
- Degraded mode banner when tooling is unavailable

## 14.5 Dashboard summary

Add a compact storage card to the main dashboard:
- Number of drives
- Hottest drive
- Warning/critical count
- Shortcut to the Drives page

Do not place raw SMART tables on the main dashboard.

---

## 15. Docker and Deployment

Drive monitoring has deployment implications that must be documented and implemented.

## 15.1 Runtime image

The backend runtime image should include `smartmontools` so advanced drive monitoring is available when the container is granted device access.

## 15.2 Compose files

Update both:
- `docker/docker-compose.yml`
- `docker/docker-compose.privileged.yml`

### Default compose
- Document that drive monitoring may run in degraded mode or be unavailable due to missing device access

### Privileged compose
- Document that full drive monitoring typically requires:
  - `privileged: true` or explicit device mappings
  - Access to host block devices
  - Required capabilities such as `SYS_RAWIO` where applicable

## 15.3 Container UX

If running in a container without required privileges:
- The feature remains visible
- The UI must clearly report:
  - `smartctl unavailable`
  - `permission denied`
  - `device access unavailable in container`

---

## 16. Backend Implementation Layout

## 16.1 Python

Add:
- `backend/app/api/routes/drives.py`
- `backend/app/services/drive_monitor_service.py`
- `backend/app/services/drive_health_normalizer.py`
- `backend/app/services/drive_self_test_service.py`
- `backend/app/db/repositories/drive_repo.py`
- `backend/app/hardware/drives/base.py`
- `backend/app/hardware/drives/native_provider.py`
- `backend/app/hardware/drives/smartctl_provider.py`
- `backend/app/hardware/drives/composite_provider.py`

Also update:
- Auth scope mapping
- Startup lifecycle
- Settings/config repositories
- Sensor publication path
- Alert pipeline

## 16.2 C#

Add:
- `backend-cs/Api/DrivesController.cs`
- `backend-cs/Services/DriveMonitorService.cs`
- `backend-cs/Services/DriveHealthNormalizer.cs`
- `backend-cs/Services/DriveSelfTestService.cs`
- `backend-cs/Hardware/Drives/IDriveProvider.cs`
- `backend-cs/Hardware/Drives/NativeDriveProvider.cs`
- `backend-cs/Hardware/Drives/SmartctlDriveProvider.cs`
- `backend-cs/Hardware/Drives/CompositeDriveProvider.cs`

Also update:
- `DbService.cs`
- `Program.cs`
- API key scope definitions
- Background service registration
- Sensor metadata models as needed

---

## 17. Alerts and Notifications

Add these internal alert types:
- `drive_temp_high`
- `drive_temp_critical`
- `drive_health_degraded`
- `drive_predicted_failure`
- `drive_self_test_failed`
- `drive_media_error_increase`
- `drive_wear_threshold`

They must route through the existing channels:
- In-app alerts
- Web Push
- Email
- Webhooks

Do not build a separate notification pipeline.

Webhook payloads should be extended with drive-specific fields:
- `drive_id`
- `drive_name`
- `model`
- `device_path`
- `health_status`
- `temperature_c`
- `alert_reason`

---

## 18. Test Strategy

## 18.1 Unit tests

- Parse ATA `smartctl -j` output
- Parse NVMe `smartctl -j` output
- Validate device path allowlist behavior
- Validate health classification
- Validate history retention flags
- Validate self-test state transitions
- Validate API-key scope enforcement

## 18.2 Integration tests

- `GET /api/drives` shape and filtering
- `GET /api/drives/{id}` detail shape
- Degraded mode behavior when `smartctl` is unavailable
- Refresh and rescan actions
- Self-test start and abort flows
- Sensor publication for drive temperatures

## 18.3 Frontend tests

- Drives page renders mixed drive types
- Sorting and filtering work
- Detail drill-in renders correctly
- `Use for cooling` preselects the correct sensor
- `Create storage cooling preset` requires fan selection
- Degraded mode banner renders when provider access fails

## 18.4 Cross-backend parity tests

- Python and C# return the same shapes for all `/api/drives*` routes
- Python and C# use the same normalized health labels
- Python and C# enforce the same capability and error semantics

## 18.5 Mocking seams

### Python
- `smartctl` execution must be behind a dedicated executor wrapper so subprocesses can be mocked cleanly

### C#
- `SmartctlDriveProvider` must depend on an injectable process runner abstraction for testability

---

## 19. Acceptance Criteria

The feature is complete when:
- The user can open a Drives page and see local drives with temps and health
- The user can inspect detailed SMART/NVMe information
- The user can start and monitor a safe self-test where supported
- Drive temperatures appear in the existing fan-curve workflow
- Drive-specific alerts flow through the existing notification channels
- The feature degrades cleanly when `smartctl` is missing or blocked
- Python and C# expose the same user-visible behavior and API shapes

---

## 20. Assumptions and Defaults

- Full Python/C# parity is required in the first release
- Drive temperature is the only storage signal that directly affects fan curves in v1
- SMART health and raw attributes are advisory and alerting inputs only
- `smartctl` is used for advanced health data and all self-test actions
- `smartctl` is not required for app startup; missing access results in degraded mode, not fatal startup failure
- Real-time drive state changes are surfaced through existing alert WebSocket fields rather than a new WebSocket schema
- The frontend remains on the current single-page navigation pattern

