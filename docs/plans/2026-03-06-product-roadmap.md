# DriveChill Product Roadmap

**Date:** 2026-03-06  
**Status:** Working Roadmap  
**Basis:** current branch state, audit findings, remaining v2.3 checklist, customer-value prioritization

---

## 1. Goal

This roadmap answers three questions:

1. What must ship before the current branch can be called release-complete?
2. What should be built next for the highest user value?
3. What should remain explicitly deferred so the project keeps momentum and quality?

This is a product roadmap, not a raw backlog dump.

---

## 2. Current Product Position

DriveChill already has strong product substance:

- local fan control with profiles, curves, and safety controls
- quiet hours
- temperature targets with PID controls
- drive monitoring and SMART polling
- webhooks, email, push notifications, and HTTP notification channels
- config export/import
- multi-machine groundwork
- substantial Python and C# test coverage

The product is now past the "prove the concept" stage. The remaining work is
mostly about:

- release hardening
- filling a few important control-model gaps
- expanding hardware and integration breadth carefully

---

## 3. Roadmap Structure

The roadmap is split into:

1. `Must Ship In Current Release`
2. `Next Release`
3. `Customer-Driven Wishlist`

Each item is prioritized by a mix of:

- customer value
- safety and reliability impact
- engineering risk
- whether it blocks clean release readiness

---

## 4. Must Ship In Current Release

These items should be completed before calling the current branch release-ready.

### 4.0 Status

| Item | Status | Note |
|------|--------|------|
| A. C# SMART trend delivery parity | ✅ DONE | `DriveMonitorService` calls `AlertService.InjectEvent` on every poll; `SmartTrendAlert` carries `ActualValue`/`Threshold`; 14 new C# tests |
| B. Import-path notification-channel validation | ✅ DONE | Save-time (controller) + send-time (service) URL validation in both backends; 6 Python + 14 C# SSRF tests |
| C. Windows-local Playwright fix | ✅ DONE | `cross-env` added to `devDependencies`; `playwright.config.ts` updated to cross-platform `cross-env` command |
| D. Python auth-helper cleanup | ✅ DONE | Stale `require_write_role` removed from `auth.py`; regression test confirms viewer API key cannot write |
| E. Startup safety profile | ✅ DONE | Both backends hold fans at 50% for 15s on startup; exits on profile load or timer; panic/release override; 14 new Python tests |
| F. Frontend E2E for top workflows | ✅ DONE | `quiet-hours.spec.ts`, extended `temperature-targets.spec.ts`, `fan-curves.spec.ts`, `settings.spec.ts` — all top workflows covered |

### 4.1 Release blockers

#### A. C# SMART trend delivery parity

**Why:** SMART trend alerts now appear in C# event history, but they still do
not fully participate in the outward delivery pipeline. That leaves a backend
parity gap in one of the product's higher-value monitoring features.

**Expected outcome:**
- SMART trend alerts create real alert events
- those events reach notification channels, webhooks, email, and push
- behavior matches Python expectations closely enough to describe as parity

#### B. Import-path notification-channel validation

**Why:** create/update paths now validate outbound URLs, but import paths still
allow persistence of configs that the API would reject.

**Expected outcome:**
- imported notification channel configs are validated consistently
- invalid imported destinations are rejected or explicitly skipped with a clear summary

#### C. Windows-local Playwright fix

**Why:** local Windows E2E is still broken. That slows development, reduces
confidence in UI flows, and makes regressions easier to miss.

**Expected outcome:**
- Playwright starts locally on Windows without manual env workarounds
- Linux CI remains unaffected

#### D. Python auth-helper cleanup

**Why:** stale auth code still sends the wrong signal during audits and future
maintenance even if the live request path is safe today.

**Expected outcome:**
- no misleading dead/stale auth dependency path remains
- viewer-role API-key write blocking is clearly enforced and test-backed
- note: do not assume a specific helper name from older reviews; inspect the
  current `backend/app/api/dependencies/auth.py` and clean up the actual stale
  or redundant path in the live file

### 4.2 Release hardening

#### E. Startup safety profile

**Why:** this is a small, high-value safety feature. Users care more about safe
startup behavior than about another integration checkbox.

**Expected outcome:**
- safe fixed fan speed applies during startup stabilization
- normal control resumes automatically
- panic and explicit release behaviors still win over startup safety

#### F. Frontend E2E for top workflows

**Why:** the product is now broad enough that route tests and unit tests are not
enough for confidence.

**Minimum flows to cover:**
- Quiet Hours CRUD
- PID target controls
- benchmark calibration auto-apply
- config export/import

### 4.3 Release output

If the items above are complete, the current release can reasonably be framed as:

- stable fan-control product
- strong Windows/local-first experience
- usable notification and drive-monitoring feature set
- reliable export/import

---

## 5. Next Release — Milestone B: Complete

**Status:** All Milestone B items shipped. Virtual sensors, load-based inputs,
and control transparency are implemented in both backends and the frontend.

### 5.1 Priority 1: Virtual Sensors — ✅ DONE

**Why it matters:**
- unlocks the most powerful fan-control workflows
- creates the abstraction needed for several later features
- directly addresses advanced-user demand without requiring hardware-specific work

**User value:**
- combine CPU + GPU + drive + case signals
- use `max`, `min`, `avg`, `weighted`, `delta`, and `moving_avg`
- build cooling behavior around real thermal intent instead of single raw sensors

**PM view:** this is the highest-leverage next feature.

### 5.2 Priority 2: Load-Based Fan Inputs — ✅ DONE

