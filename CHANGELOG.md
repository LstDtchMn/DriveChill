# Changelog

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
