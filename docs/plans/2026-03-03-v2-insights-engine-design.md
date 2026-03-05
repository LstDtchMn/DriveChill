# DriveChill v2.0 "Insights Engine" Design

**Date:** 2026-03-03  
**Status:** Draft  
**Authors:** Product + Engineering  
**Scope:** Release plan for the v2.0 analytics and observability milestone

---

## 1. Purpose

This document defines the v2.0 architecture and scope for DriveChill's **Insights Engine**.

v2.0 is the release where DriveChill moves from being only a fan controller with basic history into a true thermal analytics system.

The release should:
- Expand historical analytics from simple time-window lookups into real ranged analysis.
- Add explainable anomaly and regression detection.
- Correlate temperatures, fan behavior, and system load.
- Reuse the new drive-monitoring telemetry for storage-aware insight.
- Make the analytics surface consistent across Python and C#.
- Satisfy the PRD's v2.0 performance and observability gates.

This design assumes v1.6 drive monitoring is the immediate predecessor release and uses that telemetry as part of the final analytics model.

---

## 2. Product Positioning

DriveChill's v2.0 story should be:

**Not just "set fan speeds," but understand thermal behavior over time.**

### Core product thesis

DriveChill v2.0 should position itself as:

**Thermal control, storage health awareness, and explainable performance insights for serious PCs and small fleets**

### Why this matters

Most competitors focus on:
- Current readings
- Manual curve editing
- Little or no explainable historical analysis

DriveChill already has:
- A web UI
- Remote access patterns
- Existing analytics scaffolding
- Alerts, webhooks, push, and email
- Drive temperature and health telemetry in progress

v2.0 should unify those into a coherent insights layer rather than adding more one-off dashboard widgets.

---

## 3. Preconditions

v2.0 work must not start as the active implementation branch until the remaining v1.6 blockers are closed.

### Required preconditions
- The drive monitoring contract bugs are fixed.
- Python and C# `/api/drives*` behavior is aligned.
- The drive monitoring UX is complete enough to be treated as a stable telemetry source.
- The full Python, frontend, and C# validation matrix is green.
- The v1.6 work is committed as a stable baseline.

Reason: v2.0 depends on stable telemetry contracts. Building analytics on top of moving or inconsistent route shapes will create rework.

---

## 4. Goals and Non-Goals

### Goals
- Add real ranged historical analytics for temperatures, loads, and fan speeds.
- Support preset and custom time ranges.
- Support multi-sensor overlay analysis.
- Provide stable min/max/avg statistics for arbitrary windows.
- Add anomaly detection based on explainable statistical rules.
- Add thermal regression detection against a rolling baseline.
- Add load-vs-temperature correlation analysis.
- Include storage telemetry in analytics when drive monitoring is enabled.
- Preserve cross-backend API parity.
- Meet PRD performance and Prometheus validation gates.

### Non-Goals
- No opaque machine learning models in v2.0.
- No cloud sync.
- No MQTT / Home Assistant work in this release.
- No GPU direct fan control in this release.
- No major front-end routing rewrite.
- No PDF export in v2.0 GA unless everything else is already complete and green.

### v2.0 cut line

#### Must ship
- Ranged history + downsampling
- Statistics cards and richer analytics filters
- Anomaly detection
- Regression detection
- Correlation analysis
- Python/C# parity
- Performance tests and Prometheus validation

#### Can slip to v2.1
- Heatmap calendar
- PDF reports
- Noise optimization suggestions

> **Scope revision note (supersedes PRD §6.3 and §6.4 for this milestone):**
> The PRD ([`2026-02-25-drivechill-prd.md`](2026-02-25-drivechill-prd.md) lines 287, 304, 311) lists
> heatmap calendar, PDF reports, and noise optimization as v2.0 features. This design intentionally
> defers them to v2.1 to keep the release focused on the core query-latency and parity goals.
> This document is the authoritative scope definition for the v2.0 implementation milestone.
> The PRD will be updated separately to reflect the revised milestone boundary.

This keeps the v2.0 scope aligned with the "Insights Engine" identity while avoiding schedule sprawl.

---

## 5. Current Baseline

The repo already contains partial analytics functionality:
- `GET /api/analytics/history`
- `GET /api/analytics/stats`
- `GET /api/analytics/anomalies`
- `GET /api/analytics/regression`
- `GET /api/analytics/report`
- Existing `AnalyticsPage.tsx`

This means v2.0 is an **expansion and contract hardening** release, not a greenfield feature.

### Implication

The preferred approach is:
- keep existing route prefixes
- preserve existing route names where possible
- extend contracts additively
- introduce `/api/v2/analytics/*` only if a breaking response shape is unavoidable

Default choice: keep the existing `/api/analytics/*` surface and evolve it without breaking the current frontend.

---

## 6. Core Decisions

