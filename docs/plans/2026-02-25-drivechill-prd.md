# DriveChill â€” Product Requirements Document

**Version:** 1.0
**Date:** 2026-02-25
**Status:** In Progress â€” approved scope, v1.5 implementation underway, prerequisites complete

---

## 1. Vision & Positioning

**Product:** DriveChill â€” Temperature-based PC fan speed management with a modern web dashboard.

**Vision:** The only fan controller you can use from your phone, from another room, or from another machine entirely. DriveChill turns PC thermal management from a "set and forget desktop app" into a monitoring-grade tool for enthusiasts and homelab operators alike.

**Target Users:**

1. **PC Enthusiasts / Gamers** â€” Want fine-grained noise-vs-cooling control, custom curves, low fan noise during light loads.
2. **Homelab / Home Server Admins** â€” Need remote thermal monitoring of headless machines, historical data to catch overnight thermal events, multi-machine oversight.

**Business Model:** Free and open source with donation support (GitHub Sponsors, Ko-fi). All features available to all users.

### Competitive Positioning

| Differentiator | Fan Control | NZXT CAM | iCUE | Argus Monitor | DriveChill |
|---|---|---|---|---|---|
| Web-based remote UI | No | No | No | No | **Yes** |
| Historical data logging | No | No | No | No | **Yes** |
| Cross-platform (Win+Linux) | No | No | No | No | **Yes** |
| REST API | No | No | No | No | **Yes** |
| Multi-machine monitoring | No | No | No | No | **Planned** |
| Fan benchmarking | No | No | No | No | **Yes** |
| Free & open source | Yes | No | No | No | **Yes** |

**Key insight:** Every competitor is a desktop-only application with zero remote access capability. DriveChill's web architecture makes it the only fan controller usable from a phone, a tablet, or another PC. No competitor stores historical thermal data â€” users cannot answer basic questions like "what was my CPU temperature at 3am?"

---

## 2. What Already Exists

DriveChill has a substantially complete initial implementation covering all 6 phases of the original PLAN.md:

### Fully Implemented
- FastAPI backend with lifespan management, CORS, health endpoints
- 4 hardware backends: LHM HTTP, LHM Direct (pythonnet), lm-sensors, Mock
- REST API: 30+ endpoints across sensors, fans, profiles, alerts, settings
- WebSocket real-time streaming (1s default interval) with alert integration
- React/Next.js dashboard with radial temp gauges, fan speed cards, live time-series charts
- Interactive SVG fan curve editor with draggable control points
- 4 preset profiles: Silent, Balanced, Performance, Full Speed
- Alert system with threshold rules, active alert tracking, event history
- SQLite data logging (10s intervals) with CSV export
- Settings page with polling interval, temp units, data retention
- Dark/light theme with CSS variable-based theming
- Windows system tray integration (pystray)
- Fan benchmarking tool (RPM sweep with stall detection)
- C# alternative backend (full .NET mirror of Python backend)
- Docker support (Dockerfile + docker-compose)
- Zustand state management, custom WebSocket hook with auto-reconnect

### Known Issues in Current Build
- **Profiles not persisted** â€” Stored in Python dict, lost on restart
- **Settings not persisted** â€” In-memory only, revert on restart
- **Active profile not remembered** â€” No record of which profile was active
- **Temp unit toggle broken** â€” UI toggles C/F but values are not converted
- **No hysteresis** â€” Fans oscillate when temp hovers near curve thresholds
- **No auto-start** â€” Users must manually launch on every boot
- **No fan safety fallback** â€” If app crashes, fans stay at last-set speed instead of returning to BIOS control (addressed in v1.0 section 4.3)
- **No browser notifications** â€” Alert system exists but no Web Notifications API integration
- **No authentication** â€” Web UI is open to anyone on the network (addressed in v1.0: localhost-only binding + mandatory session auth for non-localhost bind)
- **No schema versioning** â€” SQLite has sensor_log table but no migration framework for schema changes

### Tech Stack

| Component | Version |
|-----------|---------|
| Next.js | 14.2.0 |
| React | 18.3.0 |
| TypeScript | 5.3.0 |
| Tailwind CSS | 3.4.0 |
| Recharts | 2.12.0 |
| Zustand | 4.5.0 |
| FastAPI | 0.109.0+ |
| Uvicorn | 0.27.0+ |
| Pydantic | 2.5.0+ |
| aiosqlite | 0.19.0+ |
| pythonnet | 3.0.3+ |

---

## 3. Release Milestones

### v1.0 â€” "Daily Driver"

**Goal:** Make DriveChill reliable enough for someone to install it and never think about it again.

### v1.5 â€” "Remote Ready"

**Goal:** Unlock the core differentiator â€” managing PC thermals from anywhere, across multiple machines.

### v2.0 â€” "Insights Engine"

**Goal:** Transform DriveChill from a controller into an analytics platform for thermal management.

---

