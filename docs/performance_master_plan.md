# FlowStock Performance / Read-Model Architecture Master Plan

Документ фиксирует результаты production performance audit и целевую архитектуру read-model/projection слоя. Это план работ, а не изменение бизнес-логики.

Жесткое правило: `ledger` остается source of truth для физических остатков. Read-models и projections ускоряют чтение, но не становятся источником истины для операций склада, закрытия документов, сторно или корректировок.

## 1. Current Findings

### Production DB size

Production DB маленькая: проблема не в общем размере базы.

| Metric | Value |
|---|---:|
| DB size | 222 MB |
| `orders` | 139 rows |
| `order_lines` | 381 rows |
| `docs` | 449 rows |
| `doc_lines` | 2,296 rows |
| `ledger` | 1,159 rows |
| `production_pallets` | 336 rows |
| `production_pallet_lines` | 365 rows |
| `order_receipt_plan_lines` | 15 rows |
| `marking_code` | 310,052 rows / 207 MB |

Исключение по размеру - `marking_code`: это единственная реально крупная таблица в текущем production snapshot.

### Endpoint latency snapshot

Замеры делались с production server через `curl GET https://localhost:7154`, по 10 запусков на endpoint.

| Endpoint | Avg | Max | Payload | Rows |
|---|---:|---:|---:|---:|
| `/api/planner/warehouse-board/state` | 5,236 ms | 5,752 ms | 76 KB | `orders:139` |
| `/api/orders` | 3,032 ms | 3,211 ms | 150 KB | 108 |
| `/api/requests/summary` | 1,921 ms | 1,983 ms | 161 B | object |
| `/api/orders/hu-bindings/ready` | 1,880 ms | 1,965 ms | 112 B | `hu_rows:0` |
| `/api/orders/candidates?doc_type=OUTBOUND&limit=50` | 1,542 ms | 1,560 ms | 15 KB | 11 |
| `/api/orders?limit=50&offset=0` | 1,356 ms | 1,446 ms | 70 KB | 50 |
| `/api/docs` | 975 ms | 1,084 ms | 308 KB | 449 |
| `/api/tsd/production/filling-orders` | 905 ms | 971 ms | 4.8 KB | 8 |
| `/api/reports/warehouse-production-state` | 141 ms | 173 ms | 36 KB | 20 |
| `/api/marking/orders?include_completed=1` | 100 ms | 104 ms | 240 KB | 250 |
| `/api/reports/production-need` | 51 ms | 63 ms | 7.5 KB | 13 |
| `/api/stock/rows` | 36 ms | 40 ms | 8.2 KB | 18 |

Server logs за последние 3000 строк подтвердили ту же картину: без учета long-lived `/api/live`, самые медленные стабильные endpoints - `warehouse-board/state`, `/api/orders`, `/api/requests/summary`, paged `/api/orders`, `/api/docs`, `/api/tsd/production/filling-orders`.

### Main bottleneck pattern

Основные тормоза идут от N+1/service composition, а не от одного медленного SQL.

EXPLAIN показал, что отдельные базовые SELECT быстрые:

| Query area | Execution time |
|---|---:|
| docs list core SELECT | 4.375 ms |
| HU stock rows aggregation | 0.987 ms |
| HU context core SELECT | 4.364 ms |
| active production work items | 0.476 ms |
| pending order request count | 0.046 ms |

Следовательно, 1-5 секунд latency появляются из-за многократных вызовов store/service методов, повторной сборки read-models и per-row details в list endpoints.

### Problem files and code paths

