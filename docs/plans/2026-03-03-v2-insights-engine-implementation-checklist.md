# DriveChill v2.0 "Insights Engine" Implementation Checklist

**Date:** 2026-03-03  
**Status:** Ready For Planning / Execution  
**Primary Spec:** `docs/plans/2026-03-03-v2-insights-engine-design.md`

---

## 1. Purpose

This checklist translates the v2.0 Insights Engine design into an ordered execution list.

It is intended to be used after the remaining v1.6 blockers are closed. The design document remains the source of truth for contracts and scope decisions.

---

## 2. Gate Before Starting

Do not start active v2.0 implementation until all of the following are true:
- v1.6 drive-monitoring blockers are fixed
- Python and C# drive contracts are aligned
- the working tree is committed and stable
- the full Python, frontend, and C# build/test baseline is green

If those are not true, finish stabilization first. v2.0 depends on stable telemetry and stable parity.

---

## 3. Execution Order

Implement in this order:
1. Stabilize the analytics contract and shared types
2. Expand backend query engines in Python
3. Bring the C# analytics layer to the same semantics
4. Upgrade the shared analytics page
5. Add correlation and regression coverage
6. Validate performance and Prometheus compatibility
7. Update docs, audit inventory, and release gates

Do not start frontend UI expansion before the final backend response shapes are locked.

---

## 4. Shared Contract Work

### 4.1 Lock the public analytics contract

- Preserve the existing `/api/analytics/*` route family if possible
- Add `GET /api/analytics/correlation`
- Extend existing query contracts to support:
  - `hours`
  - or `start` / `end`
  - `sensor_id`
  - or `sensor_ids`
  - `bucket_seconds`

### 4.2 Standardize shared response shapes

- History response:
  - `series`
  - `bucket_seconds`
  - `requested_range`
  - `returned_range`
  - `retention_limited`
- Stats response:
  - `stats`
  - `requested_range`
  - `returned_range`
- Anomalies response:
  - `anomalies`
  - `z_score_threshold`
  - range metadata
- Regression response:
  - `regressions`
  - `baseline_period_days`
  - `recent_period_hours`
  - `threshold_delta`
- Correlation response:
  - `x_sensor_id`
  - `y_sensor_id`
  - `correlation_coefficient`
  - `sample_count`
  - `samples`
- Report response:
  - `generated_at`
  - range metadata
  - `stats`
  - `anomalies`
  - `regressions`
  - `top_anomalous_sensors`

### 4.3 Confirm additive-only change policy

- Avoid breaking existing analytics consumers
- If a breaking change is unavoidable, explicitly version under `/api/v2/analytics/*`

### 4.4 Retention Migration Prerequisite

This subsection is required by design doc §6.4. No 30-day performance gate can pass without it.

**Raise the default retention in all three locations:**
- `backend/app/config.py`: `history_retention_hours: int = 24` → `720`
- `backend/app/db/repositories/settings_repo.py`: `"history_retention_hours": "24"` → `"720"`
- `backend-cs/AppSettings.cs`: `HistoryRetentionHours { get; set; } = 24` → `720`

**Add a one-time migration for existing installs in both backends:**
- At startup, read the stored `history_retention_hours` value
- If it equals the legacy default `24`, overwrite it with `720`
- If the user has already set any other value (e.g. `48`, `168`, `720`), preserve it unchanged

**Settings UI:**
- Add a help note near the retention setting explaining that 30-day analytics require at least
  30 days of retention

**Performance test pre-condition:**
- The automated large-history test (§8.1) must seed a full 30 days of synthetic data into SQLite
  before measuring query latency — the test is invalid without this seeding step

---

## 5. Python Backend

### 5.1 Expand analytics query handling

- Update `backend/app/api/routes/analytics.py`
- Support custom `start` / `end` ranges
- Support `sensor_ids`
- Support auto bucket sizing when `bucket_seconds` is omitted
- Return range metadata and retention information

### 5.2 Implement max-preserving downsampling

- Ensure each bucket preserves raw max values
- Keep bucket counts bounded for large ranges
- Prefer SQL-side aggregation where practical

### 5.3 Upgrade anomaly logic

- Use configurable `z_score_threshold`
- Reject undersampled series
- Return stable severity labels

### 5.4 Upgrade regression logic