### 6.1 Use the existing telemetry stores

v2.0 should build on:
- `sensor_log` for temperatures, loads, and fan values
- `drive_health_snapshots` for storage health trend data
- existing `hdd_temp` sensor history for drive temperatures

Do not create a second raw time-series store in v2.0.

### 6.2 Explainable analytics only

All analytics logic must be deterministic and inspectable:
- moving averages
- z-score thresholds
- bounded rolling baselines
- clear severity thresholds

Do not introduce black-box ML or opaque scoring.

### 6.3 Downsample with max preservation

The primary downsampling rule is:
- preserve **per-bucket max values** so thermal peaks remain visible

This is required by the PRD's v2.0 release gate and must drive both the backend implementation and tests.

### 6.4 Retention prerequisite for 30-day queries

The PRD v2.0 release gate (v2.0-1) requires a 30-day history query to complete in under 2 seconds.
The current default `history_retention_hours` is **24 hours**, which means no installation can pass
this gate unless retention is explicitly raised.

**Decision: raise the default to 720 hours (30 days) as part of the v2.0 milestone.**

Required changes:
- `config.py`: change `history_retention_hours` default from `24` to `720`
- `settings_repo.py`: change the seeded `"history_retention_hours"` default from `"24"` to `"720"`
- `AppSettings.cs`: change `HistoryRetentionHours` default from `24` to `720`
- Add a Settings UI note explaining that 30-day analytics require at least 30 days of retention

For existing installs that already have `history_retention_hours = 24` stored in the database,
the migration must explicitly update the stored value if it equals the old default.
The implementation checklist section `4.4 Retention Migration Prerequisite` must include this step.

The automated performance test (checklist §8.1) must pre-populate SQLite with 30 days of synthetic
data to be a valid gate. Without that pre-population step, the test proves nothing about 30-day
query latency.

### 6.5 Shared frontend, no backend branching

The shared frontend must not need backend-specific conditionals for analytics behavior.

Python and C# must match on:
- endpoint existence
- request query semantics
- field names
- nullability
- severity labels
- error semantics

### 6.6 Storage-aware insights are in scope

Because drive monitoring is now a first-class telemetry source, v2.0 should include:
- drive temperatures in analytics overlays
- drive health snapshots in regression and maintenance visibility

Storage does not need a separate analytics product. It should integrate into the shared insights model.

---

## 7. Data Model and Query Model

## 7.1 Supported source signals

v2.0 analytics should support:
- CPU temperature
- GPU temperature
- Case / motherboard temperatures
- HDD / SSD / NVMe temperatures (via `hdd_temp`)
- CPU load
- GPU load
- Fan RPM
- Fan percentage

Drive health snapshot counters are queryable through the shared `/api/analytics/*` family using
`sensor_type=hdd_temp` or `sensor_id` filters. No separate drive-specific analytics route is planned
for v2.0; storage signals integrate into the shared analytics model (see §6.6).

## 7.2 Time-range model

All analytics endpoints should support one of:
- `hours`
- or `start` + `end`

### Rules
- `hours` is a convenience shorthand
- `start` / `end` is authoritative when present
- all timestamps are UTC ISO 8601
- reject ranges where `end <= start`
- cap maximum query window at 365 days in v2.0

## 7.3 Sensor selection model

Support:
- a single `sensor_id`
- or multiple `sensor_ids`

When both are provided:
- reject as invalid

The response must preserve per-series identity so the frontend can render overlays.

## 7.4 Bucket model

Support:
- `bucket_seconds`

Rules:
- minimum `10`
- maximum `86400`
- default chosen automatically by backend based on requested range if omitted

### Auto-bucket target

Aim for roughly:
- `300` to `1200` buckets per query

This gives stable frontend rendering and bounded response size without over-downsampling short windows.

---

## 8. Analytics Features

## 8.1 Historical analytics

### Required capabilities
- Preset ranges: `1h`, `24h`, `7d`, `30d`
- Custom date/time range
- Multi-sensor overlays
- Min/max/avg summaries
- Downsampled series responses
- Max-preservation validation

### Response model

Each time-series response should provide:
- `series`
- `bucket_seconds`
- `requested_range`
- `returned_range`
- `retention_limited`

Each series entry should include:
- `sensor_id`
- `sensor_name`
- `sensor_type`
- `unit`
- `points`

Each point should include:
- `timestamp_utc`
- `min_value`
- `max_value`
- `avg_value`
- `sample_count`

## 8.2 Anomaly detection

Anomalies should be computed from recent windows using simple z-score behavior.

### Default behavior
- Compare each bucket to a rolling mean and standard deviation for the selected range
- Default threshold: `z_score >= 3.0`
- Only flag anomalies when sample count is sufficient

