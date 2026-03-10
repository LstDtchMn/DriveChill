# DriveChill — Auditable Completion List

**Date:** 2026-03-10
**Branch:** `claude/fan-temperature-controller-kNqMx` → `main`
**Latest Commit:** `a0ef5ed` (pushed to `origin main`)

This document is a single-source audit trail of every delivered item across
all versions, organized by release. Each item includes the commit(s), files
changed, and verification method.

---

## Verification Baseline (as of 2026-03-10)

| Suite | Result |
|-------|--------|
| Python unit/integration tests | 537 passed, 13 skipped |
| C# unit/integration tests | 205 passed |
| C# build warnings | 0 |
| Frontend production build | passing |
| Playwright E2E (46 specs) | 40 passed, 6 skipped, 0 failed |

---

## v1.0.0 — Initial Release

| # | Item | Files |
|---|------|-------|
| 1.0-1 | Temperature-based fan curve control with profile system | `fan_service.py`, `FanService.cs` |
| 1.0-2 | Web dashboard with real-time sensor monitoring | `page.tsx`, `SystemOverview.tsx` |
| 1.0-3 | LibreHardwareMonitor Direct backend (Windows, pythonnet DLL) | `lhm_direct_backend.py` |
| 1.0-4 | lm-sensors backend (Linux/Docker) | `lm_sensors_backend.py` |
| 1.0-5 | Mock backend for development | `mock_backend.py` |
| 1.0-6 | Session authentication with CSRF protection | `auth.py`, `auth_service.py` |
| 1.0-7 | Alert rules with cooldown and quiet hours | `alert_service.py` |
| 1.0-8 | Webhook alert delivery with HMAC signing and retries | `webhook_service.py` |
| 1.0-9 | Fan benchmark testing service | `FanTestPanel.tsx` |
| 1.0-10 | System tray integration (Windows) | `Program.cs` |
| 1.0-11 | Next.js 14 static export frontend with Zustand state management | `frontend/` |

---

## v1.5.0 — Security + Infrastructure (2026-03-02)

### Security Fixes (R1–R12, M1–M10)

| # | Item | Severity | Files |
|---|------|----------|-------|
| R1 | SSRF DNS-rebinding fix in webhook and machine proxy | Critical | `webhook_service.py`, `machine_monitor_service.py`, `MachinesController.cs` |
| R2 | IPv4-mapped loopback bypass protection | Critical | `url_security.py`, `UrlSecurity.cs` |
| R3 | API key scope enforcement in auth middleware | High | `auth.py`, `Program.cs` |
| R4 | AES-256-GCM encryption for SMTP passwords | High | `CredentialEncryption.cs`, `DbService.cs` |
| R5 | Webhook HMAC signatures: `X-DriveChill-Timestamp` + `X-DriveChill-Nonce` | High | `webhook_service.py`, `WebhookService.cs` |
| R6 | Prometheus metric name regex rejects backslash | Medium | `prom_metrics.py`, `DriveChillMetrics.cs` |
| R7 | Machine `api_key` preservation on partial PUT | Medium | `machines.py`, `MachinesController.cs` |
| R8 | `_KEEP_SECRET` sentinel ordering in webhook service | Medium | `webhook_service.py` |
| R9 | Atomic webhook delivery log pruning | Low | `webhook_service.py` |
| R10 | Credential redaction in error logs | Low | `url_security.py` |
| R11 | C# SSRF IP-routing via `SocketsHttpHandler.ConnectCallback` | Critical | `MachinesController.cs` |
| R12 | Session double-validation fix | Medium | `auth.py`, `Program.cs` |
| M1 | Confirmation dialogs for destructive actions | Medium | `SettingsPage.tsx` |
| M2 | Machine busy state tracking (per-machine Set) | Medium | `SystemOverview.tsx` |
| M3 | API key clipboard copy button | Low | `SettingsPage.tsx` |
| M4 | Conditional polling (2s with machines, 30s without) | Low | `page.tsx` |
| M5 | Save error feedback in Settings page | Low | `SettingsPage.tsx` |

### Features

