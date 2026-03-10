# Milestone C Implementation — Retrospective Audit

**Scope:** Retrospective audit of commit `837cff3` on `main`. This document was
written after that commit and is not part of the audited change set.

**Date:** 2026-03-10
**Milestone position:** Backend-complete, UI-partial. All backend and API work
is implemented. Python test coverage was expanded (+23 new tests); C# changes
were validated by build plus existing tests, with dedicated new C# tests
deferred (see Section 11). Frontend MQTT configuration UI and telemetry wiring
into the runtime poll loops are deferred to Milestone D.

### Verification Evidence

**Baseline** (commit `a0ef5ed`, pre-milestone):
- Python: 537 passed, 13 skipped
- C#: 205 passed
- E2E: 39 passed (1 pre-existing settings toast flake)

**Result** (commit `837cff3`, post-milestone):
- Python: 560 passed, 13 skipped (+23 new test cases, +2 updated existing assertions)
- C#: 205 passed, 0 new C# tests added (see Known Gaps, Section 11)
- Frontend build: clean (0 errors)
- E2E: 39 passed, 1 pre-existing flake (settings °F toast timing)

**Commands used:**
```
cd backend && python -m pytest tests/ -q
cd backend-cs && dotnet build --nologo -v q
cd backend-cs && dotnet test Tests/DriveChill.Tests.csproj --nologo -v q
cd frontend && npm run build
cd frontend && npx playwright test --reporter=list
```

All verification was run locally on Windows 11 (no CI run ID). No tests were
newly skipped, disabled, or filtered for this milestone; the 13 skipped Python
tests are pre-existing environmental skips (e.g., platform-specific tests on
Windows) and are included in both baseline and result counts.

---

## 1. MQTT Publish-Only Integration

**Business rationale:** Users running home automation (Home Assistant, Node-RED) need DriveChill alerts and sensor telemetry published to an MQTT broker. This was the #1 integration request in the product roadmap (Section 6.1). Designed as publish-only to avoid the complexity of inbound command handling.

**Architecture decision:** MQTT is implemented as a new notification channel type (`mqtt`) rather than a separate subsystem. This reuses the existing `NotificationChannelService` fan-out pipeline and database schema (`notification_channels` table with `type='mqtt'`) — no new tables or services needed. The backend API and service layers are complete in both Python and C#. The frontend Settings UI does not yet expose MQTT as a channel type option; that is deferred to Milestone D (see Section 10). MQTT channels can currently be created and managed via the API directly.

| # | File | Change | Why |
|---|------|--------|-----|
| 1 | `backend/app/services/notification_channel_service.py` | Added `mqtt` to `VALID_CHANNEL_TYPES`, `_mqtt_clients` dict, `_get_mqtt_client()`, `_send_mqtt()`, `publish_telemetry()`, `_close_mqtt_clients()` | Python backend needs to publish alerts and telemetry to MQTT brokers. Lazy client caching avoids reconnecting on every publish. `publish_telemetry()` is separate from `_send_mqtt()` because telemetry publishes per-sensor topics (`{prefix}/sensors/{id}`) on every poll cycle, while alerts publish to `{prefix}/alerts` only on threshold breach. |
| 2 | `backend-cs/Services/NotificationChannelService.cs` | Added `mqtt` to `ValidTypes`, `MqttClientWrapper` class, `GetOrCreateMqttClientAsync()`, `SendMqttAsync()`, `PublishTelemetryAsync()`, `CloseMqttClientsAsync()`, `GetInt()`/`GetBool()` helpers | C# backend parity. Same MQTT channel behavior as Python. `MqttClientWrapper` wraps `IMqttClient` with a `SemaphoreSlim` to prevent concurrent publishes on a single TCP connection (MQTTnet is not thread-safe per client). `GetInt()`/`GetBool()` extract typed values from the `Dictionary<string, object>` config — needed because JSON deserialization produces `JsonElement` not native types. |
| 3 | `backend/requirements.txt` | Added `aiomqtt>=2.0.0` | Python MQTT client library. `aiomqtt` is the maintained asyncio wrapper around `paho-mqtt`, compatible with the existing async service architecture. |
| 4 | `backend-cs/DriveChill.csproj` | Added `MQTTnet 4.3.7.1207` | C# MQTT client library. MQTTnet is the most widely used .NET MQTT library with native async support. |

