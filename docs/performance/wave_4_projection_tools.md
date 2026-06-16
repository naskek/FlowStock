# Wave 4: Projection Rebuild, Check, Metadata, and Audit Tools

Wave 4 makes projections operationally safe. It does not introduce a universal orchestrator and does not make projections authoritative.

## Core invariants

1. `ledger` remains the source of truth for physical stock.
2. Closed documents are not edited.
3. Corrections happen only through explicit correction, storno, or controlled maintenance flows.
4. Read-models and projections are not source of truth.
5. Projections must be rebuildable and checkable from source tables.
6. Performance work must not change business logic.

## Scope

Wave 4 covers focused maintenance tooling for projections:

- rebuild commands;
- check commands;
- projection metadata;
- audit output;
- operator-safe diagnostics.

Wave 4 does not approve a broad background framework, a universal god-orchestrator, or production write automation.

## Tooling principles

- Build focused commands per projection or projection family.
- Make every command explicit: rebuild, check, report.
- Default to dry-run/reporting behavior where practical.
- Require clear output: source row counts, projection row counts, mismatches, timestamps, duration, and status.
- Keep write paths independent from projection tooling.
- Do not hide projection failures; report stale or inconsistent state.

## Task sequence

1. Define projection metadata fields needed for rebuild/check status.
2. Define command output format for rebuild and check reports.
3. Implement focused rebuild/check commands only after the projection they support has a defined source-of-truth query.
4. Add tests for idempotent rebuild, mismatch detection, and audit output.
5. Document operator workflow for running check before deploy and after rebuild.

## Forbidden changes

- No universal god-orchestrator.
- No automatic production repair without explicit maintenance flow.
- No business-logic changes.
- No production DB changes as part of planning.
- No schema/index DDL or migration work as immediate Wave 4 documentation output.
- No projection can be treated as source of truth.
- No command may edit closed documents directly.

## Required tests

| Category | Required coverage |
|---|---|
| Rebuild tests | Rebuild is deterministic and idempotent. |
| Check tests | Missing, extra, and mismatched projection rows are reported. |
| Metadata tests | Rebuild/check timestamps and status are updated as designed. |
| Audit tests | Reports include counts, mismatch summaries, and safe diagnostics. |
| Safety tests | Commands do not mutate source-of-truth facts except through explicitly approved maintenance flows. |

## Acceptance criteria

- Each projection has a focused rebuild command.
- Each projection has a focused check command.
- Check reports row counts, mismatch counts, mismatch samples, source timestamp range where available, projection timestamp, and command duration.
- Rebuild can be rerun safely.
- Operators can determine whether a projection is fresh, stale, missing, or inconsistent.
- No endpoint treats stale or missing projection state as authoritative for writes.
