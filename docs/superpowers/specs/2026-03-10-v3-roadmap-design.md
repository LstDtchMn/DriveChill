# DriveChill v3.0 Roadmap: Milestones D through F

**Date:** 2026-03-10
**Status:** Approved Design
**Prerequisite:** Milestones A (release completion), B (control model upgrade),
and C (platform expansion) are complete. Current version is v2.3-rc.

---

## 1. Goal

Ship DriveChill v3.0 as a major evolution: clean up all deferred debt from
Milestones A–C, complete unfinished feature scope, then deliver headline
features (noise profiling, optimization advisor, dashboard customization)
and integration capabilities (MQTT subscribe, Home Assistant, profile
scheduling) that differentiate DriveChill from basic fan-control tools.

Three sequential milestones — D, E, F — are released together as v3.0.

---

## 2. Milestone Summary

| Milestone | Theme | Scope |
|---|---|---|
| D-Core | Debt cleanup | MQTT UI, telemetry wiring, C# test backfill, session rotation, E2E flake |
| D-Features | Deferred feature completion | Interactive trend charts, period comparison cards |
| D-Decision | Prerequisite decisions | Timeline-marker data source |
| E | Headline features | Noise profiling, noise advisor, scheduled reports, event annotations, dashboard widgets |
| F | Integration & automation | HA auto-discovery, MQTT subscribe, profile scheduling, PDF export |

---

## 3. Milestone D-Core: Debt Cleanup

### D1. MQTT Frontend Config UI

**Why:** The MQTT backend channel type (`"mqtt"` in `VALID_CHANNEL_TYPES`) is
fully implemented in both Python (`notification_channel_service.py`) and C#
(`NotificationChannelService.cs`), but users cannot configure it. The frontend
`NotificationChannelType` union in `types.ts` only includes `'discord' |
'slack' | 'ntfy' | 'generic_webhook'`, and the `<select>` in
`SettingsPage.tsx:1656` has no MQTT option.

**What:**
- Add `'mqtt'` to the `NotificationChannelType` union in
  `frontend/src/lib/types.ts:481`
- Add `<option value="mqtt">MQTT</option>` to the channel type selector in
  `SettingsPage.tsx:1656–1659`
- Add MQTT placeholder hint text at line 1674:
  `— { "broker_url": "mqtt://host:1883", "topic_prefix": "drivechill",
  "username": "", "password": "", "client_id": "drivechill-hub", "qos": 1,
  "retain": false, "publish_telemetry": false }`
- Add MQTT default placeholder in the textarea at line 1680

**Design decision — JSON textarea model retained:**
The current notification channel form uses a single JSON config textarea with
per-type placeholder hints (lines 1667–1683). MQTT follows this same pattern.
No password masking is provided — the JSON textarea cannot meaningfully offer
field-level masking. Password masking is deferred until structured per-type
form fields replace the textarea (not in this milestone).

**Mandatory extraction:** The notification channel form section of
`SettingsPage.tsx` (currently lines 1564–1720+) must be extracted into a
`NotificationChannelForm.tsx` subcomponent. The SettingsPage file is already
too large and this is the right time to decompose.

**Test button behavior:** Sends a test alert message to
`{topic_prefix}/alerts` with a test payload via the existing
`api.notificationChannels.test(id)` endpoint. No new endpoint needed.

**Complications:**
- MQTT broker URLs use `mqtt://` or `mqtts://` schemes, not `https://`. The
  existing SSRF URL validation in both backends already handles this (it
  validates against allowed schemes per channel type). Verify that `mqtt://`
  and `mqtts://` are accepted for MQTT channels specifically.
- JSON config is user-editable — invalid JSON is caught by the existing
  `JSON.parse()` guard at line 1690.

**Files changed:**
- `frontend/src/lib/types.ts` — add `'mqtt'` to union
- `frontend/src/components/settings/SettingsPage.tsx` — add option, hint, placeholder
- `frontend/src/components/settings/NotificationChannelForm.tsx` — new extracted component

---

### D2. MQTT Telemetry Wiring

**Why:** Both backends have a `publish_telemetry()` / `PublishTelemetryAsync()`
method (Python: `notification_channel_service.py:371`; C#:
`NotificationChannelService.cs:310`) that publishes sensor readings to MQTT
channels with `publish_telemetry: true` in their config. However, nothing
calls these methods. The call site was never wired into the sensor poll
lifecycle.

**What — Python:**
Create a dedicated background telemetry publisher task in `main.py` that:
1. Starts after `sensor_service.start()` (line 206) and after
   `notification_channel_svc` is created (line 256)