---

## 2. Linux hwmon Write Hardening

**Business rationale:** The hwmon backend (186 lines, 18 tests at baseline) could write to sysfs PWM nodes but had sparse error handling. Users on Linux reported confusion when fan control silently failed due to permissions. The roadmap (Section 5.4) called for hardening the write path before broader Linux adoption.

**Architecture decision:** Per-fan health status (ok/degraded/error) rather than backend-wide failure. A single bad fan node shouldn't disable control of all other fans.

| # | File | Change | Why |
|---|------|--------|-----|
| 5 | `backend/app/hardware/hwmon_backend.py` | Added `WRITE_TIMEOUT_SECONDS = 2.0` constant | sysfs writes can hang indefinitely if the kernel driver stalls (e.g., during USB disconnect of a USB-attached fan controller). The 2s timeout prevents the entire poll loop from blocking. |
| 6 | Same | Added `MAX_RETRY_ON_EIO = 1` constant | EIO (errno 5) is a transient error on some chipsets (notably nct6775) when the Super I/O bus is temporarily busy. A single retry resolves most transient cases without masking real hardware failures. |
| 7 | Same | Added `status: str = "ok"` field to `HwmonFanNode` dataclass | Tracks per-fan health so the frontend can show which fans are controllable vs degraded vs permanently failed. Three states: `ok` (writable), `degraded` (transient error, still attempting writes), `error` (permission denied, writes refused). |
| 8 | Same | Added `_verify_write_permissions()` | On startup, test-writes the current PWM value back to each fan. Catches permission errors immediately with actionable `chmod`/`chgrp` instructions instead of failing silently on the first real speed change minutes later. Marks fans as `error` (PermissionError) or `degraded` (other OSError) so the user sees the problem in the dashboard. |
| 9 | Same | Updated `set_fan_speed()` with retry on EIO, degraded/error state tracking, EACCES → error with chmod instructions, recovery from degraded on success | Before: any write error returned `False` with a log warning. After: EIO gets one retry (resolves transient bus contention), EACCES permanently marks the fan as `error` with repair instructions, other errors mark `degraded` (still retried next cycle), and a successful write after `degraded` auto-recovers to `ok`. This prevents a single glitch from permanently disabling a fan. |
| 10 | Same | Added `get_fan_status()` method | Returns `{fan_id: {status, chip_name, pwm_path}}` dict for frontend display. Needed so the dashboard can show per-fan health indicators. |
| 11 | Same | Updated `get_backend_name()` to show ok/degraded/error counts | Before: `"hwmon (Linux, 2 writable fans)"`. After: `"hwmon (Linux, 2 ok)"` or `"hwmon (Linux, 1 ok, 1 degraded)"`. Gives operators immediate visibility into fan controller health from the system overview. |
| 12 | Same | Added `asyncio.wait_for` timeout on `_write_sysfs_async()` | Wraps the `loop.run_in_executor` sysfs write with `WRITE_TIMEOUT_SECONDS`. Without this, a hung kernel driver blocks the executor thread pool and eventually stalls all fan control. Raises `OSError` on timeout so the retry/degradation logic handles it consistently. |

---

## 3. Liquidctl Device Expansion

**Business rationale:** The liquidctl backend worked generically with any device but treated all devices identically. Users with known hardware (NZXT Kraken, Corsair Commander Pro) got generic sensor names and no reconnect handling. The roadmap (Section 6.2) called for better USB controller breadth.

**Architecture decision:** Device family profiles provide optimized parsing for known hardware while preserving generic fallback for unknown devices. Reconnect uses exponential backoff (3 failures → offline, re-init every 10th poll) to avoid hammering disconnected USB devices.