### Response fields
- `timestamp_utc`
- `sensor_id`
- `sensor_name`
- `value`
- `unit`
- `z_score`
- `mean`
- `stdev`
- `severity`

## 8.3 Thermal regression

Thermal regression is the most important "insight" feature in v2.0.

### Definition

A regression means:
- the same sensor
- comparable recent workload conditions
- recent thermal average exceeds baseline average by a configurable delta

### First-pass baseline model

Use:
- rolling 30-day baseline
- recent window default `24h`
- compare recent averages to baseline averages

### Matching rules

For v2.0, "comparable workload" is approximated by:
- same sensor
- same broad load band
  - low: `0-25%`
  - medium: `25-75%`
  - high: `75-100%`
- matching by `cpu_load` or `gpu_load` where relevant

This is simpler and more explainable than a complex baseline engine while still being useful.

### Output
- `sensor_id`
- `sensor_name`
- `baseline_avg`
- `recent_avg`
- `delta`
- `severity`
- `message`

### Severity defaults
- `warning` at `>= 5C`
- `critical` at `>= 10C`

### Storage-aware extension

For storage telemetry:
- allow regression on drive temperature sensors
- also surface persistent worsening of drive error counters as a maintenance warning in drive views, but not as part of the main generic analytics chart layer

## 8.4 Correlation analysis

Add a new correlation endpoint for load vs temperature analysis.

### First release scope
- CPU load vs CPU temp
- GPU load vs GPU temp
- optional drive temp vs case temp overlays where both exist

### Response model
- `x_sensor_id`
- `y_sensor_id`
- `samples`

Each sample:
- `x_value`
- `y_value`
- `timestamp_utc`

Also include:
- `correlation_coefficient`
- `sample_count`

No regression line fitting is required in v2.0.

## 8.5 JSON report output

Keep JSON reporting in v2.0 as the programmatic export target.

The report should aggregate:
- stats
- anomalies
- regressions
- top anomalous sensors
- generated window metadata

Do not make PDF reporting a gate for v2.0 GA.

---

## 9. Public API Contract

All analytics routes must be available in both Python and C#.

## 9.1 Existing route family (preferred)

Keep:
- `GET /api/analytics/history`
- `GET /api/analytics/stats`
- `GET /api/analytics/anomalies`
- `GET /api/analytics/regression`
- `GET /api/analytics/report`

Add:
- `GET /api/analytics/correlation`

## 9.2 Query contract

### `GET /api/analytics/history`
Supports:
- `hours`
- or `start` + `end`
- `sensor_id`
- or `sensor_ids`
- `bucket_seconds`

Returns:
- `series`
- `bucket_seconds`
- `requested_range`
- `returned_range`
- `retention_limited`

> **Backward-compatibility decision:**
> The current frontend client (`api.ts` lines 387-389) and `AnalyticsPage.tsx` expect a `buckets`
> key in the response. The new contract adds a top-level `series` key in its place.
> **Chosen path: additive — include both `buckets` and `series` in the v2.0 response.**
> `buckets` must remain an array of the same `AnalyticsBucket` shape the frontend currently uses,
> so the existing client continues to work without modification.
> `series` is the new preferred key, with the richer per-sensor structure.
> The frontend migration to `series` can happen as a follow-on PR after the backend ships.
> If at any point both keys cannot coexist (e.g., response size constraint), the endpoint must
> be moved to `/api/v2/analytics/history` and the old route preserved unchanged.

### `GET /api/analytics/stats`
Supports:
- `hours`
- or `start` + `end`
- `sensor_id`
- or `sensor_ids`

Returns:
- `stats`
- `requested_range`
- `returned_range`

### `GET /api/analytics/anomalies`
Supports:
- `hours`
- or `start` + `end`
- `sensor_id`
- or `sensor_ids`
- `z_score_threshold`

Returns:
- `anomalies`
- `requested_range`
- `returned_range`
- `z_score_threshold`

### `GET /api/analytics/regression`
Supports:
- `baseline_days`
- `recent_hours`
- `threshold_delta`
- optional `sensor_id`

Returns:
- `regressions`
- `baseline_period_days`
- `recent_period_hours`
- `threshold_delta`

### `GET /api/analytics/correlation`
Supports:
- `hours`
- or `start` + `end`
- `x_sensor_id`
- `y_sensor_id`

Returns:
- `x_sensor_id`
- `y_sensor_id`
- `correlation_coefficient`
- `sample_count`
- `samples`

### `GET /api/analytics/report`
Supports:
- `hours`
- or `start` + `end`
- optional `sensor_id`

Returns:
- `generated_at`
- `requested_range`
- `returned_range`
- `stats`
- `anomalies`
- `regressions`
- `top_anomalous_sensors`

## 9.3 Versioning rule

If any existing analytics response shape must break:
- introduce `/api/v2/analytics/*`