**Why it matters:**
- users do not always want temp-only control
- load-based response improves perceived responsiveness and noise behavior

**User value:**
- use CPU load, GPU load, and eventually power-like proxies as control inputs
- smooth fan behavior during bursty workloads
- reduce lag between workload spikes and cooling response

**PM view:** this should ship right after virtual sensors because it builds on
the same control model and makes the product feel more intelligent immediately.

### 5.3 Priority 3: Explainability / Control Transparency — ✅ DONE

**Why it matters:**
- advanced users trust the tool more when they can see why a fan changed speed
- support burden drops when the UI explains active rules and targets clearly

**Examples:**
- "Fan 2 at 64% because Virtual Sensor X = 58C and Target Y requested 64%"
- active profile source: manual / quiet hours / alert / startup safety
- visible current control mode and winning constraint

**PM view:** this is not flashy, but it materially improves user trust and reduces friction.

### 5.4 Priority 4: Linux write-path completion

**Why it matters:**
- broadens product legitimacy beyond Windows-first usage
- matters to homelab and enthusiast users

**Scope:**
- harden `hwmon` write path
- test with mock sysfs and at least one real hardware validation pass if possible

**PM view:** valuable, but only after core control-model work.

**Milestone assignment:** defer to Milestone C rather than Milestone B. It
should not compete with virtual sensors or load-based control work for the next
execution pass.

---

## 6. Customer-Driven Wishlist

These items are good product candidates, but I would not let them destabilize
the next release sequence.

### 6.1 MQTT / Home Assistant / Node-RED integration

**What customers will ask for:**
- publish alerts and telemetry into home automation and observability stacks
- trigger automations outside DriveChill

**PM decision:** defer until the current HTTP notification and telemetry model is
stable and well-understood.

### 6.2 USB controller breadth

**What customers will ask for:**
- direct support for Kraken, Corsair, Aquacomputer, and similar devices
- no ambiguity with duplicate hardware

**PM decision:** continue Python support and hardware hardening, but defer C#
parity until core control-model work is done.

### 6.3 Insights and reporting UX

**What customers will ask for:**
- heatmaps
- trend reports
- "optimize for noise" suggestions
- PDF exports

**PM decision:** these are useful, but they are product polish after the control
foundation is solid.

### 6.4 Multi-machine orchestration maturity

**What customers will ask for:**
- one dashboard controlling several systems
- stronger remote-state reliability
- remote profile automation

**PM decision:** strategically valuable, but do not prioritize ahead of local
control quality unless the target market shifts toward fleet-style usage.

**Current state note:** significant groundwork already exists in the codebase:
machine registration, remote snapshots, remote profile activation, and remote
fan settings proxy flows are already present. What is deferred is orchestration
maturity, not greenfield implementation.

---

## 7. What I Would Ask For As A Customer

If I were an active customer, these would be my top asks in order.

### 7.1 Reliability asks

- "I want startup fan behavior to be safe every time."
- "If an alert fires, I want it delivered no matter which backend I run."
- "Import/export must be trustworthy."

### 7.2 Control-power asks

- "Let me create virtual sensors."
- "Let me control fans by combined thermal/load signals, not just one temperature."
- "Show me why a fan is at its current speed."

### 7.3 Hardware asks

- "Give me working Linux fan-write support."
- "Support my USB AIO/controller without weird collisions."

### 7.4 Integration asks

- "Send alerts where I already live: Discord, Slack, ntfy, webhook, MQTT."
- "Expose data cleanly for Home Assistant / Node-RED."

---

## 8. PM Recommendation

If I were running the project, I would do this next:

1. Finish release blockers
2. Ship startup safety and Windows Playwright fix
3. Freeze the current release and cut a candidate
4. Start the next milestone with:
   - virtual sensors
   - load-based inputs
   - control transparency / explainability
5. Keep MQTT, deeper insights, and C# USB parity as explicit follow-on work

This keeps the project disciplined:

- finish what is already close
- ship something defensible
- then build the next genuinely high-value control layer

---

## 9. Proposed Milestones

### Milestone A: Release Completion — ✅ COMPLETE (2026-03-06)

Scope:
- C# SMART delivery parity — done
- import-path validation — done
- Playwright Windows fix — done
- auth-helper cleanup — done
- startup safety — done
- top-flow frontend E2E — done
- release docs/changelog closeout — done

Success metric:
- current branch can be called release-ready without qualification

### Milestone B: Control Model Upgrade — ✅ COMPLETE (2026-03-06)

Scope:
- virtual sensor CRUD — done (6 types: max, min, avg, weighted, delta, moving_avg)
- load-based inputs — done (cpu_load, gpu_load accepted in curve sensor picker)
- delta and moving-average support — done (EMA implementation, window_seconds)
- control explainability in UI/API — done (per-fan control_sources in WS + REST; badges in FanCurvesPage)

Success metric:
- advanced users can express most real control strategies without hacks

### Milestone C: Platform and Integration Expansion

Scope:
- Linux `hwmon` write hardening
- more complete `liquidctl` support
- MQTT
- first-pass insights/reporting polish

Success metric:
- broader hardware support and better ecosystem fit without weakening core reliability

---

## 10. Final Prioritization

If forced to choose only five things from here, I would choose:

1. C# SMART alert delivery parity
2. Windows Playwright fix
3. Startup safety profile
4. Virtual sensors
5. Load-based fan inputs

That is the best balance of:
- release readiness
- user trust
- product differentiation
- engineering leverage
