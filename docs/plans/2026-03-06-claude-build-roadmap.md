# DriveChill Claude Build Roadmap

**Date:** 2026-03-06  
**Status:** Ready For Claude Execution  
**Audience:** Claude implementation pass  
**Source Documents:**  
- `docs/plans/2026-03-06-product-roadmap.md`
- `docs/plans/2026-03-06-audit-remediation-execution-plan.md`
- `docs/plans/2026-03-06-v2.3-remaining-implementation-checklist.md`

---

## 1. Purpose

This document is the Claude-facing execution version of the roadmap.

It is not a strategy memo. It is a build order with:
- exact priorities
- implementation boundaries
- file targets
- validation requirements
- stop conditions

Claude should follow this document in order and avoid improvising outside the
defined scope unless a blocker is discovered.

---

## 2. Claude Operating Rules

Claude should follow these rules during execution:

1. Do not re-open completed release-1 work unless a fix below requires it.
2. Fix one milestone at a time.
3. Add regression tests in the same pass as every code change.
4. Reuse existing helpers and infrastructure instead of duplicating logic.
5. Do not broaden scope into new features not named here.
6. If a required fix appears to need architectural redesign, stop and document:
   - the blocker
   - why the current design is insufficient
   - the smallest safe next step
7. Before marking any item complete, verify the runtime path, not just the model
   or controller layer.

---

## 3. What Claude Should Build First

Claude should execute in this order:

1. Milestone A1: release blockers
2. Milestone A2: release hardening
3. Milestone A3: release validation and closeout
4. Milestone B1: virtual sensors
5. Milestone B2: load-based inputs
6. Milestone B3: control transparency

Do not start Milestone B until Milestone A is complete.

### 3.1 Status Tracking

| Item | Status | Notes |
|------|--------|-------|
| A1.1 C# SMART trend delivery parity | ✅ DONE | `AlertService.DrainInjectedEvents()` + SensorWorker fan-out |
| A1.2 Notification-channel import-path validation | ✅ DONE | SSRF check in `backup_service.py` + `SettingsController.cs` |
| A1.3 Windows-local Playwright fix | ✅ DONE | `cross-env` in devDependencies + `playwright.config.ts` |
| A1.4 Python auth-helper cleanup | ✅ DONE | No stale `require_write_role`; regression test in `test_auth_http.py` |
| A2.1 Startup safety profile | ✅ DONE | 50%/15s in both backends; Python tests in `test_fan_service_startup_safety.py` |
| A2.2 Frontend E2E for top workflows | ✅ DONE | `quiet-hours.spec.ts` added; settings/fan-curves/temp-targets extended |
| A3 Release validation and closeout | ✅ DONE | 537 Python + 205 C# passing; frontend builds; docs updated |
| B1 Virtual sensors | ✅ DONE | Service + CRUD routes + DB migration + types + Settings UI; all 6 types |
| B2 Load-based inputs | ✅ DONE | `cpu_load`/`gpu_load` in curve engine + frontend Load sensor group |
| B3 Control transparency | ✅ DONE | `control_sources` per-fan in both backends; fan status API + WS + frontend badge |

Use these statuses on every item as work progresses:

- `DONE`
- `IN PROGRESS`
- `TODO`
- `BLOCKED`
- `REVIEW REQUIRED`

Initial status for this roadmap:

| Item | Status | Note |
|------|--------|------|
| A1.1 C# SMART trend delivery parity | REVIEW REQUIRED | Prior implementation claims exist, but runtime delivery parity must be re-audited before closing |
| A1.2 Notification-channel import-path validation | REVIEW REQUIRED | Validation work may exist, but import behavior must report invalid items explicitly to be release-complete |
| A1.3 Windows-local Playwright fix | TODO | Open |
| A1.4 Python auth-helper cleanup | TODO | Open |
| A2.1 Startup safety profile | TODO | Open |
| A2.2 Frontend E2E for top workflows | BLOCKED | Depends on A1.3 |
| A3.1 Validation | TODO | Run after A1 and A2 |
| A3.2 Docs closeout | TODO | Run after validation |
| B1 Virtual sensors | TODO | Do not start before Milestone A complete |
| B2 Load-based inputs | TODO | Depends on B1 |
| B3 Control transparency | TODO | Depends on B1 and B2 |