2. Calls `sensor_service.subscribe()` to get an `asyncio.Queue` (the existing
   pub/sub API at `sensor_service.py:66`)
3. Reads snapshots from the queue in a loop
4. Calls `notification_channel_svc.publish_telemetry(readings)` for each snapshot
5. Uses **single-flight pattern**: if the previous `publish_telemetry()` call
   is still in-flight (tracked via an `asyncio.Event` or `asyncio.Lock`),
   **drop** the current batch rather than queuing indefinitely. This prevents
   unbounded concurrent publishes if the MQTT broker is slow or unreachable.
6. **Shutdown**: during lifespan teardown, unsubscribe from `sensor_service`,
   cancel the task with a short grace period (2 seconds) for any in-flight
   publish to complete, then proceed with remaining cleanup.

**What — C#:**
Wire `PublishTelemetryAsync()` into `SensorWorker.cs` after sensor readings are
collected. `SensorWorker` already owns orchestration concerns (alert evaluation,
webhook delivery), so this coupling is appropriate for C#. Use the same
single-flight pattern: skip if previous publish is still running.

**Why not inside `SensorService._poll_loop()` (Python):**
`SensorService` is the lowest-level polling service. Adding MQTT transport
concerns there violates separation of responsibilities. The subscriber-based
approach in `main.py` keeps `SensorService` focused on hardware polling and
snapshot distribution.

**Complications:**
- The subscriber queue has `maxsize=10` (`sensor_service.py:68`). If the
  telemetry publisher falls behind, `put_nowait()` will raise `QueueFull`
  and the snapshot is dropped (existing behavior). This is acceptable —
  telemetry is best-effort.
- The single-flight pattern means at most one publish is in-flight at any time.
  If poll interval is 1 second and broker round-trip is 3 seconds, 2 out of
  every 3 snapshots are dropped. This is the correct trade-off for a
  best-effort telemetry channel.

**Files changed:**
- `backend/app/main.py` — new telemetry publisher task in lifespan
- `backend-cs/Services/SensorWorker.cs` — add `PublishTelemetryAsync()` call

---

### D3. C# Test Backfill for Milestone C

**Why:** Milestone C shipped new C# code for MQTT, CSV/JSON export, and machine
status eviction without corresponding unit tests. This is a quality and
confidence gap that must be closed before building more features on top.

**What — full coverage of Milestone C C# changes:**

1. **MQTT type validation and config parsing**
   - Valid `mqtt://` and `mqtts://` broker URLs are accepted
   - Invalid/malformed broker URLs produce graceful failure (not crash)
   - Missing required config fields (broker_url) return error
   - `publish_telemetry` boolean parsing (true/false/missing)

2. **MQTT send and publish**
   - Mock `IMqttClient` via `MqttClientWrapper` injection
   - Alert publish to `{prefix}/alerts` produces correct topic and payload
   - Telemetry publish to `{prefix}/sensors/{id}` produces correct per-sensor messages
   - Connection failure evicts cached client and retries on next call
   - `CloseMqttClientsAsync()` disconnects all cached clients

3. **CSV export endpoint**
   - `GET /api/analytics/export?format=csv&hours=24` returns `text/csv` content type
   - CSV headers match expected column names
   - Query params (hours, sensor_type filter) are respected
   - Empty result set returns headers-only CSV

4. **JSON export endpoint**
   - Response shape matches API contract (stats, anomalies, history sections)
   - Sensor filtering by type parameter works correctly

5. **Machine status eviction**
   - Machine with 3+ consecutive health check failures is marked `status: offline`
   - Machine that recovers after being offline returns to `status: online`
   - `last_seen_at` and `last_error` fields are updated correctly
   - `consecutive_failures` counter resets on successful health check

**Success criteria:** Every new Milestone C C# code path has at least one
positive test case (expected behavior) and one negative test case (error/edge
case). All existing C# tests continue to pass.

**Complications:**
- MQTTnet mocking requires working with `IMqttClient` interface. The existing
  `MqttClientWrapper` class (`NotificationChannelService.cs:384`) wraps the
  concrete client — tests may need a test double or interface extraction.
- CSV export tests need to parse CSV output and validate structure, not just
  check status codes.

**Files changed:**
- `backend-cs/Tests/` — new test files for MQTT, export, machine status

---

### D4. Session Token Rotation (narrowed scope)

**Why:** Security hygiene — when a user changes their own password, old session
tokens should be invalidated. Currently, a password change does not affect
existing sessions, meaning a compromised session token remains valid even
after a password reset.

**Scope:** Self-password-change only. Not all sensitive operations (that is
deferred to a later milestone).