## 4. v1.0 â€” "Daily Driver" Feature Spec

### 4.1 Persistence (Critical)

| Feature | Description | Priority |
|---------|-------------|----------|
| Profile persistence | Save fan profiles and custom curves to SQLite. Survive restarts. | P0 |
| Settings persistence | Save poll interval, temp unit, retention hours to SQLite or config file. | P0 |
| Active profile memory | Remember which profile was active on shutdown, restore it on startup. | P0 |
| Schema versioning | Use a `schema_version` table in SQLite. Each migration is a numbered .sql file applied on startup. Enables future schema changes without data loss. | P0 |
| Backup before migration | Before applying any schema migration, auto-create a timestamped backup of the database file (e.g., `drivechill.db.bak-20260225-143012`). If migration fails, restore from backup automatically and log the failure. | P0 |
| Manual backup/restore CLI | `drivechill --backup` exports profiles, curves, settings, sensor labels, and alert rules to a portable JSON file (no raw sensor history). `drivechill --restore <backup.json>` imports it. | P1 |
| DB snapshot restore CLI | `drivechill --restore-db <drivechill.db.bak-...>` restores a full SQLite snapshot created by auto-backup before migrations. | P1 |

### 4.2 Fan Curve Engine Improvements

| Feature | Description | Priority |
|---------|-------------|----------|
| Hysteresis / deadband | Configurable dead zone (default 3 deg C). Fan ramps up at threshold but doesn't ramp down until threshold minus deadband. Prevents oscillation. | P0 |
| Composite curves | Allow a fan to be governed by multiple sensors (e.g., case fan responds to MAX(CPU, GPU)). Explicit UI for selecting multiple source sensors. | P1 |
| Minimum speed floor | Per-fan configurable minimum speed. Some fans stall below ~30%. Informed by existing fan benchmark data. | P1 |
| Ramp rate limiting | Smooth speed changes over time (e.g., max 10%/sec) to avoid jarring speed jumps. | P2 |
| Zero-RPM support | Curve editor allows 0% speed. Per-fan "zero-RPM capable" checkbox overrides the minimum speed floor. Fan benchmark should auto-detect zero-RPM capability. Enables true silent operation at idle for compatible hardware. | P1 |
| Use-case presets | Expand built-in presets beyond generic labels. Add: **Gaming** (aggressive GPU cooling, quiet CPU at idle), **Rendering** (moderate sustained load, all fans balanced), **Sleep** (absolute minimum RPM, zero-RPM where supported). Keep existing Silent/Balanced/Performance/Full Speed. | P1 |
| Dangerous curve warning | On save, if any curve point sets fans below 20% at temps above 75 deg C, show a confirmation dialog: "This curve could allow high temperatures with low fan speed. Apply anyway?" Prevents accidental thermal damage. | P0 |

### 4.3 System Integration

| Feature | Description | Priority |
|---------|-------------|----------|
| Auto-start on boot | Windows: Task Scheduler or Startup folder registration. Linux: systemd service file generation. | P0 |
| Minimize to tray by default | Tray support exists; make it the default UX â€” launch minimized, tray icon always visible. | P1 |
| Fan safety on exit | Restore fans to BIOS/auto mode on graceful exit (SIGTERM, SIGINT, service stop, tray quit). Implemented via signal handlers and atexit. | P0 |
| Localhost-only binding by default | Bind to 127.0.0.1 (not 0.0.0.0) out of the box. Users must explicitly opt in to LAN/remote access via config. | P0 |
| Session auth (auto-required off localhost) | Form-based username/password login producing a server-side session. **One rule:** disabled when binding to 127.0.0.1 (default); automatically required when binding to any other address â€” startup fails if no password is configured. See Security Baseline below. | P1 |

#### Security Baseline â€” v1.0 Required Controls

When session auth is enabled (any non-localhost binding), the following controls are required. These are not optional enhancements â€” they define what "session auth" means in this codebase:

| Control | Requirement |
|---|---|
| Password storage | Passwords hashed with bcrypt (cost factor >= 12). Never stored or logged in plaintext. |
| Brute-force protection | Lock account for 15 minutes after 5 consecutive failed login attempts. Log each failed attempt. |
| Rate limiting | Auth endpoints rate-limited to 10 requests/minute per IP. |
| Session management | Session tokens are random (32+ bytes, cryptographically secure). Configurable TTL (default 8 hours of inactivity, range 15minâ€“30 days). HTTP-only, SameSite=Strict cookie flags. |
| CSRF protection | State-changing requests (POST/PUT/DELETE) from cookie-authenticated sessions require a CSRF token (double-submit cookie or synchronizer token pattern). API key-authenticated requests are exempt (stateless, not cookie-based). |
| Secure defaults | Auth automatically enables for any non-localhost bind address. Startup fails with a descriptive error if no password is configured. No silent fallback to open access. |
| Auth audit log | All auth events (login success, login failure, lockout triggered, password change, session expiry) written to a dedicated `auth_log` table in SQLite. Minimum fields: timestamp, event_type, ip_address, outcome. Retained for 90 days. |