Default target:
- no breaking changes
- additive fields only

---

## 10. Frontend Design

## 10.1 Analytics page remains the existing page

Extend the existing analytics page rather than creating a new app section.

Keep:
- current `analytics` page entry in the single-page navigation model

Extend it with:
- time-range selector
- custom range controls
- multi-sensor selection
- stats cards
- anomaly table
- regression summary
- correlation chart

## 10.2 Required UX sections

### A. Time-range controls
- Preset buttons: `1h`, `24h`, `7d`, `30d`
- Custom start/end picker

### B. Sensor overlay selection
- Multi-select sensor list grouped by type
- Include drive temps in the same selection list when present

### C. Summary cards
- Min / max / average per selected sensor

### D. Main timeline chart
- Overlay lines
- Toggle individual series visibility

### E. Anomalies panel
- Sortable anomaly table

### F. Regression panel
- Highlight sensors trending hotter than baseline

### G. Correlation panel
- Scatter plot for selected x/y series

### H. Empty / degraded states
- If insufficient history exists, show "not enough data yet"
- If retention truncates the request, show a clear retention-limited banner

## 10.3 Deferred UI

The following are intentionally not required for v2.0 GA:
- heatmap calendar
- PDF export UI

They can be added later without changing the core data model.

---

## 11. Backend Implementation Strategy

## 11.1 Python

Expand the existing analytics implementation in:
- `backend/app/api/routes/analytics.py`

Add or extend supporting service/query logic as needed. If the current route file is becoming too large, split the query logic into dedicated analytics service modules without changing the public route surface.

### Required Python deliverables
- ranged history query support
- multi-sensor query support
- auto-bucket selection
- max-preserving downsampling
- correlation endpoint
- regression logic updated to use current v2.0 semantics

## 11.2 C#

Mirror the same API and semantics in the C# backend.

### Required C# deliverables
- route parity for the full analytics surface
- the same bucketing/downsampling rules
- the same regression thresholds and labels
- the same error handling and validation behavior

The shared frontend must be able to call either backend without behavior forks.

## 11.3 Storage-aware analytics

Leverage:
- `sensor_log` for drive temperatures
- `drive_health_snapshots` for drive-health trends

Do not fork storage analytics into a separate subsystem. Reuse the existing analytics API family unless a drive-specific drill-in endpoint is later needed.

---

## 12. Observability and Metrics

v2.0 must close the Prometheus/Grafana loop described in the PRD.

### Required deliverables
- `/metrics` behavior validated in both backends
- metric naming consistent across Python and C#
- no backend-specific metric renaming for core sensors
- basic Grafana usage documented

### Non-goal
- do not build a custom Grafana dashboard pack as a release gate

The release gate is interoperability, not polished dashboard templates.

---

## 13. Performance Rules

The v2.0 analytics layer must be intentionally bounded.

### Targets
- 30-day query over the PRD reference dataset returns in under `2s`
- Response size remains bounded through auto-bucketing
- Downsampled output preserves max values per bucket

### Required implementation behavior
- use SQL aggregation where practical
- avoid returning raw 1-second data for large windows
- keep bucket count in a bounded target range

---

## 14. Tests and Release Gates

## 14.1 Unit tests
- bucket selection logic
- max-preserving downsampling
- range validation
- anomaly z-score behavior
- regression severity thresholds
- correlation coefficient calculation

## 14.2 Backend integration tests
- history route with preset ranges
- history route with custom ranges
- retention-limited responses
- multi-sensor overlays
- correlation route
- report route

## 14.3 Cross-backend parity tests
- same route shapes
- same field names
- same threshold behavior
- same severity labels

## 14.4 Frontend tests
- time-range switching
- multi-sensor selection
- retention-limited banner
- regression panel rendering
- correlation panel rendering

## 14.5 Performance gate tests
- automated large-history dataset test meeting the PRD target
- explicit downsampling correctness test that verifies max preservation

## 14.6 Prometheus validation
- scrape both backends with Prometheus
- verify core metrics appear without custom transforms

---

## 15. Acceptance Criteria

v2.0 is complete when:
- users can query 1h, 24h, 7d, 30d, and custom ranges from the UI
- users can overlay multiple sensors on one timeline
- the analytics page shows stable stats, anomalies, regressions, and correlation data
- drive temperatures participate in the analytics surface
- Python and C# analytics routes behave the same
- the PRD's performance and Prometheus gates pass

---

## 16. Assumptions and Defaults

- v2.0 builds on a stable v1.6 baseline
- existing `/api/analytics/*` routes remain the preferred contract
- additive contract expansion is preferred over route replacement
- no ML is introduced in v2.0
- storage telemetry is included where it naturally fits, not as a separate analytics product
- heatmap calendar, PDF reports, and noise-optimization UX are intentionally deferred unless schedule is far ahead of plan

