# FlowStock.Marking Storage Plan

## Purpose

This document describes the first storage and SQL integration draft for `FlowStock.Marking` in the style of the current `FlowStock.Data` layer.

Scope of this document:

- new schema objects for `FlowStock.Marking`
- how they fit into `PostgresDataStore.Initialize()`
- minimal `IDataStore` surface for MVP 1
- row mapping strategy
- coexistence with legacy `Km*`

Non-goals:

- no import pipeline yet
- no server API yet
- no UI yet
- no destructive migration of legacy `km_code` / `km_code_batch`

## Current FlowStock.Data Pattern

Observed pattern in the current repo:

- schema is created inside `PostgresDataStore.Initialize()` as one large raw SQL block
- indexes are usually created immediately after the table definition
- `IDataStore` is a flat interface with domain-oriented methods
- `PostgresDataStore` implements methods directly with raw SQL and `Npgsql`
- read mapping is done with private `Read*` helpers such as `ReadItem`, `ReadOrder`, `ReadKmCode`
- query reuse is sometimes extracted into private `Build*Query(...)` helpers
- most existing date and time fields are stored as `TEXT` and mapped through `ToDbDate(...)` / `FromDbDate(...)`
- existing primary keys are usually `BIGSERIAL`, but the new `Marking*` domain models already use `Guid`

Practical conclusion:

- `FlowStock.Marking` should follow the same raw SQL and `IDataStore` style
- it should not introduce EF Core
- it should keep new schema parallel to legacy KM
- it may still use `UUID` for new marking IDs because the new domain models are already Guid-based and `Npgsql` supports this cleanly

## A. New Tables To Add

Recommended new tables:

- `marking_order`
- `marking_code_import`
- `marking_code`
- `marking_print_batch`
- `marking_print_batch_code`

Important note:

- existing repo table naming is mostly plural
- for the new module, use the singular `marking_*` names already fixed in the design docs
- this keeps the new domain internally consistent and clearly separated from legacy `km_*`

### A.1 `marking_order`

Purpose:

- one marking request context for a business order

Recommended columns:

| Column | Type | Null | Notes |
|---|---|---:|---|
| `id` | `UUID` | no | PK |
| `order_id` | `BIGINT` | no | FK to `orders(id)` |
| `item_id` | `BIGINT` | yes | FK to `items(id)` |
| `gtin` | `TEXT` | yes | optional |
| `requested_quantity` | `INTEGER` | no | must be positive |
| `request_number` | `TEXT` | no | unique internal request number |
| `status` | `TEXT` | no | string status |
| `notes` | `TEXT` | yes | operator/system notes |
| `requested_at` | `TEXT` | yes | stored via `ToDbDate(...)` |
| `codes_bound_at` | `TEXT` | yes | stored via `ToDbDate(...)` |
| `created_at` | `TEXT` | no | stored via `ToDbDate(...)` |
| `updated_at` | `TEXT` | no | stored via `ToDbDate(...)` |

PK:

- `PRIMARY KEY (id)`

FK:

- `FOREIGN KEY (order_id) REFERENCES orders(id)`
- `FOREIGN KEY (item_id) REFERENCES items(id)`

Indexes and unique constraints:

- `UNIQUE (request_number)`
- index on `order_id`
- index on `status`
- index on `gtin`

Notes:

- `item_id` is preferred over `product_id` because current FlowStock uses `Item` as the product-like domain entity
- do not enforce `UNIQUE(order_id)` in MVP 1

### A.2 `marking_code_import`

Purpose:

- one imported source file plus its matching/binding result

Recommended columns:

| Column | Type | Null | Notes |
|---|---|---:|---|
| `id` | `UUID` | no | PK |
| `original_filename` | `TEXT` | no | original source file name |
| `storage_path` | `TEXT` | no | current file path |
| `file_hash` | `TEXT` | no | SHA-256 hex, unique |
| `source_type` | `TEXT` | no | e.g. `csv`, `tsv` |
| `detected_request_number` | `TEXT` | yes | parsed candidate |
| `detected_gtin` | `TEXT` | yes | parsed candidate |
| `detected_quantity` | `INTEGER` | yes | parsed candidate |
| `matched_marking_order_id` | `UUID` | yes | FK to `marking_order(id)` |
| `match_confidence` | `NUMERIC(5,4)` | yes | optional |
| `status` | `TEXT` | no | string status |
| `imported_rows` | `INTEGER` | no | default 0 |
| `valid_code_rows` | `INTEGER` | no | default 0 |
| `duplicate_code_rows` | `INTEGER` | no | default 0 |
| `error_message` | `TEXT` | yes | processing/binding failure details |
| `created_at` | `TEXT` | no | stored via `ToDbDate(...)` |
| `processed_at` | `TEXT` | yes | stored via `ToDbDate(...)` |