**Release gate:** Auth controls have a dedicated v1.0 gate (v1.0-11). End-to-end auth flow is also tested in v1.5-3.

#### Safe Mode / Panic Mode

DriveChill must specify its behavior in degraded conditions. These are not edge cases â€” they are expected operational events.

| Condition | Behavior | Rationale |
|---|---|---|
| Sensor read fails (1-3 consecutive errors) | Log warning. Continue using last known value. | Transient read errors are common. Avoid unnecessary fan changes. |
| Sensor read fails (>3 consecutive errors) | Set all controlled fans to 100%. Log error. Show alert banner in UI. | No data = no safe curve execution. Full speed is safe; no speed is not. |
| Temperature exceeds configurable panic threshold (default: CPU 95Â°C, GPU 90Â°C) | Immediately set all fans to 100%, regardless of active profile. Trigger alert. Stay at 100% until temp drops below threshold minus 5Â°C (hysteresis applied). | Prevent thermal damage. Overrides all user curves. |
| Backend cannot read DB (locked, corrupt) | Continue in read-only mode using last-known-good in-memory profile if available. If no cached profile exists, immediately set all controlled fans to 100%, log critical error, and disable curve control until DB recovers. | DB errors should not silently degrade to unsafe control behavior. |
| Backend starts with no valid profile | Default to "Balanced" preset. Log that default was applied. | Never start with uncontrolled fans if user has not configured anything. |
| Sensor present in profile but missing from hardware | Ignore that sensor's contribution. Apply max of remaining sensors. Log which sensor is missing. | Hardware changes (e.g., disconnect) should not break fan control for the whole machine. |

#### Fan Safety: Failure Mode Analysis

The app cannot guarantee fan restoration in all failure scenarios. The safety strategy is **defense in depth**:

| Failure Mode | Mitigation | App Responsibility |
|---|---|---|
| Graceful exit (quit, SIGTERM) | Signal handler restores BIOS fan control before exit | Full |
| Service manager stop | systemd/Task Scheduler sends SIGTERM first; same handler fires | Full |
| App crash (unhandled exception) | atexit handler + Python signal handler. Best-effort. | Partial |
| `kill -9` / hard kill | No handler runs. Rely on hardware behavior (see below). | None |
| Power loss / kernel panic | No software can respond. Rely on hardware behavior. | None |
| OS shutdown | OS sends SIGTERM to services before halt; handler fires if shutdown is orderly | Full |

**Hardware fallback:** Most motherboard fan controllers revert to BIOS-defined speeds when software PWM signals stop arriving. This is the relied-upon safety net for unrecoverable failures (`kill -9`, power loss). DriveChill documents this assumption and recommends users verify their BIOS fan fallback behavior during initial setup. The setup wizard (v1.0) should include a "verify BIOS fallback" step.

### 4.4 UX & Polish

| Feature | Description | Priority |
|---------|-------------|----------|
| Temperature unit conversion | Actually convert displayed values when user selects Fahrenheit. Apply to gauges, charts, alerts, and curve editor axes. | P1 |
| Browser notifications | Wire up Web Notifications API for temperature alerts. Request permission on first alert rule creation. | P2 |
| Frontend disconnect handling | Show clear "Disconnected" banner when backend is unreachable. Gray out controls. Auto-reconnect indicator. | P1 |
| Alert cooldown & dedup | Per-rule configurable cooldown (default 5 minutes). No repeat notifications for the same rule until it clears and re-triggers. Prevents alert spam during sustained high temps. | P1 |
| Sensor labeling | Users can assign custom names to sensors (e.g., rename "Sensor 1" to "Top Exhaust Fan"). Labels persist in SQLite and are used in all UI views, alerts, and exports. | P1 |
| Profile quick-switch (tray) | System tray menu lists all profiles for one-click activation. No need to open the full UI. | P2 |
| Profile import/export | Export any profile (curves + metadata) to JSON. Import JSON to create a new profile. Enables sharing between machines and manual rollback. | P1 |
| Quiet hours (scheduled profiles) | Automatic profile switching on a time schedule (e.g., "Silent" 11 PM-7 AM, "Balanced" during the day). Configurable per day of week. Manual override is respected until the next scheduled boundary. | P1 |
| Release Fan Control panic button | Always-visible red button on the dashboard that immediately sets all fans to BIOS/auto mode. One click, no confirmation dialog. Also available in tray menu. Provides instant escape from any fan control issue. | P0 |
| Curve "you are here" indicator | On the curve editor graph, show a dot marking the current operating point (current temp on X-axis, current fan speed on Y-axis). Updates in real-time. Invaluable for tuning curves to actual thermal behavior. | P1 |
| Curve editor keyboard shortcuts | Arrow keys nudge selected control point (1 deg / 1% per press, 5 with Shift held). Delete key removes selected point. Enables precise fine-tuning without mouse dragging. | P2 |
| Post-update changelog banner | After a schema migration or version update, show a dismissible "What's new" notification banner in the UI with a brief summary of changes. Not a popup â€” inline banner that persists until dismissed. | P2 |

