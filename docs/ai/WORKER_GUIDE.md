# FlowStock AI worker guide

Before changing code, always read:

- docs/ai/INVARIANTS.md
- docs/ai/MASTER_PLAN.md
- docs/ai/STATUS.md
- docs/ai/WORKER_GUIDE.md

## Rules
- Do not rely on chat memory.
- Do not change code before auditing the relevant flow.
- First output:
  1. files inspected
  2. current behavior
  3. suspected bug/risk
  4. proposed changes
  5. tests to run/add
- Do not edit closed-document logic, ledger logic, marking logic, or order status transitions without checking INVARIANTS.md.
- If a proposed change conflicts with INVARIANTS.md, stop and explain the conflict.
- Keep changes focused on the current subplan.
- Do not mix unrelated refactoring with bug fixes.

## After implementation, output
1. files changed
2. behavior changed
3. tests run
4. remaining risks
5. whether STATUS.md / MASTER_PLAN.md should be updated

## Preferred workflow
1. Audit.
2. Plan.
3. Implement.
4. Test.
5. Report.
6. Update docs if needed.
