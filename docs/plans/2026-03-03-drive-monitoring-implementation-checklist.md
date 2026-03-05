# DriveChill Drive Monitoring Implementation Checklist

**Date:** 2026-03-03  
**Status:** Ready For Execution  
**Primary Spec:** `docs/plans/2026-03-03-drive-monitoring-and-thermal-link-design.md`

---

## 1. Purpose

This checklist is the execution companion to the drive monitoring design spec.

It is intended for implementation agents and engineers who need a concrete, ordered task list with acceptance checks. It does not replace the design document. Where this checklist is shorter than the design, the design document remains the source of truth.

---

## 2. Execution Order

Implement in this order to reduce rework:
1. Schema and shared backend settings contracts
2. Provider abstractions and `smartctl` execution layer
3. Python backend API and polling integration
4. C# backend parity implementation
5. Frontend page, drill-in, and fan-curve linkage
6. Alerts, webhooks, and notification wiring
7. Docker and deployment updates
8. Tests, parity verification, and documentation sync

Do not start frontend work before backend response shapes are stable in both backends.

---

## 3. Backend Foundation

### 3.1 Database and configuration

- Add Python migration `008_drive_monitoring.sql`
- Add C# mirrored schema initialization for the same tables
- Add new tables:
  - `drives`
  - `drive_health_snapshots`
  - `drive_attributes_latest`
  - `drive_self_test_runs`
- Add global drive-monitoring settings storage
- Add per-drive override settings storage
- Ensure migration order remains strictly sequential

### 3.2 API key scope and auth

- Add new API key scope domain: `drives`
- Add `read:drives`
- Add `write:drives`
- Map `/api/drives` to the `drives` scope in Python
- Map `/api/drives` to the `drives` scope in C#
- Verify browser auth behavior matches existing admin/session semantics

### 3.3 Shared models and contracts

- Define shared response contract for:
  - `DriveSummary`
  - `DriveDetail`
  - `DriveCapabilitySet`
  - `DriveRawAttribute`
  - `DriveSelfTestRun`
- Extend sensor metadata with:
  - `drive_id`
  - `entity_name`
  - `source_kind`
- Keep field names identical between Python and C#

---

## 4. Provider Layer

### 4.1 Create provider abstractions

Python:
- `backend/app/hardware/drives/base.py`
- `backend/app/hardware/drives/native_provider.py`
- `backend/app/hardware/drives/smartctl_provider.py`
- `backend/app/hardware/drives/composite_provider.py`

C#:
- `backend-cs/Hardware/Drives/IDriveProvider.cs`
- `backend-cs/Hardware/Drives/NativeDriveProvider.cs`
- `backend-cs/Hardware/Drives/SmartctlDriveProvider.cs`
- `backend-cs/Hardware/Drives/CompositeDriveProvider.cs`

### 4.2 Implement `smartctl` execution guardrails

- Use argument-array invocation only
- Never shell out through `cmd.exe`, shell wrappers, or `shell=True`
- Restrict commands to the allowlisted forms in the design spec
- Parse JSON output only
- Add timeout handling
- Sanitize stderr before logging
- Normalize provider error codes

### 4.3 Validate device paths

- Accept only server-resolved `drive_id`
- Resolve to stored `device_path`
- Validate against OS-specific allowlist
- Reject actions if the path is not in the latest discovered inventory
- Do not derive execution paths from user-visible labels

### 4.4 Build degraded mode behavior

- Native data only if `smartctl` is missing
- SMART details unavailable when `smartctl` is unavailable
- Self-test actions disabled when unsupported
- Provider status surfaced to API and UI

---

## 5. Python Backend

### 5.1 Add services and repository

- Add:
  - `backend/app/api/routes/drives.py`
  - `backend/app/services/drive_monitor_service.py`
  - `backend/app/services/drive_health_normalizer.py`
  - `backend/app/services/drive_self_test_service.py`
  - `backend/app/db/repositories/drive_repo.py`

### 5.2 Implement polling and lifecycle wiring

- Add startup inventory scan
- Add 15s temperature poll
- Add 300s health poll
- Add 900s rescan poll
- Add 30s self-test status polling while active
- Add restart reconciliation for self-tests marked `running`

### 5.3 Implement Python endpoints

- `GET /api/drives`
- `POST /api/drives/rescan`
- `GET /api/drives/{drive_id}`
- `GET /api/drives/{drive_id}/attributes`
- `GET /api/drives/{drive_id}/history`
- `POST /api/drives/{drive_id}/refresh`
- `POST /api/drives/{drive_id}/self-tests`
- `GET /api/drives/{drive_id}/self-tests`
- `POST /api/drives/{drive_id}/self-tests/{run_id}/abort`
- `GET /api/drives/settings`
- `PUT /api/drives/settings`
- `GET /api/drives/{drive_id}/settings`
- `PUT /api/drives/{drive_id}/settings`

### 5.4 Integrate with existing systems