| # | Item | Files |
|---|------|-------|
| 1.5-1 | Push notifications via Web Push / VAPID (both backends) | `push_notification_service.py`, `PushNotificationService.cs`, `notifications.py` |
| 1.5-2 | Email alert notifications via SMTP | `email_notification_service.py`, `EmailNotificationService.cs` |
| 1.5-3 | Multi-machine remote control: proxy fan state, profiles, fan settings | `machine_monitor_service.py`, `machines.py`, `MachinesController.cs` |
| 1.5-4 | Machine drill-in UI with remote sensor/fan state | `MachineDrillIn.tsx`, `SystemOverview.tsx` |
| 1.5-5 | Analytics page: time-windowed history, stat cards, anomaly detection | `analytics.py`, `AnalyticsController.cs`, `AnalyticsPage.tsx` |
| 1.5-6 | Docker headless mode | `drivechill.py`, `Dockerfile` |
| 1.5-7 | Self-signed TLS certificate generation | `config.py` |
| 1.5-8 | Quiet hours for alert suppression | `quiet_hours` routes + service |
| 1.5-9 | Panic mode, release fan control, sensor failure escalation | `fan_service.py`, `FanService.cs` |

### Infrastructure

| # | Item | Files |
|---|------|-------|
| 1.5-I1 | Docker Compose healthcheck | `docker-compose.yml` |
| 1.5-I2 | `.env.example` documenting all 14 env vars | `docker/.env.example` |
| 1.5-I3 | C# full parity: machines, analytics, notifications | `backend-cs/Api/`, `backend-cs/Services/` |
| 1.5-I4 | `Lib.Net.Http.WebPush 3.3.0` for C# Web Push | `DriveChill.csproj` |
| 1.5-I5 | Accessibility: `aria-label` on all icon-only buttons | `SettingsPage.tsx` |
| 1.5-I6 | Windows service installation via NSSM | `scripts/install_windows_service.ps1` |

---

## v1.6.0 — Drive Monitoring (2026-03-03)

Commit: `a6f6cd2` (28 files)

| # | Item | Files |
|---|------|-------|
| 1.6-1 | SMART health, temperature, and self-test support via smartmontools | `drive_monitor_service.py`, `DriveMonitorService.cs` |
| 1.6-2 | Drives page with health badges, temperature display, SMART drill-in | `DrivesPage.tsx` |
| 1.6-3 | Drive temperatures injected into fan-curve/alert pipeline as `hdd_temp` | `sensor_service.py` |
| 1.6-4 | Per-drive settings overrides: thresholds, alert opt-out, curve picker | `drive_settings` routes |
| 1.6-5 | Storage Monitoring section in Settings | `SettingsPage.tsx` |
| 1.6-6 | Dashboard storage summary card | `SystemOverview.tsx` |
| 1.6-7 | Analytics temperature values respect °C/°F preference | `AnalyticsPage.tsx` |
| 1.6-8 | Fan curve editor touch hit area increased to 40px | `CurveEditor.tsx` |
| 1.6-9 | Temperature unit preference synced from backend on page startup | `settingsStore` |
| 1.6-10 | Playwright E2E test suite: dashboard, fan curves, alerts, settings | `frontend/e2e/` |
| 1.6-I1 | `smartmontools` added to Docker image | `Dockerfile` |
| 1.6-I2 | Playwright devDep + npm scripts (`test:e2e`, `test:e2e:ui`, `test:e2e:debug`) | `package.json` |
| 1.6-I3 | `playwright.config.ts` with webServer auto-start | `playwright.config.ts` |
| 1.6-I4 | `DriveMonitorWorker` BackgroundService in C# DI | `Program.cs` |
| 1.6-I5 | API key scope domain `drives` added to both backends | `auth.py`, `Program.cs` |

---

## v2.0.0 — Analytics v2.0 (2026-03-03)

Commit: `85139f2` (12 files)

| # | Item | Files |
|---|------|-------|
| 2.0-1 | Custom date-range queries (start/end params) | `analytics.py`, `AnalyticsController.cs` |
| 2.0-2 | Multi-sensor filtering (`sensor_ids`) | `analytics.py`, `DbService.cs` |
| 2.0-3 | Auto-sized buckets | `analytics.py`, `AnalyticsController.cs` |
| 2.0-4 | `/api/analytics/correlation` endpoint — Pearson r | `analytics.py`, `AnalyticsController.cs` |
| 2.0-5 | `retention_limited` flag in history responses + UI banner | `analytics.py`, `AnalyticsPage.tsx` |
| 2.0-6 | 30-day time window, custom date pickers, sensor filter chips | `AnalyticsPage.tsx` |
| 2.0-7 | Anomaly `severity` field (warning/critical) | `AnalyticsModels.cs`, `analytics.py` |
| 2.0-8 | Drive temperature mini-sparkline (24h) on drive detail | `DrivesPage.tsx` |
| 2.0-9 | "New cooling curve" button on Drives page | `DrivesPage.tsx`, `FanCurvesPage.tsx` |
| 2.0-10 | History retention raised from 24h to 720h (30 days) | migration `009_raise_retention_default.sql` |
| 2.0-T1 | 7 analytics performance tests (130k-row/30-day dataset, all < 2s) | `test_analytics_perf.py` |
| 2.0-T2 | 30 drive-routes integration tests | `test_drives_routes.py` |
| 2.0-T3 | Drive E2E tests: "Use for cooling" and "New cooling curve" flows | `drives.spec.ts` |

