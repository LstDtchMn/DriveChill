# DriveChill v2.3 Audit Remediation Execution Plan

**Date:** 2026-03-06  
**Status:** Ready For Execution  
**Audience:** Claude execution pass  
**Primary Inputs:**  
- `docs/plans/2026-03-06-v2.3-remaining-implementation-checklist.md`
- `docs/plans/v2.3-proposal.md`
- current audit findings from 2026-03-06

---

## 1. Purpose

This plan is the execution handoff for the concrete issues still preventing the
current branch from being considered release-complete.

This is not a feature wishlist. It is a remediation plan for:
- security defects
- runtime wiring gaps
- config durability gaps
- hardware identity edge cases
- missing regression coverage

Use this document as the source of truth for the next Claude implementation
pass. Do not re-open already completed v2.3 release-1 work unless a fix below
requires touching it.

---

## 2. What This Plan Must Finish

The current branch is close, but not complete. The remaining work is:

1. Close the notification-channel SSRF gap in Python and C#
2. Make `revert_after_clear` work correctly in Python and C#
3. Finish C# runtime wiring for notification channels and SMART alert delivery
4. Make backup/export/import durable for new alert and notification data
5. Harden `liquidctl` device identity for duplicate hardware
6. Add direct regression tests for each fix
7. Re-run validation and update docs so the branch status is accurate

---

## 3. Non-Goals

Do not expand scope into new features during this pass.

Specifically out of scope unless required for one of the fixes below:
- new notification providers
- MQTT
- new SMART rule types
- redesigning alert architecture
- redesigning backup format beyond the fields needed here
- `hwmon` or `liquidctl` feature expansion beyond identity correctness
- frontend redesign

If a fix appears to require broader redesign, stop and document the blocker
instead of improvising a larger system change.

---

## 4. Execution Rules

Follow these rules while implementing:

1. Fix correctness before refactoring style.
2. Prefer reusing existing security helpers over introducing parallel logic.
3. Preserve backward compatibility for persisted config where practical.
4. Add regression tests in the same pass as each fix.
5. Do not claim parity unless the runtime path is actually wired and tested.
6. Do not leave "model-only" fields that the runtime ignores.
7. Do not modify unrelated dirty files unless the change is required.

---

## 5. Ordered Execution Plan

Implement in this exact order:

1. Security: notification-channel SSRF remediation
2. Behavior correctness: `revert_after_clear`
3. C# runtime wiring: notification channels and SMART delivery
4. Durability: backup/export/import completeness
5. Hardware hardening: `liquidctl` duplicate-device identity
6. Full targeted regression coverage
7. Validation run and doc closeout

Reason: later items depend on the semantics established by earlier ones.

---

## 6. Phase 1: Notification-Channel SSRF Remediation

### 6.1 Problem

The current notification-channel route validation only checks for `http://` or
`https://`. That still permits:
- `127.0.0.1`
- `localhost` variants not caught by string checks
- RFC1918 private IPs
- link-local metadata targets such as `169.254.169.254`
- DNS rebinding if a hostname resolves safely at save time and unsafely later

The repository already contains shared outbound URL validation utilities. Reuse
them instead of maintaining ad hoc route-specific checks.

### 6.2 Python tasks

Replace route-local scheme validation with the existing URL security helpers.

Targets:
- `backend/app/api/routes/notification_channels.py`
- `backend/app/services/notification_channel_service.py`
- `backend/app/utils/url_security.py` (reuse only; extend only if truly needed)

Required implementation:
- On create/update:
  - validate `config["url"]`
  - validate `config["webhook_url"]`
  - reject loopback, link-local, private, reserved, multicast, and unresolved hosts
- At send time:
  - revalidate the stored destination immediately before the request
  - fail closed if revalidation fails
  - log a useful warning without leaking secrets

Preferred implementation pattern:
- route layer uses `validate_outbound_url(...)`
- send path uses `validate_outbound_url_at_request_time(...)`

### 6.3 C# tasks

Replace controller-local prefix checks with the shared C# URL security helper.

Targets:
- `backend-cs/Api/NotificationChannelsController.cs`
- `backend-cs/Services/NotificationChannelService.cs`
- `backend-cs/Utils/UrlSecurity.cs` (reuse only; extend only if necessary)

Required implementation:
- On create/update:
  - validate `url` and `webhook_url` via `UrlSecurity.TryValidateOutboundHttpUrl`
- At send time:
  - revalidate before sending the request
  - reject unsafe destinations even if the saved config was previously accepted

### 6.4 Tests

Add direct regression tests for both save-time and send-time enforcement.

Python:
- route create rejects `http://127.0.0.1/...`
- route create rejects `http://169.254.169.254/...`
- route create rejects `http://192.168.1.10/...`
- send path refuses a destination that fails request-time validation

C#:
- controller create rejects loopback/private URL configs
- controller update rejects loopback/private URL configs
- delivery service refuses request-time-invalid targets

