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

## Notes

- Python remains source for machine-hub orchestration until C# machine registry is implemented.
- Shared response fields for health handshake:
  - `api_version`
  - `capabilities[]`
