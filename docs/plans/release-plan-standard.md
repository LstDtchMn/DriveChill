# DriveChill Release Plan Standard

**Created:** 2026-03-10
**Source:** v2.1.1 plan retrospective + external review feedback

This document defines the required structure for any future release plan. It
exists because the v2.1.1 plan, while successful in outcome, had process gaps
that would have caused problems at scale: unnamed security work, unrecorded
validation, no rollback procedure, no dirty-tree triage.

---

## 1. Required Sections

Every release plan must contain all of the following sections. A plan missing
any section is incomplete and should not be executed.

### 1.1 Scope Declaration

State what the release is and what it is not.

```
Type: [stabilization | feature | security patch]
Version: [exact version string]
Branch: [exact branch name]
Base commit: [SHA]
```

One sentence describing the release goal. No more.

### 1.2 Must-Ship Items

Numbered list of items that must be in the release. Each item needs:

- A unique ID (e.g., `FIX-1`, `FEAT-3`)
- One-sentence description of the change
- Target file(s)
- Acceptance criteria (what "done" means, testably)

### 1.3 Security Items

**Mandatory section even if empty.** If the release touches any of these
boundaries, list the validation work explicitly:

- External input paths (API params, file uploads, webhook payloads)
- Authentication or authorization logic
- URL handling (SSRF, redirect, scheme validation)
- Subprocess invocation (command injection, argument sanitization)
- Cryptographic operations (key handling, password storage)

If none apply, write: "No security-relevant changes in this release."

Do not bury security work inside other items. A semver regex that validates
user input before passing it to PowerShell is a security item, not a side
effect of "updater hardening."

### 1.4 Explicitly Deferred Items

Numbered list of items that are valid work but not in this release. Each needs:

- One-sentence description
- Reason for deferral
- Target release (if known)

This list exists to prevent scope creep. If someone asks "why isn't X in this
release," the answer is here.

### 1.5 Phase 0: Pre-Existing State Triage

**Mandatory section.** Before any plan work begins, assess:

- Is the working tree clean? If not, what uncommitted work exists?
- Are there untracked files that need to be committed or gitignored?
- Is the test baseline green? Record exact counts.

If the tree is dirty, the plan must include explicit steps to triage and commit
(or stash) pre-existing work before starting plan items. Do not fold unrelated
changes into release commits.

If the tree is clean, write: "Working tree clean at plan start. Test baseline:
[counts]."

### 1.6 Phases (Implementation)

Numbered phases with:

- Goal (one sentence)
- Changes (specific files and what changes in each)
- Exit criteria (testable — command to run, expected output)

Phases must be ordered by dependency. Do not parallelize phases unless they are
genuinely independent.

**Specify exact values, not examples.** "Use a valid Actions glob, e.g. `v*`"
is not acceptable. "Change tag filter to `v[0-9]*.[0-9]*.[0-9]*`" is.

### 1.7 Validation Protocol

**Mandatory section.** This is a checklist, not a wishlist. Each item needs:

| Check | Command | Pass | Fail | Record |
|-------|---------|------|------|--------|
| Python tests | `cd backend && python -m pytest tests/ -q` | 537+ passed, 0 failed | Any failure | Paste summary into release checklist |
| C# tests | `cd backend-cs && dotnet test Tests/DriveChill.Tests.csproj --nologo -v q` | 205+ passed, 0 failed | Any failure | Paste summary |
| C# build | `cd backend-cs && dotnet build` | 0 warnings, 0 errors | Any warning or error | Paste summary |
| Frontend build | `cd frontend && npm run build` | Exit 0 | Any error | Paste summary |
| E2E tests | `cd frontend && npx playwright test --reporter=list` | 0 failures | Any failure | Paste full output |

If the plan includes manual checks (e.g., "trigger update from Settings"),
each manual check must have:

- Precondition (what state the system should be in)
- Action (what to do)
- Expected observation (what you should see)
- Failure observation (what indicates the check failed)
- Where to record the result

### 1.8 Commit Strategy

Specify:

- How many commits (or commit grouping strategy)
- Naming convention for commit messages
- Whether to squash, rebase, or merge

"Do not make one giant catch-all commit" is necessary but not sufficient. State
the grouping principle.

### 1.9 Rollback Procedure

**Mandatory section.** Answer:

- If the release breaks production, what is the immediate rollback action?
- If a tag was pushed, how is it removed or superseded?
- If Docker images were published, what is the recovery path?
- If database migrations ran, are they reversible?

Even a simple release needs: "Revert to [previous tag]. If migrations ran,
[statement about reversibility]."

### 1.10 Assumptions

List assumptions that, if violated, invalidate the plan. Examples:

- "The 7 failures are test-side issues, not product bugs."
- "Docker update remains manual in this release."
- "The mock backend provides sufficient data for E2E."

If an assumption is disproven during execution, stop and reassess the plan.

---

## 2. Plan Quality Checks

Before executing a plan, verify:

| Check | Pass |
|-------|------|
| Every must-ship item has acceptance criteria | Yes |
| Security section exists and is non-empty or explicitly "none" | Yes |
| Phase 0 addresses working tree state | Yes |
| No phase uses "e.g." or "for example" for a value that will be in code | Yes |
| Validation protocol has command + pass + fail for every check | Yes |
| Rollback procedure exists | Yes |
| Deferred list exists | Yes |
| Commit strategy is specified | Yes |

---

## 3. Plan vs Audit Separation

A release plan answers: "What will we do?"
A release audit answers: "Did we do it, and is the code correct?"

Do not mix these in the same document. The plan is written before work starts.
The audit is written after work is done. They reference each other but serve
different audiences and different moments in time.

When reviewing a completed plan:

- **Plan quality** = was the plan well-specified? Could someone execute it
  without guessing?
- **Plan fulfillment** = did the implementation match the plan's items?
- **Code correctness** = is the shipped code actually correct?

These are three separate evaluations. A plan can be well-fulfilled but poorly
specified (v2.1.1). A plan can be well-specified but poorly fulfilled. Keep
the evaluations distinct.

---

## 4. Addressing Prior Review Feedback

When a plan or its implementation receives external review:

1. If the feedback identifies a bug that has since been fixed, state: "This
   feedback was valid when written. The defect was fixed in [commit]. The
   feedback is no longer applicable to the current tree."

2. If the feedback identifies a current defect, state: "This feedback
   identifies a current defect. [action taken or planned]."

3. If the feedback is about process, not code, state: "This feedback is about
   planning/process quality, not code correctness. [what changed in the
   process]."

Do not conflate "the code is now correct" with "the feedback was wrong." The
feedback may have been correct at the time and prompted the fix.