**Exact behavioral specification:**
1. User submits password change via the existing password-change endpoint
2. Server validates old password, sets new password hash
3. Server invalidates **all other sessions** for that user (delete from
   `sessions` table where `user_id = X AND session_id != current`)
4. Server generates a **fresh session token + CSRF token** and sets them as
   cookies in the password-change response
5. The current browser session **continues without forced logout** — the user
   sees a success message and remains authenticated with the new tokens
6. Frontend: after receiving the password-change success response, dispatches
   the existing auth/session refresh flow to pick up the new cookies
7. **WebSocket:** frontend **closes the current WebSocket connection
   client-side** after password-change success, triggering the existing
   automatic reconnect logic. The reconnected socket uses the new session
   cookie. No reliance on periodic revalidation — explicit close ensures
   immediate reconnect with fresh credentials.

**Both backends must implement.** Frontend handles cookie swap + explicit
WebSocket close.

**Complications:**
- The CSRF token must be regenerated alongside the session token. If only the
  session token changes, the next CSRF-validated request will fail.
- Race condition: if a WebSocket message arrives between password change and
  socket close, it should still be processed (the old session is still the
  "current" session until replaced).
- Other logged-in browsers/tabs for the same user will be logged out on their
  next request (their sessions are invalidated). This is intentional.

**Files changed:**
- `backend/app/api/routes/auth.py` — password-change handler
- `backend-cs/` — equivalent auth controller
- `frontend/src/components/settings/SettingsPage.tsx` — post-password-change
  refresh + WS close
- `frontend/src/hooks/useWebSocket.ts` — verify reconnect handles cookie change

---

### D5. Settings E2E Flake Fix

**Why:** The settings E2E spec has intermittent failures that reduce confidence
in the test suite and slow CI feedback loops.

**What:**
1. Investigate root cause before applying any fix. Likely candidates:
   - Timing: assertions fire before UI has finished rendering/animating
   - Stale state: Zustand store not reset between test runs
   - Animation: `animate-card-enter` CSS transitions interfere with selectors
2. Apply targeted fix based on root cause analysis

**Success criteria:**
- No `test.retry()` or `expect.poll()` workarounds needed locally
- Stable across **5 consecutive CI runs** without flakes
- Assertions include both:
  - Successful API response verification (network layer)
  - Visible UI feedback verification (toast message, updated state)

**Complications:**
- Flake root causes can be subtle. If the issue is animation-timing-related,
  the fix might involve waiting for `animationend` events or disabling
  animations in test mode via a CSS class.
- Must not introduce test-only code paths in production components.

**Files changed:**
- `frontend/e2e/settings.spec.ts` — test fixes
- Potentially `frontend/playwright.config.ts` — if timing/retry config needed

---

## 4. Milestone D-Features: Deferred Feature Completion

### D6. Interactive Trend Charts

**Why:** Current sparklines in `AnalyticsPage.tsx` are too small to be useful
for thermal diagnosis. Users cannot zoom, inspect individual data points, or
see value ranges. This was listed in the Milestone C design (Section 4.1.1)
but deferred.

**What:**
- Replace static SVG sparklines with full interactive line charts
- Time-range brush: drag to select a sub-range for zoom
- Min/max band overlay: shaded region showing value range per bucket
- Click-to-inspect: hover/tap shows exact value + timestamp tooltip
- Touch support for mobile/tablet users

**How:**
- Pure SVG rendering — no external charting library (matches project convention
  of zero external UI dependencies)
- Downsample data to ~500 points for rendering performance. The 720-hour
  retention window at 1-second poll intervals produces far too many points
  for direct SVG rendering. Use LTTB (Largest-Triangle-Three-Buckets) or
  simple min-max-per-bucket downsampling.
- Mouse events for drag-to-zoom: `onMouseDown` start, `onMouseMove` draw
  selection rect, `onMouseUp` filter data to selected range
- Reset zoom button to return to full range

**Complications:**
- Large datasets: even with downsampling, 500 SVG `<circle>` elements plus
  a `<path>` can cause layout thrashing. Use a single `<path>` for the line
  and avoid per-point DOM elements where possible.
- Touch events have different semantics from mouse events (no hover). Need
  `onTouchStart`/`onTouchMove`/`onTouchEnd` handlers.
- Time-range brush state must not conflict with page-level time window
  selector (the existing 1h/6h/24h/7d/30d picker).

**Files changed:**
- `frontend/src/components/analytics/TrendChart.tsx` — new interactive chart
  component
- `frontend/src/components/analytics/AnalyticsPage.tsx` — replace sparklines
  with TrendChart
