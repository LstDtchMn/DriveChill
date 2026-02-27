# DriveChill API Compatibility Matrix

**Date:** 2026-02-26  
**Status:** Approved  
**Owner:** Engineering

---

## 1. Purpose

This file is the source of truth for client-to-API compatibility, as required by PRD prerequisite `pre-1.5-C`.

Current policy:
- `/api/*` is API v1 (unversioned path, semantically versioned contract).
- Additive response fields are non-breaking.
- Breaking changes require a versioned path (for example, `/api/v2/*`).

---

## 2. Compatibility Matrix

| Client | Client Version | API Contract | Auth Mode | Status | Notes |
|---|---|---|---|---|---|
| Web Dashboard (Next.js UI) | 1.5.0 | v1 (`/api/*`) | Session cookie + CSRF | Compatible | Primary shipped client |
| System Tray Local Actions | 1.5.0 | v1 (`/api/*`) | Internal token header | Compatible | Local loopback only |
| CLI Maintenance (`drivechill.py`) | 1.5.0 | n/a (local process + DB) | n/a | Compatible | Backup/restore/autostart commands |
| Multi-Machine Hub Client | Planned v1.5.x | v1 (`/api/*`) + capability handshake | API key | Planned | Pending implementation |
| Mobile App | Not started | TBD | TBD | Not Applicable | Explicit non-goal for v1.5 |

---

## 3. Endpoint Contract Coverage (v1)

| Area | Endpoints | Client(s) |
|---|---|---|
| Health | `GET /api/health` | Web Dashboard |
| Auth | `/api/auth/login`, `/api/auth/logout`, `/api/auth/session`, `/api/auth/status` | Web Dashboard |
| API Keys | `/api/auth/api-keys*` | Web Dashboard, Multi-Machine Hub |
| Sensors | `/api/sensors*` | Web Dashboard |
| Fans | `/api/fans*` | Web Dashboard, System Tray |
| Profiles | `/api/profiles*` | Web Dashboard, System Tray |
| Alerts | `/api/alerts*` | Web Dashboard |
| Settings | `/api/settings` | Web Dashboard |
| Quiet Hours | `/api/quiet-hours*` | Web Dashboard |
| Webhooks | `/api/webhooks*` | Web Dashboard |
| Realtime | `WS /api/ws` | Web Dashboard |

---

## 4. Known Compatibility Constraints

- Auth requirement is deployment-dependent:
  - localhost bind (`127.0.0.1`, `localhost`, `::1`) can run without login
  - non-localhost bind requires session auth and startup password bootstrap
- CSRF header is required for state-changing session-authenticated requests.
- Internal token bypass is for trusted local tray-to-backend traffic only.
- API key scope enforcement is active on protected `/api/*` routes.
- New API keys default to `read:sensors` unless explicit scopes are provided at creation.

---

## 5. Change Control

Any proposed API change must classify as one of:

1. Additive (non-breaking):
   - New endpoint
   - New optional request field
   - New response field
2. Breaking:
   - Removed field
   - Changed field type
   - Path change
   - Auth requirement change

Breaking changes require:
- New versioned route family (`/api/v2/*`)
- Compatibility matrix update in this file
- Contract tests for old and new clients during deprecation window

---

## 6. Update Procedure

When a new client or API version is introduced:

1. Add a new row to the compatibility matrix.
2. Record auth mode and minimum supported backend version.
3. Link release notes/PR in the row notes.
4. Add/adjust contract tests in backend test suite.

---

## 7. Release Checklist Hook

Before shipping any minor release:
- Matrix includes all active clients.
- No row marked `Compatible` without passing contract tests.
- Planned rows have owners and target milestone.
