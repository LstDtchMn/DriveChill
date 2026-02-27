# DriveChill Multi-Machine Design (v1.5)

**Date:** 2026-02-26  
**Status:** Approved  
**Authors:** Product + Engineering  
**Scope:** Pre-1.5 architecture baseline for hub/agent monitoring

---

## 1. Purpose

This document defines the v1.5 multi-machine architecture required by PRD prerequisite `pre-1.5-A`.

It covers:
- Discovery model
- Auth bootstrap
- Version compatibility handling
- Conflict resolution
- Partial failure behavior

---

## 2. Goals and Non-Goals

### Goals (v1.5)
- One DriveChill instance can run in **hub mode** and monitor multiple **agent** instances.
- Hub shows per-machine health and thermal data with freshness under 3 seconds in normal conditions.
- Agent outage on one machine does not degrade data/control for others.
- Agent-to-hub traffic uses API keys (session cookies remain for browser users).

### Non-Goals (v1.5)
- Cloud relay / WAN discovery
- Cross-user RBAC
- Distributed write consensus
- PostgreSQL migration (SQLite remains default)

---

## 3. Runtime Roles

### Agent
- Existing backend running on each monitored machine.
- Owns hardware access and fan control for that machine only.
- Exposes REST + WebSocket endpoints for status/control.

### Hub
- One DriveChill backend + UI that aggregates data from agents.
- Maintains machine registry and health state.
- Proxies or dispatches per-machine control actions.

---

## 4. Discovery Model

### Decision
Use **manual machine registry** in v1.5.

### Why
- Deterministic and secure for LAN homelab environments.
- No multicast/UPnP complexity.
- Easier to audit and test for v1.5 release gates.

### Registry fields
- `machine_id` (UUID)
- `display_name`
- `base_url` (`http(s)://host:port`)
- `api_key_id` (reference to stored credential)
- `enabled` (bool)
- `poll_interval_seconds` (default 2)
- `timeout_ms` (default 1200)
- `created_at`, `updated_at`, `last_seen_at`
- `status` (`online`, `degraded`, `offline`, `auth_error`, `version_mismatch`)

### Health behavior
- Poll interval target: 2 seconds.
- Mark `offline` after 3 consecutive failures or 10 seconds without success (whichever first).
- Online transition requires 1 successful health response.

---

## 5. Data Flow

1. Hub scheduler polls each enabled agent `/api/health` and lightweight snapshot endpoint.
2. Hub stores latest machine snapshot in memory cache (plus optional compact persistence for restart warm state).
3. UI machine grid reads aggregated hub state.
4. Control actions (activate profile, release fans, etc.) route hub -> target agent with API key auth.

### Performance targets
- 3 machines at 2-second polling with no starvation on shared event loop.
- Per-agent timeout isolation so one slow machine does not block others.

---

## 6. Authentication Bootstrap

### Principles
- Keep existing v1.0 session auth for human browser clients.
- Introduce API key auth for machine-to-machine paths only.
- No plaintext key storage in DB.

### API key format and storage
- Display key once at creation (`dc_live_<random>` style).
- Store only `key_id`, `key_prefix`, `key_hash`, `created_at`, `revoked_at`, `last_used_at`.
- Hash with SHA-256 + server-side pepper.

### Request auth precedence
1. Internal token (existing tray local path)
2. API key header (`Authorization: Bearer <key>` or `X-API-Key`)
3. Session cookie

### CSRF
- Session-authenticated state-changing requests require CSRF (existing behavior).
- API key requests are stateless and CSRF-exempt.

### Bootstrap flow
1. Admin creates API key on agent (scoped for hub ingestion/control).
2. Admin registers machine in hub with agent URL + key.
3. Hub performs verification call and stores hashed credential reference.
4. Failed verification keeps machine in `auth_error`.

---

## 7. Version Compatibility Matrix Rules

### Baseline
- Current `/api/*` is treated as API v1.
- Additive changes are allowed in-place.
- Breaking changes require `/api/v2/*`.

### Handshake
Agent health payload includes:
- `app_version`
- `api_version` (e.g., `v1`)
- optional `capabilities[]` (e.g., `webhooks`, `composite_curves`, `fan_settings`)

Hub policy:
- Reject incompatible major API versions.
- Mark machine `version_mismatch` and keep other machines operational.

---

## 8. Conflict Resolution

### Scope boundaries
- Each agent is source-of-truth for its own fan control state.
- Hub does not perform cross-agent transactional operations.

### Command model
- Last-write-wins per machine with monotonic timestamp on command dispatch.
- Idempotent operations where possible (`activate profile X`, `release control`).

### Concurrent control
- If local UI and hub both issue commands to the same agent, agent accepts latest valid command and logs actor/context.
- Hub reflects resulting state on next poll/WebSocket update.

---

## 9. Partial Failure Behavior

### Agent offline
- Only that machine card becomes `offline`.
- Other machines continue normal polling/control.

### Agent auth failure
- Machine enters `auth_error`.
- Hub backs off retry (2s -> 4s -> 8s capped at 30s).

### Hub restart
- Registry and credentials persist in SQLite.
- On restart, machines begin in `degraded` until first successful poll.

### Slow agent
- Per-request timeout prevents blocking global scheduler.
- Machine marked `degraded` before `offline` when intermittent.

### WebSocket/poll disruption
- Hub falls back to polling-only for affected machine.
- UI freshness indicator shows stale age.

---

## 10. API Surface (Planned v1.5 Additions)

Hub-local:
- `GET /api/machines`
- `POST /api/machines`
- `PUT /api/machines/{id}`
- `DELETE /api/machines/{id}`
- `POST /api/machines/{id}/verify`
- `GET /api/machines/{id}/snapshot`

Agent-local (auth extension):
- `POST /api/auth/api-keys`
- `GET /api/auth/api-keys`
- `DELETE /api/auth/api-keys/{id}`

Webhook support (separate v1.5 gate):
- `GET /api/webhooks`
- `PUT /api/webhooks`

---

## 11. Test Plan Mapping (v1.5 Gates)

- `v1.5-2` Multi-machine data flow:
  - Run 3 mock agents on separate ports.
  - Verify hub freshness <3s.
  - Kill one agent; verify `offline` <=10s and no impact to other cards.
- `v1.5-3` Auth:
  - 401 for unauthenticated session routes when auth enabled.
  - API key required and accepted for hub-to-agent routes.
- `v1.5-4` Webhook:
  - Alert event emits webhook within 3s and required payload fields.

---

## 12. Risks and Mitigations

- Risk: API key leakage in logs.
  - Mitigation: redact headers; only log key prefix + key_id.
- Risk: Poll storm with many machines.
  - Mitigation: bounded concurrency + jitter + timeout caps.
- Risk: Version skew.
  - Mitigation: explicit `api_version` handshake and `version_mismatch` state.

---

## 13. Approval Record

- **Product approval:** Approved (2026-02-26)
- **Engineering approval:** Approved (2026-02-26)
- **Release impact:** Satisfies PRD prerequisite `pre-1.5-A`.