- Use the v2.0 baseline model:
  - rolling 30-day baseline
  - recent window default 24h
  - load-band-aware comparisons where feasible
- Keep the logic explainable and deterministic

### 5.5 Add correlation endpoint

- Add `GET /api/analytics/correlation`
- Support at least:
  - CPU load vs CPU temp
  - GPU load vs GPU temp
- Return correlation coefficient and sampled points

### 5.6 Include storage-aware telemetry

- Ensure drive temperatures appear naturally in analytics via existing `hdd_temp` history
- Reuse `drive_health_snapshots` where drive-specific trend analysis is needed

---

## 6. C# Backend

### 6.1 Match the analytics route surface

- Implement all Python analytics routes
- Add the correlation route
- Match the same validation rules and errors

### 6.2 Match bucketing and retention semantics

- Same auto-bucket behavior
- Same `retention_limited` rules
- Same `requested_range` / `returned_range` fields

### 6.3 Match anomaly and regression behavior

- Same thresholds
- Same severity labels
- Same nullability and field names

### 6.4 Match storage-aware analytics behavior

- Include drive temperatures in the same analytics paths when available
- Keep storage analytics behavior consistent with Python

---

## 7. Frontend

### 7.1 Extend the existing analytics page

- Keep the current `analytics` page
- Add:
  - preset time-range selector
  - custom start/end controls
  - multi-sensor selection
  - richer summary cards
  - regression summary
  - correlation panel

### 7.2 Preserve current navigation model

- Do not introduce a new routing model
- Keep the existing single-page app structure

### 7.3 Add storage-aware analytics visibility

- Include drive temperature sensors in analytics sensor pickers
- Show storage data in overlays when available

### 7.4 Add degraded and retention UX

- Show a clear retention-limited banner when queries are truncated
- Show "not enough data yet" states for sparse histories

---

## 8. Performance and Observability

### 8.1 Performance gate

- Build an automated large-history test matching the PRD target
- Validate:
  - 30-day query under 2 seconds
  - bounded response sizes
  - max preservation in downsampled output

### 8.2 Prometheus / Grafana verification

- Verify `/metrics` on both backends
- Confirm core metrics scrape cleanly in Prometheus
- Confirm Grafana can display them without custom translation layers

### 8.3 Metric naming parity

- Ensure Python and C# metric names and labels remain aligned

---

## 9. Test Work

### 9.1 Unit tests

- Bucket size selection
- Range validation
- Max-preserving downsampling
- Z-score anomaly thresholds
- Regression severity thresholds
- Correlation coefficient calculation

### 9.2 Backend integration tests

- History route with:
  - preset ranges
  - custom ranges
  - multi-sensor overlays
  - retention truncation
- Correlation route
- Report route

### 9.3 Frontend tests

- Time-range selection
- Sensor overlay selection
- Regression panel rendering
- Correlation panel rendering
- Retention-limited banner

### 9.4 Cross-backend parity tests

- Same response shapes from Python and C#
- Same threshold behavior
- Same severity labels
- Same error payloads

---

## 10. Documentation

### 10.1 Update product-facing docs

- Update `AUDIT.md` analytics inventory
- Update any endpoint-count references
- Update versioned feature summaries in `CHANGELOG.md` when the release is cut

### 10.2 Update operator docs

- Document Prometheus scrape expectations
- Document retention behavior for analytics queries

### 10.3 Keep the roadmap clean

- Move deferred items such as:
  - heatmap calendar
  - PDF reports
  - noise optimization UX
  into the next milestone if they do not make the cut

---

## 11. Acceptance Checklist

- User can query 1h, 24h, 7d, 30d, and custom ranges
- User can overlay multiple sensors on the analytics timeline
- User can view stable stats, anomalies, regressions, and correlation data
- Drive temperatures appear in the analytics surface when available
- Python and C# analytics APIs behave the same
- The PRD's v2.0 performance gate passes
- The PRD's Prometheus/Grafana gate passes

---

## 12. Definition Of Done

The v2.0 Insights Engine work is complete only when:
- The design spec is satisfied
- Python tests pass
- Frontend build passes
- C# build passes
- New parity tests cover both backends
- Performance tests prove the 30-day query target
- Prometheus/Grafana validation is documented and verified
- The release scope matches the cut line documented in the design

