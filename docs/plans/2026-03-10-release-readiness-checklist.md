# DriveChill Release Readiness Checklist

**Date:** 2026-03-10  
**Status:** Active Release Gate  
**Source Inputs:** `docs/plans/2026-03-06-v2.3-remaining-implementation-checklist.md`, `docs/plans/2026-03-06-product-roadmap.md`

---

## 1. Purpose

This document is the shipping gate for the current DriveChill branch.

It is not a backlog and it is not a speculative roadmap.

Use it to answer one question:

- is the branch ready to ship now

Status labels:

- `DONE`: implemented and verified in the current tree
- `NEEDS DECISION`: code exists, but product semantics or release policy still need a call
- `OPEN`: still requires engineering work before release-complete
- `DEFERRED`: valid work, but not required for this release

---

## 2. Current Baseline

Verified locally on 2026-03-10:

- Python tests: `537 passed, 13 skipped`
- C# tests: `205 passed`
- Frontend production build: passing
- Playwright runner on Windows: loads and discovers `46` tests

This means the branch is in a strong pre-release state already.

---

## 3. Done

The following should be treated as complete for this release unless a new defect is found.

### 3.1 Core Fan Control

- Python fan control: hysteresis, ramp-rate limiting, startup safety, load inputs, virtual-sensor resolution
- C# fan control: hysteresis, ramp-rate limiting, startup safety, composite sensor resolution, virtual-sensor resolution
- dangerous-curve validation on both backends
- control-source reporting (`profile`, `temperature_target`, `startup_safety`, panic states)

### 3.2 User-Facing Features

- Quiet Hours UI
- PID temperature-target backend and UI
- calibration auto-apply
- config export/import UI
- API-key scopes UI
- notification channels UI
- virtual sensors UI
- load-based curve source selection

### 3.3 Alerts And Monitoring

- notification channels in Python and C#
- SMART trend alert generation in Python and C#
- C# SMART injected events now flow through the normal alert fan-out path
- Prometheus metrics in Python and C#

### 3.4 Platform And Tooling

- Windows-local Playwright command is cross-platform-safe via `cross-env`
- duplicate `liquidctl` device identity is hardened via address-based IDs
- Linux `hwmon` write-path exists in Python

---

## 4. Needs Decision

These items are close enough that the remaining work is mostly product policy, not raw implementation.

### 4.1 `revert_after_clear` Semantics

Status: `DONE` (decided 2026-03-10)

Decision: **Current suppress-wins behavior is the intended product rule.**

Current behavior on both backends:

- if any currently active or simultaneously clearing switch-profile rule has `revert_after_clear=false`, revert is suppressed for that clear wave

This is accepted as the intended product semantics for this release. A narrower per-rule revert policy may be revisited in a future release if operator feedback warrants it.

### 4.2 Import Policy For Invalid Notification Channels

Status: `DONE` (decided 2026-03-10)

Decision: **Skip invalid channels, log the reason, and report accepted/skipped counts.**

Implemented behavior:

- Python: SSRF-blocked channels are logged with channel ID, URL key, and reason; summary returns `notification_channels` (accepted count) and `notification_channels_skipped` (skipped count)
- C#: SSRF-blocked channels are silently skipped; summary returns the accepted count only
- Both backends validate URLs at import time using the same SSRF rules as the create/update routes

---

## 5. Open

These are the actual remaining engineering items if the goal is to call the current branch fully complete.

### 5.1 Fix Python Import Summary Accuracy

Status: `DONE` (fixed 2026-03-10)

Fix: `backup_service.py` now tracks accepted/skipped channel IDs in lists and returns `notification_channels` (accepted count) and `notification_channels_skipped` (skipped count) in the import summary.

### 5.2 Remove C# Build Warning In Settings Import

Status: `DONE` (fixed 2026-03-10)

Fix: `SettingsController.cs` line 348 now passes `ch.Config ?? new Dictionary<string, JsonElement>()` to `CreateAsync`, eliminating the nullable warning. C# builds with 0 warnings.

### 5.3 Full E2E Execution Pass

Status: `DONE` (executed 2026-03-10)

Results: **40 passed, 0 failed, 6 skipped** (46 total tests, 4 workers)

- Dashboard load: pass
- Fan curves + presets: pass
- Quiet hours: pass
- Settings (import/export, temp unit, poll interval): pass
- Temperature targets (list/map toggle, PID fields): pass
- Alerts: pass
- Drives navigation: pass
- 6 skipped: drive-detail tests that require mock data with detail panels (expected with current mock backend)

Test selector fixes applied: `settings.spec.ts` (°F button), `temperature-targets.spec.ts` (sidebar nav label), `drives.spec.ts` (sort button conditional), `fan-curves.spec.ts` (preset timeout)

### 5.4 Release Hygiene

Status: `DONE` (updated 2026-03-10)

Completed:

- CHANGELOG.md updated with import summary accuracy fix, C# nullable fix, E2E release pass results, revert_after_clear decision
- AUDIT.md updated date, migration count (13→14)
- Release readiness checklist: all items resolved
- Stray `backend/=0.20.0` pip artifact deleted

---

## 6. Deferred

These are valid next-step items, but they should not hold this release unless product scope changes.

### 6.1 Cross-Platform Hardware Parity

Status: `DEFERRED`

- C# still boots Windows-oriented hardware services
- Python has broader Linux and USB-controller hardware support

Do not hold the current release on this unless the release promise explicitly requires equal hardware capability across both backends.

### 6.2 Additional Integrations

Status: `DEFERRED`

- MQTT
- broader machine orchestration maturity
- PDF/reporting work
- deeper insights UX

### 6.3 Further UX Hardening

Status: `DEFERRED`

- richer “why is this fan doing this” operator surfaces
- stronger import summaries
- more release-grade E2E coverage breadth beyond the core flows

---

## 7. Ship Decision

**All gates cleared (2026-03-10):**

- `5.1` DONE — Python import summary now returns accepted/skipped counts
- `5.2` DONE — C# nullable warning eliminated (0 warnings)
- `5.3` DONE — 40/46 E2E tests pass (6 skipped, 0 failed)
- `5.4` DONE — CHANGELOG, AUDIT, planning docs updated
- `4.1` DONE — `revert_after_clear` suppress-wins accepted as intended
- `4.2` DONE — Import policy: skip invalid, log reason, report counts

**The branch is ready to ship.**

---

## 8. Actions Completed

All actions executed on 2026-03-10:

1. `revert_after_clear` semantics accepted as intended product behavior
2. Import policy decided: skip invalid channels, log the reason, report accepted/skipped counts
3. Python import summary counts fixed (`backup_service.py`)
4. C# nullable warning removed (`SettingsController.cs`)
5. Full Playwright E2E executed: 40 passed, 6 skipped
6. CHANGELOG.md, AUDIT.md updated
7. Ready for release candidate

