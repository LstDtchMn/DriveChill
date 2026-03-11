# Changelog

## [3.0.0] - Unreleased

### Features - C# v3.0 parity completion
- **Noise profiles CRUD (C#)**: `GET/POST/DELETE /api/noise-profiles` plus `GET /api/noise-profiles/{id}` now match the Python backend, backed by the new `noise_profiles` SQLite table and controller tests.
- **Scheduled analytics reports (C#)**: `GET/POST/PUT/DELETE /api/report-schedules` now ship with `ReportSchedulerService`, HTML email report generation, `last_sent_at` tracking, and due-logic coverage in tests.
- **Event annotations CRUD (C#)**: `GET/POST/DELETE /api/annotations` now persist timeline markers in the shared `event_log` table with range filtering and controller tests.
- **Schema/API parity cleanup (C#)**: `DbService.EnsureInitialisedAsync` now creates `noise_profiles`, `report_schedules`, and `event_log`; API-key scope mapping now includes noise profiles, report schedules, and annotations to mirror Python auth behavior.
- **MQTT Home Assistant discovery parity verified**: C# `NotificationChannelService` HA discovery behavior was reviewed against the Python implementation and confirmed aligned for config payloads, retained publishes, and stale-entity cleanup.

### Features — C# Fan-Control Parity
- **Composite sensor curves (C#)**: `FanService.ApplyCurvesAsync` now supports `SensorIds` list with MAX resolution — parity with Python `resolve_composite_temp`
- **Hysteresis deadband (C#)**: 3°C deadband prevents fan oscillation near curve thresholds
- **Ramp-rate limiting (C#)**: configurable `fan_ramp_rate_pct_per_sec` clamps speed changes to prevent audible jumps; stored in `SettingsStore`
- **PID temperature control (C#)**: full PID controller with integral anti-windup and derivative EMA in `TemperatureTargetService` — parity with Python
- **Dangerous-curve safety gate (C#)**: `PUT /api/fans/curves` returns 409 when curve has dangerously low speeds at high temps; `allow_dangerous=true` overrides
- **Startup safety profile**: both backends run fans at 50% for 15 seconds on startup before curves load; automatically exits when a profile is applied or the safety window expires; panic mode overrides startup safety

### Features — Security & Auth
- **API key role ceiling**: migration `013_api_key_role.sql` adds `created_by` and `role` columns; viewer-role users create viewer-scoped keys only
- **Password-change session invalidation**: both backends delete all user sessions immediately after password change
- **WebSocket time-based revalidation**: Python and C# revalidate session every 60 seconds (replaces message-count-based)
- **Stale auth cleanup**: removed dead `require_write_role` helper from Python auth dependencies; regression test added

### Features — Infrastructure
- **C# drive provider abstraction**: `IDriveProvider` interface with `SmartctlDriveProvider` and `MockDriveProvider` implementations; `DriveMonitorService` uses DI
- **Prometheus metrics (both backends)**: `/metrics` endpoint gated behind `DRIVECHILL_PROMETHEUS_ENABLED=true`; C# uses `DriveChillMetrics.cs`
- **API key scopes UI**: scope picker with domain grouping and role badge in Settings page
- **Quiet Hours frontend**: full CRUD page with weekly schedule view
- **Calibration auto-apply**: "Apply Calibration" button in `FanTestPanel` writes measured min speed to fan settings
- **Config export/import**: `GET/POST /api/settings/export|import` in both backends + frontend Settings
- **PID UI**: temperature targets page shows PID mode toggle + Kp/Ki/Kd sliders
- **Cross-platform E2E**: `cross-env` added for Windows-compatible Playwright dev server startup

### Features — Alert-Triggered Profile Switching
- **`revert_after_clear` semantics (both backends)**: when an alert rule's action has `revert_after_clear=false`, the backend no longer reverts to the pre-alert profile on clear; prior implementation always reverted. Runtime behavior is now deterministic: any still-active suppress rule prevents revert. **Accepted as intended product behavior (2026-03-10).**
- **C# `AlertService.InjectEvent()`**: new public method allows `DriveMonitorService` (and future callers) to inject synthetic `AlertEvent` records into the in-memory event list without going through the threshold-evaluation path; list is capped at 500 entries.

### Features — SMART Trend Alerting
- **`SmartTrendAlert.ActualValue` / `Threshold`** (C#): structured alert payloads now carry the real numeric values so downstream consumers (websocket, notification channels) receive actionable context rather than empty zeros.
- **`DriveMonitorService` → `AlertService` wiring** (C#): SMART trend alerts (reallocated-sector increase, wear crossings, power-on-hours) are now injected into the alert pipeline via `AlertService.InjectEvent` on every poll cycle; previously they were detected but never dispatched.

### Features — Notification Channel Expansion
- **`NotificationChannelService` wired into C# `SensorWorker` alert fan-out**: HTTP notification channels (ntfy, Discord, Slack, generic webhook) are now dispatched concurrently alongside webhooks, email, and push on every alert event; previously the service existed but was never called from the poll loop.
- **SSRF hardening at save time and send time (both backends)**: `POST /api/notification-channels` and `PUT /api/notification-channels/{id}` reject private/loopback/link-local URLs in `url` and `webhook_url` config fields at controller level; the service layer re-validates before any outbound HTTP, preventing bypass via direct DB writes.

### Features — Backup / Export Completeness
- **`action_json` preserved in backup round-trips** (Python): `export_backup` and `import_backup` now include the `action_json` column on `alert_rules`, so alert-triggered profile-switch actions survive export/restore cycles.
- **`notification_channels` preserved in backup round-trips** (Python): `export_backup` and `import_backup` include the `notification_channels` table; old backups that predate this field import cleanly with zero channel records.
- **Import summary accuracy** (Python): `import_backup` now returns `notification_channels` (accepted count) and `notification_channels_skipped` (skipped count) instead of the raw input length; SSRF-blocked channels are logged with channel ID and reason.
- **C# settings export/import includes notification channels**: `GET /api/settings/export` serializes all notification channels; `POST /api/settings/import` restores them, skipping duplicates; nullable `Config` handled cleanly.

### Features — liquidctl USB Controller
- **Duplicate-device disambiguation**: `_make_id_prefix` now incorporates `device.address` (USB bus/port path) so two identical controller models on different ports produce distinct fan IDs; all `liquidctl status` and `set` calls pass `--address` to target the correct device.

### Features — Virtual Sensors (B1)
- **Virtual sensor CRUD + evaluation (both backends)**: six types — `max`, `min`, `avg`, `weighted`, `delta`, `moving_avg`; stored in `virtual_sensors` table (migration `014_virtual_sensors.sql`); evaluated every control tick before curves and temperature targets run; frontend CRUD in Settings page
- **Virtual sensors as curve/target inputs**: fan curves and temperature targets can reference virtual sensor IDs; virtual sensor values are resolved into the sensor value map before control evaluation

### Features — Load-Based Fan Inputs (B2)
- **CPU/GPU load as curve input (both backends)**: `cpu_load` and `gpu_load` sensor types accepted by `apply_curves` / `ApplyCurvesAsync`; frontend curve editor exposes a "Load" sensor group alongside temperature sensors; x-axis label changes to "Load (%)" when a load sensor is selected

### Features — Control Transparency (B3)
- **Per-fan control source tracking (both backends)**: `FanService` and `FanService.cs` now track the dominant source for each fan's last applied speed: `profile`, `temperature_target`, `startup_safety`, `panic_sensor`, `panic_temp`, `released`, or `manual`
- **`GET /api/fans/status` extended**: response now includes `control_sources` (per-fan source map) and `startup_safety_active` flag; C# `GetSafeModeStatus()` extended to match
- **Frontend control source badge**: fan curve cards show a live "▸ Profile / Temp Target / Startup Safety / ..." badge sourced from the WebSocket `control_sources` field

### Features — Frontend E2E Coverage (A2.2)
- **`quiet-hours.spec.ts`**: new Playwright spec for Quiet Hours CRUD (page load, add-rule form, day/time display)
- **`settings.spec.ts` extended**: export and import config section visibility tests; °F toggle test
- **`fan-curves.spec.ts` extended**: benchmark calibration section presence and no-crash test
- **`temperature-targets.spec.ts` extended**: PID field accessibility test and target temperature input validation
- **E2E release pass**: 40 passed, 6 skipped (drive-detail tests require mock data) across dashboard, fan curves, quiet hours, settings, temperature targets, alerts, drives, analytics

### Tests
- Python: 537 passing, 13 skipped (startup safety + control transparency tests in `test_fan_service_startup_safety.py`; virtual sensor service tests; all prior passing)
- C#: 205 passing (startup safety FanService tests; all prior passing)

### Migrations
- `014_virtual_sensors.sql`: `virtual_sensors` table with `id`, `name`, `type`, `source_ids_json`, `weights_json`, `window_seconds`, `offset`, `enabled`, `created_at`, `updated_at`

### Migrations
- `013_api_key_role.sql`: adds `created_by TEXT`, `role TEXT NOT NULL DEFAULT 'admin'` to `api_keys`

## [2.2.0] - 2026-03-05

### Features
- **PID temperature controller**: temperature targets now support full PID control (Kp, Ki, Kd) as an opt-in alternative to the proportional band. Derivative term uses an EMA low-pass filter (α=0.7) to suppress sensor noise; integral uses conditional anti-windup to prevent overshoot. Default gains (Kp=1, Ki=0, Kd=0) are pure proportional — fully backwards-compatible.
- **Multi-user RBAC (viewer role)**: users can now be created with an `admin` or `viewer` role. Viewers have full read access but all write/mutate routes return 403. Logout is exempt from the write-block. Frontend disables all write controls (buttons, inputs, toggles) when the authenticated role is `viewer` and shows a "Read-only access" banner on write-heavy pages.
- **Last-admin safety guard**: demoting or deleting the last remaining admin account is blocked with a 409 response in both Python and C# backends.
- **Session security hardening**: deleting a user now immediately invalidates all their active sessions (explicit DELETE + INNER JOIN validation — deleted users cannot keep using existing session cookies).
- **User management UI**: Settings page gains a User Management section (admin-only) — list users, create users with role selection, change role, change password, delete user.
- **Custom confirm/modal dialogs**: `ConfirmDialog` component and `useConfirm()` hook replace all `window.confirm` calls; `ToastProvider` and `useToast()` hook replace all `window.alert` error messages. Dialogs respect dark/light theme.
- **°F gauge fix**: `TempGauge` arc colours and fill ratios are computed in the user's display unit so a 90°F reading no longer appears in the danger zone.
- **C# profile export/import**: `GET /api/profiles/{id}/export` returns `{export_version: 1, profile: {name, preset, curves}}`; `POST /api/profiles/import` accepts the flat profile body matching the Python contract. Frontend import/export round-trip verified.
- **C# auth_log**: login, logout, user CRUD events are written to the `auth_log` table (both Python and C# backends now parity). Logs are pruned hourly to a 90-day retention window.
- **E2E tests in CI**: GitHub Actions `e2e.yml` workflow starts the Python mock backend, installs Playwright, and runs all specs including new `analytics.spec.ts` and `temperature-targets.spec.ts`.

### Migrations
- `011_pid_fields.sql`: adds `pid_mode`, `pid_kp`, `pid_ki`, `pid_kd` columns to `temperature_targets` (default: proportional-only)
- `012_rbac.sql`: adds `role TEXT NOT NULL DEFAULT 'admin'` to `users`; adds `role` column to `sessions`

### Tests
- Python: 418 passing (9 new RBAC tests in `test_auth.py`: viewer write-block, logout exemption, session invalidation on delete, last-admin guards, role propagation)
- C#: 75 passing (up from 16) — new test files: `FanServiceTests.cs`, `ProfilesControllerTests.cs`, `AlertServiceTests.cs`, `WebhookServiceTests.cs`, `SessionServiceTests.cs` covering RBAC, fan service, alert cooldown, webhook delivery, profile export/import

## [2.1.2] - 2026-03-05

### Bug Fixes
- Fix C# `ActivateProfile` leaving orphaned curves from the previous profile active (now calls `FanService.SetCurves` which clears all slots before applying the new profile's curves)
- Fix Python `AlertService.remove_rule` mutating in-memory state before the DB delete (now consistent with `add_rule` — DB write first, then mutation)
- Fix C# `PUT /api/fans/curves` accepting `FanCurve` directly instead of `{curve, allow_dangerous}` body — frontend was sending the wrapped form, causing silent deserialization failure

### Features
- C# `GET /api/profiles/{id}` endpoint added (parity with Python)
- C# `GET /api/fans/settings` + `GET/PUT /api/fans/{fanId}/settings` endpoints added — per-fan minimum speed floor and zero-RPM capability now fully supported
- C# `FanService` enforces per-fan minimum speed floor in `ApplyCurvesAsync` (zero-RPM fans exempt at 0%)
- C# dangerous-curve safety gate: `PUT /api/fans/curves` now returns 409 with warning list when curve has dangerously low speeds at high temps; `allow_dangerous=true` overrides
- C# `POST /api/fans/curves/validate` endpoint added — pre-check curve for dangerous speeds without saving
- C# `GET /api/webhooks/deliveries` now supports `offset` query parameter for pagination

## [2.1.1] - 2026-03-05

### Bug Fixes
- Fix C# analytics `GetRegression` accepting one-sided custom range (now requires both start and end, matching Python)
- Fix release workflow tag glob pattern (`v[0-9]+` → `v[0-9]*`) for reliable GitHub Actions triggering
- Fix Docker pull command in release notes using wrong tag (v-prefixed vs semver-only)
- Add `NODE_OPTIONS=--max-old-space-size=4096` to CI frontend build to prevent OOM

### Improvements
- Update script (`update_windows.ps1`): add `-Artifact` parameter to select Python or Windows ZIP
- Update script: install-dir fallback chain (explicit → NSSM service → script-relative)
- C# `UpdateController`: robust script discovery with env var override + fallback paths
- C# `UpdateController`: passes `-Artifact windows -InstallDir` to update script
- Python `update.py`: passes `-Artifact python` to update script

### Tests
- Add C# `ResolveRange` unit tests (6 tests: both/one-sided/neither/invalid/reversed)
- Add Python updater route tests (8 tests: version comparison, semver regex, check, apply)

### Docs
- Add `docs/updating.md` — update procedures for Python, C#, Docker, and rollback

## [2.1.0] - 2026-03-04

### Features
- Temperature Targets: set a target temperature for any drive sensor with a tolerance band, floor fan speed, and fan selection
- Proportional fan control: fans ramp linearly within the tolerance band, hold floor below, and go 100% above
- Multi-drive shared fan support: when multiple targets point to the same fan, the hottest drive wins (max speed)
- Relationship Map view: SVG bipartite diagram showing drive-to-fan connections with thermal-state coloured lines
- List view: card-based target list with live speed labels and enable/disable toggle
- Temperature targets integrate with existing fan curves via union-of-fan-IDs merge (higher speed wins)
- Full C# backend parity: TemperatureTargetService, controller, DB schema, and FanService integration
- API key scope `temperature_targets` added to both Python and C# backends

## [2.0.0] - 2026-03-03

### Features
- Analytics v2.0: custom date-range queries (start/end params), multi-sensor filtering (`sensor_ids`), auto-sized buckets
- Analytics v2.0: new `/api/analytics/correlation` endpoint — Pearson r between any two sensors
- Analytics v2.0: `retention_limited` flag in history responses; banner shown in UI when active
- Analytics page: 30-day time window option, custom start/end date pickers, sensor filter chips, correlation panel with scatter plot
- Analytics anomalies: `severity` field (`warning` / `critical`) based on z-score vs threshold
- Drive temperature mini-sparkline (24h) on the drive detail panel
- "New cooling curve" button on the Drives page: creates a pre-configured storage cooling curve draft and navigates to Fan Curves
- History retention default raised from 24 hours to 720 hours (30 days) for all new and existing installations
- Migration 009: auto-upgrades existing installs still at the 24-hour retention default

### Infrastructure
- Python analytics routes rewritten for v2.0 (custom ranges, multi-sensor IN clause, correlation, retention gate)
- C# analytics parity with Python v2.0: `AnalyticsController` updated, `DbService` analytics methods accept `DateTimeOffset start/end` + `string[]? sensorIds`; correlation method added
- `AnalyticsAnomaly` model gains `Severity` property; new `AnalyticsCorrelationSample` model
- C# `AnalyticsController` reads retention from `SettingsStore.RetentionDays` (was `AppSettings`)
- 7 analytics performance tests — each query over a 130 k-row / 30-day dataset must complete under 2 s
- 30 drive-routes integration tests added
- Drive E2E tests: "Use for cooling" and "New cooling curve" flows

## [1.6.0] - 2026-03-03

### Features
- Drive monitoring: SMART health, temperature, and self-test support via smartmontools
- New Drives page with health badges, per-drive temperature display, and SMART attribute drill-in
- Drive temperatures injected into the fan-curve and alert pipeline as `hdd_temp` sensors
- Per-drive settings overrides: custom temperature warning/critical thresholds, alert opt-out, curve picker opt-out
- Storage Monitoring section in Settings: global thresholds for HDD, SSD, and NVMe drives
- Dashboard storage summary card: drive count, health summary, and hottest drive temperature
- Analytics page temperature values now respect the user's °C/°F unit preference
- Fan curve editor touch hit area increased to 40px diameter for reliable mobile dragging
- Temperature unit preference synced from backend on every page startup (no longer resets to °C)
- Playwright E2E test suite: dashboard, fan curves, alerts, and settings flows

### Infrastructure
- `smartmontools` added to Docker image for SMART data collection
- `@playwright/test` added as devDependency; `test:e2e`, `test:e2e:ui`, `test:e2e:debug` npm scripts
- `playwright.config.ts` with webServer auto-start (Next.js dev server + mock backend)
- Frontend package version bumped to 1.6.0
- Drive monitoring `DriveMonitorWorker` BackgroundService registered in C# DI
- API key scope domain `drives` added to both Python and C# backends

## [1.5.0] - 2026-03-02

### Security
- Fix SSRF DNS-rebinding in webhook and machine proxy (re-validate resolved IP at request time)
- Add IPv4-mapped loopback bypass protection in URL security validation
- Enforce API key scopes (`read:sensors`, `write:control`) in auth middleware
- Add AES-256-GCM encryption for SMTP passwords (format: `v1:<base64>`)
- Add `X-DriveChill-Timestamp` + `X-DriveChill-Nonce` headers to webhook HMAC signatures
- Fix Prometheus metric name regex to reject backslash characters
- Fix machine `api_key` preservation on partial PUT (sentinel pattern)
- Fix `_KEEP_SECRET` sentinel ordering in webhook service
- Atomic webhook delivery log pruning (single DELETE query)
- Redact credentials from URLs in error logs
- C# backend: SSRF IP-routing via `SocketsHttpHandler.ConnectCallback`

### Features
- Push notifications via Web Push / VAPID (both Python and C# backends)
- Email alert notifications via SMTP (aiosmtplib / System.Net.Mail)
- Multi-machine remote control: proxy fan state, activate profiles, release fans, update fan settings
- Machine drill-in UI with real-time remote sensor and fan state
- Analytics page: time-windowed history, stat cards, anomaly detection, SVG sparklines
- Docker headless mode (`python drivechill.py --headless`)
- Self-signed TLS certificate generation for HTTPS
- Quiet hours for alert suppression
- Panic mode, release fan control, and sensor failure escalation

### Bug Fixes
- Fix Docker data persistence (database now stored at `DRIVECHILL_DATA_DIR=/app/data`)
- Fix Dockerfile CMD to use `drivechill.py --headless` (respects env vars for host/port/SSL)
- Fix Windows service installation via NSSM (split exe/args, use `drivechill.py --headless`)
- Fix session double-validation in auth middleware
- Fix dashboard polling (2s with machines, 30s without)
- Fix machine busy state tracking (per-machine `Set<string>` instead of single boolean)
- Add confirmation dialogs for destructive actions (API key revoke, machine remove)
- Add clipboard copy button for newly issued API keys
- Surface save error feedback in Settings page

### Infrastructure
- Docker Compose healthcheck via `/api/health`
- `docker/.env.example` documenting all 14 `DRIVECHILL_` environment variables
- C# backend: full parity with Python for machine registry, analytics, notifications
- `Lib.Net.Http.WebPush` 3.3.0 for C# Web Push delivery
- Accessibility: `aria-label` on all icon-only buttons in Settings

## [1.0.0] - Initial Release

- Temperature-based fan curve control with profile system
- Web dashboard with real-time sensor monitoring
- LibreHardwareMonitor Direct backend (Windows, pythonnet DLL)
- lm-sensors backend (Linux/Docker)
- Mock backend for development
- Session authentication with CSRF protection
- Alert rules with cooldown and quiet hours
- Webhook alert delivery with HMAC signing and retries
- Fan benchmark testing service
- System tray integration (Windows)
- Next.js 14 static export frontend with Zustand state management
