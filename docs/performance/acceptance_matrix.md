# Performance Acceptance Matrix

This matrix maps master plan findings to execution waves. Targets are production-like local targets through localhost/nginx unless a later implementation task defines a stricter environment.

## Core invariants

1. `ledger` remains the source of truth for physical stock.
2. Closed documents are not edited.
3. Corrections happen only through explicit correction, storno, or controlled maintenance flows.
4. Read-models and projections are not source of truth.
5. Projections must be rebuildable and checkable from source tables.
6. Performance work must not change business logic.

## Matrix

| Area / endpoint | Baseline | Target | Wave | Required tests | Deploy risk |
|---|---:|---:|---|---|---|
| `/api/requests/summary` | 1,921 ms | < 50 ms | Wave 1 | Contract, no-N+1 guard, ledger no-write, perf smoke | Low if response shape is unchanged |
| `/api/docs` | 975 ms | < 200 ms | Wave 1 | Contract, batch summary SQL/read-model, no-N+1 guard, perf smoke | Medium due to WPF list compatibility |
| `/api/orders?limit=50&offset=0` | 1,356 ms | < 200 ms | Wave 1 | Contract, pagination/search, batch pallet summary, no-N+1 guard, perf smoke | Medium due to order list UI behavior |
| `/api/orders` | 3,032 ms | Prefer paged < 200 ms | Wave 1 | Contract, compatibility for unpaged callers, no-N+1 guard | Medium; unpaged behavior must be handled deliberately |
| `/api/tsd/outbound/orders` | Not measured in master plan / optimized path | No regression | Wave 1 | TSD contract, optimized-store guard, perf smoke | Low if optimized path remains |
| `/api/orders/hu-bindings/ready` | 1,880 ms | Improve without semantic change | Wave 1 | Ready-HU contract, candidate eligibility regression, ledger no-write | Medium due to eligibility complexity |
| `/api/planner/warehouse-board/state` | 5,236 ms | < 500 ms initial, < 200 ms later | Wave 2 | Board contract, old/new equivalence, no-N+1 guard, perf smoke | High due to broad response and planner workflow |
| `current_stock` projection | Not present | Rebuild/check matches `ledger` | Wave 3 | Rebuild, check, idempotency, ledger regression | High until rebuild/check tooling exists |
| `current_hu_balance` projection | Not present | Rebuild/check matches `ledger` | Wave 3 | Rebuild, check, idempotency, ledger regression | High until rebuild/check tooling exists |
| Projection rebuild tools | Not present | Idempotent rebuild with audit output | Wave 4 | Rebuild, metadata, audit, safety tests | Medium; maintenance-only when explicit |
| Projection check tools | Not present | Detect missing/extra/mismatched rows | Wave 4 | Mismatch detection, report shape, safety tests | Medium; must be operator-safe |

## Deployment gates

- No production deploy without backup and explicit manual confirmation.
- No schema/index DDL or migration work is approved by these docs as immediate work.
- No endpoint migration is complete until contract tests and no-N+1/perf guards pass.
- No projection is operational until rebuild and check tooling exists.
- No performance fix may change ledger semantics, closed-document behavior, correction flow, or business rules.
