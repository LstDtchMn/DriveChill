# DriveChill Feature & Security Audit Document

**Generated:** 2026-03-03 | **Updated:** 2026-03-10 (release readiness: import summary accuracy fix, C# nullable warning fix, product decisions documented, E2E pass 40/46; prior: audit remediation, Milestone A+B complete)
**Version:** 2.3.0-dev (branch: `claude/fan-temperature-controller-kNqMx`)
**Repo:** DriveChill-1

---

## Related Design Documents

- Drive monitoring + thermal link design: `docs/plans/2026-03-03-drive-monitoring-and-thermal-link-design.md`
- v2.0 Insights Engine design: `docs/plans/2026-03-03-v2-insights-engine-design.md`
- v2.0 Insights Engine checklist: `docs/plans/2026-03-03-v2-insights-engine-implementation-checklist.md`

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [API Endpoint Inventory (80 endpoints)](#2-api-endpoint-inventory)
3. [Authentication & Authorization](#3-authentication--authorization)
4. [Security Controls](#4-security-controls)
5. [Data Storage & Encryption](#5-data-storage--encryption)
6. [Real-Time Communication](#6-real-time-communication)
7. [Fan Control & Safety](#7-fan-control--safety)
8. [Multi-Machine Hub](#8-multi-machine-hub)
9. [Notifications](#9-notifications)
10. [Analytics & Monitoring](#10-analytics--monitoring)
11. [Frontend](#11-frontend)
12. [Deployment & Infrastructure](#12-deployment--infrastructure)
13. [Test Coverage](#13-test-coverage)
14. [Known Limitations & Deferred Items](#14-known-limitations--deferred-items)

---

## 1. Architecture Overview

DriveChill is a PC fan temperature controller with three independently deployable components:

| Component | Stack | Entry Point |
|-----------|-------|-------------|
| Python backend | FastAPI + aiosqlite + uvicorn | `backend/drivechill.py --headless` |
| C# backend | ASP.NET Core 10 + Microsoft.Data.Sqlite | `backend-cs/Program.cs` |
| Frontend | Next.js 14 static export + Zustand | `frontend/out/` (served by either backend) |

**Database:** SQLite in WAL mode. 14 sequential migrations (`001`–`014`) applied at startup.

**Hardware backends:**

| Backend | File | Platform | Notes |
|---------|------|----------|-------|
| LHM Direct | `lhm_direct_backend.py` | Windows | pythonnet DLL, primary path |
| LHM HTTP | `lhm_backend.py` | Any | HTTP bridge to LHM REST API |
| lm-sensors | `lm_sensors_backend.py` | Linux | Shell exec |
| Mock | `mock_backend.py` | Any | Synthetic data for dev/test |

Selection: `DRIVECHILL_HARDWARE_BACKEND` env var (`auto` | `mock` | `lhm_direct` | `lhm_http`).

---

## 2. API Endpoint Inventory

**Total: 92 HTTP + 1 WebSocket = 93 endpoints**

### 2.1 Authentication (`/api/auth/*`) — 8 endpoints

| Method | Path | Auth | CSRF | Description |
|--------|------|------|------|-------------|
| POST | `/api/auth/login` | No | No | Create session (username + password) |
| POST | `/api/auth/logout` | Yes | Yes | Destroy session |
| GET | `/api/auth/session` | No | No | Check session validity |
| GET | `/api/auth/status` | No | No | Check if auth is enabled |
| POST | `/api/auth/setup` | No | No | First-time admin creation (blocked after first user) |
| GET | `/api/auth/api-keys` | Yes | No | List API key metadata |
| POST | `/api/auth/api-keys` | Yes | Yes | Create scoped API key |
| DELETE | `/api/auth/api-keys/{key_id}` | Yes | Yes | Revoke API key |

### 2.2 Sensors (`/api/sensors/*`) — 6 endpoints

| Method | Path | Auth | CSRF | Description |
|--------|------|------|------|-------------|
| GET | `/api/sensors` | Yes | No | Current sensor readings with labels |
| GET | `/api/sensors/labels` | Yes | No | All user-defined sensor labels |
| PUT | `/api/sensors/{sensor_id}/label` | Yes | Yes | Set custom sensor label (max 100 chars) |
| DELETE | `/api/sensors/{sensor_id}/label` | Yes | Yes | Remove custom label |
| GET | `/api/sensors/history` | Yes | No | Historical data (query: `sensor_id`, `hours`) |
| GET | `/api/sensors/export` | Yes | No | CSV export of sensor history |

### 2.3 Fans (`/api/fans/*`) — 15 endpoints

| Method | Path | Auth | CSRF | Description |
|--------|------|------|------|-------------|
| GET | `/api/fans` | Yes | No | List all controllable fans |
| POST | `/api/fans/speed` | Yes | Yes | Manually set fan speed (0–100%) |
| GET | `/api/fans/curves` | Yes | No | Get all fan curves |
| PUT | `/api/fans/curves` | Yes | Yes | Create/update curve (with dangerous-speed check) |
| DELETE | `/api/fans/curves/{curve_id}` | Yes | Yes | Delete curve |
| POST | `/api/fans/curves/validate` | Yes | Yes | Pre-validate curve for dangerous speeds |
| GET | `/api/fans/settings` | Yes | No | All per-fan settings |
| GET | `/api/fans/{fan_id}/settings` | Yes | No | Per-fan settings (min speed, zero-RPM) |
| PUT | `/api/fans/{fan_id}/settings` | Yes | Yes | Update per-fan settings |
| POST | `/api/fans/{fan_id}/test` | Yes | Yes | Start benchmark sweep |
| GET | `/api/fans/{fan_id}/test` | Yes | No | Get benchmark result |
| DELETE | `/api/fans/{fan_id}/test` | Yes | Yes | Cancel benchmark |
| GET | `/api/fans/status` | Yes | No | Control status + safe-mode state |
| POST | `/api/fans/release` | Yes | Yes | Release all fans to BIOS auto |
| POST | `/api/fans/resume` | Yes | Yes | Resume software fan control |

### 2.4 Profiles (`/api/profiles/*`) — 8 endpoints

| Method | Path | Auth | CSRF | Description |
|--------|------|------|------|-------------|
| GET | `/api/profiles` | Yes | No | List all profiles |
| GET | `/api/profiles/{id}` | Yes | No | Get specific profile |
| POST | `/api/profiles` | Yes | Yes | Create profile |
| PUT | `/api/profiles/{id}/activate` | Yes | Yes | Activate profile, apply curves |
| DELETE | `/api/profiles/{id}` | Yes | Yes | Delete profile (blocked if active) |
| GET | `/api/profiles/{id}/preset-curves` | Yes | No | Get preset curve points |
| GET | `/api/profiles/{id}/export` | Yes | No | Export as portable JSON |
| POST | `/api/profiles/import` | Yes | Yes | Import from JSON (fresh curve IDs) |

### 2.5 Alerts (`/api/alerts/*`) — 4 endpoints

| Method | Path | Auth | CSRF | Description |
|--------|------|------|------|-------------|
| GET | `/api/alerts` | Yes | No | Rules + events + active set |
| POST | `/api/alerts/rules` | Yes | Yes | Add/update rule |
| DELETE | `/api/alerts/rules/{id}` | Yes | Yes | Delete rule (404 if not found) |
| POST | `/api/alerts/clear` | Yes | Yes | Clear all events |

### 2.6 Settings (`/api/settings`) — 2 endpoints

| Method | Path | Auth | CSRF | Description |
|--------|------|------|------|-------------|
| GET | `/api/settings` | Yes | No | Current settings |
| PUT | `/api/settings` | Yes | Yes | Update settings (poll interval, retention, temp unit) |

### 2.7 Machines — Remote Hub (`/api/machines/*`) — 10 endpoints

| Method | Path | Auth | CSRF | Description |
|--------|------|------|------|-------------|
| GET | `/api/machines` | Yes | No | List registered machines |
| POST | `/api/machines` | Yes | Yes | Register remote machine |
| PUT | `/api/machines/{id}` | Yes | Yes | Update machine config |
| DELETE | `/api/machines/{id}` | Yes | Yes | Unregister machine |
| GET | `/api/machines/{id}/snapshot` | Yes | No | Cached remote snapshot |
| POST | `/api/machines/{id}/verify` | Yes | Yes | Test connectivity |
| GET | `/api/machines/{id}/state` | Yes | No | Full remote state |
| POST | `/api/machines/{id}/profiles/{pid}/activate` | Yes | Yes | Activate remote profile |
| POST | `/api/machines/{id}/fans/release` | Yes | Yes | Release remote fans |
| PUT | `/api/machines/{id}/fans/{fid}/settings` | Yes | Yes | Update remote fan settings |

### 2.8 Webhooks (`/api/webhooks/*`) — 3 endpoints

| Method | Path | Auth | CSRF | Description |
|--------|------|------|------|-------------|
| GET | `/api/webhooks` | Yes | No | Get config |
| PUT | `/api/webhooks` | Yes | Yes | Update config (URL, signing secret, retry policy) |
| GET | `/api/webhooks/deliveries` | Yes | No | Delivery log (paginated) |

### 2.9 Notifications (`/api/notifications/*`) — 7 endpoints

| Method | Path | Auth | CSRF | Description |
|--------|------|------|------|-------------|
| GET | `/api/notifications/email` | Yes | No | Email settings (password redacted) |
| PUT | `/api/notifications/email` | Yes | Yes | Update email settings |
| POST | `/api/notifications/email/test` | Yes | Yes | Send test email |
| GET | `/api/notifications/push-subscriptions` | Yes | No | List push subs (keys redacted) |
| POST | `/api/notifications/push-subscriptions` | Yes | Yes | Register push subscription |
| DELETE | `/api/notifications/push-subscriptions/{id}` | Yes | Yes | Remove push subscription |
| POST | `/api/notifications/push-subscriptions/test` | Yes | Yes | Send test push |

### 2.10 Quiet Hours (`/api/quiet-hours/*`) — 4 endpoints

| Method | Path | Auth | CSRF | Description |
|--------|------|------|------|-------------|
| GET | `/api/quiet-hours` | Yes | No | List rules |
| POST | `/api/quiet-hours` | Yes | Yes | Create rule |
| PUT | `/api/quiet-hours/{id}` | Yes | Yes | Update rule |
| DELETE | `/api/quiet-hours/{id}` | Yes | Yes | Delete rule |

### 2.11 Analytics (`/api/analytics/*`) — 6 endpoints

| Method | Path | Auth | CSRF | Description |
|--------|------|------|------|-------------|
| GET | `/api/analytics/history` | Yes | No | Time-bucketed history (avg/min/max/count per bucket) |
| GET | `/api/analytics/stats` | Yes | No | Aggregate stats (min/max/avg/p95, per sensor) |
| GET | `/api/analytics/anomalies` | Yes | No | Z-score anomaly detection |
| GET | `/api/analytics/regression` | Yes | No | Thermal regression vs rolling baseline; load-band aware when load data present |
| GET | `/api/analytics/correlation` | Yes | No | Pearson correlation between two sensors |
| GET | `/api/analytics/report` | Yes | No | Combined stats + anomalies + regression snapshot |

### 2.12 Drive Monitoring (`/api/drives/*`) — 13 endpoints

| Method | Path | Auth | CSRF | Description |
|--------|------|------|------|-------------|
| GET | `/api/drives` | Yes | No | List all drives (summary, health, temperature) |
| POST | `/api/drives/rescan` | Yes | Yes | Trigger full drive rescan |
| GET | `/api/drives/settings` | Yes | No | Global drive monitoring settings |
| PUT | `/api/drives/settings` | Yes | Yes | Update global drive monitoring settings |
| GET | `/api/drives/{drive_id}` | Yes | No | Drive detail (SMART attributes, serial, full health) |
| GET | `/api/drives/{drive_id}/attributes` | Yes | No | Raw SMART / NVMe attribute table |
| GET | `/api/drives/{drive_id}/history` | Yes | No | Health history snapshots (query: `hours`, capped to retention) |
| POST | `/api/drives/{drive_id}/refresh` | Yes | Yes | Force immediate re-poll of a single drive |
| POST | `/api/drives/{drive_id}/self-tests` | Yes | Yes | Start SMART self-test (short / extended / conveyance) |
| GET | `/api/drives/{drive_id}/self-tests` | Yes | No | List last 10 self-test runs |
| POST | `/api/drives/{drive_id}/self-tests/{run_id}/abort` | Yes | Yes | Abort running self-test |
| GET | `/api/drives/{drive_id}/settings` | Yes | No | Per-drive settings override |
| PUT | `/api/drives/{drive_id}/settings` | Yes | Yes | Update per-drive settings override |

### 2.13 Temperature Targets (`/api/temperature-targets/*`) — 6 endpoints

| Method | Path | Auth | CSRF | Description |
|--------|------|------|------|-------------|
| GET | `/api/temperature-targets` | Yes | No | List all temperature targets |
| POST | `/api/temperature-targets` | Yes | Yes | Create a temperature target |
| GET | `/api/temperature-targets/{id}` | Yes | No | Get a single target |
| PUT | `/api/temperature-targets/{id}` | Yes | Yes | Update a target |
| DELETE | `/api/temperature-targets/{id}` | Yes | Yes | Delete a target |
| PATCH | `/api/temperature-targets/{id}/enabled` | Yes | Yes | Toggle target enabled state |

### 2.14 Infrastructure — 3 endpoints

| Method | Path | Auth | CSRF | Description |
|--------|------|------|------|-------------|
| GET | `/api/health` | No | No | Health + version + capabilities |
| GET | `/metrics` | No | No | Prometheus exposition (gated by `DRIVECHILL_PROMETHEUS_ENABLED`) |
| WS | `/api/ws` | Yes | No | Real-time sensor stream |

---

## 3. Authentication & Authorization

### Session Auth
- **Login:** POST username + password → server sets `drivechill_session` (httponly) + `drivechill_csrf` (JS-readable) cookies
- **Session TTL:** Configurable via `DRIVECHILL_SESSION_TTL` (default `8h` on both backends)
- **CSRF double-submit:** All POST/PUT/DELETE require `X-CSRF-Token` header matching cookie value
- **API key bypass:** CSRF is skipped when authenticating via `Authorization: Bearer <key>` or `X-API-Key: <key>`

### API Keys
- **Storage:** HMAC-SHA256 hash stored in DB; plaintext shown only once at creation
- **Scoping:** Per-resource read/write (e.g., `read:sensors`, `write:fans`, `read:alerts`, `write:control`)
- **Scope enforcement:** Auth middleware maps request path + method to required scope; rejects with 403 if insufficient

### Rate Limiting & Lockout
- **IP rate limit:** 10 login attempts per minute per IP (Python); evicts stale entries (C#)
- **Account lockout:** 5 failed attempts → 15-minute lockout (C#) / 30-minute lockout (Python)
- **Audit log:** All login attempts (success/failure) logged to `auth_log` table with IP, username, timestamp

### Conditional Auth
- **Localhost binding** (`127.0.0.1`): Auth disabled — all routes accessible without credentials
- **Remote binding** (`0.0.0.0` or non-loopback): Auth enforced — session or API key required
- **`DRIVECHILL_FORCE_AUTH=true`**: Forces auth even on localhost

### First-Time Setup
- `POST /api/auth/setup`: Creates admin user. Blocked after first user exists.
- `DRIVECHILL_PASSWORD`: Auto-creates admin user on startup (headless/Docker use).

---

## 4. Security Controls

### 4.1 Content Security Policy (CSP)

Both backends apply identical CSP via middleware:

```
default-src 'self';
script-src 'self' 'unsafe-inline';
style-src 'self' 'unsafe-inline' https://fonts.googleapis.com;
img-src 'self' data:;
connect-src 'self' ws://{request-host} wss://{request-host};
font-src 'self' https://fonts.gstatic.com;
frame-ancestors 'none';
```

- `connect-src` uses the request `Host` header so WebSocket CSP works for any deployment (not just localhost). This is safe because CSP is a browser-side directive and the Host header reflects the origin the browser used.
- `'unsafe-inline'` required for Next.js static export bootstrap scripts
- `frame-ancestors 'none'` prevents clickjacking

### 4.2 Additional Security Headers

| Header | Value |
|--------|-------|
| `X-Content-Type-Options` | `nosniff` |
| `X-Frame-Options` | `DENY` |
| `Referrer-Policy` | `strict-origin-when-cross-origin` |

### 4.3 SSRF Protection

- **Outbound URL validation:** Private/loopback IPs blocked (`10.x`, `172.16-31.x`, `192.168.x`, `127.x`, `::1`)
- **IPv4-mapped loopback:** `::ffff:127.0.0.1` detected and blocked
- **DNS rebinding (C#):** `SocketsHttpHandler.ConnectCallback` locks resolved IP at connection time
- **Machine proxy paths:** `profile_id` and `fan_id` validated via `^[a-zA-Z0-9_\-]{1,128}$` regex to prevent path traversal
- **Override:** `DRIVECHILL_ALLOW_PRIVATE_OUTBOUND_TARGETS=true` for LAN use cases

### 4.4 Credential Protection

- **SMTP passwords:** AES-256-GCM encrypted at rest (format: `v1:<base64(nonce+ct+tag)>`); requires `DRIVECHILL_SECRET_KEY`
- **API keys:** Only HMAC-SHA256 hash stored in DB; plaintext shown once at creation
- **Session tokens:** 256-bit random hex strings
- **Password hashing:** Argon2 (Python) / PBKDF2 with 100K iterations (C#)
- **Error redaction:** Credentials/URLs stripped from error messages in logs

### 4.5 Webhook Signing

- HMAC-SHA256 signature in `X-DriveChill-Signature` header
- Unix timestamp in `X-DriveChill-Timestamp` header
- Nonce in `X-DriveChill-Nonce` header
- Payload: `{timestamp}.{nonce}.{body}`

### 4.6 TLS

- Direct TLS via `DRIVECHILL_SSL_CERTFILE` + `DRIVECHILL_SSL_KEYFILE`
- Self-signed cert generation via `DRIVECHILL_SSL_GENERATE_SELF_SIGNED=true`
- Reverse proxy detection via `X-Forwarded-Proto` header (both Python and C# backends)
- Secure cookie flags set when TLS detected (direct or proxied)

---

## 5. Data Storage & Encryption

### Database Schema (SQLite WAL)

| Table | Purpose | Sensitive Data |
|-------|---------|---------------|
| `profiles` | Fan profile definitions | No |
| `fan_curves` | Point arrays per profile | No |
| `fan_settings` | Per-fan min speed, zero-RPM | No |
| `settings` | Key-value config (poll interval, temp unit, etc.) | No |
| `sensor_labels` | Custom sensor names | No |
| `alert_rules` | Threshold rules | No |
| `alert_events` | Triggered alert history | No |
| `quiet_hours` | Schedule rules | No |
| `sensor_log` | Historical readings (timestamp, sensor_id, value, unit) | No |
| `users` | username + password_hash | **Yes** (password hash) |
| `sessions` | session_token, csrf_token, username, IP, expiry | **Yes** (session tokens) |
| `api_keys` | key_hash, scopes_json, name | **Yes** (key hash) |
| `auth_log` | event_type, IP, username, outcome, detail | PII (IP addresses) |
| `machines` | name, base_url, api_key, snapshot_json, capabilities_json | **Yes** (remote API keys) |
| `webhook_config` | target_url, signing_secret, timeouts | **Yes** (signing secret) |
| `webhook_deliveries` | timestamp, status_code, payload, error | No |
| `push_subscriptions` | endpoint, p256dh, auth, user_agent | **Yes** (push keys) |
| `email_notification_settings` | SMTP host/port/user/password, TLS flags | **Yes** (SMTP password, encrypted) |
| `drives` | id, name, model, serial, device_path, bus_type, media_type, capacity, firmware | No |
| `drive_health_snapshots` | drive_id, recorded_at, temperature_c, health_status, health_percent, SMART counters | No |
| `drive_attributes_latest` | drive_id, key, name, normalized/worst/threshold values, raw_value, status | No |
| `drive_self_test_runs` | id, drive_id, type, status, progress_percent, started_at, finished_at, provider_ref | No |
| `temperature_targets` | id, name, drive_id, sensor_id, fan_ids_json, target_temp_c, tolerance_c, min_fan_speed, enabled | No |

### Encryption at Rest

| Data | Method | Key Source |
|------|--------|-----------|
| SMTP password | AES-256-GCM | `DRIVECHILL_SECRET_KEY` env var |
| User password | Argon2 / PBKDF2 | N/A (one-way hash) |
| API keys | HMAC-SHA256 | N/A (one-way hash) |
| Session tokens | N/A | Stored as-is (ephemeral, TTL-bound) |

---

## 6. Real-Time Communication

### WebSocket (`/api/ws`)

- **Auth:** Session token validated on connect; re-validated every 60 seconds (time-based, both backends)
- **Heartbeat:** 5-second interval (server pings; client timeout fallback)
- **Message payload:**
  ```json
  {
    "type": "sensor_update",
    "timestamp": "ISO-8601",
    "readings": [...],
    "applied_speeds": { "fan_id": speed_pct },
    "alerts": [...],
    "active_alerts": [...],
    "safe_mode": { "active": bool, "reason": str },
    "fan_test": [...],
    "control_sources": { "fan_id": "profile|temperature_target|startup_safety|panic_sensor|panic_temp|released|manual" },
    "startup_safety_active": bool
  }
  ```
- **Close codes:** `1008` on auth failure; `1000` on clean shutdown
- **Reconnect guard:** Client sets `enabledRef = false` before closing to prevent reconnect scheduling after React unmount

---

## 7. Fan Control & Safety

### Control Loop

1. Sensor service polls hardware at configurable interval (0.5–30s, default 1s)
2. **Virtual sensor resolution**: virtual sensors evaluated before control tick; composed from real sensors via max/min/avg/weighted/delta/moving_avg
3. **Startup safety**: fans run at 50% for 15 seconds before curves load (both backends)
4. For each active curve: evaluate temperature or load → interpolated speed via curve points (cpu_load/gpu_load supported as curve inputs)
5. Composite sensor resolution: MAX of `sensor_ids` list (both backends)
6. Apply maximum speed across all applicable curves per fan; temperature targets can override upward (hottest-wins)
7. **Control transparency**: per-fan `control_source` recorded (`profile`, `temperature_target`, `startup_safety`, `panic_sensor`, `panic_temp`, `released`, `manual`); exposed via REST `/api/fans/status` and WebSocket
8. Hysteresis: 3°C deadband prevents oscillation near threshold points (both backends)
9. Ramp-rate limiting: configurable `fan_ramp_rate_pct_per_sec` clamps speed changes (both backends)
10. Minimum speed floor enforced per-fan (persisted in `fan_settings` table)
11. Zero-RPM: fans flagged as `zero_rpm_capable` allowed to reach 0%

### Safety Modes

| Mode | Trigger | Behavior |
|------|---------|----------|
| Startup safety | Service start (first 15s) | All fans → 50%, exits on profile load or timer expiry |
| Sensor panic | 3+ consecutive sensor read failures | All fans → 100%, overrides release and startup safety |
| Temperature panic | CPU >95°C or GPU >90°C | All fans → 100%, overrides release and startup safety |
| Released | User explicitly releases control | Fans return to BIOS/firmware auto |

### Curve Presets

| Preset | Curve Points (Temp°C → Speed%) |
|--------|-------------------------------|
| Silent | 0→0, 45→15, 65→35, 80→60, 90→100 |
| Balanced | 0→20, 40→30, 60→50, 75→75, 85→100 |
| Performance | 0→35, 35→50, 55→70, 70→90, 80→100 |
| Full Speed | 0→100, 100→100 |
| Gaming | 0→15, 40→20, 55→40, 70→75, 80→95, 90→100 |
| Rendering | 0→25, 45→40, 60→55, 75→70, 85→85, 95→100 |
| Sleep | 0→0, 50→0, 60→10, 75→30, 85→60, 95→100 |

### Fan Benchmarking

- Configurable speed sweep (steps, settle time, min RPM threshold)
- Determines minimum operational speed and RPM response curve
- Results persisted and broadcast via WebSocket
- Cancellable via `DELETE /api/fans/{id}/test`

---

## 8. Multi-Machine Hub

### Architecture

Hub instance polls N remote agent instances. Each agent runs its own DriveChill backend with hardware access.

### Registration

- `POST /api/machines`: name, base_url, api_key, poll_interval (5–3600s), timeout (500–30000ms)
- Outbound URL validated against SSRF blocklist

### Polling

- Background async loop per machine
- Exponential backoff on failure (initial 5s → max 5 min)
- Snapshot cached; freshness > 10s → "offline" status

### Remote Control

- Profile activation, fan release, fan settings — proxied to remote agent
- `last_command_at` timestamp tracked per machine
- Path segments validated against `^[a-zA-Z0-9_\-]{1,128}$`

---

## 9. Notifications

### Web Push (VAPID)

- VAPID key pair configured via `DRIVECHILL_VAPID_PUBLIC_KEY` / `DRIVECHILL_VAPID_PRIVATE_KEY`
- Subscriptions stored with endpoint + p256dh + auth keys
- Auto-cleanup: 410/404 responses trigger subscription deletion
- Python: `pywebpush` library; C#: `Lib.Net.Http.WebPush` v3.3.0

### Email (SMTP)

- Configurable SMTP host, port, TLS/SSL, credentials
- Password encrypted with AES-256-GCM (requires `DRIVECHILL_SECRET_KEY`)
- Test email endpoint for validation
- Python: `aiosmtplib`; C#: `System.Net.Mail`

### HTTP Notification Channels

- Supported types: `ntfy`, `discord`, `slack`, `generic_webhook`
- CRUD: `GET/POST /api/notification-channels`, `GET/PUT/DELETE /api/notification-channels/{id}`
- SSRF protection: `url` and `webhook_url` fields validated at save time (controller) and send time (service)
- C# backend wires `NotificationChannelService.SendAlertAllAsync` into `SensorWorker` fan-out
- Python backend calls `send_alert_all` from `alert_service` on threshold crossing

### Alert Dispatch

- Triggered by `alert_service` when rule threshold crossed
- Fire-and-forget delivery (non-blocking `asyncio.create_task` / `Task.Run`)
- Per-rule cooldown (default 300s) prevents spam
- SMART trend events injected via `AlertService.InjectEvent` (C#) / `inject_event` (Python) from drive monitor

---

## 10. Analytics & Monitoring

### Endpoints

| Endpoint | Description |
|----------|-------------|
| `/api/analytics/history` | Time-bucketed aggregation (min/avg/max per bucket, 10s–86400s buckets); `hours`, `start`/`end`, `sensor_id(s)`, `bucket_seconds`; retention-gated |
| `/api/analytics/stats` | Per-sensor aggregate (min, max, avg, p95, sample count); `hours`, `start`/`end`, `sensor_id(s)` |
| `/api/analytics/anomalies` | Z-score anomaly detection; `z_score_threshold` 1.0–10.0; `hours`, `start`/`end`, `sensor_id(s)` |
| `/api/analytics/regression` | Thermal regression vs rolling baseline; `baseline_days` (7–90), `recent_hours` (1–168), `threshold_delta`; load-band aware (`low`/`medium`/`high`) when cpu_load/gpu_load data is present; `load_band_aware` flag in response |
| `/api/analytics/correlation` | Pearson correlation coefficient between two sensors; requires `x_sensor_id` + `y_sensor_id`; returns coefficient, sample count, raw scatter points |
| `/api/analytics/report` | Combined stats + anomalies + top anomalous sensors + regression snapshot |

### Prometheus Metrics

- `GET /metrics` — text exposition format (disabled by default)
- Enabled via `DRIVECHILL_PROMETHEUS_ENABLED=true`
- Metric name regex validated to reject injection characters

---

## 11. Frontend

### Stack

- Next.js 14 with static export (`npm run build` → `out/`)
- Zustand for state management (3 stores: `appStore`, `authStore`, `settingsStore`)
- CSS: custom properties + utility classes (no component library)
- Icons: `lucide-react`

### Pages

| Page | Component | Description |
|------|-----------|-------------|
| Dashboard | `SystemOverview.tsx` | Sensor cards, fan cards, machine hub, temp charts, storage summary card |
| Fan Curves | `FanCurvesPage.tsx` | Curve editor (SVG), preset selector, profile manager, drive sensor preselection |
| Alerts | `AlertsPage.tsx` | Rule CRUD, event log, active alert indicators |
| Analytics | `AnalyticsPage.tsx` | Stat cards, anomaly table, sparkline charts |
| Settings | `SettingsPage.tsx` | General, notifications, webhooks, quiet hours, API keys, Storage Monitoring |
| Drives | `DrivesPage.tsx` | Drive list (health/temp badges), detail drill-in, SMART attributes, self-test management |

### Key Frontend Features

| Feature | Implementation |
|---------|---------------|
| Real-time updates | WebSocket via `useWebSocket` hook (auto-reconnect with backoff) |
| °C/°F toggle | `tempUnit.ts` utilities; `settingsStore` synced from backend on mount; applied in Drives, Analytics, Dashboard |
| "You are here" indicator | Animated pulsing dot + crosshairs on curve editor |
| Touch-friendly curves | 40px invisible hit-area circles on draggable points |
| Safe mode banner | Red/blue banner with explanation text |
| Changelog banner | Shows on version update, dismissible via localStorage |
| Machine drill-in | Clickable machine cards → remote sensor/fan/profile state |
| Dark/light theme | CSS custom properties toggled via `ThemeToggle` component |
| Confirmation dialogs | All destructive actions (delete profile, revoke key, remove machine) |
| Drive "Use for cooling" | Button in drive detail → pre-selects drive's `hdd_temp` sensor in Fan Curves sensor picker |
| Degraded mode banner | Drives page shows warning when `smartctl` unavailable |

---

## 12. Deployment & Infrastructure

### Docker

| File | Purpose |
|------|---------|
| `docker/Dockerfile` | Multi-stage build: Node 20 (frontend) → Python 3.12 (backend); non-root `drivechill` user |
| `docker/docker-compose.yml` | Standard deployment with health check; notes on drive device access |
| `docker/docker-compose.privileged.yml` | Privileged mode for full drive monitoring without device mapping |
| `docker/.env.example` | Documents all 14 `DRIVECHILL_*` environment variables |

### Windows

| File | Purpose |
|------|---------|
| `scripts/install_windows.ps1` | Standalone installer |
| `scripts/install_windows_service.ps1` | NSSM service registration (AppDirectory + relative AppParameters) |

### Linux

| File | Purpose |
|------|---------|
| `scripts/install_linux.sh` | systemd unit file installation |

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `DRIVECHILL_HOST` | `127.0.0.1` | Bind address |
| `DRIVECHILL_PORT` | `8085` | Bind port |
| `DRIVECHILL_DATA_DIR` | `./data` | Database + cert storage |
| `DRIVECHILL_PASSWORD` | — | Auto-create admin user |
| `DRIVECHILL_HARDWARE_BACKEND` | `auto` | `mock` / `lhm_direct` / `lhm_http` |
| `DRIVECHILL_VAPID_PUBLIC_KEY` | — | Web Push VAPID public key |
| `DRIVECHILL_VAPID_PRIVATE_KEY` | — | Web Push VAPID private key |
| `DRIVECHILL_VAPID_CONTACT_EMAIL` | `admin@localhost` | VAPID contact |
| `DRIVECHILL_SECRET_KEY` | — | AES-256-GCM encryption key |
| `DRIVECHILL_SSL_CERTFILE` | — | TLS certificate path |
| `DRIVECHILL_SSL_KEYFILE` | — | TLS key path |
| `DRIVECHILL_SSL_GENERATE_SELF_SIGNED` | `false` | Auto-generate self-signed cert |
| `DRIVECHILL_FORCE_AUTH` | `false` | Force auth even on localhost |
| `DRIVECHILL_ALLOW_PRIVATE_OUTBOUND_TARGETS` | `false` | Allow LAN machine targets |
| `DRIVECHILL_SESSION_TTL` | `8h` | Session TTL (e.g., `15m`, `8h`, `7d`) |
| `DRIVECHILL_PROMETHEUS_ENABLED` | `false` | Enable `/metrics` endpoint |

---

## 13. Test Coverage

### Python Backend (537 passing, 13 skipped)

| Test File | Tests | Coverage Area |
|-----------|-------|---------------|
| `test_alert_cooldown.py` | 6 | Alert cooldown, active alerts, event limit |
| `test_alert_profile_switching.py` | 7 | Alert-triggered profile switch, `revert_after_clear` true/false, suppress-wins, multi-rule |
| `test_analytics_contract.py` | — | Analytics API contract (history/stats/anomalies/regression/correlation/report) |
| `test_analytics_downsampling.py` | 4 | §6.3 downsampling correctness: raw min/max preserved per bucket, no bleed-through |
| `test_analytics_perf.py` | 7 | Analytics performance over 130k-row/30d dataset (all queries < 2 s) |
| `test_api_keys.py` | 3 | API key CRUD |
| `test_api_key_scopes.py` | 4 | Scoped key filtering, wildcard matching |
| `test_auth.py` | 19 | Login/logout/setup, rate limiting, lockout |
| `test_auth_http.py` | 7 | HTTP auth (Bearer, X-API-Key), session revalidation |
| `test_backup_restore.py` | 12 | Profile/curve/setting export/import, DB snapshots, `action_json` round-trip, `notification_channels` round-trip, old-backup compat |
| `test_composite_curves.py` | 3 | Composite temp resolution (CPU/GPU/HDD) |
| `test_drive_monitoring.py` | 60 | ATA/NVMe parsing, device path validation, health normalization, self-test state, route validation, degraded mode |
| `test_drives_parity.py` | 38 | Cross-backend contract parity for all `/api/drives/*` endpoints |
| `test_drives_routes.py` | 30 | Drive route integration tests |
| `test_fan_settings.py` | 4 | Per-fan min speed, zero-RPM, persistence |
| `test_liquidctl_backend.py` | 16 | liquidctl parsing, discovery, sensor reads, fan control, duplicate-device disambiguation |
| `test_machine_registry.py` | 13 | Machine CRUD, polling, snapshots, backoff |
| `test_migration_backup.py` | 6 | Migration execution, backup before migration |
| `test_notification_channel_service.py` | 6 | SSRF delivery blocking (ntfy/discord/slack/generic), safe-URL pass-through, multi-channel tracking |
| `test_profile_import_export.py` | 8 | Profile JSON import/export, curve ID freshening |
| `test_ramp_rate.py` | 5 | Fan ramp-rate service (gradual acceleration, deceleration) |
| `test_release_gates.py` | 20 | Safety gates (dangerous curves, active profile deletion, quiet hours) |
| `test_fan_service_startup_safety.py` | 14 | Startup safety active state, expiry, apply_profile exit, control loop 50%, panic override; control transparency per-fan sources |
| `test_security_regressions.py` | 4 | Auth bypass, CSRF, credential redaction |
| `test_sensor_labels.py` | 5 | Label CRUD, max length, collision handling |
| `test_settings_ttl_validation.py` | 1 | Session TTL enforcement |
| `test_thermal_regression.py` | 6 | Thermal regression detection (warning/critical/insufficient baseline) |
| `test_webhooks.py` | 11 | Webhook delivery, retries, signing, failure handling |

### Frontend

| Type | Tool | Status |
|------|------|--------|
| TypeScript type-check | `npm run build` | Passing (0 errors) |
| E2E tests | Playwright | 8 spec files (dashboard, fan-curves, alerts, settings, drives, analytics, temperature-targets, quiet-hours) |

### C# Backend (205 passing)

| Type | Tool | Status |
|------|------|--------|
| Build verification | `dotnet build` | Passing (0 warnings, 0 errors) |
| Unit tests | `dotnet test` | 205 tests passing |
| Test files | xUnit | AlertServiceTests (profile switching, `revert_after_clear`, InjectEvent), DriveMonitorServiceTests, FanServiceTests, TemperatureTargetServiceTests, AuthLogTests, FansControllerTests, MachinesControllerTests, NotificationChannelServiceTests (SSRF save-time + send-time), QuietHoursControllerTests, SettingsControllerTests (export/import notification channels), SmartTrendServiceTests, AnalyticsControllerTests |

---

## 14. Known Limitations & Deferred Items

| Item | Status | Notes |
|------|--------|-------|
| ~~E2E tests in CI~~ | ✅ Resolved | GitHub Actions `e2e.yml` workflow runs Playwright specs (v2.2.0) |
| ~~C# backend unit tests~~ | ✅ Resolved | 167+ tests in `Tests/DriveChill.Tests.csproj` |
| ~~C# drive-monitoring provider layer~~ | ✅ Resolved | `IDriveProvider` abstraction with `SmartctlDriveProvider` + `MockDriveProvider` (v2.3) |
| ~~`alert()` / `confirm()` dialogs~~ | ✅ Resolved | `ConfirmDialog` + `ToastProvider` components (v2.2.0) |
| ~~°F gauge rendering~~ | ✅ Resolved | Gauge arc/colors computed in display unit (v2.2.0) |
| Playwright browser install | Manual | `npx playwright install chromium` required before first run |
| ~~Multi-user RBAC~~ | ✅ Resolved | Admin/viewer roles with session propagation (v2.2.0) |
| ~~Audit log rotation~~ | ✅ Resolved | `CleanupOldAuthLogsAsync` — 90-day retention, hourly prune (v2.2.0) |
| Session token rotation | Deferred | Tokens valid until TTL expiry; low risk after password-change invalidation |
| ~~WebSocket auth renegotiation~~ | ✅ Resolved | Time-based 60-second revalidation in both backends (v2.3) |
| ~~Virtual sensor CRUD~~ | ✅ Resolved | 6 types (max/min/avg/weighted/delta/moving_avg), CRUD in both backends, runtime resolution before control tick, Settings-page UI (v2.3) |
| ~~Load-based fan-curve inputs~~ | ✅ Resolved | cpu_load/gpu_load accepted in curve sensor picker; curve editor labels/groups update by source type (v2.3) |
| ~~Control transparency~~ | ✅ Resolved | Per-fan control_source tracked (profile/temp_target/startup_safety/panic_sensor/panic_temp/released/manual); REST + WS; badges in FanCurvesPage (v2.3) |
| ~~Alert-triggered profile switching~~ | ✅ Resolved | `revert_after_clear` semantics correct in both backends; Python + C# tests passing (v2.3) |
| ~~SMART trend alerting~~ | ✅ Resolved | `DriveMonitorService` → `AlertService.InjectEvent` wired in C#; `SmartTrendAlert` carries `ActualValue`/`Threshold`; 14 new C# tests (v2.3) |
| ~~Notification channel expansion~~ | ✅ Resolved (HTTP) | ntfy, Discord, Slack, generic webhook; SSRF hardening save-time + send-time; C# `SensorWorker` fan-out wired; MQTT deferred to v2.4 |
| ~~liquidctl duplicate-device ambiguity~~ | ✅ Resolved | ID prefix includes USB address; `--address` flag on all targeted commands; 4 new Python tests (v2.3) |
| ~~Backup `action_json` + `notification_channels` data loss~~ | ✅ Resolved | Both fields preserved in Python export/import; C# settings export/import includes notification channels (v2.3) |
| Linux hwmon fan-write | Open (stretch) | Planned for v2.3 Phase 10 |
| USB liquidctl support | Partial | Python backend + tests done; C# deferred; no hardware smoke test yet |

---

*End of audit document.*