- `frontend/src/lib/downsample.ts` — optional utility for LTTB/min-max
  downsampling

---

### D7. Period Comparison Cards

**Why:** "How is my cooling performing compared to yesterday?" is a natural
user question. Showing deltas gives users an at-a-glance sense of whether
things are improving or degrading. Listed in Milestone C design (Section 4.1.3)
but deferred.

**What:**
- Delta cards showing "Last 24h vs previous 24h":
  - Average temperature change (e.g., "+2.1°C" or "−0.5°C")
  - Fan speed average change
  - Anomaly count change (e.g., "3 more" or "2 fewer")
- Color-coded: green for improvement, red for degradation, gray for no change
- Shown above or beside the existing CoolingScore gauge

**How — data sources (4 API calls total):**
- Two calls to `GET /api/analytics/stats` with different time windows:
  - Current: `?hours=24`
  - Previous: `?start=<48h ago>&end=<24h ago>`
- Two calls to `GET /api/analytics/anomalies` with the same time windows:
  - Current: `?hours=24`
  - Previous: `?start=<48h ago>&end=<24h ago>`
- All computation is client-side. No new backend endpoints.

**Complications:**
- Previous period may have no data (new installation, data pruned). Show
  "No baseline" or "N/A" rather than zero-delta.
- Four API calls on page load adds latency. Fetch in parallel
  (`Promise.all()`). Consider caching in component state to avoid re-fetch
  on tab switches.
- The anomalies endpoint may return different anomaly types. Count should
  aggregate across all types.

**Files changed:**
- `frontend/src/components/analytics/PeriodComparison.tsx` — new component
- `frontend/src/components/analytics/AnalyticsPage.tsx` — integrate
  PeriodComparison

---

## 5. Milestone D-Decision: Prerequisite Decisions

### D8. Decide Timeline-Marker Data Source

**Why:** Users want to correlate drive temperature spikes with alert firings
on a timeline view. However, this feature cannot be implemented until a data
source decision is made.

**Current state:**
Alert events are **in-memory only**. In Python, `AlertService._events` is a
list capped at `_max_events` items (default ~1000). The only persistent trace
of alerts is:
- `prom_metrics.alert_events_total` — a Prometheus counter (not queryable
  for individual events)
- `webhook_deliveries` table — records delivery attempts, but only for
  channels that were active at alert time

There is **no `alert_events` database table** in any migration (001–015).
The `001_initial_schema.sql` creates `alert_rules` (line 44) but not
`alert_events`.

**Decision options:**
- **(a) New `alert_events` table:** Persist every alert firing with timestamp,
  rule_id, sensor_id, value, threshold. Pro: clean data model. Con: adds
  write load on every alert, needs retention policy and pruning.
- **(b) Use `webhook_deliveries` as proxy:** Already persisted. Con: only
  covers alerts that triggered a webhook delivery, not all alerts. Misses
  alerts when no channels are configured.
- **(c) Lightweight event log table:** A generic `event_log` table that can
  record alerts, profile changes, startup events, etc. Pro: reusable for
  event annotations (E4). Con: more design work upfront.

**This is a design decision, not an implementation task.** The decision must
be made before timeline markers can be built. Implementation of timeline
markers moves into Milestone E or F after this decision.

**Recommendation:** Option (c) is the best long-term choice because it
directly supports E4 (event annotations) with the same table. But option (a)
is simpler if annotations are descoped.

---

## 6. Milestone E: Headline Features

### E1. Noise Profiling

**Why:** DriveChill controls fan speeds but has no data about how loud each fan
is at each speed. Users currently guess at noise-performance trade-offs.
Browser-based noise measurement enables data-driven fan curve optimization
without external tools.

**What:**
A guided noise profiling flow that:
1. User selects a fan to profile
2. System performs a controlled sweep: set fan to 0%, wait for stabilization,
   measure ambient noise via microphone, then step through 10%, 20%, ... 100%
3. At each step: hold speed for N seconds, measure dB(A) via Web Audio API
4. Build a noise-vs-RPM curve for that fan
5. Store the profile for use by the Noise Optimization Advisor (E2)

**Two modes:**
- **Quick mode:** Profile the selected fan without changing other fans. Faster
  but includes ambient noise from other fans at their current speed.
- **Precise mode (B-style isolation):** Before starting the sweep, present a
  checklist of all other fans. User can mute (set to 0%) any/all other fans
  for the duration of the profiling. After profiling completes, restore
  previous fan speeds automatically.