PK:

- `PRIMARY KEY (id)`

FK:

- `FOREIGN KEY (matched_marking_order_id) REFERENCES marking_order(id)`

Indexes and unique constraints:

- `UNIQUE (file_hash)`
- index on `(status, created_at)`
- index on `matched_marking_order_id`
- index on `detected_request_number`

Notes:

- `file_hash` is the import-level idempotency key

### A.3 `marking_code`

Purpose:

- one stored marking code bound to one marking order and one import

Recommended columns:

| Column | Type | Null | Notes |
|---|---|---:|---|
| `id` | `UUID` | no | PK |
| `code` | `TEXT` | no | authoritative raw code value |
| `code_hash` | `TEXT` | no | SHA-256 hex |
| `gtin` | `TEXT` | yes | optional |
| `marking_order_id` | `UUID` | no | FK to `marking_order(id)` |
| `import_id` | `UUID` | no | FK to `marking_code_import(id)` |
| `status` | `TEXT` | no | string status |
| `source_row_number` | `INTEGER` | yes | optional diagnostics |
| `printed_at` | `TEXT` | yes | stored via `ToDbDate(...)` |
| `applied_at` | `TEXT` | yes | future use |
| `reported_at` | `TEXT` | yes | future use |
| `introduced_at` | `TEXT` | yes | future use |
| `created_at` | `TEXT` | no | stored via `ToDbDate(...)` |
| `updated_at` | `TEXT` | no | stored via `ToDbDate(...)` |

PK:

- `PRIMARY KEY (id)`

FK:

- `FOREIGN KEY (marking_order_id) REFERENCES marking_order(id)`
- `FOREIGN KEY (import_id) REFERENCES marking_code_import(id)`

Indexes and unique constraints:

- `UNIQUE (code)`
- index on `code_hash`
- index on `(marking_order_id, status)`
- index on `import_id`
- index on `gtin`

Notes:

- `UNIQUE(code)` is the authoritative dedup guard
- `code_hash` is only a lookup accelerator

### A.4 `marking_print_batch`

Purpose:

- one operational print batch for codes already bound to a marking order

Recommended columns:

| Column | Type | Null | Notes |
|---|---|---:|---|
| `id` | `UUID` | no | PK |
| `marking_order_id` | `UUID` | no | FK to `marking_order(id)` |
| `batch_number` | `INTEGER` | no | sequential within order |
| `status` | `TEXT` | no | string status |
| `codes_count` | `INTEGER` | no | snapshot count |
| `printer_target_type` | `TEXT` | yes | e.g. `path`, `name` |
| `printer_target_value` | `TEXT` | yes | actual printer identifier |
| `debug_layout` | `INTEGER` | no | `0/1`, consistent with current repo style |
| `reprint_of_batch_id` | `UUID` | yes | self-FK |
| `printed_at` | `TEXT` | yes | stored via `ToDbDate(...)` |
| `created_at` | `TEXT` | no | stored via `ToDbDate(...)` |
| `updated_at` | `TEXT` | no | stored via `ToDbDate(...)` |
| `notes` | `TEXT` | yes | optional notes |

PK:

- `PRIMARY KEY (id)`

FK:

- `FOREIGN KEY (marking_order_id) REFERENCES marking_order(id)`
- `FOREIGN KEY (reprint_of_batch_id) REFERENCES marking_print_batch(id)`

Indexes and unique constraints:

- `UNIQUE (marking_order_id, batch_number)`
- index on `(marking_order_id, status)`
- index on `reprint_of_batch_id`

Notes:

- `debug_layout` as `INTEGER` avoids introducing a new flag storage style in the first iteration

### A.5 `marking_print_batch_code`

Purpose:

- link table between print batches and codes

Recommended columns:

| Column | Type | Null | Notes |
|---|---|---:|---|
| `id` | `UUID` | no | PK |
| `print_batch_id` | `UUID` | no | FK to `marking_print_batch(id)` |
| `marking_code_id` | `UUID` | no | FK to `marking_code(id)` |
| `sequence_no` | `INTEGER` | no | stable order inside batch |
| `created_at` | `TEXT` | no | stored via `ToDbDate(...)` |

PK:

- `PRIMARY KEY (id)`

FK:

- `FOREIGN KEY (print_batch_id) REFERENCES marking_print_batch(id)`
- `FOREIGN KEY (marking_code_id) REFERENCES marking_code(id)`

Indexes and unique constraints:

- `UNIQUE (print_batch_id, sequence_no)`
- `UNIQUE (print_batch_id, marking_code_id)`
- index on `marking_code_id`

Notes:

- this table is required already in MVP 1 because reprint is a real scenario

## B. SQL Naming And Rollout

### B.1 Where To Add Schema

Recommended placement:

- add the new `marking_*` schema block inside `PostgresDataStore.Initialize()`
- place it after the current legacy `km_code_batch` / `km_code` block

Reason:

- keeps all marking-related schema together
- avoids mixing the new module into unrelated parts of the large initialization block
- keeps the diff local and easier to review

### B.2 Creation Order

Recommended order:

1. `marking_order`
2. `marking_code_import`
3. `marking_code`
4. `marking_print_batch`
5. `marking_print_batch_code`

Why:

- `marking_code_import` depends on `marking_order`
- `marking_code` depends on both `marking_order` and `marking_code_import`
- `marking_print_batch` depends on `marking_order`
- `marking_print_batch_code` depends on both `marking_print_batch` and `marking_code`

### B.3 What To Add Immediately

Add in the first schema rollout:

- all five tables
- all foreign keys
- all uniqueness constraints
- all critical lookup indexes

These should not be delayed because they define correctness:

- `UNIQUE(request_number)`
- `UNIQUE(file_hash)`
- `UNIQUE(code)`
- `UNIQUE(marking_order_id, batch_number)`
- `UNIQUE(print_batch_id, sequence_no)`
- `UNIQUE(print_batch_id, marking_code_id)`

### B.4 What Can Be Deferred

Can be deferred if rollout pressure requires it:

- secondary reporting indexes not used by MVP 1
- additional partial indexes for future lifecycle stages
- any advanced search indexes on `notes` or text payload fields

## C. IDataStore Surface Draft

Recommendation:

- do not add a large marking repository surface up front
- add only methods needed for `Manual Marking Storage` MVP 1
- keep method naming aligned with existing `IDataStore` verbs

### C.1 Suggested Minimal Methods

`MarkingOrder`

- `Guid AddMarkingOrder(MarkingOrder order);`
- `MarkingOrder? GetMarkingOrder(Guid id);`
- `MarkingOrder? FindMarkingOrderByRequestNumber(string requestNumber);`
- `IReadOnlyList<MarkingOrder> GetMarkingOrdersByOrder(long orderId);`
- `void UpdateMarkingOrderStatus(Guid id, string status, DateTime? codesBoundAt, DateTime updatedAt);`

`MarkingCodeImport`

- `Guid AddMarkingCodeImport(MarkingCodeImport import);`
- `MarkingCodeImport? GetMarkingCodeImport(Guid id);`
- `MarkingCodeImport? FindMarkingCodeImportByHash(string fileHash);`
- `IReadOnlyList<MarkingCodeImport> GetUnresolvedMarkingCodeImports(int take);`
- `void UpdateMarkingCodeImport(MarkingCodeImport import);`
- `void BindMarkingCodeImport(Guid importId, Guid markingOrderId, decimal? matchConfidence, string status, DateTime processedAt);`

`MarkingCode`

- `Guid AddMarkingCode(MarkingCode code);`
- `void AddMarkingCodes(IReadOnlyList<MarkingCode> codes);`
- `MarkingCode? FindMarkingCodeByRaw(string code);`
- `bool ExistsMarkingCodeByRaw(string code);`
- `IReadOnlyList<MarkingCode> GetMarkingCodesByOrder(Guid markingOrderId, string? status, int take);`
- `int CountMarkingCodesByOrder(Guid markingOrderId, string? status);`

`MarkingPrintBatch`

- `Guid AddMarkingPrintBatch(MarkingPrintBatch batch);`
- `MarkingPrintBatch? GetMarkingPrintBatch(Guid id);`
- `IReadOnlyList<MarkingPrintBatch> GetMarkingPrintBatchesByOrder(Guid markingOrderId);`
- `void UpdateMarkingPrintBatch(MarkingPrintBatch batch);`

`MarkingPrintBatchCode`

- `void AddMarkingPrintBatchCodes(IReadOnlyList<MarkingPrintBatchCode> rows);`
- `IReadOnlyList<MarkingPrintBatchCode> GetMarkingPrintBatchCodes(Guid printBatchId);`