---

## 4. Milestone A1: Release Blockers

These items are the minimum remaining work before the current branch can be
called release-ready.

### A1.1 C# SMART trend delivery parity

**Status:** `REVIEW REQUIRED`

**Problem**
- SMART trend alerts are injected into `AlertService`, but they do not fully
  flow through the normal delivery pipeline.

**Claude tasks**
- inspect the current C# SMART event path end-to-end
- ensure SMART-generated events participate in:
  - webhooks
  - email
  - push
  - notification channels
- ensure the behavior is actually runtime-complete, not only test-complete

**Primary targets**
- `backend-cs/Services/DriveMonitorService.cs`
- `backend-cs/Services/AlertService.cs`
- `backend-cs/Services/SensorWorker.cs`
- `backend-cs/Tests/SmartTrendServiceTests.cs`
- add worker/integration-focused C# tests if needed

**Definition of done**
- SMART trend alerts reach the same delivery pipeline as normal alerts
- tests prove the runtime path, not only event creation
- if prior work already exists, Claude must still verify the live runtime path
  before flipping this item to `DONE`

### A1.2 Notification-channel import-path validation

**Status:** `REVIEW REQUIRED`

**Problem**
- create/update validation is now stronger, but import paths can still persist
  channel configs that normal API writes would reject

**Claude tasks**
- validate imported notification channels in both backends
- choose a clear behavior:
  - reject whole import on invalid channel config, or
  - skip invalid channels and report them explicitly
- keep behavior consistent and documented

**Default policy for this roadmap**
- do not silently skip invalid channels
- if using skip behavior, return/import-log an explicit summary with counts and reasons
- if product/API constraints make that awkward, stop and ask for policy confirmation

**Primary targets**
- `backend/app/services/backup_service.py`
- `backend-cs/Api/SettingsController.cs`
- related tests:
  - `backend/tests/test_backup_restore.py`
  - `backend-cs/Tests/SettingsControllerTests.cs`

**Definition of done**
- imported channel data cannot bypass normal safety policy silently
- import result clearly reports what happened

### A1.3 Windows-local Playwright fix

**Status:** `TODO`

**Problem**
- local Windows E2E startup is still broken

**Claude tasks**
- make Playwright startup command cross-platform
- preserve Linux CI behavior
- do not add OS-specific config branches unless unavoidable
- explicitly handle cross-platform shell differences:
  - Unix inline env assignment
  - command chaining syntax
  - quoting rules in `webServer.command`

**Primary targets**
- `frontend/playwright.config.ts`
- `frontend/package.json`
- optional helper script under `frontend/scripts/`

**Validation**
- local Windows startup works
- frontend build still works
- existing CI assumptions are unchanged

### A1.4 Python auth-helper cleanup

**Status:** `TODO`

**Problem**
- stale auth helper still misleads audits and maintenance

**Claude tasks**
- inspect the current `backend/app/api/dependencies/auth.py` and identify the
  exact stale or redundant auth dependency path before editing
- remove or align the stale path so the code matches the real auth path
- add or preserve an explicit regression test for viewer-role API-key write blocking

**Primary targets**
- `backend/app/api/dependencies/auth.py`
- `backend/tests/test_auth_http.py`

**Definition of done**
- no stale helper implies incorrect security posture
- auth behavior remains test-backed

---

## 5. Milestone A2: Release Hardening

These items improve safety and release confidence without changing the product direction.

### A2.1 Startup safety profile

**Status:** `TODO`

**Claude tasks**
- add a short startup safety window using a safe fixed fan speed
- keep first pass simple:
  - built-in default duration
  - built-in default speed
  - no new user-facing editor