| # | File | Change | Why |
|---|------|--------|-----|
| 13 | `backend/app/hardware/liquidctl_backend.py` | Added `DEVICE_PROFILES` dict with 4 families: kraken, commander, corsair_hydro, aquacomputer | Each family has `match_keywords` (for description matching), `expected_fans`/`expected_temps` (for optimized channel parsing), and `friendly_prefix` (for human-readable names). This replaces generic `"lctl_unknown_device"` names with `"Kraken"`, `"Commander"`, etc. in the dashboard. |
| 14 | Same | Added `_match_device_profile()` function | Matches a device description string against known profiles using case-insensitive keyword search. Returns `None` for unknown devices so they still work via generic discovery. |
| 15 | Same | Added `firmware_version`, `profile`, `status`, `consecutive_failures` fields to `LiquidctlDevice` | `firmware_version`: parsed from status output, useful for debugging device-specific issues. `profile`: matched device family for optimized parsing. `status`: ok/offline tracking. `consecutive_failures`: counter for reconnect logic. |
| 16 | Same | Updated `_discover_devices()` to match profiles, capture firmware version, log family info | On discovery, each device is matched against known profiles. Firmware version is extracted from status keys containing "firmware" or "version". Log output now includes family name and firmware: `"Discovered liquidctl device: NZXT Kraken X63 [Kraken] (usb:1:2) fans=[fan, pump] temps=[liquid] fw=1.0.7"`. |
| 17 | Same | Added reconnect logic in `get_sensor_readings()` | Before: if `liquidctl status` returned empty, the device was silently skipped forever. After: 3 consecutive empty status responses → mark `offline` with a log warning. Every 10th poll of an offline device, attempt `liquidctl initialize --address {addr}` + re-probe status. On success, mark `ok` and reset counter. This handles USB cable reconnects without requiring a full backend restart. |
| 18 | Same | Added `get_device_info()` | Returns device list with family, status, firmware, channels for frontend display. Needed for a future "Devices" panel showing USB controller health. |
| 19 | Same | Updated `get_backend_name()` to show online/offline counts | Before: `"liquidctl (2 devices)"`. After: `"liquidctl (1 online, 1 offline)"`. Immediate visibility into USB device health. |

---

## 4. Analytics CSV/JSON Export

**Business rationale:** Users need to get data out of DriveChill for external analysis, record-keeping, or sharing with hardware vendors when diagnosing cooling issues. The design spec (Section 4.1.2) called for CSV and JSON export of analytics data.

**Architecture decision:** Two export paths exist by design. The backend `GET /api/analytics/export` endpoint supports both `format=csv` and `format=json` for API consumers and large dataset exports (streams from DB, handles retention gating). The frontend UI uses the backend CSV endpoint for CSV downloads but generates JSON client-side from already-loaded data (avoids a redundant API round-trip for the current view). The backend JSON export exists for API parity and programmatic consumers but is not used by the frontend UI.

| # | File | Change | Why |
|---|------|--------|-----|
| 20 | `backend/app/api/routes/analytics.py` | Added `GET /api/analytics/export` endpoint | Accepts `format` (csv/json), `hours`, `start`/`end`, `sensor_id`/`sensor_ids`. Reuses existing `_resolve_range`, `_parse_sensor_ids`, `_retention_hours`, `_sensor_in_clause`, `_auto_bucket_seconds` helpers. Returns `text/csv` or `application/json` with `Content-Disposition: attachment` header. Backend-generated CSV is needed because client-side export can't handle datasets larger than what's loaded in the browser. |
| 21 | `backend-cs/Api/AnalyticsController.cs` | Added matching `[HttpGet("export")]` endpoint | C# parity. Same query parameters and response format. Uses `StringBuilder` for CSV (no external lib), `JsonSerializer` for JSON. Filenames include timestamp (`drivechill-export-20260310-143022.csv`). |
| 22 | `frontend/src/lib/api.ts` | Added `api.analytics.exportUrl()` method | Builds the download URL with query params. Used by `ExportButtons` to trigger browser download via anchor click. Returns a URL string rather than fetching — the browser handles the download directly. |
| 23 | `frontend/src/components/analytics/ExportButtons.tsx` | New component | CSV button: creates a temporary anchor element pointing to the backend export URL, triggers click, removes anchor. JSON button: serializes the already-loaded client-side data (stats, anomalies, history, regressions) into a Blob, creates an object URL, triggers download. Two buttons because they serve different purposes: CSV for spreadsheets, JSON for programmatic use. |

---

## 5. Enhanced Analytics Visualizations

**Business rationale:** The analytics page had sparklines and stat cards but lacked visual tools for identifying thermal patterns over time. The design spec (Section 4.1.1, 4.1.3) called for a heatmap and cooling score gauge.

**Architecture decision:** Pure SVG components with no external charting library. Consistent with the existing codebase convention (sparklines, correlation scatter plot are all hand-drawn SVG).