Readiness / coordination

- `bool IsMarkingOrderReadyForPrint(Guid markingOrderId);`
- `int CountMarkingCodesReadyForPrint(Guid markingOrderId);`

### C.2 Why Stop Here

This is intentionally smaller than the legacy KM surface.

Do not add yet:

- print execution methods
- transport-related methods
- browser-agent methods
- line application/reporting methods
- bulk search surface for future reporting

Those belong after the import pipeline and the first orchestration draft exist.

## D. Mapping Strategy

### D.1 Row Mapper Style

Recommended pattern:

- add private helpers in `PostgresDataStore`, matching current style:
  - `ReadMarkingOrder(...)`
  - `ReadMarkingCodeImport(...)`
  - `ReadMarkingCode(...)`
  - `ReadMarkingPrintBatch(...)`
  - `ReadMarkingPrintBatchCode(...)`

Use `Build*Query(...)` helpers only when query reuse becomes real. Do not over-abstract early.

### D.2 Guid Mapping

Recommended approach:

- store marking IDs as PostgreSQL `UUID`
- map them directly with `Guid`

Read side:

- `reader.GetGuid(...)`
- nullable UUID: `reader.IsDBNull(i) ? null : reader.GetGuid(i)`

Write side:

- `command.Parameters.AddWithValue("@id", marking.Id);`

Reason:

- matches already-added `FlowStock.Core.Models.Marking` models
- avoids introducing synthetic `long` IDs only for the storage layer
- `Npgsql` handles `Guid <-> UUID` cleanly

### D.3 Timestamp Mapping

Recommended first iteration:

- keep timestamp columns as `TEXT`
- serialize with existing `ToDbDate(...)`
- parse with existing `FromDbDate(...)`

Reason:

- matches current repo convention
- avoids introducing mixed date-storage rules in the same `PostgresDataStore`
- minimizes risk while the new module is still draft-level

This is a pragmatic integration choice, not an idealized schema doctrine.

### D.4 Nullable and Flag Mapping

Recommended approach:

- nullable strings: `reader.IsDBNull(i) ? null : reader.GetString(i)`
- nullable integers: `reader.IsDBNull(i) ? null : reader.GetInt32(i)`
- nullable decimals: `reader.IsDBNull(i) ? null : reader.GetDecimal(i)`
- `debug_layout`: store as integer `0/1`, map to `bool`

## E. Idempotency / Uniqueness Enforcement

The following must be enforced at DB level from day one:

- `UNIQUE(request_number)`
- `UNIQUE(file_hash)`
- `UNIQUE(code)`
- `UNIQUE(marking_order_id, batch_number)`
- `UNIQUE(print_batch_id, sequence_no)`
- `UNIQUE(print_batch_id, marking_code_id)`

Interpretation:

- `request_number` prevents duplicate logical request identity
- `file_hash` prevents re-importing the same source file
- `code` prevents duplicate physical code storage
- batch number uniqueness preserves stable order inside one marking order
- sequence uniqueness preserves deterministic code order inside one batch
- `(print_batch_id, marking_code_id)` prevents the same code from being attached twice to the same batch

Important note:

- `code_hash` should be indexed but not made the authoritative uniqueness constraint
- raw `code` remains the business-truth uniqueness key

## F. Coexistence With Legacy KM

Rules for coexistence:

- new `marking_*` tables must be added in parallel
- existing `km_code` and `km_code_batch` must remain untouched functionally
- no destructive migration now
- no renaming of legacy tables
- no forced backfill from `km_*` into `marking_*` yet

Practical implication:

- `Marking` storage lands as a new storage island beside legacy KM
- legacy KM removal is a later phase, after new runtime behavior is cut over

## G. Recommended Implementation Order

1. add `marking_*` schema block to `PostgresDataStore.Initialize()`
2. add the minimal `IDataStore` method surface for `Marking`
3. implement those methods in `PostgresDataStore`
4. add row mappers for `Marking*`
5. only then build the `Marking` import pipeline draft

## Recommendation For This Step

Safe choice for this step:

- add this storage-plan document only
- do not modify `IDataStore` or `PostgresDataStore` yet

Reason:

- the new `Marking*` models are in place, but the first real caller will be the import pipeline
- if the storage surface is added before the import pipeline draft, there is a high chance of freezing the wrong method set too early
- current repo keeps storage interfaces very concrete, so premature interface changes create extra churn

That makes document-first the safer move at this stage.