- ensure panic or explicit release behavior still overrides safety mode

**Primary targets**
- `backend/app/main.py`
- `backend/app/services/fan_service.py`
- `backend-cs/Services/FanService.cs`
- `backend-cs/Services/SensorWorker.cs`
- relevant tests in both backends

**Definition of done**
- safe fan behavior exists before normal control fully settles
- tests prove runtime behavior, not only flag state:
  - safe speed is actually applied during startup
  - normal control resumes after the safety window or normal readiness signal
  - panic/release paths still override startup safety as expected

### A2.2 Frontend E2E for top workflows

**Status:** `BLOCKED` until `A1.3` is complete

**Claude tasks**
- add or extend Playwright coverage for:
  - Quiet Hours CRUD
  - temperature target PID controls
  - benchmark calibration auto-apply
  - config export/import

**Primary targets**
- `frontend/e2e/quiet-hours.spec.ts`
- `frontend/e2e/temperature-targets.spec.ts`
- `frontend/e2e/fan-curves.spec.ts`
- `frontend/e2e/settings.spec.ts`

**Definition of done**
- top user workflows are covered by browser-level tests
- local Windows Playwright path is working well enough to validate them

---

## 6. Milestone A3: Release Validation And Closeout

Claude should not treat this as optional.

### A3.1 Validation

Run at minimum:

**Python**
- `python -m pytest backend/tests/test_auth_http.py -q`
- `python -m pytest backend/tests/test_backup_restore.py -q`
- `python -m pytest backend/tests/test_notification_channel_service.py -q`
- `python -m pytest backend/tests -q`

**C#**
- `dotnet test DriveChill.sln --filter "FullyQualifiedName~AlertServiceTests"`
- `dotnet test DriveChill.sln --filter "FullyQualifiedName~SmartTrend"`
- `dotnet test DriveChill.sln --filter "FullyQualifiedName~SettingsControllerTests"`
- `dotnet test DriveChill.sln`

If solution-level test execution stops being reliable, Claude may fall back to
the test project directly:

- `dotnet test backend-cs/Tests/DriveChill.Tests.csproj`

**Frontend**
- `npm --prefix frontend run build`
- relevant Playwright commands after the Windows fix

### A3.2 Docs closeout

Update:
- `CHANGELOG.md`
- `AUDIT.md`
- `docs/plans/2026-03-06-v2.3-remaining-implementation-checklist.md`
- `docs/plans/2026-03-06-product-roadmap.md` if milestone status changes materially

### A3.3 Definition of done

Milestone A is complete only when:
- release blockers are fixed
- release-hardening items are complete
- tests are green
- docs match the real branch state

---

## 7. Milestone B1: Virtual Sensors

Claude should start this only after Milestone A is complete.

### B1.1 Objective

Create the control-model foundation needed for more expressive fan behavior.

### B1.2 Required feature set

**Status:** `TODO`

Support these virtual sensor types in the first shipping version:
- `max`
- `min`
- `avg`
- `weighted`
- `delta`
- `moving_avg`

Do not defer `delta`.

**Temporal-state rule**
- first pass may keep temporal state in memory only
- `delta` and `moving_avg` do not need persistence across restart in v1
- if Claude discovers that restart persistence is required to keep semantics
  coherent, stop and document the decision point before widening scope

### B1.3 Claude tasks

- add storage model and migration
- add CRUD endpoints in Python and C#
- add service-layer evaluation
- integrate virtual sensor resolution into control paths that consume readings
- add unit tests and route tests
- add frontend CRUD UI

### B1.4 Primary targets

Python:
- `backend/app/db/migrations/`
- `backend/app/services/virtual_sensor_service.py`
- `backend/app/api/routes/virtual_sensors.py`

C#:
- `backend-cs/Services/DbService.cs`
- `backend-cs/Services/VirtualSensorService.cs`
- `backend-cs/Api/VirtualSensorsController.cs`

Frontend:
- `frontend/src/components/`
- `frontend/src/lib/api.ts`
- `frontend/src/lib/types.ts`

