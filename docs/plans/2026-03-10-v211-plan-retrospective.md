# v2.1.1 Plan Retrospective

**Date:** 2026-03-10
**Plan evaluated:** "DriveChill v2.1.1 Must-Ship Plan"
**Actual delivery:** Commits `03687ac`–`4f3af75` (Session 7)

---

## Overall Grade: B+

The plan achieved its primary goal — a tight stabilization release that didn't
mix in feature work. Scope discipline was maintained. All five phases shipped.
The deferred list was respected.

Where it falls short is in specificity: validation criteria were vague, a
critical security fix was unnamed, and the pre-existing dirty-tree problem
wasn't anticipated.

---

## Phase Grades

### Phase 1: Analytics Parity Fix — A

Plan was precise, change was narrow, tests were specified. The actual
implementation matches exactly: `GetRegression` now requires both `start &&
end`, with 6 `ResolveRange` tests covering all edge cases.

No notes.

### Phase 2: Release Workflow Fixes — A-

All three items shipped correctly:
- Tag filter: `v[0-9]*.[0-9]*.[0-9]*` (stricter than the plan's `v*` — good
  deviation)
- Docker tag: `type=raw,value=${{ github.ref_name }}` added
- Frontend memory: `NODE_OPTIONS=--max-old-space-size=4096`

**Deduction:** The plan said "e.g. `v*`" which would have been too broad (would
match `vacation`, `vendor/`, etc.). The implementer chose a better pattern, but
the plan should have specified the correct glob upfront rather than leaving it
to improvisation.

### Phase 3: Windows Updater Hardening — B+

All changes shipped. Fallback chain, `-Artifact` parameter, script discovery
all implemented as specified.

**Deduction:** The plan didn't mention SEC-1 (semver regex validation of GitHub
version strings before passing to PowerShell). This was a security fix that
shipped in the same commit (`03687ac`) but wasn't named in the plan. A
stabilization plan that touches the update pipeline should explicitly call out
input validation as a named work item, not leave it as an implicit side effect.

### Phase 4: Docs and Versioning — A-

`docs/updating.md` created with all four sections (Python, C#, Docker,
rollback). CHANGELOG and AUDIT updated.

**Deduction:** The plan says "Run bump_version.py for 2.1.1 only after
code/tests are final" but doesn't specify what version sources exist or how to
verify they all agree. In practice, `AppSettings.cs`, `package.json`,
`pyproject.toml` (if present), and `CHANGELOG.md` all carry version info.

### Phase 5: Validation — C+

Backend tests passed (409 Python, 16 C#). But the validation phase has real
problems:

1. **"Manual checks" with no recording mechanism.** The plan lists "Trigger
   update check from Settings" and "POST /api/update/apply starts updater" but
   doesn't define pass criteria or where to record results. There's no audit
   evidence that manual validation was performed.

2. **"Release workflow dry-run"** was never documented as executed. The plan
   says "test tag or RC tag triggers workflow" — no record of this happening.

3. **No E2E mention.** The plan doesn't include Playwright tests in the
   validation phase. At v2.1.1, E2E tests existed (~20 specs). They should have
   been a required check.

4. **No rollback plan.** What happens if the release breaks production? The
   `docs/updating.md` covers end-user rollback, but the plan has no developer
   rollback procedure (revert commits, delete tag, re-release).

### Commit Strategy — A

Plan called for 5 focused commits. Actual delivery used 8 (splitting
pre-existing workstreams from the stabilization fixes). Better granularity than
planned. The spirit was followed.

### Deferred Items — A

All five deferred items stayed deferred. Each shipped in the correct later
version:
- Dangerous-curve gate → v2.1.2
- Fan/profile endpoints → v2.1.2
- Drive provider abstraction → v2.3.0-rc
- PID controller → v2.2.0
- Temp Targets E2E → v2.3.0-rc

No scope creep occurred.

---

## What the Plan Got Right

1. **Scope boundary was clear and held.** The explicit deferred list prevented
   the stabilization patch from becoming a feature release.
2. **Phase ordering was correct.** Analytics fix first (smallest, most
   verifiable), then release workflow (infrastructure), then updater (riskiest),
   then docs (can't write until code is final), then validation (last).
3. **Commit strategy worked.** Separate commits made the code reviewable and
   revertable.
4. **Assumptions section prevented misunderstandings.** "Docker update remains
   manual" and "existing dirty-tree changes should not be folded in" were useful
   guardrails.

## What the Plan Got Wrong

1. **SEC-1 was invisible.** A security fix (semver input validation) shipped
   unnamed in the plan. Plans that touch external input boundaries should always
   name the validation work explicitly.
2. **Validation was a wishlist, not a protocol.** No pass/fail definitions, no
   recording location, no E2E requirement, no rollback procedure.
3. **Dirty-tree triage was unplanned.** Session 7 started with 81 modified +
   ~30 untracked files. The plan assumed a clean starting state. A Phase 0
   ("triage and commit pre-existing work") should have been included.
4. **Tag glob was under-specified.** The plan said "e.g. `v*`" — an example is
   not a specification. The implementer fixed it, but the plan should have
   provided the correct pattern.

---

## Lessons for Future Plans

1. **Name every security-relevant change.** If the plan touches auth, update,
   or external input paths, list the input validation work as a named item.
2. **Define validation as a checklist with pass/fail.** Each check needs: what
   command to run, what output means pass, what output means fail, where to
   record the result.
3. **Include a Phase 0 for dirty-tree triage.** If the repo might have
   uncommitted work, the plan should acknowledge and sequence it.
4. **Specify exact values, not examples.** "e.g." in a release plan is a code
   smell. Replace with the actual value.
5. **Include rollback procedure.** Even a one-line "revert to previous tag and
   re-release" is better than nothing.
6. **Include E2E in validation.** If E2E tests exist, they're a required gate.
   The v2.3.0-rc E2E regression (7 failures discovered on rerun) shows what
   happens when E2E isn't treated as a hard gate.