| # | File | Change | Why |
|---|------|--------|-----|
| 24 | `frontend/src/components/analytics/CoolingScore.tsx` | New SVG arc gauge component | Computes a 0-100 score from: -5 per anomaly (max -30), -15 per critical regression, -8 per warning regression, -2 per sensor with p95 > 85°C. Displayed as a 270° arc with color bands (green ≥80, yellow ≥50, red <50). Gives users a single at-a-glance metric for cooling health instead of scanning multiple tables. Breakdown counts (anomalies, regressions, hot sensors) shown beside the gauge for transparency. |
| 25 | `frontend/src/components/analytics/Heatmap.tsx` | New SVG heatmap component | X-axis: hour of day (0-23 UTC). Y-axis: temperature sensors. Cell color: blue (cool) → yellow → red (hot) based on average temperature. Uses the existing `/api/analytics/history` bucket data grouped by hour. Answers "when does my system run hottest?" — common question for users optimizing quiet hours or identifying workload-driven thermal spikes. Tooltip on hover shows exact value. |
| 26 | `frontend/src/components/analytics/AnalyticsPage.tsx` | Integrated ExportButtons, CoolingScore, Heatmap | ExportButtons placed after time window picker (accessible regardless of scroll position). CoolingScore placed as first card in stats grid (most prominent position). Heatmap placed after temperature history sparklines (natural visual flow: individual sparklines → combined heatmap view). |

---

## 6. Machine Orchestration Polish

**Business rationale:** Machine monitoring endpoints existed but lacked a one-shot health check API and the frontend showed raw status strings. The design spec (Section 4.2) called for error resilience and better status feedback.

| # | File | Change | Why |
|---|------|--------|-----|
| 27 | `backend/app/services/machine_monitor_service.py` | Added `check_machine_health()` method | One-shot health check of all enabled machines. Pings each machine's `/api/health` endpoint with SSRF validation, updates DB status, tracks consecutive failures (3 → offline), resets to online on recovery. Before: health was only tracked via the background poll loop. After: operators can trigger an immediate check from the dashboard. Uses existing `update_health`/`increment_failures` repo methods. |
| 28 | `backend/app/api/routes/machines.py` | Added `POST /api/machines/health-check` endpoint | Exposes `check_machine_health()` as an API endpoint with CSRF protection. Returns `{results: {machine_id: {status, last_seen_at, last_error}}}`. Enables a "Check Now" button in the frontend (not yet wired but endpoint is ready). |
| 29 | `frontend/src/components/dashboard/MachineDrillIn.tsx` | Added colored status dot + human-readable labels | Before: raw `machine.status` string in a badge (e.g., `"auth_error"`). After: 6px colored dot (green=online, red=offline/auth_error, gray=unknown, yellow=degraded/other) + capitalized label (e.g., `"Auth error"`). Makes machine health immediately scannable. |
| 30 | `frontend/src/components/dashboard/SystemOverview.tsx` | Same status dot + label treatment on machine cards | Consistency with MachineDrillIn. Machine cards on the dashboard show the same visual treatment as the drill-in modal. |

---

## 7. Tests

**Rationale for each test class:** Tests validate the new behavior paths and guard against regressions. Each test class maps to a specific feature above.