- Publish drive temperatures as `hdd_temp` sensors
- Attach additive drive metadata to those sensors
- Feed drive alerts into the existing alert pipeline
- Extend webhooks with drive-specific fields
- Reuse existing sensor history retention semantics and expose `retention_limited` in drive history responses

---

## 6. C# Backend

### 6.1 Add services and controller

- Add:
  - `backend-cs/Api/DrivesController.cs`
  - `backend-cs/Services/DriveMonitorService.cs`
  - `backend-cs/Services/DriveHealthNormalizer.cs`
  - `backend-cs/Services/DriveSelfTestService.cs`

### 6.2 Register runtime services

- Register drive services in `Program.cs`
- Wire startup scan and polling
- Ensure polling does not block the sensor loop or WebSocket flow

### 6.3 Implement C# parity endpoints

- Match every Python `/api/drives*` route exactly
- Match the same status codes, response shapes, and error payloads
- Match the same health normalization and degraded-mode semantics

### 6.4 Integrate with existing systems

- Publish drive temperatures as standard `hdd_temp` readings
- Add the same additive sensor metadata as Python
- Feed drive alerts into the existing alert/notification path
- Mirror webhook payload extensions

---

## 7. Frontend

### 7.1 Add navigation and page state

- Add `drives` to the existing `Page` union/store
- Add a sidebar navigation entry
- Render the new page through the existing app shell

### 7.2 Build the drives page

- Drive list with:
  - name
  - model
  - media type
  - capacity
  - current temperature
  - health badge
  - SMART availability
  - last self-test state
- Sorting by:
  - temperature
  - health
  - name
- Filtering by:
  - health state
  - media type

### 7.3 Build the drive detail drill-in

- Overview
- Health
- SMART / Attributes
- Temperature history
- Self-tests
- Cooling link

Use a drill-in or in-page detail surface consistent with the current single-page UI.

### 7.4 Add curve integration flows

- Add `Use for cooling`
- Store transient `preselectedCurveSensorId`
- Make the curve editor consume and clear that transient state
- Add `Create storage cooling preset`
- Require the user to choose a target fan before preloading the suggested curve
- Do not auto-save the generated curve

### 7.5 Add settings and dashboard hooks

- Add a Storage Monitoring section in Settings
- Add a compact storage summary card on the dashboard
- Show degraded-mode status banners when provider access is limited

---

## 8. Alerts, Notifications, and Real-Time

### 8.1 Add internal alert types

- `drive_temp_high`
- `drive_temp_critical`
- `drive_health_degraded`
- `drive_predicted_failure`
- `drive_self_test_failed`
- `drive_media_error_increase`
- `drive_wear_threshold`

### 8.2 Reuse existing delivery channels

- In-app alerts
- Web Push
- Email
- Webhooks

Do not build a separate notification subsystem.

### 8.3 WebSocket rules

- Drive temperatures ride the existing sensor snapshot channel
- Do not add a new drive-specific WebSocket schema in v1
- Drive health transitions should appear through existing `alerts` and `active_alerts`

---

## 9. Docker and Deployment

### 9.1 Container runtime

- Add `smartmontools` to the backend runtime image

### 9.2 Compose files

- Update `docker/docker-compose.yml`
- Update `docker/docker-compose.privileged.yml`
- Document degraded behavior for unprivileged containers
- Document full-access requirements for privileged/device-mapped containers

### 9.3 Operator guidance

- Document that full drive monitoring may require device access and elevated capabilities
- Document expected degraded mode when those permissions are missing

---

## 10. Tests

### 10.1 Unit tests

- ATA `smartctl -j` parsing
- NVMe `smartctl -j` parsing
- Device path validation
- Health normalization
- Self-test state transitions
- History retention flags
- API key scope enforcement

### 10.2 Integration tests

- Drive list and detail routes
- Degraded mode when `smartctl` is missing
- Refresh and rescan actions
- Self-test start and abort
- Drive temperature publication into the sensor pipeline

### 10.3 Frontend tests

- Drives page render
- Sorting and filtering
- Drill-in rendering
- `Use for cooling` preselection
- `Create storage cooling preset` fan selection flow
- Degraded mode banners

### 10.4 Cross-backend parity tests

- Same route shapes from Python and C#
- Same health labels
- Same error semantics
- Same capability semantics

---

## 11. Acceptance Checklist

- User can open the Drives page and see local drives with health and temperature
- User can inspect detailed SMART/NVMe information
- User can start a safe self-test where supported
- User can abort a self-test where supported
- Drive temperatures appear in the existing fan-curve workflow
- Drive-specific alerts flow through existing notification channels
- The feature degrades gracefully when `smartctl` is unavailable or blocked
- Python and C# backends behave the same for all new `/api/drives*` routes

---

## 12. Definition Of Done

The work is complete only when all of the following are true:
- The design spec and this checklist are both satisfied
- Python tests pass
- Frontend build passes
- C# build passes
- New parity tests cover both backends
- Docker docs and compose guidance are updated
- `AUDIT.md` and any relevant inventory docs are updated to include the new drive monitoring surface

