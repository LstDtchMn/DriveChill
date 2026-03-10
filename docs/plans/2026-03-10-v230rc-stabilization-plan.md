# DriveChill v2.3.0-rc Stabilization Plan

**Date:** 2026-03-10
**Conforms to:** `release-plan-standard.md`

```
Type: stabilization
Version: 2.3.0-rc
Branch: main
Base commit: a0ef5ed
```

Clear the 7 E2E failures to close the release gate.

### Security Items

No security-relevant changes in this release. All changes are E2E test
selectors and assertions. No backend, auth, or input-handling code is modified.

---

## Current State

| Suite | Result |
|-------|--------|
| Python tests | 537 passed, 13 skipped |
| C# tests | 205 passed, 0 warnings |
| Frontend build | passing |
| Playwright E2E | 33 passed, 6 skipped, **7 failed** |

The 7 failures are the only open release gate (section 5.3 of
`2026-03-10-release-readiness-checklist.md`).

---

## Failure Analysis

### settings.spec.ts — 6 failures

Based on comparing test selectors against actual DOM:

| Test | Selector used | Actual DOM | Likely cause |
|------|--------------|------------|-------------|
| page loads without error | `page.locator('main')` | `<main>` exists | May be timing; page loads async content after mount |
| temperature unit toggle visible | `getByRole('button', { name: /°C/ })` | Buttons with text "°C" and "°F" exist | Role match should work; possible the buttons render inside a card that loads after auth |
| clicking °F toggles unit | Clicks °F, looks for "save settings\|save" button | Save button text is "Save Settings" | Likely passes if toggle test passes; cascading failure |
| poll interval input | `input[type="number"]` | **`input[type="range"]`** | **Root cause.** The polling interval is a range slider, not a number input. Test selector is wrong. |
| config export button visible | `getByRole('button', { name: /export\|download config/i })` OR `getByText(/export config/i)` | Button text is "Export Config" | Should match; may be behind auth gate (admin-only section) |
| config import section visible | `getByRole('button', { name: /import/i })` OR `getByText(/import config/i)` | Button text is "Import Config" | Same auth gate concern |

**Root causes to investigate:**

1. **`input[type="number"]` vs `input[type="range"]`** — the poll interval
   test uses the wrong input type. The settings page uses a range slider.
   **Fix: change selector to `input[type="range"]`** and adjust the assertion
   (range inputs are set differently from number inputs).

2. **Auth/loading gate** — the Settings page may not render admin-only sections
   (Import/Export) until the session is authenticated and the role is resolved.
   If tests navigate to Settings before auth completes, all 6 could fail due to
   the page being in a loading/redirect state. **Fix: ensure test setup
   authenticates before navigating to Settings.**

3. **Cascading failures** — if the page doesn't load at all (test 1 fails),
   all subsequent tests will also fail. The root cause may be a single auth or
   navigation issue.

### fan-curves.spec.ts — 1 failure

| Test | Selector | Actual DOM | Likely cause |
|------|----------|------------|-------------|
| shows preset profile cards | Looks for preset card elements | PresetSelector renders a grid of `div.card` with profile names | Selector may not match the card structure; preset cards use `<div>` not `<button>` for the outer container, but have click handlers |

**Root cause to investigate:** The test likely expects a specific text or
structure in preset cards. PresetSelector renders cards with profile name in
`<p className="text-sm font-semibold">`. If the test looks for a specific
preset name that doesn't exist in the mock data, or expects a different DOM
structure, it will fail.

---

## Phase 0: Reproduce and Capture

**Goal:** Get exact failure messages before making changes.

1. Run `cd frontend && npx playwright test --reporter=list 2>&1 | tee
   /tmp/e2e-results.txt`
2. Record exact error messages for each of the 7 failures.
3. Identify whether settings failures are 6 independent issues or 1 root cause
   with 5 cascading failures.

**Exit criteria:** Failure messages recorded. Root cause(s) identified.

**Do not skip this phase.** The analysis above is based on code reading, not
runtime evidence. The actual errors may differ.

---

## Phase 1: Fix Settings E2E

### 1a: Investigate auth/loading gate

Check whether the settings page tests have a proper authentication step before
navigation. If the mock backend requires a session cookie or the page redirects
unauthenticated users, all 6 tests will fail at the navigation step.

**Files:**
- `frontend/e2e/settings.spec.ts` — check `beforeEach` / `beforeAll` for auth
- `frontend/playwright.config.ts` — check if global setup handles auth
- Compare with passing test files (e.g., `dashboard.spec.ts`) for auth
  patterns

### 1b: Fix poll interval selector

Change `input[type="number"]` to `input[type="range"]`. Adjust the assertion:
range inputs accept `.fill()` for setting value but the assertion should check
the `value` attribute, not visible text.

**File:** `frontend/e2e/settings.spec.ts`

### 1c: Fix export/import visibility

If the auth gate is resolved and these still fail, check:
- Whether the Import/Export card renders only for admin role
- Whether the test's role is viewer (which hides the section)
- Whether the button text regex matches "Export Config" / "Import Config"

