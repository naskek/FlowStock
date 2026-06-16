# Wave 1: List Endpoints and Summary Read-Models

Wave 1 removes obvious full-model builds and per-row detail reads from low-risk list and summary endpoints. It must not change FlowStock business behavior.

## Core invariants

1. `ledger` remains the source of truth for physical stock.
2. Closed documents are not edited.
3. Corrections happen only through explicit correction, storno, or controlled maintenance flows.
4. Read-models and projections are not source of truth.
5. Projections must be rebuildable and checkable from source tables.
6. Performance work must not change business logic.

## Scope

Only these endpoints are in Wave 1:

| Endpoint | Baseline from master plan | Target |
|---|---:|---:|
| `/api/requests/summary` | 1,921 ms | < 50 ms |
| `/api/docs` | 975 ms | < 200 ms |
| `/api/orders` | 3,032 ms | < 200 ms for paged list |
| `/api/orders?limit=50&offset=0` | 1,356 ms | < 200 ms |
| `/api/tsd/outbound/orders` | 43 ms | keep optimized |
| `/api/orders/hu-bindings/ready` | 1,880 ms | no semantic change; optimize shared pieces only |

## Task sequence

1. Replace `/api/requests/summary` ready-HU indicator with a cheap dedicated count/exists read-model. Do not build the full ready-HU screen model for a tiny summary response.
2. Add batch document pallet summaries for `/api/docs`, then remove per-doc pallet detail reads from docs list mapping.
3. Keep `/api/orders` on paged SQL read-models by default and add batch order pallet summaries for list rows.
4. Preserve `/api/tsd/outbound/orders` optimized SQL path and add guard coverage that prevents fallback to heavy full-detail reads.
5. For `/api/orders/hu-bindings/ready`, keep the full screen response behavior intact; only share or batch internal candidate/read-model pieces where it does not change eligibility rules.

## Forbidden changes

- No business-logic changes for order status, ready-HU binding, marking, production fill, outbound close, or ledger writes.
- No production DB changes.
- No schema/index DDL or migration work as part of Wave 1.
- No response contract break for existing WPF/TSD/Web clients.
- No caching that hides N+1 while leaving the underlying per-row composition in place.
- No write-path dependency on list read-model output.

## Required tests

| Category | Required coverage |
|---|---|
| Contract tests | JSON shape remains compatible for all Wave 1 endpoints. |
| No-N+1 guard tests | List endpoints do not call detail methods per row. |
| Ledger regression tests | Read-model changes do not write `ledger` or mutate source facts. |
| SQL/read-model tests | Batch methods return correct rows for multiple ids and empty input. |
| Perf acceptance tests | Local or integration smoke timings meet Wave 1 targets with coarse thresholds. |

## Acceptance criteria

- `/api/requests/summary` returns in < 50 ms through localhost/nginx.
- `/api/docs` returns in < 200 ms through localhost/nginx.
- Paged `/api/orders` returns in < 200 ms through localhost/nginx.
- `/api/tsd/outbound/orders` remains fast and does not regress from its optimized path.
- `/api/orders/hu-bindings/ready` remains semantically compatible with current ready-HU rules.
- No ledger rows, closed documents, order quantities, statuses, marking states, reservations, or production pallet facts are changed by read endpoints.

## Commit and deploy guidance

- Keep Wave 1 in focused commits grouped by endpoint/read-model.
- Include tests with each endpoint change.
- Do not deploy to production without backup, manual approval, and before/after endpoint timing evidence.
- If any endpoint needs a contract change, stop and write a separate compatibility plan before implementation.