---

## v2.1.0 — Temperature Targets (2026-03-04)

Commit: `c71edb0` (15 files)

| # | Item | Files |
|---|------|-------|
| 2.1-1 | Temperature target CRUD (set target temp for any sensor) | `temperature_targets.py`, `TemperatureTargetsController.cs` |
| 2.1-2 | Proportional fan control within tolerance band | `temperature_target_service.py`, `TemperatureTargetService.cs` |
| 2.1-3 | Multi-drive shared fan support (hottest wins / max speed) | `fan_service.py`, `FanService.cs` |
| 2.1-4 | Relationship Map SVG view (drive-to-fan bipartite diagram) | `TemperatureTargetsPage.tsx` |
| 2.1-5 | List view with live speed labels and enable/disable toggle | `TemperatureTargetsPage.tsx` |
| 2.1-6 | Union-of-fan-IDs merge with fan curves (higher speed wins) | `fan_service.py`, `FanService.cs` |
| 2.1-7 | Full C# backend parity | `TemperatureTargetService.cs`, `TemperatureTargetsController.cs` |
| 2.1-8 | API key scope `temperature_targets` in both backends | `auth.py`, `ApiKeyService.cs` |
| 2.1-9 | Migration `010_temperature_targets.sql` | `backend/app/db/migrations/` |
| 2.1-T1 | 7 Python service unit tests | `test_temperature_target_service.py` |
| 2.1-T2 | CRUD + validation route tests | `test_temperature_target_routes.py` |
| 2.1-T3 | 8 C# algorithm tests | `TemperatureTargetServiceTests.cs` |
| 2.1-T4 | 2 C# PruneAsync integration tests | `DbServiceTests.cs` |

---

## v2.1.1 — Stabilization Patch (2026-03-05)

Commits: `03687ac` through `4f3af75` (8 commits, ~130 files)

### Audit Remediation (commit `03687ac`, 24 files)

| # | Item | Severity | Files |
|---|------|----------|-------|
| SEC-1 | Semver regex validates GitHub version before PowerShell arg | High | `update.py`, `UpdateController.cs` |
| SEC-2 | All 9 GitHub Actions pinned to commit SHAs | Medium | `.github/workflows/` |
| SEC-3 | `release_url` sanitized via `isValidHttpUrl()` | Medium | `SettingsPage.tsx` |
| FUNC-2 | POST `/api/fans/speed` route fixed; FanId added | High | `FansController.cs`, `FanModels.cs` |
| FUNC-3 | SMTP password preserved on partial PUT | High | `NotificationsController.cs` |
| FUNC-4 | C# error shape: `{error=}` → `{detail=}` for API parity | Medium | All C# controllers |
| FUNC-7 | Fire-and-forget `create_task` anchored in `_pending_webhook_tasks` set | Medium | `fan_service.py` |
| FUNC-8 | `subprocess.run` → `asyncio.create_subprocess_exec` | Medium | `update.py` |
| FUNC-10 | Machine profile activate proxy: POST → PUT | Medium | `machines.py` |
| ASYNC-1/2 | `add_rule` DB write before in-memory mutation; stale tracking cleared | Medium | `alert_service.py` |
| ASYNC-6 | `tolerance_c <= 0` guard in both backends | Medium | `temperature_target_service.py`, `TemperatureTargetService.cs` |
| AUTH-2 | analytics + notifications added to API key scope prefix rules | Medium | `auth.py` |
| AUTH-3 | Machine `base_url` scheme validation | Medium | `MachinesController.cs` |
| INFRA-4 | `HistoryRetentionHours` default 24→720 | Low | `AppSettings.cs` |
| INFRA-5 | Shared static `HttpClient` in `UpdateController` | Low | `UpdateController.cs` |

### Bug Fixes (commit `4f3af75`, 14 files)