### B1.5 Definition of done

- virtual sensors are persisted, editable, and evaluated correctly
- fan-control and related consumers can use them
- direct tests exist for every virtual sensor type

---

## 8. Milestone B2: Load-Based Inputs

### B2.1 Objective

Allow users to build fan behavior using load signals, not only temperatures.

### B2.2 Claude tasks

- expose load-based inputs cleanly in the curve/input model
- allow CPU and GPU load to be selected as control inputs
- reuse virtual sensor infrastructure where appropriate
- ensure UI makes load-based selection understandable
- add tests for control behavior and UI/API integration

### B2.3 Primary targets

Python:
- `backend/app/models/sensors.py`
- curve and control services that consume readings

C#:
- `backend-cs/Services/FanService.cs`
- related sensor/control models

Frontend:
- curve editor and related control-selection UI

### B2.4 Definition of done

- users can select load-based inputs in supported workflows
- behavior is validated by tests

---

## 9. Milestone B3: Control Transparency

### B3.1 Objective

Make the product explain its own behavior.

### B3.2 Claude tasks

- expose why a fan is currently at its current speed
- expose current control source:
  - manual
  - profile
  - quiet hours
  - alert
  - startup safety
- temperature target
- virtual sensor chain
- add lightweight UI presentation for this data

**Minimum deliverables**
- an API-visible control-state explanation surface
- a UI-visible presentation for the currently active explanation
- WebSocket transport is optional in the first pass unless the API-only path
  proves too stale for useful UX

### B3.3 Definition of done

- an advanced user can answer "why is this fan at this speed right now?" from
  the product itself

---

## 10. Explicit Deferrals

Claude should not start these in this pass unless asked explicitly:

- MQTT / Home Assistant integration
- deeper reporting UX
- PDF reports
- C# `liquidctl` parity
- broad Linux hardware expansion beyond already-scoped tasks
- marketplace / plugin concepts

These are roadmap items, not immediate execution work.

---

## 11. Claude Stop Conditions

Claude should stop and ask for direction if:

1. a Milestone A fix requires a broader architecture rewrite
2. virtual sensors require a breaking API contract change that affects shipped UI
3. Playwright cannot be made cross-platform without changing team tooling assumptions
4. backup/import validation policy needs product-level policy choice
   - reject-whole-import vs skip-invalid-items
5. a hardware-specific path cannot be validated meaningfully without real device access

When stopping, Claude should provide:
- the blocker
- what was learned
- the smallest safe forward options

---

## 12. Claude Prompts

### 12.1 Milestone A Prompt

> Execute Milestone A from `docs/plans/2026-03-06-claude-build-roadmap.md` in order.  
> Use and update the status tracking in Section 3.1 as you progress.  
> Do not mark A1.1 or A1.2 done from prior summaries alone; verify the live runtime path first.  
> Fix only the items in Milestone A unless a blocker forces escalation.  
> Add regression tests with each fix.  
> Verify runtime behavior, not just model/controller behavior.  
> Run the listed validation commands before closing the milestone.  
> Update docs/changelog/audit files so the branch state matches reality.  
> If a blocker requires product-policy or architecture decisions, stop and summarize the blocker with the smallest safe next options.

### 12.2 Milestone B Prompt

> Execute Milestone B from `docs/plans/2026-03-06-claude-build-roadmap.md` in order, starting with B1 only after Milestone A is complete.  
> Keep temporal-state handling for `delta` and `moving_avg` in-memory for the first pass unless a blocker requires escalation.  
> Add regression tests and API/UI validation with each feature.  
> Do not start MQTT, broader hardware expansion, or reporting work in this pass.  
> If a feature requires a breaking contract or broader architecture redesign, stop and summarize the smallest safe options.

---

## 13. Final Definition Of Success

Claude succeeds on this roadmap when:

- Milestone A is completed cleanly
- the current branch can be called release-ready with minimal qualification
- Milestone B is ready to start on a stable, well-tested base