### 4.5 v1.0 Non-Goals
- No multi-machine support
- No USB controller support
- No mobile-specific layouts
- No native mobile app

### 4.6 v1.0 Scope Cut Line

**Must ship for v1.0 GA (cannot slip):**
- All v1.0 release gates (v1.0-1 through v1.0-17) pass.
- Persistence and migration safety: profile/settings persistence, schema versioning, pre-migration backup + rollback.
- Core control safety: hysteresis, dangerous-curve warning, safe mode/panic mode escalation, release-fan-control panic button.
- Reliability baseline: auto-start (Windows/Linux), graceful fan restore (mock + hardware checks), localhost-by-default networking.
- Security baseline for non-localhost binds: session auth, lockout/rate limit/CSRF/session TTL, auth audit logging.
- Daily usability baseline: quiet hours, sensor labeling, profile import/export.

**Can slip to v1.1 if schedule risk appears (without blocking v1.0 GA):**
- Browser notifications.
- Use-case presets (Gaming/Rendering/Sleep).
- Profile quick-switch from tray.
- Curve "you are here" operating-point indicator.
- Curve editor keyboard shortcuts.
- Post-update changelog banner.

---

## 5. v1.5 â€” "Remote Ready" Feature Spec

### 5.1 Mobile-Responsive Dashboard

| Feature | Description | Priority |
|---------|-------------|----------|
| Responsive layout | Dashboard, curve editor, alerts, and settings usable on phone-sized screens (320px+). | P0 |
| Touch-friendly curve editor | Touch drag for control points, pinch-to-zoom on curve graph, long-press to delete points. | P1 |
| Compact dashboard mode | Simplified single-column view for mobile: key temps + fan speeds at a glance. | P1 |

### 5.2 Multi-Machine Monitoring

> **Note:** This section is intentionally concept-level. A detailed multi-machine architecture design doc (discovery model, auth bootstrap, version compatibility matrix, conflict resolution, partial failure behavior) will be written before v1.5 implementation begins.

| Feature | Description | Priority |
|---------|-------------|----------|
| Agent architecture | Each machine runs a DriveChill agent (existing backend). One instance acts as "hub" aggregating data from agents via REST APIs. | P0 |
| Machine registry | Hub UI to add/remove machines by hostname/IP + port. Health check polling with status indicators. | P0 |
| Unified dashboard | Overview page showing all machines' thermal status in a grid. Click to drill into individual machine dashboards. | P0 |
| Per-machine fan control | Apply profiles and curves to remote machines from the hub UI. | P1 |

### 5.3 Security & Remote Access

| Feature | Description | Priority |
|---------|-------------|----------|
| Authentication | Session auth (shipped in v1.0) extended for multi-machine API key use. Auth auto-enables on any non-localhost bind â€” same rule as v1.0. In hub mode, agents authenticate to hub via API keys. | P0 |
| HTTPS support | Self-signed cert generation or Let's Encrypt integration for encrypted connections. | P1 |
| API keys | Token-based auth for agent-to-hub communication in multi-machine setups. | P1 |

### 5.4 Notifications

| Feature | Description | Priority |
|---------|-------------|----------|
| Push notifications | Web Push API for browser notifications even when tab is in background. | P1 |
| Email alerts | Optional SMTP configuration for critical temperature alerts (e.g., "GPU hit 95 deg C on Server-02"). | P2 |
| Webhook support | POST to user-defined URL on alert events. Enables Discord, Slack, Home Assistant integrations without custom code. | P1 |

### 5.5 GPU Fan Control Investigation

GPU fans are the loudest component in a gaming PC, yet no cross-platform open-source tool provides unified CPU + GPU fan curve management.

| Feature | Description | Priority |
|---------|-------------|----------|
| NVIDIA GPU fan control | Investigate NVML / nvidia-smi / NVAPI for direct GPU fan speed control on Windows and Linux. Prototype as a new hardware backend extension. | P1 |
| AMD GPU fan control | Investigate ROCm-SMI / OverdriveN API for AMD GPU fan speed control. Prototype as a new hardware backend extension. | P2 |
| GPU temp as curve source | Even before direct GPU fan control, allow GPU temp to drive case/CPU fan curves (already partially supported via composite curves). | P0 |

**Note:** Direct GPU fan control is OS-specific and GPU-vendor-specific. The investigation will determine feasibility and produce a design doc before implementation commits.