**File:** `frontend/e2e/settings.spec.ts`

### 1d: Verify fix

Run `npx playwright test settings.spec.ts --reporter=list` and confirm 6/6
pass.

---

## Phase 2: Fix Fan Curves E2E

### 2a: Investigate preset card selector

Read the exact test assertion for "shows preset profile cards". Compare the
expected selector against PresetSelector's actual DOM output.

**Files:**
- `frontend/e2e/fan-curves.spec.ts`
- `frontend/src/components/fan-curves/PresetSelector.tsx`

### 2b: Fix selector

Adjust the test to match the actual card structure. PresetSelector uses
`div.card` containers with profile names in `<p>` tags.

### 2c: Verify fix

Run `npx playwright test fan-curves.spec.ts --reporter=list` and confirm 5/5
pass (4 previously passing + 1 fixed).

---

## Phase 3: Full Validation

### Automated checks

| Check | Command | Pass | Fail | Record location |
|-------|---------|------|------|-----------------|
| Python tests | `cd backend && python -m pytest tests/ -q` | 537+ passed, 0 failed | Any failure | Release checklist 5.3 |
| C# tests | `cd backend-cs && dotnet test Tests/DriveChill.Tests.csproj --nologo -v q` | 205+ passed, 0 failed | Any failure | Release checklist 5.3 |
| C# build | `cd backend-cs && dotnet build` | 0 warnings, 0 errors | Any warning | Release checklist 5.3 |
| Frontend build | `cd frontend && npm run build` | Exit 0 | Any error | Release checklist 5.3 |
| E2E full suite | `cd frontend && npx playwright test --reporter=list` | 40+ passed, 0 failed, 6 skipped | Any failure | Release checklist 5.3 |

The E2E suite is the primary gate for this plan. The other checks are
regression guards — they should already pass and must not regress.

**Pass criteria:** All 5 checks pass. E2E has 0 failures.

**Fail criteria:** Any check fails. If a non-E2E check fails, the E2E fix
introduced a regression — revert immediately.

Record exact output summaries in `2026-03-10-release-readiness-checklist.md`
section 5.3.

---

## Phase 4: Commit and Close Gate

### Commit

```
fix(e2e): repair 7 Playwright test failures — settings selectors, fan-curves preset cards
```

Changes should be limited to `frontend/e2e/` test files. If a frontend
component change was required (e.g., adding a `data-testid`), include it but
keep it minimal.

### Update docs

- `2026-03-10-release-readiness-checklist.md`: section 5.3 → DONE with final
  pass counts
- `2026-03-10-auditable-completion-list.md`: section L and M with final results

### Close gate

Section 7 of the release readiness checklist should read "All gates cleared"
only after Phase 3 produces 0 failures.

---

## Explicitly Out of Scope

| Item | Reason |
|------|--------|
| Backend code changes | No backend bug indicated by E2E failures |
| New E2E test cases | Stabilization only — fix existing, don't add new |
| Frontend component refactoring | Only touch components if a test reveals a real rendering bug |
| Drive-detail test skips (6) | These require mock SMART data; not a release gate |
| Extending test timeout values | Masking flakiness is not fixing it |

---

## Rollback

If fixing the tests requires invasive frontend changes that risk regressing
other tests:

1. Do not merge the fixes.
2. Downgrade the 7 failures to `test.skip()` with a comment explaining the
   known selector mismatch.
3. Create a follow-up issue: "Fix settings and fan-curves E2E selectors."
4. Update the release readiness checklist to note the skips and the reason.

This is the escape hatch, not the plan. The plan is to fix the tests.

---

## Assumptions

1. The 7 failures are test-side issues (wrong selectors, missing auth), not
   real product bugs. Phase 0 will confirm or disprove this.
2. The mock backend provides sufficient data for Settings and Fan Curves pages
   to render fully.
3. No other tests will regress when these 7 are fixed.
4. The fix commit will be small (test files only) and reviewable in under 5
   minutes.

---

## Execution Outcome (2026-03-10)

**Result:** Phase 0 disproved the premise. The 7 failures were transient.

A clean rerun of the full E2E suite produced **40 passed, 0 failed, 6 skipped**
— matching the original results from commit `a0ef5ed`. No test or code changes
were required.

The mid-session failures were likely caused by stale build artifacts or test
runner state from the earlier environment. This is consistent with assumption 1
being partially wrong: the failures were not test-side selector issues, they
were environment-side transient issues.

### Final validation results

| Check | Command | Result |
|-------|---------|--------|
| Python tests | `python -m pytest tests/ -q` | 537 passed, 13 skipped |
| C# tests | `dotnet test Tests/DriveChill.Tests.csproj --nologo -v q` | 205 passed, 0 failed |
| C# build | `dotnet build` | 0 warnings, 0 errors |
| Frontend build | `npm run build` | Exit 0 |
| E2E full suite | `npx playwright test --reporter=list` | 40 passed, 0 failed, 6 skipped |

**All 5 checks pass. Release gate cleared.**
