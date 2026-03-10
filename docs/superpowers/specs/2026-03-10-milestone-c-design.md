# DriveChill Milestone C: Platform and Integration Expansion

**Date:** 2026-03-10
**Status:** Approved Design
**Prerequisite:** Milestone A+B complete (v2.3-rc released)

---

## 1. Goal

Expand DriveChill's reporting, integration, and platform capabilities without
weakening core reliability. Two parallel tracks minimize delivery time.

---

## 2. Scope Summary

| Workstream | Track | Priority | Scope |
|---|---|---|---|
| Reporting & Insights | B (frontend-heavy) | 1 | In-app visual reports + CSV/JSON export |
| MQTT Integration | A (backend-heavy) | 2 | Publish-only: alerts + telemetry to broker |
| Linux Hardware | A (backend-heavy) | 3 | hwmon error hardening + liquidctl device expansion |
| Machine Orchestration | B (frontend-heavy) | 4 | Polish existing flows: stale cleanup, error handling |

---

## 3. Track A: Backend-Heavy (MQTT + Linux Hardware)

### 3.1 MQTT Publish-Only Integration

**Design decision:** MQTT is a new notification channel type, not a separate
subsystem. It plugs into the existing `NotificationChannelService` fan-out
pipeline in both backends.

**Channel type:** `mqtt`

**Config schema:**
```json
{
  "broker_url": "mqtt://192.168.1.100:1883",
  "topic_prefix": "drivechill",
  "username": "",
  "password": "",
  "client_id": "drivechill-hub",
  "qos": 1,
  "retain": false
}
```

**Publish behavior:**
- On alert: publish to `{topic_prefix}/alerts` with JSON payload matching
  existing alert event shape
- On telemetry poll (configurable): publish to `{topic_prefix}/sensors/{sensor_id}`
  with latest reading
- Telemetry publishing is opt-in via a `publish_telemetry` boolean in channel config
- Telemetry interval follows the existing sensor poll interval (not a separate timer)

**Implementation:**
- Python: `paho-mqtt` library (async wrapper via `asyncio_mqtt` / `aiomqtt`)
- C#: `MQTTnet` NuGet package
- Both backends add `"mqtt"` to `VALID_CHANNEL_TYPES` / `ValidTypes`
- New `_send_mqtt` / `SendMqttAsync` methods in `NotificationChannelService`
- Connection lifecycle: lazy connect on first send, reconnect on failure,
  disconnect on service close
- SSRF validation: broker_url validated same as other outbound URLs

**Telemetry publishing path:**
- Python: `SensorWorker` (or equivalent poll loop) calls
  `channel_svc.publish_telemetry(readings)` after each poll cycle
- C#: `SensorWorker.PollSensors()` calls
  `channelSvc.PublishTelemetryAsync(readings)` after each poll
- Only MQTT channels with `publish_telemetry: true` receive telemetry
- Non-MQTT channels are unaffected

**Frontend:**
- Add "MQTT" option to notification channel type picker
- Render MQTT-specific config fields (broker_url, topic_prefix, username,
  password, client_id, qos, retain, publish_telemetry)
- Test button sends a test alert message to the configured topic

### 3.2 Linux hwmon Write Hardening

**Current state:** 186 lines, 18 tests. Basic sysfs read/write works but
error paths are sparse.

**Improvements:**
- Permission pre-check: on init, test-write a no-op value and log clear
  error if permission denied (with setup instructions)
- Graceful degradation: if a single fan node becomes unwritable at runtime,
  mark it as degraded rather than failing the whole backend
- Timeout on sysfs writes: add configurable timeout (default 2s) to
  `_write_sysfs_async`
- Retry logic: single retry on transient EIO errors before marking degraded
- `get_fan_status()` method: return per-fan health status (ok/degraded/error)
  for frontend display

### 3.3 Liquidctl Device Expansion

**Current state:** 277 lines, 21 tests. Generic discovery via `liquidctl list`
+ `liquidctl status` works for any device liquidctl supports.

**Improvements:**
- Device family profiles: known device patterns (Kraken, Commander Pro,
  Aquacomputer D5 Next) get optimized channel parsing and human-readable names
- Reconnect on device disconnect: if `liquidctl status` fails for a device,
  attempt re-initialization before marking offline
- Multiple fan curve support per device: some controllers (Commander Pro) have
  6+ independent channels — ensure all are discoverable and controllable
- Firmware version reporting: parse and expose firmware info from
  `liquidctl status` output where available

---

## 4. Track B: Frontend-Heavy (Reporting + Machine Polish)

### 4.1 Reporting & Insights

**New features added to existing AnalyticsPage:**

**4.1.1 Enhanced Visualizations**
- **Heatmap component:** Time-of-day (x) vs sensor (y) grid colored by
  temperature. Uses existing `/api/analytics/history` data bucketed by hour.
- **Trend chart upgrade:** Replace sparklines with full interactive line
  charts (still SVG, no external lib). Support zoom via time-range brush.
  Show min/max band overlay.
- **Fan efficiency chart:** Scatter plot of fan RPM vs temperature delta
  (ambient to sensor). Helps identify cooling effectiveness.

**4.1.2 Data Export**
- **CSV export button** on each analytics section: downloads the current view's
  data as CSV. Generated client-side from the already-fetched API data.