### 5.6 v1.5 Non-Goals
- No native mobile app
- No cloud-hosted hub (hub runs on user's LAN)
- No USB controller support

---

## 6. v2.0 â€” "Insights Engine" Feature Spec

### 6.1 Historical Analytics Dashboard

| Feature | Description | Priority |
|---------|-------------|----------|
| Time-range selector | View thermal data over 1h, 24h, 7d, 30d, or custom range. | P0 |
| Multi-sensor overlay charts | Compare CPU, GPU, and case temps on the same timeline. Correlate with fan speeds and system load. | P0 |
| Min/max/avg statistics | Summary cards per sensor per time range (e.g., "CPU: avg 58 deg C, peak 87 deg C at 3:42 AM"). | P1 |
| Heatmap calendar | Daily thermal summary view (like GitHub contribution graph but for peak temperatures). | P2 |

### 6.2 Thermal Trend Analysis

| Feature | Description | Priority |
|---------|-------------|----------|
| Anomaly detection | Flag unusual thermal behavior (e.g., "GPU is 12 deg C hotter than usual under similar load"). | P1 |
| Baseline comparison | Let users set a "baseline" and compare current performance over time to detect degradation. | P2 |
| Load-vs-temp correlation | Scatter plot of load% vs temperature to help users understand cooling efficiency. | P2 |

### 6.3 Noise Optimization

| Feature | Description | Priority |
|---------|-------------|----------|
| Fan noise profiling | Use benchmark data to estimate relative noise at each speed. Show noise-vs-cooling tradeoff of current curve. | P2 |
| Optimization suggestions | Recommend curve adjustments (e.g., "Drop case fan from 60% to 45% â€” temps rise ~2 deg C but noise drops significantly"). | P2 |

### 6.4 Reporting & Export

| Feature | Description | Priority |
|---------|-------------|----------|
| PDF thermal reports | Exportable summary of thermal performance over a time period. Useful for homelab documentation. | P2 |
| Grafana integration | Prometheus-compatible metrics endpoint (/metrics) for feeding data into existing Grafana dashboards. | P1 |
| JSON analytics API | Expose trend/statistics endpoints for programmatic access. | P1 |

---

## 7. Future Roadmap (Post-v2.0)

These items are not scoped but should be architected for in earlier releases:

- **Native mobile app** â€” React Native app consuming the REST API with push notifications.
- **USB controller support** â€” Corsair, NZXT, Aquacomputer hardware integration via HID/USB libraries.
- **Cloud sync** â€” Optional cloud service to view machines remotely without LAN access.
- **Plugin API** â€” Community extensions for custom backends, notification channels, dashboard widgets.
- **AIO pump curve control** â€” Liquid cooler pump speed management.
- **Home Assistant integration** â€” Native HA add-on or MQTT discovery.

---

## 8. Release Gates & Acceptance Tests

Each milestone has explicit go/no-go criteria. All tests must pass before the milestone ships.

### v1.0 Release Gates

| ID | Test | Procedure | Pass Criteria |
|---|---|---|---|
| v1.0-1 | Profile persistence | Create custom profile with 5-point curve. Restart backend. Query `GET /api/profiles`. | Custom profile and curve points returned identically. |
| v1.0-2 | Settings persistence | Change poll interval to 2s and temp unit to F. Restart backend. Query `GET /api/settings`. | Settings returned as configured. |
| v1.0-3 | Active profile restore | Activate "Silent" profile. Restart backend. Query `GET /api/profiles`. | "Silent" profile is active. Fan speeds match Silent curve within 5 seconds of startup. |
| v1.0-4 | Hysteresis (no oscillation) | Using mock backend, set temp to oscillate between 64-66 deg C with a curve threshold at 65 deg C (3 deg C deadband). Monitor fan speed over 60 seconds. | Fan speed changes no more than once in 60 seconds. No rapid toggling. |
| v1.0-5 | Auto-start (Windows) | Install auto-start. Reboot machine. Check system tray after login. | DriveChill icon appears in tray within 30 seconds of desktop load. |
| v1.0-6 | Auto-start (Linux) | Install systemd service. Reboot. `systemctl status drivechill`. | Service is active (running). |
| v1.0-7 | Graceful fan restore (mock) | With mock backend, set fans to 50%. Send SIGTERM to backend process. | Backend exits within 5 seconds. Final log line confirms fan-restore call. Mock backend records speed as "auto". |
| v1.0-7H | **[HARDWARE] Graceful fan restore â€” Windows/LHM** | On real hardware with LHM Direct backend, manually set a fan to 60% via UI. Quit from system tray. Observe fan with RPM meter or BIOS fan monitor. | Fan RPM returns to BIOS-configured speed within 10 seconds of quit. Verified with hardware RPM reading, not just software state. Manual test, logged in release checklist. |
| v1.0-7L | **[HARDWARE] Graceful fan restore â€” Linux/lm-sensors** | On real hardware with lm-sensors backend, set a fan to 60%. Send SIGTERM. Observe fan RPM via `sensors` or physical measurement. | Fan RPM returns to BIOS-configured speed within 10 seconds. Manual test, logged in release checklist. |
| v1.0-8 | Localhost binding | Start backend with default config. Attempt connection from a different machine on LAN to port 8085. | Connection refused. Only localhost connections succeed. |
| v1.0-9 | Memory footprint | Run backend with mock backend for 1 hour, polling at 1s. Measure RSS. | RSS stays below 100MB. No memory leak trend (RSS at t=60min within 20% of RSS at t=10min). |
| v1.0-10 | Schema migration | Start with v0 database (no schema_version table). Upgrade to v1.0 backend. | Backend applies migrations automatically. Existing sensor_log data preserved. New tables created. |
| v1.0-11 | Session auth unit tests | Run `pytest tests/test_auth.py` (or equivalent). | bcrypt hash verified correct; brute-force lockout triggers after exactly 5 failures; rate limiter blocks 11th request in a minute; CSRF token rejected on tamper; session expires after configured TTL; auth_log records all events. All tests pass with 0 failures. |
| v1.0-12 | Quiet hours | Configure "Silent" 23:00-07:00, "Balanced" otherwise. Run mock backend across scheduled boundaries. | Profile switches within 60 seconds of boundary. Manual override during quiet hours is respected until next boundary. |
| v1.0-13 | Dangerous curve warning | Create/edit a profile so a point is set below 20% at >75 deg C (e.g., 80 deg C @ 0%). Click save/apply. | Blocking warning dialog appears. Cancel keeps prior curve unchanged. Confirm applies curve and logs explicit override event. |
| v1.0-14 | Safe mode escalation on sensor failure | Configure mock sensor read to fail repeatedly. Observe behavior across first 4 failed polls. | First 3 failures only log warnings and keep prior fan command. On 4th consecutive failure, all controlled fans switch to 100% and UI shows a persistent alert banner. |
| v1.0-15 | Panic threshold override | Set panic threshold low in test config (e.g., CPU 70 deg C). Feed mock reading above threshold, then below threshold minus hysteresis. | All controlled fans switch to 100% within 1 second of threshold crossing and stay there until temp drops below threshold minus 5 deg C. |
| v1.0-16 | Release Fan Control panic button | With active profile controlling fans, click dashboard panic button and tray panic entry. | Both paths set all fans to BIOS/auto within 2 seconds, show confirmation toast, and keep control released until user explicitly reapplies a profile. |
| v1.0-17 | Migration auto-backup + rollback | Introduce a deliberately failing migration after backup step. Start backend upgrade. | Timestamped `.db.bak` snapshot is created before migration. On failure, DB is restored from snapshot automatically, startup aborts with clear error, and pre-upgrade data remains intact. |

### v1.5 Prerequisites (must be met before v1.5 implementation starts)

| ID | Prerequisite | Done When |
|---|---|---|
| pre-1.5-A | Multi-machine architecture design doc written and approved | [Done 2026-02-26] `docs/plans/2026-02-26-multi-machine-design.md` exists and status is "Approved" |
| pre-1.5-B | Auth security baseline from v1.0 is shipped and passing | v1.0 release gates v1.0-8 and auth unit tests all green |
| pre-1.5-C | API compatibility matrix initialized | [Done 2026-02-26] `docs/api-compatibility.md` exists with v1.0 client entries |

### v1.5 Release Gates

| ID | Test | Procedure | Pass Criteria |
|---|---|---|---|
| v1.5-1 | Mobile layout | Open dashboard in Chrome DevTools at 375x667 (iPhone SE). Navigate all pages. | [Done 2026-02-26] Automated mobile gate passed: no horizontal scroll, all controls >=44x44, curve editor touch drag verified. |
| v1.5-2 | Multi-machine data flow | Run 3 DriveChill agents (mock backend) on different ports. Configure hub to monitor all 3. | Hub displays all 3 machines with <3s data freshness. If one agent stops, hub shows "offline" within 10 seconds. Other machines unaffected. |
| v1.5-3 | Authentication | Enable auth via config. Attempt unauthenticated API call. | Returns 401. After login, session persists across page reloads. API key auth works for agent-to-hub. |
| v1.5-4 | Webhook latency | Configure webhook to POST to a local HTTP listener. Trigger temperature alert. | Webhook fires within 3 seconds of threshold crossing. Payload contains sensor_id, value, threshold, timestamp. |

### v2.0 Release Gates

| ID | Test | Procedure | Pass Criteria |
|---|---|---|---|
| v2.0-1 | Historical query performance | Populate SQLite with 30 days of sensor data (1 reading/10s = ~259k rows per sensor, 4 sensors = ~1M rows total). Query 30-day range for all sensors. | Response returned in <2 seconds (measured from request to last byte of response). Downsampled data preserves per-bucket max value: for any 5-minute window in the raw data, the maximum raw reading in that window must appear in the downsampled output within Â±1 bucket. Verified by automated test comparing raw vs downsampled max values across 10 randomly selected windows. |
| v2.0-2 | Prometheus endpoint | Configure default Prometheus to scrape `/metrics`. View in Grafana. | Metrics appear in Grafana without custom parsing rules. CPU temp, GPU temp, fan speeds all visible as named metrics. |

---

## 9. Technical Considerations

### Data Storage
- v1.0: SQLite for profiles, curves, settings, and sensor history (already used for history)
- v1.5+: Consider PostgreSQL option for multi-machine hub with higher write throughput
- Sensor data retention: downsample older data (1s resolution for 24h, 1min for 7d, 5min for 30d)

### Security
- **One rule:** v1.0 binds to 127.0.0.1 by default. Any non-localhost bind address automatically enables session auth and requires a password at startup â€” there is no "LAN without auth" mode.
- v1.5 extends auth with API keys for agent-to-hub communication; the single-rule binding behavior carries forward unchanged.
- HTTPS should be easy to enable but not mandatory (many homelab users use a reverse proxy in front).

### Runtime Configuration Contract (v1.0)

| Key | Default | Valid Range / Format | Source of Truth | Hot Reload |
|---|---|---|---|---|
| DRIVECHILL_HOST | 127.0.0.1 | Valid bind address | Environment (startup) | No |
| DRIVECHILL_PORT | 8085 | 1-65535 | Environment (startup) | No |
| DRIVECHILL_SESSION_TTL | 8h | 15m-30d | Settings DB | No (takes effect on new sessions) |
| panic_cpu_temp_c | 95 | 70-110 deg C | Settings DB | Yes |
| panic_gpu_temp_c | 90 | 65-110 deg C | Settings DB | Yes |
| panic_hysteresis_c | 5 | 1-15 deg C | Settings DB | Yes |
| sensor_failure_limit | 3 | 1-10 consecutive failures | Settings DB | Yes |
| `alert_cooldown_seconds` | `300` | `0-86400` | Settings DB | Yes |
| quiet_hours_schedule | Disabled | JSON schedule; local timezone | Settings DB | Yes |
| poll_interval_seconds | 1 | 1-60 | Settings DB | Yes |

- Settings stored in SQLite are the source of truth for runtime behavior.
- Environment variables are reserved for deployment/bootstrap controls (host, port, startup credentials) and are evaluated at process start.

### Performance
- WebSocket broadcasts should support 10+ concurrent clients without degradation
- SQLite write batching: buffer sensor readings and flush every 10s (already implemented)
- Frontend: virtualize long lists (alert history, sensor logs) to avoid DOM bloat

### Compatibility
- Windows 10/11 with LibreHardwareMonitor
- Linux: Debian/Ubuntu, Fedora, Arch with lm-sensors
- Docker: Linux containers. Investigate running with `--device` mappings and `SYS_RAWIO` capability instead of `privileged: true`. Privileged containers are a security risk that homelab users will reject. Provide both a minimal-permissions docker-compose (preferred) and a privileged fallback for hardware that requires it.
- Browsers: Chrome, Firefox, Edge, Safari (latest 2 versions)

### API Versioning
- All endpoints are served under `/api/` (current, unversioned â€” treated as v1)
- When breaking changes are needed (v1.5 multi-machine or v2.0 analytics), introduce `/api/v2/` prefix
- Deprecated endpoints remain available for at least one minor version with `Deprecation: <date>` response header and logged deprecation warning
- Breaking change = removed field, changed field type, changed endpoint path, or changed auth requirement
- Additive changes (new fields, new endpoints) are non-breaking and do not require a version bump

**Contract test requirement:** Any endpoint consumed by the frontend, a hub agent, or a mobile app must have a contract test (request schema + response schema assertions) in the test suite. Breaking a contract test requires a version bump.

**Compatibility matrix:** Maintained in `docs/api-compatibility.md`. Updated whenever a new client (frontend version, hub, mobile) is introduced. Specifies which API version each client requires.

**Deprecation timeline:** Deprecated endpoints are announced in the changelog when marked, supported for the duration of the next minor release cycle, then removed in the following minor version. Minimum notice: 30 days.

### Architecture Principles
- Hardware abstraction layer remains the extension point for new backends
- REST API is the contract between frontend and backend â€” mobile app and multi-machine hub both consume it
- All features must work with the mock backend for development and testing
- Backend should be stateless where possible (state in SQLite, not in-memory dicts)

---

## 10. Operational Safety & Recovery

This section defines the runbook and recovery paths that must be documented and tested before v1.0 ships. These are not just docs â€” they are acceptance criteria for the setup experience.

### 10.1 Install & Service Runbook (per OS)

Each supported platform must have a documented one-page runbook covering:

| Step | Windows (desktop) | Windows (service) | Linux (systemd) |
|---|---|---|---|
| Install | Run `install_windows.ps1` | Run `install_windows_service.ps1` | Run `install_linux.sh` |
| Auto-start | Task Scheduler entry created | Windows Service registered | `systemctl enable drivechill` |
| Verify running | Tray icon visible | `sc query DriveChill` â†’ RUNNING | `systemctl status drivechill` |
| Open UI | Tray â†’ "Open Dashboard" | http://localhost:8085 | http://localhost:8085 |
| Verify BIOS fallback | Set fan to 60%. Exit from tray. Observe RPM in BIOS Hardware Monitor. | Set fan to 60%. Stop service. Observe RPM. | Set fan to 60%. `sudo systemctl stop drivechill`. Observe via `sensors`. |
| Uninstall | Run `uninstall_windows.ps1` | Run `uninstall_windows_service.ps1` | Run `uninstall_linux.sh` |

Runbooks are maintained in `docs/runbooks/` and linked from the README.

### 10.2 Backup & Restore

| Operation | Command | What It Includes |
|---|---|---|
| Backup | `drivechill --backup [--output path]` | All profiles, curves, settings, sensor labels, alert rules. Does NOT include raw sensor history (too large). |
| Restore JSON backup | `drivechill --restore <backup.json>` | Profiles, curves, settings, sensor labels, alert rules. Prompts before overwriting existing data. |
| Restore DB snapshot | `drivechill --restore-db <drivechill.db.bak-...>` | Full SQLite restore from an auto-backup snapshot. |
| Auto-backup before migration | Automatic | Full DB copy to `<db_path>.bak-<timestamp>` before every schema migration. |
| Export history | UI Settings page â†’ "Export CSV" | Raw sensor readings for selected time range. |

Backup files are plain JSON, human-readable, and can be manually edited or shared. Format is versioned (top-level `"backup_version"` field).

### 10.3 Safe Mode Behavior

See Section 4.3 (Safe Mode / Panic Mode table) for full specification. Summary:

- **Sensor failure > 3 reads:** fans go to 100%, UI shows alert banner.
- **Temp panic threshold crossed:** fans go to 100% immediately, held until temp drops below threshold minus 5Â°C.
- **DB error on startup:** use in-memory profile if available; otherwise force 100%, disable curve control, and notify user.
- **No profile configured:** apply "Balanced" preset, log that default was used.

### 10.4 Recovery Procedures

| Problem | Recovery |
|---|---|
| Profiles lost after update | Restore from auto-backup: `drivechill --restore-db drivechill.db.bak-<timestamp>` |
| Fans stuck at non-auto speed | Tray â†’ "Release Fan Control" sets all fans back to BIOS/auto immediately |
| DB corrupt | Delete `drivechill.db`. App recreates schema from scratch. Restore profiles from manual backup if available. |
| App won't start (port conflict) | Set `DRIVECHILL_PORT=<other>` in config. |
| Forgot to set password before LAN binding | App refuses to start with descriptive error: "Session auth required for non-localhost binding. Set DRIVECHILL_PASSWORD before starting." |

---

## 11. Appendix: Competitor Analysis Summary

### Fan Control (Rem0o) â€” GitHub, Free, Windows Only
**Strengths:** Most advanced curve system (mix, linear, custom functions), huge community (10k+ GitHub stars), graph-based fan-to-sensor mapping, hysteresis support, tray-only mode.
**Weaknesses:** Windows only, no remote access, no historical data, no web UI, steep learning curve for advanced features.

### NZXT CAM â€” Free, Windows Only
**Strengths:** Polished UI, integrated RGB control, game overlay with FPS counter, NZXT hardware auto-detection.
**Weaknesses:** NZXT hardware only, telemetry/privacy concerns, Windows only, no API, resource heavy (~200MB RAM).

### Corsair iCUE â€” Free, Windows Only
**Strengths:** Deep Corsair ecosystem integration (fans, AIO, RGB, peripherals), macro support, lighting sync.
**Weaknesses:** Corsair hardware only, very resource heavy (~300MB+ RAM), complex UI, no remote access, frequent updates/instability.

### Argus Monitor â€” Paid ($26), Windows Only
**Strengths:** HDD/SSD health monitoring (SMART), accurate fan control, lightweight, stable.
**Weaknesses:** Paid license, dated UI, Windows only, no remote access, small community.

### SpeedFan â€” Free, Windows Only (Abandoned)
**Strengths:** Was the go-to for years, lightweight, highly configurable.
**Weaknesses:** Abandoned (last update ~2015), doesn't support modern hardware, confusing UI, no longer maintained.

### MacsFanControl â€” Freemium, macOS + Windows
**Strengths:** Cross-platform (Mac focus), simple UI, reliable.
**Weaknesses:** Limited fan curve options (constant speed or sensor-based), pro features behind paywall, no Linux, no remote access.

### lm-sensors / fancontrol â€” Free, Linux Only
**Strengths:** Native Linux integration, rock-solid reliability, scriptable.
**Weaknesses:** CLI only, no GUI, complex configuration files, no real-time dashboard, no remote monitoring.

---

*DriveChill PRD v1.0 â€” 2026-02-25*