**How — Web Audio API:**
- `navigator.mediaDevices.getUserMedia({ audio: true })` to access microphone
- `AudioContext` + `AnalyserNode` for real-time frequency analysis
- Compute A-weighted dB from FFT data (approximate — true dB(A) requires
  calibrated hardware, but relative measurements are sufficient for
  comparison between fan speeds)
- Sample for 3–5 seconds per step, take median value to reduce transient noise

**Backend:**
- New `noise_profiles` table:
  ```sql
  CREATE TABLE noise_profiles (
    id TEXT PRIMARY KEY,
    fan_id TEXT NOT NULL,
    mode TEXT NOT NULL,  -- 'quick' or 'precise'
    data_json TEXT NOT NULL,  -- [{rpm: number, db: number}, ...]
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
  );
  ```
- CRUD endpoints: `GET/POST/DELETE /api/noise-profiles`
- No C# parity required initially (noise profiling is a frontend-driven
  feature; backend just stores results)

**Complications:**
- **Browser mic permissions:** User must grant microphone access. If denied,
  show a clear explanation of why it's needed and how to enable it.
- **Ambient noise floor:** In noisy environments, measurements may be
  unreliable. Show a "noise floor" indicator before starting and warn if
  ambient noise is high.
- **Calibration variance:** Different microphones produce different absolute
  dB values. The system should display a disclaimer that values are relative,
  not calibrated. Cross-profile comparisons are only meaningful with the same
  microphone in the same position.
- **Fan speed stabilization:** After changing fan speed, RPM takes time to
  stabilize (2–5 seconds for most fans). Wait for RPM to stabilize before
  measuring noise.

**Files changed:**
- `frontend/src/components/settings/NoiseProfiler.tsx` — new component
- `frontend/src/lib/audioMeter.ts` — Web Audio API utilities
- `backend/app/api/routes/noise_profiles.py` — CRUD endpoints
- `backend/app/db/migrations/016_noise_profiles.sql` — new table
- `frontend/src/lib/types.ts` — `NoiseProfile` type
- `frontend/src/lib/api.ts` — noise profile API methods

---

### E2. Noise Optimization Advisor

**Why:** Having noise profiles (E1) is useful, but the real value is automated
recommendations. "Which fans should I slow down to reduce noise without
exceeding my temperature targets?" is the key question.

**What:**
An advisor panel that:
1. Reads all noise profiles and current temperature targets
2. For each fan with a noise profile, calculates the noise cost of each RPM
   step
3. Recommends fan curve adjustments that minimize total noise while keeping
   all temperature targets met
4. Presents recommendations as actionable suggestions: "Reduce Fan 2 from
   70% to 55% — saves ~4 dB, target still met with 3°C margin"

**How:**
- Client-side algorithm (no backend computation needed)
- Greedy heuristic: sort fans by noise-reduction-per-degree-margin ratio,
  reduce the highest-ratio fan first, check if targets are still met, repeat
- This is a recommendation engine, not automatic application. User reviews
  and applies suggestions manually.

**Complications:**
- Multi-fan optimization is NP-hard in the general case. The greedy heuristic
  is a deliberate simplification that produces good-enough results for
  typical setups (2–6 fans).
- Requires both noise profiles AND temperature targets to be configured.
  If either is missing, show a clear message about what's needed.
- Temperature targets use PID control with dynamic behavior. The advisor
  works with steady-state assumptions, which may not match transient behavior
  during load spikes.

**Files changed:**
- `frontend/src/components/analytics/NoiseAdvisor.tsx` — new component
- `frontend/src/lib/noiseOptimizer.ts` — optimization algorithm

---

### E3. Scheduled Reports

**Why:** Users want periodic summaries without manually checking the dashboard.
"Send me a daily email with my cooling score and any anomalies" is a
natural ask for set-and-forget monitoring.

**What:**
- Configurable schedule: daily or weekly
- Report contents: cooling score, anomaly summary, regression summary,
  avg/max temperatures, fan utilization
- Delivered via the existing email notification service
- Backend cron-style task that fires at the configured time

**How:**
- New `report_schedules` table:
  ```sql
  CREATE TABLE report_schedules (
    id TEXT PRIMARY KEY,
    frequency TEXT NOT NULL,  -- 'daily' or 'weekly'
    time_utc TEXT NOT NULL,   -- '09:00'
    timezone TEXT NOT NULL DEFAULT 'UTC',
    enabled INTEGER NOT NULL DEFAULT 1,
    last_sent_at TEXT,
    created_at TEXT NOT NULL
  );
  ```
- Background task in Python (`asyncio` periodic check) and C# (`Timer`-based)
  checks every minute if a report is due
- Report generation reuses the existing `/api/analytics/report` internal logic
  (stats + anomalies + regressions) — no new analytics computation