| Area | Files | Problem |
|---|---|---|
| Warehouse board | `apps/windows/FlowStock.Server/WarehouseBoardStateEndpoints.cs` | `GetOrders()`, `GetHuStockRows()`, затем per-order `GetDocsByOrder`, `GetProductionPalletsByDoc`, `GetOrderReceiptRemaining` |
| Orders list | `apps/windows/FlowStock.Server/Program.cs`, `apps/windows/FlowStock.Core/Services/ProductionPalletService.cs` | list rows дополнительно строят shipment/pallet summaries per order |
| Requests summary | `apps/windows/FlowStock.Server/Program.cs`, `apps/windows/FlowStock.Server/ReadyHuBindingEndpoint.cs`, `apps/windows/FlowStock.Core/Services/ReadyHuBindingReadModelService.cs` | tiny summary вызывает полный ready-HU read-model |
| Docs list | `apps/windows/FlowStock.Server/Program.cs`, `apps/windows/FlowStock.Core/Services/ProductionPalletService.cs` | `GetDocs()` быстрый, но pallet summary строится per doc |
| Filling orders | `apps/windows/FlowStock.Core/Services/ProductionPalletService.cs` | часть preload уже есть, но остаются lookup paths и composition overhead |
| Store/read-model SQL | `apps/windows/FlowStock.Data/PostgresDataStore.cs` | есть optimized SQL read-models, но не все list screens переведены на batch/projection reads |

## 2. Core Invariants

Эти инварианты нельзя нарушать ради performance fixes.

1. `ledger` остается source of truth для физических остатков.
2. Закрытые документы не редактируются.
3. Исправления складских фактов выполняются через явные корректировки, сторно или controlled maintenance flow.
4. Read-models и projections не являются source of truth.
5. Любая projection должна быть rebuildable из source tables.
6. Projection rebuild должен иметь check/verification mechanism против source data.
7. List/read endpoints могут использовать projections, но write paths должны повторно проверять source-of-truth состояние внутри transaction.
8. Новые performance fixes не должны менять semantics close/fill/outbound, ledger writes, order status transitions или marking rules.

## 3. Target Architecture

### Dedicated SQL read-models for list screens

List endpoints должны получать готовые строки списка из dedicated SQL read-models. Цель - один bounded query на экран вместо композиции сервисов поверх full details.

Примеры целевых read-models:

| Screen/API | Target read model |
|---|---|
| `/api/orders` | `orders_page_read_model`: order header + effective status + shipment/receipt flags + pallet summary |
| `/api/docs` | `docs_page_read_model`: doc header + line count + pallet summary by doc |
| `/api/planner/warehouse-board/state` | `warehouse_board_read_model`: orders + pallet plan summaries + HU stock/context in batch |
| `/api/requests/summary` | cheap summary counts/exists, no full ready-HU model |
| `/api/tsd/outbound/orders` | outbound picking rows from optimized SQL only |

### Batch methods instead of per-row details

Required batch capabilities:

| Current pattern | Target |
|---|---|
| `foreach order -> GetDocsByOrder(order.Id)` | `GetDocsByOrderIds(orderIds)` |
| `foreach doc -> GetProductionPalletsByDoc(doc.Id)` | `GetProductionPalletSummariesByDocIds(docIds)` / `GetProductionPalletsByDocIds(docIds)` |
| `foreach order -> GetOrderReceiptRemaining(order.Id)` | `GetOrderReceiptRemainingByOrderIds(orderIds)` or SQL projection |
| `foreach order -> BuildOrderOwnedPalletSummary(order.Id)` | `GetOrderPalletSummaries(orderIds)` |
| tiny count -> full read-model build | dedicated `COUNT`/`EXISTS` read-model |

### Future projections

Projection tables are future acceleration layers, not immediate truth sources.

| Projection | Purpose | Source of truth |
|---|---|---|
| `current_stock` | item/location physical balance | `ledger` |
| `current_hu_balance` | item/location/HU physical balance | `ledger` |
| `order_progress_projection` | shipped/produced/remaining/order readiness | `orders`, `order_lines`, `docs`, `doc_lines`, `ledger`, reservation tables |
| `warehouse_board_projection` | board state for planner screen | source read-model inputs |
| `ready_hu_binding_projection` | cheap ready-HU notification/list | `ledger`, `orders`, `order_lines`, reservation/pallet sources |

Every projection must support:

- full rebuild from source tables;
- consistency check against source tables;
- version/updated timestamp metadata;
- safe fallback to SQL read-model when projection is missing or stale, where feasible.

## 4. Runtime Projection Strategy Options

### A. SQL read-models only, no materialized projection

Use dedicated SQL queries and batch methods. Do not store derived rows.

Pros:

- Lowest correctness risk.
- No invalidation problem.
- Easy to compare with current behavior.
- Good fit while production DB is small.

Cons:

- Heavy views may stay CPU/query expensive as data grows.
- Repeated computation remains per request.
- Complex SQL can become hard to maintain.

Risks:

- Large SQL read-models can turn into hidden monoliths.
- Without no-N+1 tests, service composition can reappear.

Apply to:

- Wave 1 endpoints.
- First version of `/api/planner/warehouse-board/state`.
- Any screen where data volume is still small and correctness is more important than caching.

### B. Projection tables updated synchronously on close/fill/outbound

Write paths update derived projection rows in the same transaction as source writes.

Pros:

- Fast reads.
- Strong transactional freshness.
- Good for balances and counters that must be current immediately.

Cons:

- More code in critical write paths.
- Higher risk of partial/inconsistent projection bugs.
- Every source mutation needs projection maintenance.

Risks:

- Accidentally making projection authoritative.
- Missing a write path and creating stale reads.
- Increasing transaction complexity around close/fill/outbound.

Apply to:

- Later `current_stock` / `current_hu_balance` only after rebuild/check tools exist.
- Small, deterministic aggregates with clear source events.

### C. Async projection rebuild/orchestrator

Source writes append/change facts; background process rebuilds or refreshes projections.

Pros:

- Keeps write paths simpler.
- Can rebuild heavy projections outside user requests.
- Supports full consistency checks and repair.

Cons:

- Eventual consistency.
- Requires orchestration, status, retry and observability.
- More moving parts than the current app needs for Wave 1.

Risks:

- Building a universal god-orchestrator.
- UI seeing stale state without clear freshness metadata.
- Hard-to-debug race conditions if write paths trust async projections.

Apply to:

- Long-term heavy board/progress projections.
- Rebuild/check tooling.
- Non-critical planning dashboards where slight staleness is acceptable and visible.

### D. Hybrid

Use SQL read-models first, add selective sync projections for balances, and add async rebuild/check tooling for heavy projections.

Pros:

- Best risk/reward path.
- Lets Wave 1 deliver performance without new persistence semantics.
- Allows projections only where measured need exists.

Cons:

- Requires clear boundaries between SQL read-models, sync projections and async rebuilds.
- Needs documentation and tests to prevent mixed semantics.

Risks:

- Inconsistent freshness expectations by endpoint.
- Overlapping implementations if old service composition is not retired.

Apply to:

- Recommended target strategy.
- Wave 1 and Wave 2 via SQL/batch read-models.
- Wave 3 with selective balance projections.
- Wave 4 with rebuild/orchestrator tools.

## 5. Endpoint Migration Plan

### Wave 1 - quick wins and low-risk list screens

Goal: remove obvious full-model builds and per-row details without changing business behavior.

Targets:

| Endpoint | Change |
|---|---|
| `/api/requests/summary` | Replace full `ReadyHuBindingReadModelService.Build()` with cheap count/exists read-model |
| `/api/orders/hu-bindings/ready` | Keep full model for the actual screen, but share optimized candidate/batch pieces where possible |
| `/api/docs` | Add batch pallet summary by doc ids; remove per-doc `GetProductionPalletsByDoc` from list mapping |
| `/api/orders` | Use paged SQL read-model by default; add batch pallet summaries by order ids |
| `/api/tsd/outbound/orders` | Keep optimized SQL path; add guard tests that it does not fall back to full details |

Acceptance:

- `/api/requests/summary` under 50 ms.
- `/api/docs` under 200 ms.
- `/api/orders?limit=50&offset=0` under 200 ms.
- No change to JSON contract except additive fields explicitly covered by tests.