- **JSON export button:** downloads raw API response as formatted JSON.
- **Full report export:** hits `/api/analytics/report` and downloads as JSON
  with all sections (stats, anomalies, regressions, history).
- **Backend CSV endpoint:** `GET /api/analytics/export?format=csv&hours=24`
  returns `text/csv` with proper headers. Supports same query params as
  `/api/analytics/history`.

**4.1.3 Summary Dashboard Cards**
- **Cooling score:** 0-100 score based on: % time in target range, anomaly
  count, regression count. Displayed as a prominent gauge.
- **Period comparison:** "Last 24h vs previous 24h" delta cards showing
  avg temp change, anomaly count change, fan avg change.

### 4.2 Machine Orchestration Polish

**Current state:** Full CRUD + proxy endpoints exist. Missing error resilience.

**Improvements:**
- **Stale machine eviction:** Background task checks machine health every 60s.
  Machines that fail 3 consecutive health checks are marked `status: offline`.
  Auto-recovers when connectivity returns.
- **Timeout resilience:** All proxy calls use configurable per-machine
  `timeout_ms` (already in schema, field exists). Add retry with backoff on
  first timeout before failing.
- **Error feedback:** Machine list endpoint returns `last_error` and
  `last_seen_at` fields. Frontend shows connectivity status badge.
- **Connection pooling:** Python: reuse `httpx.AsyncClient` per machine
  (currently creates new). C#: already uses `IHttpClientFactory` (no change).

---

## 5. New Dependencies

| Package | Backend | Purpose |
|---|---|---|
| `aiomqtt` (>=2.0) | Python | MQTT publish client |
| `MQTTnet` (>=4.3) | C# | MQTT publish client |

No new frontend dependencies. All visualizations use native SVG.

---

## 6. Database Changes

**Migration 015: MQTT and machine status**
```sql
-- No schema changes needed for MQTT — it uses existing notification_channels
-- table with type='mqtt' and config_json for broker settings.

-- Machine status tracking
ALTER TABLE machines ADD COLUMN status TEXT NOT NULL DEFAULT 'unknown';
ALTER TABLE machines ADD COLUMN last_seen_at TEXT;
ALTER TABLE machines ADD COLUMN last_error TEXT;
ALTER TABLE machines ADD COLUMN consecutive_failures INTEGER NOT NULL DEFAULT 0;
```

---

## 7. File Impact Summary

### Track A (backend-heavy)
| File | Change |
|---|---|
| `backend/app/services/notification_channel_service.py` | Add `mqtt` type + `_send_mqtt` + `publish_telemetry` |
| `backend-cs/Services/NotificationChannelService.cs` | Add `mqtt` type + `SendMqttAsync` + `PublishTelemetryAsync` |
| `backend/app/hardware/hwmon_backend.py` | Permission pre-check, degraded state, timeout, retry |
| `backend/app/hardware/liquidctl_backend.py` | Device profiles, reconnect, multi-channel |
| `backend/requirements.txt` | Add `aiomqtt>=2.0.0` |
| `backend-cs/DriveChill.csproj` | Add `MQTTnet` package |

### Track B (frontend-heavy)
| File | Change |
|---|---|
| `frontend/src/components/analytics/AnalyticsPage.tsx` | Heatmap, trend charts, export buttons, summary cards |
| `frontend/src/components/analytics/Heatmap.tsx` | New: SVG heatmap component |
| `frontend/src/components/analytics/TrendChart.tsx` | New: interactive line chart |
| `frontend/src/components/analytics/ExportButtons.tsx` | New: CSV/JSON download logic |
| `frontend/src/components/analytics/CoolingScore.tsx` | New: gauge component |
| `frontend/src/lib/api.ts` | Add export endpoint, machine status fields |
| `frontend/src/lib/types.ts` | Add export types, machine status types |
| `backend/app/api/routes/analytics.py` | Add CSV export endpoint |
| `backend-cs/Api/AnalyticsController.cs` | Add CSV export endpoint |
| `backend/app/services/machine_monitor_service.py` | Stale eviction, connection pooling |
| `backend-cs/Api/MachinesController.cs` | Status fields in response |

### Shared
| File | Change |
|---|---|
| `backend/app/db/migrations/015_mqtt_machine_status.sql` | Machine status columns |
| `backend-cs/Services/DbService.cs` | Machine status columns |
| Frontend notification channel form | MQTT type option |

---

## 8. Testing Strategy

- MQTT: mock broker tests (Python: `pytest` with mock transport; C#: mock `IHttpClientFactory` pattern adapted for MQTTnet)
- hwmon: existing mock sysfs pattern extended with permission error scenarios
- liquidctl: mock subprocess responses for new device families
- Export: Python/C# endpoint tests for CSV format correctness
- Frontend: extend existing E2E specs for export buttons and heatmap render
- Machine status: unit tests for stale eviction logic

---

## 9. Explicitly Deferred

| Item | Reason | Target |
|---|---|---|
| MQTT subscribe (inbound commands) | Complexity; publish-only covers primary use case | Milestone D |
| PDF export | High implementation cost, low demand signal | Milestone D |
| Session token rotation | Not user-facing; security polish | Milestone D |
| Drive detail timeline markers | Requires SMART data pipeline changes | Milestone D |
| C# Linux hardware parity | C# remains Windows-focused | Milestone D+ |
