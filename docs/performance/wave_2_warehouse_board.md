# Wave 2: Warehouse Board Read-Model

Wave 2 replaces `/api/planner/warehouse-board/state` service composition with a dedicated batch read-model. The goal is to make the board predictable and fast without changing planning semantics.

## Core invariants

1. `ledger` remains the source of truth for physical stock.
2. Closed documents are not edited.
3. Corrections happen only through explicit correction, storno, or controlled maintenance flows.
4. Read-models and projections are not source of truth.
5. Projections must be rebuildable and checkable from source tables.
6. Performance work must not change business logic.

## Scope

Only this endpoint is in Wave 2:

| Endpoint | Baseline from master plan | Initial target | Later target |
|---|---:|---:|---:|
| `/api/planner/warehouse-board/state` | 5,236 ms | < 500 ms | < 200 ms |

## Batch read-model approach

Build the board response from batched read-model inputs:

- order rows and effective display status;
- order receipt/production need flags required by the board;
- production pallet summaries by order/doc;
- active pallet plan rows by order/doc;
- HU stock rows and HU order context;
- locations.

The first Wave 2 implementation should use SQL read-models and batch methods. It should not require projection tables. Projection-backed board state belongs to later work only after rebuild/check tooling exists.

## Response compatibility

- Preserve the current `/api/planner/warehouse-board/state` response shape.
- Keep existing field names and meanings for `orders`, `hu_stock`, `pallet_plans`, and `locations`.
- Additive fields are allowed only if existing clients can ignore them.
- Do not remove or reinterpret existing statuses, flags, or display fields.

## Task sequence

1. Capture current response contract with tests using representative board data.
2. Introduce a dedicated board read-model that loads all required source data in batches.
3. Replace per-order calls such as `GetDocsByOrder`, `GetProductionPalletsByDoc`, and `GetOrderReceiptRemaining` in board list assembly.
4. Compare old and new board outputs in tests for the same seeded data.
5. Add no-N+1 guard coverage for the board endpoint.
6. Measure before/after latency through localhost/nginx.

## Forbidden changes

- No business-logic changes for planning, pallet adoption, filling, order eligibility, or HU ownership.
- No production DB changes.
- No schema/index DDL or migration work as part of Wave 2.
- No projection table requirement in the initial Wave 2 implementation.
- No write path may trust the board read-model without rechecking source tables.
- No partial response shape rewrite that forces WPF/Web/TSD client changes.

## Required tests

| Category | Required coverage |
|---|---|
| Contract tests | Board payload shape and field meanings remain compatible. |
| Equivalence tests | New batch read-model matches old board output for seeded scenarios. |
| No-N+1 guard tests | Board assembly does not call per-order/per-doc detail methods. |
| Ledger regression tests | Board reads do not write `ledger` or mutate source facts. |
| Perf acceptance tests | Board endpoint meets < 500 ms initial target. |

## Acceptance criteria

- `/api/planner/warehouse-board/state` returns in < 500 ms through localhost/nginx after Wave 2.
- Existing clients continue to parse and display the board response.
- The endpoint uses bounded batch reads instead of per-row service composition.
- No closed document, ledger row, order status, reservation, or pallet fact is changed by this read path.
- Later optimization work may target < 200 ms using projections only after rebuild/check tooling exists.