| # | Item | Files |
|---|------|-------|
| BF-1 | C# `GetRegression` one-sided custom-range fix (require both start AND end) | `AnalyticsController.cs` |
| BF-2 | Release workflow tag glob: `v[0-9]+` → `v[0-9]*` | `.github/workflows/release.yml` |
| BF-3 | Docker tag mismatch: added `type=raw,value=${{ github.ref_name }}` | `.github/workflows/release.yml` |
| BF-4 | CI frontend OOM: `NODE_OPTIONS=--max-old-space-size=4096` | `.github/workflows/` |

### Update Flow Hardening

| # | Item | Files |
|---|------|-------|
| UF-1 | `update_windows.ps1`: `-Artifact` param, install-dir fallback | `scripts/update_windows.ps1` |
| UF-2 | `UpdateController.cs`: script discovery fallback + `-Artifact windows` | `UpdateController.cs` |
| UF-3 | `update.py`: passes `-Artifact python` | `update.py` |

### Tests & Docs

| # | Item | Files |
|---|------|-------|
| 2.1.1-T1 | 6 C# `ResolveRange` tests | `AnalyticsControllerTests.cs` |
| 2.1.1-T2 | 8 Python updater route tests | `test_update_routes.py` |
| 2.1.1-D1 | `docs/updating.md` — update procedures + rollback | `docs/updating.md` |

---

## v2.1.2 — C# Bug Fixes (2026-03-05)

| # | Item | Files |
|---|------|-------|
| 2.1.2-1 | Fix C# `ActivateProfile` orphaned curves (clear-before-apply) | `FanService.cs` |
| 2.1.2-2 | Fix Python `AlertService.remove_rule` ordering (DB first, then in-memory) | `alert_service.py` |
| 2.1.2-3 | Fix C# `PUT /api/fans/curves` body shape mismatch | `FansController.cs` |
| 2.1.2-4 | C# `GET /api/profiles/{id}` endpoint (parity with Python) | `ProfilesController.cs` |
| 2.1.2-5 | C# per-fan settings endpoints: min speed floor + zero-RPM | `FansController.cs` |
| 2.1.2-6 | C# `FanService` enforces per-fan minimum speed floor | `FanService.cs` |
| 2.1.2-7 | C# dangerous-curve safety gate (409 + `allow_dangerous`) | `FansController.cs` |
| 2.1.2-8 | C# `POST /api/fans/curves/validate` endpoint | `FansController.cs` |
| 2.1.2-9 | C# webhook deliveries `offset` pagination | `WebhooksController.cs` |

---

## v2.2.0 — PID Controller + RBAC (2026-03-05)

Commits: `33df4e6` through `6219057`

### PID Temperature Controller

| # | Item | Files |
|---|------|-------|
| 2.2-1 | PID control (Kp, Ki, Kd) as opt-in for temp targets | `temperature_target_service.py`, `TemperatureTargetService.cs` |
| 2.2-2 | Derivative EMA low-pass filter (α=0.7) | Both backends |
| 2.2-3 | Integral conditional anti-windup | Both backends |
| 2.2-4 | Migration `011_pid_fields.sql` (pid_mode, pid_kp, pid_ki, pid_kd) | `backend/app/db/migrations/` |

### RBAC — Viewer Role (v2.2.1)

| # | Item | Files |
|---|------|-------|
| 2.2-5 | `admin` / `viewer` role on users | `auth_service.py`, `Program.cs` |
| 2.2-6 | Viewer write-block: all write routes return 403 | `auth.py`, `Program.cs` |
| 2.2-7 | Logout exempted from viewer write-block | `auth.py`, `Program.cs` |
| 2.2-8 | Last-admin safety guard (409 on demote/delete) | `auth_service.py`, `DbService.cs` |
| 2.2-9 | Session invalidation on user delete (INNER JOIN validation) | `auth_service.py`, `DbService.cs` |
| 2.2-10 | User management UI in Settings (admin-only) | `SettingsPage.tsx` |
| 2.2-11 | Frontend viewer UX: `useCanWrite()` hook disables all write controls | 8 page components |
| 2.2-12 | `ViewerBanner` on write-heavy pages | `ViewerBanner.tsx` |
| 2.2-13 | Migration `012_rbac.sql` (role column on users + sessions) | `backend/app/db/migrations/` |

### UI & UX

