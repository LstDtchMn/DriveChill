# DriveChill v1.0 — Second-Party Review Checklist

Generated: 2026-02-26

---

## Security

| # | Item | File(s) | Why it matters |
|---|------|---------|----------------|
| S1 | **Internal token bypasses all auth** — `X-DriveChill-Internal` header skips both session auth and CSRF. Verify the token is not leaked in logs, error responses, or browser-visible headers. | `backend/app/api/dependencies/auth.py:17-20`, `backend/app/config.py:38-40` | A leaked token grants full unauthenticated API access |
| S2 | **Internal token is a pydantic-settings field** with `env_prefix="DRIVECHILL_"` — meaning `DRIVECHILL_INTERNAL_TOKEN` env var could override it to a known value | `backend/app/config.py:40-42` | Attacker with env access could set a predictable token |
| S3 | **bcrypt cost factor and lockout thresholds** — verify 5-attempt lockout, 15-min window, cost 12 are appropriate for deployment target | `backend/app/services/auth_service.py` | Too low = brutable; too high = DoS on login |
| S4 | **Session token entropy** — `secrets.token_hex(32)` = 256 bits. Verify session cookie flags: `HttpOnly`, `SameSite=Strict`, `Secure` (when not localhost) | `backend/app/api/routes/auth.py` | Stolen session = full account takeover |
| S5 | **CSRF double-submit correctness** — server-stored CSRF token compared to header; verify cookie `drivechill_csrf` is NOT HttpOnly (JS must read it) but IS `SameSite=Strict` | `backend/app/api/routes/auth.py` | Misconfig defeats CSRF protection entirely |
| S6 | **Rate limiter is in-memory only** — resets on restart, per-process. With multiple workers or behind a reverse proxy, limits don't aggregate | `backend/app/services/auth_service.py` | Distributed brute force possible |
| S7 | **SQL parameterization** — all DB queries use `?` placeholders. Verify no f-string interpolation in any query across all repos | All `repositories/` and `services/` | SQL injection |

## Data Integrity

| # | Item | File(s) | Why it matters |
|---|------|---------|----------------|
| D1 | **Profile activation is atomic** — `UPDATE ... SET is_active = CASE` in a single statement. Verify no race between concurrent activate calls (two tray clicks) | `backend/app/db/repositories/profile_repo.py` | Two profiles marked active = undefined fan behavior |
| D2 | **`seed_missing_presets()` runs on every startup** — verify it doesn't create duplicates if the preset name was renamed by the user | `backend/app/db/repositories/profile_repo.py` | Phantom preset profiles accumulating |
| D3 | **Migration 004 (auth tables)** — verify `sessions` FK to `users` has `ON DELETE CASCADE` so user deletion cleans up sessions | `backend/app/db/migrations/004_auth_tables.sql` | Orphaned sessions after user removal |
| D4 | **`auth_log` 90-day retention** — verify the cleanup query uses an indexed column and won't lock the DB for long on large tables | `backend/app/services/auth_service.py` | Slow cleanup blocks sensor writes (shared DB) |

## Fan Safety (Critical Path)

| # | Item | File(s) | Why it matters |
|---|------|---------|----------------|
| F1 | **Panic mode overrides ALL other logic** — verify `_sensor_panic` and `_temp_panic` flags cannot be cleared by profile activation, tray release, or API calls | `backend/app/services/fan_service.py` | Fans drop to low speed during thermal emergency |
| F2 | **Tray "Release Fan Control" + profile switch interaction** — if user releases fans then switches profile from tray, verify `_released` flag is properly cleared | `backend/app/services/fan_service.py`, `backend/app/tray.py` | Fans stuck in BIOS mode despite active profile |
| F3 | **Graceful shutdown fan restore** — SIGTERM/SIGINT handlers call `release_fan_control()`. Verify this works when process is killed by Task Manager (no signal) | `backend/app/main.py:211-234` | Fans stuck at last set speed after crash |
| F4 | **Dangerous curve warning bypass** — `override=true` skips the low-speed-at-high-temp check. Verify no code path sends override without user confirmation | `frontend/src/components/fan-curves/FanCurvesPage.tsx:110-111` | Silent override = hardware damage risk |
| F5 | **Curve engine handles NaN/Infinity from sensor** — if sensor returns garbage, verify interpolation doesn't produce NaN fan speed | `backend/app/services/curve_engine.py` | Fans set to 0% or undefined |

## Frontend Correctness

| # | Item | File(s) | Why it matters |
|---|------|---------|----------------|
| U1 | **Notification timestamp comparison uses string `>`** — this works for ISO 8601 strings but verify the backend always sends zero-padded UTC timestamps | `frontend/src/hooks/useNotifications.ts:65` | Out-of-order timestamps = missed or duplicate notifications |
| U2 | **`useNotifications()` runs before auth check** — if store gets alert data before auth resolves, first-render guard captures stale state | `frontend/src/app/page.tsx:74` | Spurious notification on initial load |
| U3 | **CurveEditor keyboard handler scoped to SVG** — verify `tabIndex={0}` doesn't create unexpected tab-order issues with screen readers | `frontend/src/components/fan-curves/CurveEditor.tsx` | Accessibility regression |
| U4 | **`localStorage` reads at Zustand module init** — potential SSR hydration mismatch if `notificationsEnabled` differs server vs client | `frontend/src/stores/settingsStore.ts:26` | React hydration warning in dev |
| U5 | **`window.confirm()` for dangerous curves** — blocks the main thread. Verify this doesn't cause WebSocket disconnect on slow user response | `frontend/src/components/fan-curves/FanCurvesPage.tsx:105` | WS reconnect drops applied speeds display |

## System Tray

| # | Item | File(s) | Why it matters |
|---|------|---------|----------------|
| T1 | **Profile refresh timer runs every 15s** — 15-second staleness window where the menu shows outdated active profile | `backend/app/tray.py` | User confusion after switching profile in browser |
| T2 | **`sys.exit(0)` in `_quit` runs on a pystray worker thread** — on Windows this may only exit that thread, not the process | `backend/app/tray.py` | Process lingers after "Quit" |
| T3 | **Tray -> localhost HTTP calls have 3-5s timeouts** — if backend is unresponsive, verify no tray callbacks queue up and exhaust threads | `backend/app/tray.py` | Tray becomes unresponsive |

## Autostart

| # | Item | File(s) | Why it matters |
|---|------|---------|----------------|
| A1 | **Windows `schtasks` uses `ONLOGON` trigger** — verify `/RL HIGHEST` (elevated) is actually needed; may cause UAC prompt | `backend/app/services/autostart_service.py` | UAC popup on every login |
| A2 | **Linux systemd user service** — verify `loginctl enable-linger` is documented as a requirement; without it, service stops on logout | `backend/app/services/autostart_service.py` | Service silently stops when user logs out of SSH |

## Test Coverage Gaps

| # | Item | Why it matters |
|---|------|----------------|
| G1 | **No tests for `_is_internal_request` auth bypass** | Core security path untested |
| G2 | **No integration test for tray -> API profile activation flow** | Critical user workflow |
| G3 | **No test for `seed_missing_presets` idempotency** | Could create duplicates on repeated restarts |
| G4 | **No test for notification timestamp-based dedup** | Frontend logic with subtle edge cases |
| G5 | **No test for `session_ttl` parsing edge cases** (e.g., "0h", "-1d", "abc") | Crash or infinite session TTL |