### Wave 2 - warehouse board

Goal: replace board service composition with a dedicated board read-model.

Target changes:

- Build board state from batch SQL projections for orders, pallet summaries, HU stock/context and locations.
- Remove repeated `HasPalletPlanForOrder`, `MapBoardOrder` per-row store calls and per-order receipt remaining calls.
- Keep response shape compatible with current `/api/planner/warehouse-board/state`.

Acceptance:

- Initial target under 500 ms.
- Later target under 200 ms after projection/balance improvements.

### Wave 3 - stock/HU balance projection

Goal: avoid repeated aggregation from `ledger` when data grows.

Target changes:

- Introduce `current_stock` and `current_hu_balance` only with rebuild and check tools.
- Keep `ledger` authoritative.
- Write paths may update projection synchronously only after regression tests prove ledger semantics are unchanged.

Acceptance:

- Projection rebuild from source ledger matches direct ledger aggregation.
- Any mismatch is detectable before deploy or by maintenance check.

### Wave 4 - projection orchestrator/rebuild tools

Goal: make projections operationally safe.

Target changes:

- Add explicit dev/maintenance commands for rebuild/check.
- Add freshness metadata and audit output.
- Avoid a universal orchestrator. Prefer focused projection jobs with clear ownership.

Acceptance:

- Rebuild is idempotent.
- Check reports row counts, mismatches and source/projection timestamps.
- No endpoint treats stale/missing projection as authoritative for writes.

## 6. Testing Strategy

### Contract tests

- Assert JSON shape for migrated endpoints.
- Cover empty, normal and search/pagination cases.
- Ensure old clients can still parse responses.

### Ledger regression tests

- For read-model changes, assert no ledger writes happen.
- For future projection changes around close/fill/outbound, assert ledger rows are unchanged versus existing behavior.
- Keep closed-document immutability tests in place.

### Projection rebuild tests

- Seed source tables.
- Build projection.
- Compare projection rows with direct source aggregation.
- Rebuild twice and assert idempotent output.
- Corrupt/stale projection in test and assert check detects mismatch.

### Performance acceptance tests

- Add integration/perf smoke tests for local server or test host where feasible.
- Use coarse thresholds to catch regressions, not microbenchmark noise.
- Track endpoint elapsed time, payload size and list row count.

### No-N+1 guard tests

- Source-level guard tests for list endpoints: no `GetDetails`/`GetDocLines`/`GetProductionPalletsByDoc` inside per-row mapping.
- Mock-based tests where practical: list endpoint must not call full detail methods per row.
- SQL read-model tests for batch methods with multiple ids.

## 7. Acceptance Targets

| Endpoint class | Target |
|---|---:|
| List endpoints through localhost/nginx | < 200 ms |
| Small summary endpoints | < 50 ms |
| Heavy planning board initial target | < 500 ms |
| Heavy planning board later target | < 200 ms |

These are production-like local targets. CI tests should use looser guard thresholds if timing is noisy.

## 8. Risks / Do Not Do

Do not:

- Build a universal god-orchestrator.
- Change ledger semantics.
- Make projections source of truth.
- Edit closed documents.
- Deploy schema changes without backup and manual confirmation.
- Add projection tables without rebuild/check mechanism.
- Drop indexes only because short-window `pg_stat_user_indexes` shows low usage.
- Hide slow service composition behind caching without removing the underlying N+1.
- Let write paths trust stale projections without rechecking source tables.
- Change business rules for ready HU binding, marking, production fill, outbound close or order status as part of performance work.

## Future Diagnostic Scripts

No diagnostic script is required for this document. If needed later, add dev-only scripts with explicit read-only defaults:

- endpoint latency sampler for `GET` endpoints;
- read-only DB stats collector for table/index/scan/autovacuum observations;
- static no-N+1 guard scanner for list endpoints;
- projection consistency checker for future projection tables.

Scripts must not write production data, apply migrations or call production write endpoints.