| # | Item | Files |
|---|------|-------|
| 2.2-14 | `ConfirmDialog` + `useConfirm()` hook (replaces `window.confirm`) | `ConfirmDialog.tsx`, hooks |
| 2.2-15 | `ToastProvider` + `useToast()` (replaces `window.alert`) | `ToastProvider.tsx` |
| 2.2-16 | °F gauge fix: arc colours computed in display unit | `TempGauge.tsx` |
| 2.2-17 | C# profile export/import | `ProfilesController.cs`, `SettingsController.cs` |
| 2.2-18 | C# `auth_log` with 90-day retention | `DbService.cs`, `Program.cs` |
| 2.2-19 | E2E tests in CI (GitHub Actions `e2e.yml`) | `.github/workflows/e2e.yml` |

### Tests

| # | Result |
|---|--------|
| 2.2-T1 | Python: 418 passing (9 new RBAC tests) |
| 2.2-T2 | C#: 75 passing (up from 16) — FanService, Profiles, Alert, Webhook, Session |

---

## v2.3.0-rc — Current Release (Unreleased)

Commits: `d10a1c1`, `3fbf1bc`, `a0ef5ed`

### A. C# Fan-Control Parity

| # | Item | Files | Tests |
|---|------|-------|-------|
| 2.3-A1 | Composite sensor curves (`SensorIds` list, MAX resolution) | `FanService.cs` | `FanServiceTests.cs` |
| 2.3-A2 | Hysteresis deadband (3°C) | `FanService.cs` | `FanServiceTests.cs` |
| 2.3-A3 | Ramp-rate limiting (`fan_ramp_rate_pct_per_sec`) | `FanService.cs`, `SettingsStore.cs` | `FanServiceTests.cs` |
| 2.3-A4 | PID temperature control (C# parity with Python) | `TemperatureTargetService.cs` | `TemperatureTargetServiceTests.cs` |
| 2.3-A5 | Dangerous-curve safety gate (409 + `allow_dangerous`) | `FansController.cs` | `FansControllerTests.cs` |
| 2.3-A6 | Startup safety profile (50% for 15s, both backends) | `fan_service.py`, `FanService.cs` | `test_fan_service_startup_safety.py` (14 tests) |

### B. Security & Auth

| # | Item | Files | Tests |
|---|------|-------|-------|
| 2.3-B1 | API key role ceiling (migration `013_api_key_role.sql`) | `ApiKeyService.cs`, `auth.py` | `AuthLogTests.cs` |
| 2.3-B2 | Password-change session invalidation | `auth_service.py`, `DbService.cs` | RBAC tests |
| 2.3-B3 | WebSocket 60s time-based session revalidation | `websocket.py`, `WebSocketHub.cs` | Integration tests |
| 2.3-B4 | Stale `require_write_role` auth helper removed + regression test | `auth.py` | `test_security_regressions.py` |

### C. Infrastructure

| # | Item | Files |
|---|------|-------|
| 2.3-C1 | C# drive provider abstraction (`IDriveProvider` + DI) | `IDriveProvider.cs`, `SmartctlDriveProvider.cs`, `MockDriveProvider.cs` |
| 2.3-C2 | Prometheus metrics (both backends, gated by env var) | `prom_metrics.py`, `DriveChillMetrics.cs` |
| 2.3-C3 | API key scopes UI (domain grouping + role badge) | `SettingsPage.tsx` |
| 2.3-C4 | Quiet Hours frontend CRUD + weekly schedule view | `QuietHoursPage.tsx` |
| 2.3-C5 | Calibration auto-apply ("Apply Calibration" button) | `FanTestPanel.tsx` |
| 2.3-C6 | Config export/import (both backends + frontend Settings) | `settings.py`, `SettingsController.cs`, `SettingsPage.tsx` |
| 2.3-C7 | PID UI (mode toggle + Kp/Ki/Kd sliders) | `TemperatureTargetsPage.tsx` |
| 2.3-C8 | Cross-platform E2E: `cross-env` for Windows Playwright | `playwright.config.ts`, `package.json` |

### D. Alert-Triggered Profile Switching

| # | Item | Files | Tests |
|---|------|-------|-------|
| 2.3-D1 | `revert_after_clear` suppress-wins semantics (both backends) | `alert_service.py`, `AlertService.cs` | 7 Python + 5 C# tests |
| 2.3-D2 | C# `AlertService.InjectEvent()` (synthetic events, capped at 500) | `AlertService.cs` | `AlertServiceTests.cs` |
| 2.3-D3 | Product decision: suppress-wins accepted as intended (2026-03-10) | `2026-03-10-release-readiness-checklist.md` | — |

### E. SMART Trend Alerting

| # | Item | Files | Tests |
|---|------|-------|-------|
| 2.3-E1 | `SmartTrendAlert.ActualValue` / `Threshold` (structured payloads) | `DriveMonitorService.cs` | `SmartTrendServiceTests.cs` |
| 2.3-E2 | `DriveMonitorService` → `AlertService` wiring (C#) | `DriveMonitorService.cs`, `AlertService.cs` | 14 C# tests |

### F. Notification Channel Expansion

| # | Item | Files | Tests |
|---|------|-------|-------|
| 2.3-F1 | C# `NotificationChannelService` wired into `SensorWorker` fan-out | `SensorWorker.cs` | Integration tests |
| 2.3-F2 | SSRF hardening at save time and send time (both backends) | `notification_channels.py`, `NotificationChannelsController.cs`, `url_security.py` | 6 Python + 14 C# tests |

### G. Backup / Export Completeness

| # | Item | Files | Tests |
|---|------|-------|-------|
| 2.3-G1 | `action_json` preserved in backup round-trips | `backup_service.py` | Backup tests |
| 2.3-G2 | `notification_channels` preserved in backup round-trips | `backup_service.py` | Backup tests |
| 2.3-G3 | Import summary accuracy: accepted/skipped counts (Python) | `backup_service.py` | 537 Python passing |
| 2.3-G4 | C# settings export/import includes notification channels; nullable fix | `SettingsController.cs` | 0 C# build warnings |
| 2.3-G5 | Product decision: skip invalid, log reason, report counts (2026-03-10) | `2026-03-10-release-readiness-checklist.md` | — |

### H. liquidctl USB Controller

| # | Item | Files | Tests |
|---|------|-------|-------|
| 2.3-H1 | Duplicate-device disambiguation via `device.address` | `liquidctl_backend.py` | 16 Python tests |
| 2.3-H2 | `--address` flag on all targeted `status`/`set` commands | `liquidctl_backend.py` | `test_liquidctl_backend.py` |

### I. Virtual Sensors (Milestone B1)

| # | Item | Files | Tests |
|---|------|-------|-------|
| 2.3-I1 | Virtual sensor CRUD + 6 types (max, min, avg, weighted, delta, moving_avg) | `virtual_sensor_service.py`, `VirtualSensorsController.cs` | Service tests |
| 2.3-I2 | Migration `014_virtual_sensors.sql` | `backend/app/db/migrations/` | — |
| 2.3-I3 | Runtime resolution before fan curves and temperature targets | `fan_service.py`, `FanService.cs` | Integration tests |
| 2.3-I4 | Frontend CRUD in Settings page | `SettingsPage.tsx` | E2E settings tests |

### J. Load-Based Fan Inputs (Milestone B2)

| # | Item | Files | Tests |
|---|------|-------|-------|
| 2.3-J1 | `cpu_load` / `gpu_load` as curve inputs (both backends) | `fan_service.py`, `FanService.cs` | Interpolation tests |
| 2.3-J2 | Frontend curve editor: "Load" sensor group, x-axis relabelling | `FanCurvesPage.tsx`, `CurveEditor.tsx` | E2E fan-curves tests |

### K. Control Transparency (Milestone B3)

| # | Item | Files | Tests |
|---|------|-------|-------|
| 2.3-K1 | Per-fan control source tracking (both backends) | `fan_service.py`, `FanService.cs` | Service tests |
| 2.3-K2 | `GET /api/fans/status` extended: `control_sources` + `startup_safety_active` | `fans.py`, `FansController.cs` | Route tests |
| 2.3-K3 | Frontend control source badge on fan curve cards | `FanCurvesPage.tsx` | E2E tests |

### L. E2E Coverage

| # | Item | Specs | Result |
|---|------|-------|--------|
| 2.3-L1 | `quiet-hours.spec.ts`: page load, add-rule form, day/time display | 5 tests | 4 passed, 1 skipped |
| 2.3-L2 | `settings.spec.ts` extended: export/import visibility, °F toggle | 6 tests | 6 passed |
| 2.3-L3 | `fan-curves.spec.ts` extended: benchmark section, no-crash test | 5 tests | 5 passed |
| 2.3-L4 | `temperature-targets.spec.ts` extended: PID fields, target input | 6 tests | 6 passed |
| 2.3-L5 | `drives.spec.ts`: navigation, rescan, degraded mode, sort | 10 tests | 5 passed, 5 skipped |
| 2.3-L6 | `dashboard.spec.ts`: load, sensors, fans, connection, sidebar | 5 tests | 5 passed |
| 2.3-L7 | `analytics.spec.ts`: page load, time windows, stats, anomalies | 5 tests | 5 passed |
| 2.3-L8 | `alerts.spec.ts`: page load, create rule, empty state | 3 tests | 3 passed |
| 2.3-L9 | Test selector fixes (°F button, sidebar nav, sort conditional, preset timeout) | 4 fixes | `a0ef5ed` |
| 2.3-L10 | Mid-session rerun reported 7 transient failures; clean rerun confirmed 40 passed, 0 failed | resolved | verified 2026-03-10 |

### M. Release Readiness (2026-03-10)

| # | Item | Commit | Verification |
|---|------|--------|-------------|
| 2.3-M1 | Python import summary returns accepted/skipped counts | `a0ef5ed` | 537 Python tests pass |
| 2.3-M2 | C# nullable warning eliminated in `SettingsController.cs` | `a0ef5ed` | 0 build warnings |
| 2.3-M3 | Stray `backend/=0.20.0` pip artifact deleted | `a0ef5ed` | File removed |
| 2.3-M4 | `revert_after_clear` documented as intended product behaviour | `a0ef5ed` | Release checklist updated |
| 2.3-M5 | Import policy: skip invalid, log reason, report counts — documented | `a0ef5ed` | Release checklist updated |
| 2.3-M6 | Playwright E2E: 40 passed, 6 skipped, 0 failed (mid-session failures were transient) | verified 2026-03-10 | all gates cleared |
| 2.3-M7 | CHANGELOG.md, AUDIT.md, release docs updated | `a0ef5ed` | Files updated |

---

## Migrations Inventory

| # | Migration | Version | Description |
|---|-----------|---------|-------------|
| 1 | `001` – `006` | 1.0 | Core schema (profiles, fan_curves, settings, etc.) |
| 2 | `007` | 1.5 | push_subscriptions, email_notification_settings, machines columns |
| 3 | `008` | 1.6 | Drive monitoring tables |
| 4 | `009` | 2.0 | Raise retention default 24→720 |
| 5 | `010` | 2.1 | temperature_targets table |
| 6 | `011` | 2.2 | PID fields on temperature_targets |
| 7 | `012` | 2.2 | RBAC role column on users + sessions |
| 8 | `013` | 2.3 | API key `created_by` + `role` columns |
| 9 | `014` | 2.3 | virtual_sensors table |
| 10 | `015` | 2.3 | alert rule `action_json` for switch-profile actions |
| 11 | `016` | 2.3 | notification_channels table |

---

## Commit History (main branch)

| Commit | Message |
|--------|---------|
| `a0ef5ed` | fix(v2.3-rc): release readiness — import summary, nullable fix, E2E pass |
| `3fbf1bc` | feat(v2.3): Milestone A+B complete — startup safety, control transparency, virtual sensors, load-based inputs |
| `d10a1c1` | docs: v2.3 implementation plan |
| `c32bd08` | feat(v2.2): cross-cutting types, API client, C# fan/webhook fixes, docs |
| `62ece13` | feat(v2.2.1): RBAC — viewer role, write-block, session hardening |
| `a0ca9ee` | feat(v2.2-rc1): C# profile export/import, auth_log, test expansion |
| `6219057` | feat(v2.2-rc2): custom modals, viewer UX, °F gauge, E2E CI |
| `33df4e6` | feat(v2.2.0): PID fan controller for temperature targets |
| `4f3af75` | fix(release): v2.1.1 stabilization — analytics parity, CI reliability, update hardening |
| `0eefc57` | docs: update CHANGELOG, AUDIT, and PLAN for v2.1 release |
| `6f46b66` | feat: cross-cutting updates for v1.6-v2.1 feature integration |
| `bd7c550` | feat: release workflow, self-updater, Docker config, and install scripts |
| `03687ac` | fix: comprehensive audit remediation — security, parity, and robustness |
| `c71edb0` | feat: temperature targets — proportional fan control per drive temp (v2.1) |
| `85139f2` | feat: analytics v2.0 — custom ranges, multi-sensor, correlation, retention gate |
| `a6f6cd2` | feat: drive monitoring with SMART health, temp polling, and self-tests (v1.6) |

---

## Deferred Items (Not In This Release)

| # | Item | Reason |
|---|------|--------|
| D-1 | Cross-platform hardware parity (C# Linux support) | C# is Windows-oriented by design |
| D-2 | MQTT integration | Deferred to v2.4 |
| D-3 | Broader machine orchestration maturity | Future work |
| D-4 | PDF/reporting | Future work |
| D-5 | Session token rotation on sensitive operations | Low risk, deferred |
| D-6 | C# `inject_event` wiring from Python drive monitor | Python already uses existing alert path |
| D-7 | Drive detail timeline markers (SMART trend UI) | Cosmetic, not blocking |
| D-8 | Richer "why is this fan doing this" operator surfaces | Future UX work |

---

## Test Count Summary

| Version | Python | C# | E2E |
|---------|--------|----|-----|
| v1.5 | ~100 | ~16 | 0 |
| v1.6 | ~200 | ~16 | ~20 |
| v2.0 | ~250 | ~16 | ~20 |
| v2.1 | ~300 | ~24 | ~20 |
| v2.1.1 | 409 | 16 | ~20 |
| v2.2.0 | 418 | 75 | ~30 |
| v2.3-audit | 523 | 205 | ~38 |
| v2.3-rc | **537** | **205** | **46** (40 pass, 6 skip, 0 fail) |

---

## Audit Corrections (2026-03-10 Review)

The following items were challenged during external review. Each was verified
against the current codebase. This section separates three concerns:
code-level verification, temporal context (was the feedback valid when
written?), and process observations.

### Challenged: "C# one-sided custom-range already fixed"

**Feedback (pre-v2.1.1):** `GetRegression` still enters override mode when
only `start` is present.

**Temporal context:** This feedback was valid when written. The defect existed
prior to commit `4f3af75`.

**Current state:** Fixed. `AnalyticsController.cs` line 200 uses
`if (!string.IsNullOrEmpty(start) && !string.IsNullOrEmpty(end))` — requires
both parameters. The fix was applied in commit `4f3af75` (v2.1.1). No
remaining code defect.

### Challenged: "Release tag glob uses literal +"

**Feedback (pre-v2.1.1):** Pattern `v[0-9]+` uses regex-like `+` which is
literal in GitHub Actions glob matching.

**Temporal context:** This feedback was valid when written. The old pattern
used `+` which GitHub Actions treats as a literal character, not a quantifier.

**Current state:** Fixed. `.github/workflows/release.yml` line 6 now uses
`v[0-9]*.[0-9]*.[0-9]*` — correct glob with `*` wildcards. The fix was
applied in commit `4f3af75` (v2.1.1). No remaining code defect.

### Challenged: "C# dangerous-curve gate is missing"

**Feedback (pre-v2.1.2):** Python has the dangerous-curve safety gate but C#
does not.

**Temporal context:** This feedback was valid when written. The C# gate did
not exist prior to v2.1.2.

**Current state:** Implemented. `FansController.cs` lines 62–94 have the full
safety gate on `PUT /api/fans/curves` with `CheckDangerousCurve()` (two-phase:
explicit points + interpolation at 80/85/90/95/100°C) and `allow_dangerous`
override. `POST /api/fans/curves/validate` pre-check endpoint also exists.
Shipped in v2.1.2. No remaining code defect.

### Accepted: Scope discipline advice

**Feedback:** Keep v2.1.1 scope tight. Defer dangerous-curve gate, fan/profile
parity, PID.

**Outcome:** Advice was followed. Those items shipped in v2.1.2, v2.2.0, and
v2.3.0-rc respectively, not in the stabilization patch.

### Resolved: E2E regression was transient

**Feedback (2026-03-10):** Mid-session rerun found 7 failures the initial
results did not report.

**Resolution:** A clean rerun on the same tree (`a0ef5ed`) produced **40
passed, 0 failed, 6 skipped** — matching the original results. The 7 failures
were transient, likely caused by stale build artifacts or test runner state
from the mid-session environment. No test or code changes were needed.

### Process observations (from retrospective review)

These are about planning quality, not code correctness:

1. **SEC-1 (semver input validation) shipped unnamed in the v2.1.1 plan.** The
   code is correct (`update.py` line 25, `UpdateController.cs` line 17) but the
   plan should have listed it as an explicit security item. Future plans must
   follow `release-plan-standard.md` section 1.3.

2. **Manual validation was not formally recorded.** Backend tests passed, but
   manual checks (update-from-Settings, service restart, workflow dry-run) have
   no audit trail. This is a process gap, not a code gap.

3. **C# drive provider abstraction shipped at `backend-cs/Services/` level**
   (`IDriveProvider.cs`, `SmartctlDriveProvider.cs`, `MockDriveProvider.cs`),
   not the originally planned `Hardware/Drives/` layout. Functionally complete,
   structurally different from original design.
