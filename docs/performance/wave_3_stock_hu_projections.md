# Wave 3: Stock and HU Balance Projections

Wave 3 introduces future projection support for `current_stock` and `current_hu_balance`. These projections are acceleration layers only. They must never replace `ledger` as the source of truth.

## Core invariants

1. `ledger` remains the source of truth for physical stock.
2. Closed documents are not edited.
3. Corrections happen only through explicit correction, storno, or controlled maintenance flows.
4. Read-models and projections are not source of truth.
5. Projections must be rebuildable and checkable from source tables.
6. Performance work must not change business logic.

## Scope

Wave 3 covers only future balance projections:

| Projection | Purpose | Source of truth |
|---|---|---|
| `current_stock` | item/location physical balance for fast reads | `ledger` |
| `current_hu_balance` | item/location/HU physical balance for fast reads | `ledger` |

Wave 3 does not include order progress, warehouse board projection, ready-HU projection, or a general projection orchestrator.

## Required preconditions

Do not add operational projection tables until all of these exist:

- full rebuild from `ledger`;
- consistency check against direct `ledger` aggregation;
- audit output with row counts and mismatch counts;
- clear metadata for rebuild/check time and projection version;
- regression tests proving write-path ledger semantics are unchanged.

## Task sequence

1. Define read requirements for stock and HU screens that currently aggregate `ledger`.
2. Define rebuild semantics from direct `ledger` aggregation.
3. Define check semantics that compare projection rows with direct aggregation.
4. Only after rebuild/check design is accepted, implement projection storage in a future code task.
5. Update read endpoints to use projections only when they are present and verified; write paths still recheck source facts.

## Forbidden changes

- No projection without rebuild/check.
- No business-logic changes for stock, HU, production, outbound, reservation, or marking.
- No production DB changes as part of planning.
- No schema/index DDL or migration work as immediate Wave 3 documentation output.
- No direct manual edits to balances.
- No endpoint may use projection rows as authoritative for writes.

## Required tests

| Category | Required coverage |
|---|---|
| Rebuild tests | Projection rebuilt from `ledger` matches expected balances. |
| Check tests | Mismatches between projection and direct `ledger` aggregation are detected. |
| Idempotency tests | Rebuilding twice produces the same projection state. |
| Ledger regression tests | Existing close/fill/outbound writes produce the same ledger rows as before. |
| Read fallback tests | Missing or stale projection does not create incorrect write behavior. |

## Acceptance criteria

- `current_stock` rebuild equals direct `ledger` aggregation by item/location.
- `current_hu_balance` rebuild equals direct `ledger` aggregation by item/location/HU.
- Check tooling reports row counts, missing rows, extra rows, quantity mismatches, and timestamps.
- Projection freshness is visible to operators or diagnostics.
- Read performance improves without changing stock semantics.
- No production use is approved until rebuild/check tooling is available.