Likely test files:
- `backend/tests/test_notification_channel_service.py`
- add a notification-channel route/API test file if one does not already exist
- `backend-cs/Tests/NotificationChannelsControllerTests.cs`
- `backend-cs/Tests/NotificationChannelServiceTests.cs`

### 6.5 Exit criteria

- no route-level ad hoc scheme-only validation remains
- both backends use shared outbound URL validation helpers
- both backends revalidate immediately before sending
- direct SSRF regression tests exist and pass

---

## 7. Phase 2: Make `revert_after_clear` Real

### 7.1 Problem

`AlertAction.revert_after_clear` exists in the model and is serialized, but the
runtime always reverts to the pre-alert profile after action rules clear. That
is incorrect behavior on both backends.

### 7.2 Python tasks

Targets:
- `backend/app/services/alert_service.py`
- any alert rule route/model file if validation changes are needed

Required implementation:
- when an action rule fires, preserve enough information to know whether the
  active alert stack should revert after clear
- when the last relevant action rule clears:
  - revert only if at least one applicable active rule requested revert
  - do not revert when all cleared rules had `revert_after_clear=False`
- preserve current "most recently fired wins" semantics

Do not break:
- multiple overlapping action rules
- fallback to another still-active action rule when a newer one clears
- no-revert behavior when no pre-alert profile was recorded

### 7.3 C# tasks

Targets:
- `backend-cs/Services/AlertService.cs`
- `backend-cs/Models/AlertModels.cs` if helper properties are required

Required implementation:
- same semantics as Python
- no silent ignoring of `RevertAfterClear`

### 7.4 Tests

Add direct behavior tests, not only serialization tests.

Required cases in both Python and C#:
- single action rule with `revert_after_clear=true` reverts on clear
- single action rule with `revert_after_clear=false` does not revert
- two overlapping action rules where:
  - newer rule clears and older still-active rule takes over
  - all rules clear and revert occurs only when required by semantics
- no pre-alert profile recorded -> no revert

Targets:
- `backend/tests/test_alert_profile_switching.py`
- `backend-cs/Tests/AlertServiceTests.cs`

### 7.5 Exit criteria

- runtime behavior matches the serialized contract
- `revert_after_clear=false` has direct coverage in Python and C#

---

## 8. Phase 3: Finish C# Runtime Wiring

### 8.1 Problem

The C# codebase contains new services for notification channels and SMART trend
detection, but the runtime path is incomplete:
- normal alert firing does not dispatch notification channels
- SMART trend detection logs alerts but does not inject them into the alert
  event/notification pipeline

This means the implementation is incomplete even though the services exist.

### 8.2 Notification-channel wiring in C#

Targets:
- `backend-cs/Services/SensorWorker.cs`
- `backend-cs/Services/NotificationChannelService.cs`
- `backend-cs/Services/AlertService.cs` if a better dispatch point is needed

Required implementation:
- when normal alert events fire in C#, dispatch notification channels alongside:
  - webhooks
  - email
  - push notifications
- keep delivery fire-and-forget behavior consistent with current alert fan-out
- log and swallow channel failures so the poll loop does not crash

Preferred implementation:
- add `NotificationChannelService` to the worker constructor and include
  `SendAlertAllAsync(...)` in the concurrent dispatch fan-out

### 8.3 SMART trend injection in C#

Targets:
- `backend-cs/Services/SmartTrendService.cs`
- `backend-cs/Services/DriveMonitorService.cs`
- `backend-cs/Services/AlertService.cs`

Required implementation:
- SMART trend detections must become actual alert events, not log-only messages
- those events must flow through the same outward delivery channels as normal
  alerts
- event payload should include:
  - stable synthetic rule/event ID
  - sensor/drive identity
  - real threshold
  - real actual value
  - human-readable message

Preferred implementation:
- add a public synthetic-event injection method to C# `AlertService`, mirroring
  the Python pattern
- have `SmartTrendService` produce structured alerts with real value/threshold
  data
- have `DriveMonitorService` or `SmartTrendService` inject those events through
  `AlertService`

Do not leave SMART alerts as logging-only.

### 8.4 Tests

Add direct C# tests for:
- normal alert -> notification channel dispatch path
- SMART trend condition -> alert event injection
- SMART trend synthetic event -> outward delivery path
- duplicate SMART polls do not spam events while the same condition remains active

Targets:
- `backend-cs/Tests/SensorWorkerTests.cs` if present, otherwise add focused tests
- `backend-cs/Tests/SmartTrendServiceTests.cs`
- `backend-cs/Tests/DriveMonitorServiceTests.cs`

### 8.5 Exit criteria

- C# normal alerts can reach notification channels
- C# SMART trend alerts create real alert events
- C# SMART trend alerts participate in outward delivery, not logging only

---

## 9. Phase 4: Backup / Export / Import Durability

### 9.1 Problem

New persisted data is currently lost across backup/export/import:
- Python portable backup omits `alert_rules.action_json`
- Python portable backup omits `notification_channels`
- C# settings export/import omits notification channels

That makes the branch unsafe to ship as a release-complete configuration system.

### 9.2 Python backup tasks

