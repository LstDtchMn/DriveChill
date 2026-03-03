# Changelog

## [1.6.0] - 2026-03-03

### Features
- Analytics page temperature values now respect the user's °C/°F unit preference
- Fan curve editor touch hit area increased to 40px diameter for reliable mobile dragging
- Temperature unit preference synced from backend on every page startup (no longer resets to °C)
- Playwright E2E test suite: dashboard, fan curves, alerts, and settings flows

### Infrastructure
- `@playwright/test` added as devDependency; `test:e2e`, `test:e2e:ui`, `test:e2e:debug` npm scripts
- `playwright.config.ts` with webServer auto-start (Next.js dev server + mock backend)
- Frontend package version bumped to 1.6.0

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