- Email body is HTML using a simple inline-styled template (no external
  template engine)

**Complications:**
- Timezone handling: user configures timezone, backend converts to UTC for
  scheduling. Use `zoneinfo` (Python) / `TimeZoneInfo` (C#).
- Email delivery failures: log and retry once on next check cycle. Don't
  queue indefinitely.
- Report generation may be slow if analytics data is large. Run in a
  background task, not in the scheduler tick.

**Files changed:**
- `backend/app/services/report_scheduler_service.py` — new service
- `backend/app/api/routes/report_schedules.py` — CRUD endpoints
- `backend/app/db/migrations/017_report_schedules.sql` — new table
- `backend-cs/Services/ReportSchedulerService.cs` — C# equivalent
- `frontend/src/components/settings/ReportScheduleForm.tsx` — UI

---

### E4. Event Annotations

**Why:** Users want to mark significant events on their timeline: "installed
new cooler", "repasted CPU", "moved PC to new room". These annotations help
correlate environmental changes with temperature trends.

**Prerequisite:** D6 (interactive trend charts) must be complete — annotations
are displayed as markers on the trend chart timeline.

**Also depends on:** D8 decision. If option (c) is chosen (generic event log
table), annotations use the same table. If option (a) is chosen (alert_events
only), annotations need their own table.

**What:**
- User creates an annotation with: timestamp, label, optional description
- Annotations appear as vertical markers on trend charts
- Click/tap marker to see label and description
- CRUD operations: create, list, delete

**How:**
- If D8 chose option (c): use the generic `event_log` table with
  `type = 'annotation'`
- If D8 chose option (a) or (b): new `annotations` table:
  ```sql
  CREATE TABLE annotations (
    id TEXT PRIMARY KEY,
    timestamp_utc TEXT NOT NULL,
    label TEXT NOT NULL,
    description TEXT,
    created_at TEXT NOT NULL
  );
  ```

**Complications:**
- Annotations at the same timestamp as data points need visual separation
  (vertical line + icon, not overlapping the data point).
- Annotation timestamps are user-supplied and may not align with any actual
  data point. Chart must handle arbitrary timestamp placement.

**Files changed:**
- `frontend/src/components/analytics/TrendChart.tsx` — annotation markers
- `backend/app/api/routes/annotations.py` — CRUD endpoints
- `backend/app/db/migrations/` — new table (number depends on ordering)
- `frontend/src/lib/types.ts` — `Annotation` type

---

### E5. Dashboard Widgets

**Why:** The current dashboard is a fixed layout that shows the same
information for all users. Power users want to customize what they see
first — some care about cooling score, others about drive health, others
about fan RPM.

**What:**
- Replace the fixed dashboard layout with a customizable widget grid
- Available widget types:
  - Cooling score gauge (from existing CoolingScore component)
  - Temperature sparklines per sensor
  - Fan status cards (RPM, %, control source)
  - Drive health summary
  - Anomaly count badge
  - Quick profile switcher
- Drag-to-reorder widgets
- Show/hide individual widgets
- Layout persists in localStorage (no backend storage needed)

**How:**
- CSS Grid layout with drag-and-drop via native HTML5 Drag and Drop API
  (no external library)
- Each widget is a self-contained component that fetches its own data
  (or receives it from the existing WebSocket stream)
- Widget configuration stored in localStorage as JSON:
  `{ widgets: [{ type: "cooling_score", visible: true, order: 0 }, ...] }`
- Default layout matches current fixed dashboard (backward compatible)

**Complications:**
- Drag-and-drop on touch devices requires `touch-action: none` and
  touch event handlers (HTML5 DnD has poor mobile support). May need a
  simpler reorder UI (up/down buttons) for mobile.
- Widget data fetching: if each widget fetches independently, there may be
  redundant API calls. Consider a shared data context that widgets subscribe
  to (similar to the existing WebSocket approach).
- Must not break the existing dashboard for users who don't customize.

**Files changed:**
- `frontend/src/components/dashboard/` — new widget components
- `frontend/src/components/dashboard/DashboardPage.tsx` — refactor to
  widget grid
- `frontend/src/lib/widgetConfig.ts` — layout persistence

---

## 7. Milestone F: Integration & Automation

### F1. Home Assistant Auto-Discovery

**Why:** Home Assistant is the dominant home automation platform. MQTT
auto-discovery lets HA automatically create DriveChill sensor and fan
entities without manual YAML configuration. This dramatically reduces
setup friction for the largest potential user segment.

**Prerequisite:** D1 + D2 (publish-capable MQTT integration). Does **not**
require MQTT subscribe (F2) — discovery is publish-side behavior.

**What:**
- On MQTT connect (or reconnect), publish HA discovery messages to
  `homeassistant/sensor/drivechill_{sensor_id}/config` for each sensor
- Publish `homeassistant/fan/drivechill_{fan_id}/config` for each controllable fan
- Discovery payloads follow the HA MQTT discovery schema:
  - `name`, `unique_id`, `state_topic`, `unit_of_measurement`
  - For fans: `command_topic` (requires F2 for actual control, but discovery
    can advertise the topic in advance)

**Complications:**
- HA discovery schema is version-sensitive. Target HA 2024.1+ schema.
- Discovery messages must have `retain: true` so HA picks them up even if
  it restarts after DriveChill.
- If a sensor is removed from DriveChill, the discovery message should be
  cleared (publish empty payload to the config topic). This requires tracking
  which sensors have been advertised.
- Topic prefix must be configurable (some users use non-default HA MQTT
  prefixes).

**Files changed:**
- `backend/app/services/notification_channel_service.py` — HA discovery
  publish logic
- `backend-cs/Services/NotificationChannelService.cs` — C# equivalent

---

### F2. MQTT Subscribe (Inbound Commands)

**Why:** Publish-only MQTT (Milestones C/D) covers alerting and telemetry.
Subscribe enables external systems (Home Assistant, Node-RED, custom scripts)
to control DriveChill: set fan speeds, activate profiles, trigger actions.

**What:**
- Subscribe to `{topic_prefix}/commands/#` on MQTT connect
- Supported commands:
  - `{prefix}/commands/fans/{fan_id}/speed` — payload: `{ "percent": 75 }`
  - `{prefix}/commands/profiles/activate` — payload: `{ "profile_id": "..." }`
  - `{prefix}/commands/fans/release` — release manual fan override

**Security considerations:**
- MQTT credentials (username/password) are the authentication boundary.
  Any client with valid MQTT credentials can send commands.
- **Payload validation:** All command payloads are validated against expected
  JSON schemas. Unknown fields are ignored, malformed payloads are logged
  and dropped.
- **Rate limiting:** Max 10 commands per second per topic. Excess commands
  are dropped with a warning log.
- **No privilege escalation:** MQTT commands have the same effect as the
  equivalent REST API call. They do not bypass RBAC or safety checks (panic
  mode, startup safety, etc.).

**Complications:**
- MQTT subscribe requires a persistent connection. If the broker disconnects,
  the client must reconnect and resubscribe.
- Command ordering: MQTT does not guarantee ordering across topics. Fan speed
  commands and profile activation commands may arrive in unexpected order.
  Last-write-wins semantics.
- Both backends must implement. The subscription handler must be integrated
  into the existing service lifecycle.

**Files changed:**
- `backend/app/services/mqtt_command_handler.py` — new command handler
- `backend-cs/Services/MqttCommandHandler.cs` — C# equivalent
- Both notification channel services — add subscribe logic alongside publish

---

### F3. Profile Scheduling

**Why:** Users want automatic profile switching based on time of day:
"gaming" profile in the evening, "quiet" profile overnight, "performance"
during work hours. Currently, profile switching is manual or alert-triggered.

**What:**
- Schedule entries: profile_id, start_time, end_time, days_of_week, timezone
- Multiple schedules can coexist; most-specific match wins
- Frontend schedule editor with visual timeline

**Interaction rule with quiet hours:**
Quiet hours and profile schedules both switch profiles. Priority order:
1. Panic mode (always wins)
2. Alert-triggered profile (safety)
3. Quiet hours (explicit user silence preference)
4. Profile schedule (automation convenience)
5. Manual profile selection (default)

This means quiet hours override profile schedules, which is the expected
behavior — a user who set quiet hours for 11pm–7am does not want a "gaming"
profile schedule to override that.

**Complications:**
- Timezone handling: schedules are stored in the user's configured timezone
  and converted to UTC for evaluation. DST transitions can cause 1-hour
  gaps or overlaps — document this behavior.
- Overlapping schedules: if two schedules cover the same time, the one with
  the most specific day-of-week match wins. If still tied, the one created
  most recently wins.
- Both backends must evaluate schedules. Keep evaluation logic deterministic
  so both backends make the same decision for the same inputs.

**Files changed:**
- `backend/app/services/profile_scheduler_service.py` — new service
- `backend/app/api/routes/profile_schedules.py` — CRUD endpoints
- `backend/app/db/migrations/` — new table
- `backend-cs/` — C# equivalent
- `frontend/src/components/settings/ProfileScheduleEditor.tsx` — UI

---

### F4. PDF Export

**Why:** Users want to save and share analytics reports outside the browser.
PDF is the universal document format for this purpose.

**How — browser-print approach:**
- `window.print()` triggered from an "Export PDF" button
- Print-optimized CSS stylesheet: `@media print { ... }`
  - Hide navigation, controls, and interactive elements
  - Optimize chart rendering for print (remove hover states, use darker colors)
  - Add page headers with DriveChill branding and report metadata
  - Page breaks between report sections

**Why not server-side:**
Server-side PDF generation (weasyprint, Puppeteer, etc.) adds significant
dependencies, is hard to maintain across platforms, and requires the server
to have access to the same chart rendering logic as the frontend. Browser
print avoids all of this and works offline.

**Complications:**
- SVG charts render well in print, but interactive elements (zoom, brush)
  must be hidden or reset to full view.
- Print CSS must be tested across browsers (Chrome, Firefox, Edge have
  different print rendering behaviors).
- The user's OS print dialog handles PDF save ("Save as PDF" printer).
  DriveChill cannot control the output filename or metadata.

**Files changed:**
- `frontend/src/styles/print.css` — new print stylesheet
- `frontend/src/components/analytics/AnalyticsPage.tsx` — print button
- `frontend/src/components/analytics/ExportButtons.tsx` — add PDF option

---

## 8. Explicitly Deferred (Post-v3.0)

| Item | Reason |
|---|---|
| C# Linux hardware parity (hwmon/liquidctl) | C# backend is Windows-focused; Linux users run Python. Low demand. |
| MQTT structured form fields with password masking | Requires larger SettingsPage refactor to per-type forms. Do after v3.0. |
| Noise profile cross-device calibration | Requires reference microphone standard. Research-grade problem. |
| Profile schedule conflict resolution UI | Visual conflict display is polish; rule-based resolution is sufficient. |
| Report PDF attachment in scheduled emails | Requires server-side PDF; conflicts with browser-print decision. |

---

## 9. New Dependencies

| Package | Backend | Milestone | Purpose |
|---|---|---|---|
| None | Frontend | All | All visualizations use native SVG, Web Audio API, HTML5 DnD |
| None | Python | D–F | `aiomqtt` already installed from Milestone C |
| None | C# | D–F | `MQTTnet` already installed from Milestone C |

No new runtime dependencies are introduced across v3.0.

---

## 10. Database Migrations

| Migration | Milestone | Tables |
|---|---|---|
| 016_noise_profiles.sql | E1 | `noise_profiles` |
| 017_report_schedules.sql | E3 | `report_schedules` |
| 018_annotations_or_event_log.sql | E4 | Depends on D8 decision |
| 019_profile_schedules.sql | F3 | `profile_schedules` |

Note: No migrations needed for Milestone D. MQTT uses the existing
`notification_channels` table. Session rotation modifies behavior, not schema.

---

## 11. Testing Strategy

| Milestone | Testing approach |
|---|---|
| D-Core | D3 is entirely test work. D1/D2/D4/D5 each have unit + integration tests. |
| D-Features | Frontend component tests + E2E for chart interaction. |
| E1 | Mock Web Audio API in unit tests. Manual testing with real mic required. |
| E2 | Unit tests for optimization algorithm with known noise/temp inputs. |
| E3 | Mock email service. Verify schedule evaluation logic with timezone edge cases. |
| E4 | CRUD unit tests + E2E for annotation display on charts. |
| E5 | E2E for widget drag/reorder. localStorage mock for persistence tests. |
| F1 | Mock MQTT publish. Verify discovery payload matches HA schema. |
| F2 | Mock MQTT subscribe. Verify command parsing + rate limiting. |
| F3 | Unit tests for schedule evaluation. Verify quiet-hours priority override. |
| F4 | Manual cross-browser print testing. Automated screenshot comparison optional. |

---

## 12. Execution Order

```
D-Core (all 5 items in parallel where possible)
    ↓
D-Features (D6, D7 — can start alongside late D-Core items)
    ↓
D-Decision (D8 — can happen anytime, blocks E4 only)
    ↓
E1 Noise Profiling
    ↓
E2 Noise Advisor (requires E1)
    ↓
E3 Scheduled Reports (independent of E1/E2)
E4 Event Annotations (requires D6 + D8 decision)
E5 Dashboard Widgets (independent)
    ↓
F1 HA Auto-Discovery (requires D1 + D2)
F2 MQTT Subscribe (independent of F1)
F3 Profile Scheduling (independent)
F4 PDF Export (requires D6 for chart print)
    ↓
v3.0 Release
```
