# Claude Full Roadmap Prompt

Paste the following prompt into Claude.

---

Execute the full roadmap in `docs/plans/2026-03-06-claude-build-roadmap.md` from top to bottom.

Requirements:

1. Treat `docs/plans/2026-03-06-claude-build-roadmap.md` as the execution source of truth.
2. Treat `docs/plans/2026-03-06-v2.3-remaining-implementation-checklist.md` as milestone/context support.
3. Update the status tracking in Section 3.1 of the Claude roadmap as you progress.
4. Complete all remaining items in the roadmap, not just Milestone A.
5. Do not stop after a partial implementation if the next item is feasible in the same session.
6. Do not prompt for confirmation between roadmap items.
7. Only stop and ask for input if you hit a real blocker that requires:
   - a product-policy decision
   - an architecture change larger than the scoped milestone
   - hardware access you cannot simulate meaningfully
   - a breaking API/UI contract change

Execution rules:

- Verify runtime behavior, not only controller/model code.
- Add regression tests in the same pass as each fix or feature.
- Reuse existing helpers and infrastructure where possible.
- Do not widen scope into MQTT, PDF reporting, plugin concepts, or broad hardware expansion outside the roadmap.
- Do not mark `REVIEW REQUIRED` items as `DONE` from prior summary text; audit the live code path first.
- If an item is already complete after re-audit, update its status to `DONE` and move on without asking.
- If an item is partially complete, finish it and then update the status.
- If an item is blocked, mark it `BLOCKED`, explain why, and continue with later items only if the roadmap allows it.

Milestone policy:

- Finish Milestone A fully before starting Milestone B.
- When Milestone A is complete, continue directly into Milestone B.
- Do not stop after Milestone A unless blocked.
- For Milestone B, complete B1, then B2, then B3 in order.

Validation policy:

- After every substantial change set, run the smallest relevant targeted tests first.
- After each milestone, run the milestone-level validation commands from the roadmap.
- At the end, run a final validation pass covering Python, C#, and frontend as specified in the roadmap.
- If a test fails, fix the issue and re-run until green or until a real blocker is found.

Documentation policy:

- Update these docs as work completes:
  - `CHANGELOG.md`
  - `AUDIT.md`
  - `docs/plans/2026-03-06-v2.3-remaining-implementation-checklist.md`
  - `docs/plans/2026-03-06-product-roadmap.md`
  - `docs/plans/2026-03-06-claude-build-roadmap.md`
- Keep status fields accurate.
- Record any intentionally deferred work explicitly.

Definition of completion:

- All roadmap items that are feasible without external hardware or policy input are implemented, tested, and documented.
- All validation commands that can run in the current environment have been run.
- You have double-checked your own work automatically:
  - re-read the touched runtime paths
  - confirm tests cover the intended behavior
  - confirm docs match the final state
  - confirm no `REVIEW REQUIRED` item remains incorrectly marked

Final response format:

1. Completed items
2. Validation run
3. Remaining blocked items, if any
4. Exact files changed

Do the work now and continue through the roadmap without prompting unless a real blocker forces escalation.

---
