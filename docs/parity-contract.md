# DriveChill Python/C# Parity Contract (v1)

Updated: 2026-02-27

## Core

| Area | Endpoint | Python | C# |
|---|---|---|---|
| Health handshake | `GET /api/health` | Yes | Yes |
| Sensors snapshot | `GET /api/sensors` | Yes | Yes |
| Alerts | `/api/alerts*` | Yes | Yes |
| Settings | `/api/settings` | Yes | Yes |
| Fan control | `/api/fans*` | Yes | Yes |
| Profiles | `/api/profiles*` | Yes | Yes |
| WebSocket | `WS /api/ws` | Yes | Yes |

## v1.5 security/integrations

| Area | Endpoint | Python | C# |
|---|---|---|---|
| API keys create/list/revoke | `/api/auth/api-keys*` | Yes | Yes |
| Webhook config | `/api/webhooks` | Yes | Yes |
| Webhook deliveries | `/api/webhooks/deliveries` | Yes | Yes |
| Machine registry | `/api/machines*` | Yes | Planned |
| Session auth (login/logout/setup) | `/api/auth/*` | Yes | Yes |
| Sensor labels | `/api/sensors/labels` | Yes | Yes |
| Safe mode / panic mode | `/api/fans/release` | Yes | Yes |
| Quiet hours | `/api/quiet-hours*` | Yes | Yes |

## Known C# Issues (fixed)

- ~~GDI handle leak in tray icon generation~~ — Fixed: `GetHicon()` handle now freed via `DestroyIcon`
- ~~Thread-safety: `_controls` in `LhmBackend` accessed under two different locks~~ — Fixed: unified on `SemaphoreSlim`
- ~~Thread-safety: `DbService._initialised` flag has no locking~~ — Fixed: double-check locking with `SemaphoreSlim` + `volatile`
- ~~Thread-safety: `FanService._lastApplied` written outside `ReaderWriterLockSlim`~~ — Fixed: writes under write lock
- ~~`FanService` constructor calls `GetFanIds()` before first sensor poll~~ — Fixed: lazy sync on first `ApplyCurvesAsync`
- ~~Polling interval setting change via API never takes effect~~ — Fixed: reads from `SettingsStore` at runtime
- ~~CSV export has no field escaping~~ — Fixed: RFC 4180 escaping for commas/quotes/newlines
- ~~`PUT /api/settings` accepts full `StoredData`~~ — Fixed: accepts only `PollIntervalMs`, `RetentionDays`, `TempUnit`
- ~~`WebSocketHub` does not check authentication~~ — Fixed: validates session cookie when auth is required (localhost bind remains open)

## Notes

- Python remains the primary backend for new feature development.
- C# backend targets feature parity for core control and monitoring.
- Shared response fields for health handshake:
  - `api_version`
  - `capabilities[]`