Targets:
- `backend/app/services/backup_service.py`
- `backend/tests/test_backup_restore.py`

Required implementation:
- export `action_json` with alert rules
- restore `action_json` back into `alert_rules`
- export notification channels
- restore notification channels
- preserve backward compatibility with older backups that do not include the new
  fields

Recommended backup shape:
- keep existing top-level structure
- add a new top-level `notification_channels` array
- include `action_json` in each exported alert rule row

### 9.3 C# export/import tasks

Targets:
- `backend-cs/Api/SettingsController.cs`
- `backend-cs/Services/DbService.cs`
- `backend-cs/Tests/SettingsControllerTests.cs`

Required implementation:
- include notification channels in export payload
- import notification channels back into storage
- replace existing imported channels deterministically, or document/implement a
  merge strategy explicitly
- do not drop existing alert action data when importing alert rules

### 9.4 Tests

Python:
- export/import round-trip preserves `action_json`
- export/import round-trip preserves notification channels
- restoring an old-format backup without these fields still succeeds

C#:
- export includes notification channels
- import restores notification channels
- export/import round-trip preserves alert actions if they are part of serialized
  alert rules

### 9.5 Exit criteria

- no newly added persisted feature is silently lost on backup/export/import
- backward compatibility for older backup files is preserved

---

## 10. Phase 5: Harden `liquidctl` Device Identity

### 10.1 Problem

The Python `liquidctl` backend currently:
- matches devices by description
- derives stable IDs from description only

That fails when two identical devices are attached.

### 10.2 Tasks

Targets:
- `backend/app/hardware/liquidctl_backend.py`
- `backend/tests/test_liquidctl_backend.py`

Required implementation:
- derive device identity from a stable unique discriminator:
  - prefer address or serial if available
  - fall back only when the provider truly gives no stable identity
- ensure command targeting is unique per device, not description-only
- ensure generated fan IDs remain deterministic and non-colliding

Preferred implementation:
- incorporate sanitized `address` into the device prefix
- use device-unique match arguments if supported by the `liquidctl` CLI contract
- if CLI matching cannot be made unique, document the limitation and fail
  explicitly for ambiguous duplicate devices instead of silently controlling the
  wrong unit

### 10.3 Tests

Required cases:
- two devices with the same description generate distinct IDs
- setting fan speed targets the correct device
- discovery remains stable for a single device

### 10.4 Exit criteria

- duplicate identical devices no longer collide silently
- tests cover the duplicate-device path

---

## 11. Phase 6: Validation Run

After implementation, run validation in this order.

### 11.1 Python

Run at minimum:
- targeted tests for auth/alerts/notifications/backup/liquidctl/SMART
- full backend suite if targeted tests pass

Suggested commands:
- `python -m pytest backend/tests/test_alert_profile_switching.py -q`
- `python -m pytest backend/tests/test_notification_channel_service.py -q`
- `python -m pytest backend/tests/test_backup_restore.py -q`
- `python -m pytest backend/tests/test_liquidctl_backend.py -q`
- `python -m pytest backend/tests -q`

### 11.2 C#

Run at minimum:
- targeted tests for alerts/notification channels/settings/drive monitor/SMART
- full solution tests if targeted tests pass

Suggested commands:
- `dotnet test DriveChill.sln --filter "FullyQualifiedName~AlertServiceTests"`
- `dotnet test DriveChill.sln --filter "FullyQualifiedName~Notification"`
- `dotnet test DriveChill.sln --filter "FullyQualifiedName~SmartTrend"`
- `dotnet test DriveChill.sln`

### 11.3 Frontend

Only run frontend validation if the remediation touched frontend-visible
contracts or import/export payload assumptions.

Suggested commands:
- `npm --prefix frontend run build`

### 11.4 Exit criteria

- all newly added regression tests pass
- no existing tests regress
- no new warnings/errors are introduced without explanation

---

## 12. Phase 7: Doc Closeout

After code and tests are green, update docs to match the new state.

Targets:
- `docs/plans/2026-03-06-v2.3-remaining-implementation-checklist.md`
- `CHANGELOG.md`
- `AUDIT.md` if it is tracking branch readiness

Required updates:
- mark the audit remediation items complete
- record any intentionally deferred item that remains
- state clearly that the previous blockers were:
  - SSRF hardening
  - `revert_after_clear` runtime mismatch
  - C# runtime wiring gap
  - backup/export/import data loss
  - `liquidctl` duplicate-device ambiguity

---

## 13. Final Definition Of Done

This remediation pass is complete only when all of the following are true:

- notification-channel URL handling is hardened at save time and request time
- `revert_after_clear` behaves correctly in Python and C#
- C# notification channels are wired into the normal alert delivery path
- C# SMART trend alerts become real alert events and outward notifications
- Python backup/export/import preserves alert actions and notification channels
- C# settings export/import preserves notification channels
- `liquidctl` duplicate-device ambiguity is removed or explicitly failed closed
- direct regression tests exist for each bug fixed here
- validation passes
- docs reflect reality

If any item above remains open, do not describe the branch as release-complete.