| # | File | Test Class | Count | What It Validates |
|---|------|-----------|-------|-------------------|
| 31 | `backend/tests/test_notification_channel_service.py` | Updated `test_valid_types` | 1 | `VALID_CHANNEL_TYPES` now includes `"mqtt"` |
| 32 | Same | `TestMQTTChannel` | 8 | `test_create_mqtt_channel`: CRUD works with mqtt-specific config fields. `test_mqtt_send_without_aiomqtt_graceful`: returns 0 (not crash) when aiomqtt is uninstalled. `test_mqtt_send_connection_failure_graceful`: returns 0 when broker is unreachable. `test_mqtt_disabled_channel_skipped`: disabled channels don't attempt connection. `test_mqtt_empty_broker_url_returns_false`: empty broker_url skips send. `test_publish_telemetry_no_mqtt_channels`: non-MQTT channels ignored by telemetry. `test_publish_telemetry_skips_non_telemetry_channels`: MQTT channels without `publish_telemetry: true` skipped. `test_close_mqtt_clients`: cleanup clears client cache. |
| 33 | `backend/tests/test_hwmon_backend.py` | Updated `test_name_with_fans` | 1 | Assertion updated from `"2 writable fans"` to `"2 ok"` to match new `get_backend_name()` format. |
| 34 | Same | `TestFanStatus` | 5 | `test_get_fan_status_all_ok`: all fans report ok after clean init. `test_degraded_fan_status`: manually degraded fan shows in status dict. `test_error_fan_rejected`: `set_fan_speed()` returns `False` for error-status fans. `test_degraded_recovers_on_success`: successful write after degraded restores to ok. `test_backend_name_shows_degraded`: backend name string shows "1 ok, 1 degraded". |
| 35 | `backend/tests/test_liquidctl_backend.py` | `TestDeviceProfiles` | 6 | `test_kraken_match`: "NZXT Kraken X63" matches kraken profile. `test_commander_match`: "Corsair Commander Pro" matches commander. `test_aquacomputer_match`: "Aquacomputer D5 Next" matches aquacomputer. `test_hydro_match`: "Corsair Hydro H100i" matches corsair_hydro. `test_unknown_device`: "Generic USB Device" returns None. `test_profile_used_in_discovery`: profile is attached to device during discovery. |
| 36 | Same | `TestDeviceReconnect` | 2 | `test_device_goes_offline_after_failures`: 3 consecutive empty status responses mark device offline. `test_device_starts_ok`: new device has status "ok" and consecutive_failures 0. |
| 37 | Same | `TestDeviceInfo` | 2 | `test_get_device_info`: returns correct structure with family, status, channels. `test_backend_name_with_offline`: backend name shows "1 online, 1 offline" when a device is offline. |

---

## 8. Documentation

| # | File | Change | Why |
|---|------|--------|-----|
| 38 | `docs/superpowers/specs/2026-03-10-milestone-c-design.md` | New design spec | Documents scope, architecture decisions, file impact, testing strategy, and deferred items. Required by project workflow before implementation. |

---

## 9. Bug Fix During Validation

| # | File | Change | Why |
|---|------|--------|-----|
| 39 | `backend-cs/Services/NotificationChannelService.cs` | Changed `new MqttClientFactory()` → `new MqttFactory()` | `MqttClientFactory` doesn't exist in MQTTnet 4.x. The correct class is `MqttFactory`. Caught by `dotnet build` during validation. |

---

## 10. Explicitly Not Done (Deferred)

Items from the design spec that were intentionally excluded from this milestone:

| Item | Reason | Target |
|------|--------|--------|
| MQTT subscribe (inbound commands) | Complexity; publish-only covers primary use case | Milestone D |
| PDF export | High implementation cost, low demand signal | Milestone D |
| Frontend MQTT config UI | SettingsPage notification channel form needs MQTT-specific fields | Milestone D |
| Telemetry wired into runtime poll loops | `publish_telemetry()` method exists in both backends but is not yet called from the Python sensor poll loop or the C# `SensorWorker`. Requires deciding on throttling strategy (every poll vs. configurable interval) before wiring in. | Milestone D |
| Interactive trend charts | Simplified to heatmap; full zoom/brush charts deferred | Milestone D |
| Period comparison cards | "Last 24h vs previous 24h" delta cards | Milestone D |
| Session token rotation | Not user-facing; security polish | Milestone D |
| Drive detail timeline markers | Requires SMART data pipeline changes | Milestone D |
| C# Linux hardware parity | C# remains Windows-focused | Milestone D+ |

---

## 11. Known Gaps

### No new C# tests for Milestone C

Milestone C added significant C# code — MQTT publish/config parsing in
`NotificationChannelService.cs` and the analytics export endpoint in
`AnalyticsController.cs` — but no new C# tests were written. The C# test count
remained at 205 (unchanged from baseline).

**Why this happened:** The implementation session prioritized Python test
coverage (23 new tests) and relied on the C# `dotnet build` compilation check +
existing integration tests for confidence. The new C# code follows established
patterns (same service class, same controller structure) that are already
covered by existing tests for other channel types and analytics endpoints.

**Recommended follow-up:** Add C# tests for:
- MQTT channel CRUD (create with mqtt config, validate `ValidTypes` includes "mqtt")
- `GetOrCreateMqttClientAsync` URL parsing (mqtt:// vs mqtts://, port defaults)
- `SendMqttAsync` graceful failure when broker is unreachable
- Analytics export CSV format correctness and JSON structure
- Export with sensor_id filtering and retention clamping
