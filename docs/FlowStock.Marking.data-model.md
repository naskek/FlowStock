# FlowStock.Marking

## MVP 1 Data Model Draft

This document expands [FlowStock.Marking.spec.md](D:/FlowStock/docs/FlowStock.Marking.spec.md) into a pragmatic database model for `FlowStock.Marking` MVP 1.

Scope of this document:

- domain tables
- status storage strategy
- dedup and idempotency at DB level
- relationships
- migration rollout plan

Out of scope:

- implementation code
- EF Core models
- SQL migration files
- import pipeline logic
- UI

Assumption:

- timestamps are stored in UTC
- `uuid` below means native GUID or UUID type of the target DB, or app-generated GUID if the DB does not have a native UUID type

## A. Tables / Entities for MVP 1

Recommended MVP 1 tables:

- `MarkingOrders`
- `MarkingCodeImports`
- `MarkingCodes`
- `MarkingPrintBatches`
- `MarkingPrintBatchCodes`

### A.1 MarkingOrders

Purpose:

- one domain record representing a marking request context for one business order or production task

Columns:

| Column | Type | Null | Notes |
|---|---|---:|---|
| `id` | `uuid` | no | PK |
| `order_id` | same type as core `Orders.id` | no | FK to order in main FlowStock domain |
| `product_id` | same type as core `Products.id` | yes | FK if product is already modeled separately |
| `gtin` | `varchar(32)` | yes | may be unavailable at initial draft stage |
| `requested_quantity` | `integer` | no | must be `> 0` |
| `request_number` | `varchar(64)` | no | internal request number shown to operator |
| `status` | `varchar(32)` | no | see status section |
| `notes` | `text` | yes | operational notes |
| `requested_at` | `timestamp` | yes | when operator requested codes |
| `codes_bound_at` | `timestamp` | yes | when import was successfully bound |
| `created_at` | `timestamp` | no | audit |
| `updated_at` | `timestamp` | no | audit |

PK:

- `PK_MarkingOrders (id)`

FK:

- `order_id -> Orders.id`
- optional `product_id -> Products.id`

Indexes:

- `UX_MarkingOrders_RequestNumber` unique on `request_number`
- `IX_MarkingOrders_OrderId` on `order_id`
- `IX_MarkingOrders_Status` on `status`
- `IX_MarkingOrders_Gtin` on `gtin`

Uniqueness rules:

- `request_number` must be unique
- do not enforce `UNIQUE(order_id)` in MVP 1 yet, because one-order-vs-many-marking-orders is still an open business decision

### A.2 MarkingCodeImports

Purpose:

- one record per imported CSV or TSV file

Columns:

| Column | Type | Null | Notes |
|---|---|---:|---|
| `id` | `uuid` | no | PK |
| `original_filename` | `varchar(255)` | no | original file name for traceability |
| `storage_path` | `varchar(512)` | no | current file location in Debian storage |
| `file_hash` | `char(64)` | no | SHA-256 hex of raw file bytes |
| `source_type` | `varchar(16)` | no | e.g. `csv`, `tsv` |
| `detected_request_number` | `varchar(64)` | yes | parsed from file name/content if available |
| `detected_gtin` | `varchar(32)` | yes | optional import-level signal |
| `detected_quantity` | `integer` | yes | optional import-level signal |
| `matched_marking_order_id` | `uuid` | yes | FK when matched |
| `match_confidence` | `numeric(5,4)` | yes | e.g. `0.9500` |
| `status` | `varchar(32)` | no | e.g. `New`, `Bound`, `Failed`, `ManualReview` |
| `imported_rows` | `integer` | no | total parsed rows |
| `valid_code_rows` | `integer` | no | accepted code rows |
| `duplicate_code_rows` | `integer` | no | duplicate rows found |
| `error_message` | `text` | yes | processing or binding error |
| `created_at` | `timestamp` | no | audit |
| `processed_at` | `timestamp` | yes | when file processing finished |

PK:

- `PK_MarkingCodeImports (id)`

FK:

- `matched_marking_order_id -> MarkingOrders.id`

Indexes:

- `UX_MarkingCodeImports_FileHash` unique on `file_hash`
- `IX_MarkingCodeImports_Status_CreatedAt` on `(status, created_at)`
- `IX_MarkingCodeImports_MatchedMarkingOrderId` on `matched_marking_order_id`
- `IX_MarkingCodeImports_DetectedRequestNumber` on `detected_request_number`

Uniqueness rules:

- one physical file content must not be imported twice
- `file_hash` is the authoritative idempotency key for import-level dedup

### A.3 MarkingCodes

Purpose:

- one stored Chestny Znak code bound to one marking order

Columns:

| Column | Type | Null | Notes |
|---|---|---:|---|
| `id` | `uuid` | no | PK |
| `code` | `varchar(512)` | no | raw marking code as stored source value |
| `code_hash` | `char(64)` | no | SHA-256 hex of raw code |
| `gtin` | `varchar(32)` | yes | optional, if known per-code |
| `marking_order_id` | `uuid` | no | owning order |
| `import_id` | `uuid` | no | source import file |
| `status` | `varchar(32)` | no | see status section |
| `source_row_number` | `integer` | yes | useful for diagnostics |
| `printed_at` | `timestamp` | yes | first successful print timestamp |
| `applied_at` | `timestamp` | yes | future use |
| `reported_at` | `timestamp` | yes | future use |
| `introduced_at` | `timestamp` | yes | future use |
| `created_at` | `timestamp` | no | audit |
| `updated_at` | `timestamp` | no | audit |

PK:

- `PK_MarkingCodes (id)`

FK:

- `marking_order_id -> MarkingOrders.id`
- `import_id -> MarkingCodeImports.id`

Indexes:

- `UX_MarkingCodes_Code` unique on `code`
- `IX_MarkingCodes_CodeHash` on `code_hash`
- `IX_MarkingCodes_MarkingOrderId_Status` on `(marking_order_id, status)`
- `IX_MarkingCodes_ImportId` on `import_id`
- `IX_MarkingCodes_Gtin` on `gtin`

Uniqueness rules:

- raw `code` must be unique across the whole system
- `code_hash` is an operational acceleration field, not the only integrity guard

### A.4 MarkingPrintBatches

Purpose:

- one operational print batch created from codes already bound to a marking order

Columns:

| Column | Type | Null | Notes |
|---|---|---:|---|
| `id` | `uuid` | no | PK |
| `marking_order_id` | `uuid` | no | owning order |
| `batch_number` | `integer` | no | sequential number within order |
| `status` | `varchar(32)` | no | print batch execution status |
| `codes_count` | `integer` | no | count snapshot for batch |
| `printer_target_type` | `varchar(16)` | yes | e.g. `path`, `name` |
| `printer_target_value` | `varchar(255)` | yes | printer path or printer name |
| `debug_layout` | `boolean` | no | debug or production layout |
| `reprint_of_batch_id` | `uuid` | yes | self-FK for controlled reprint |
| `printed_at` | `timestamp` | yes | when batch finished printing |
| `created_at` | `timestamp` | no | audit |
| `updated_at` | `timestamp` | no | audit |
| `notes` | `text` | yes | operator or system notes |

PK:

- `PK_MarkingPrintBatches (id)`

FK:

- `marking_order_id -> MarkingOrders.id`
- `reprint_of_batch_id -> MarkingPrintBatches.id`

Indexes:

- `UX_MarkingPrintBatches_OrderId_BatchNumber` unique on `(marking_order_id, batch_number)`
- `IX_MarkingPrintBatches_OrderId_Status` on `(marking_order_id, status)`
- `IX_MarkingPrintBatches_ReprintOfBatchId` on `reprint_of_batch_id`

Uniqueness rules:

- `batch_number` must be unique within a given `MarkingOrder`

### A.5 MarkingPrintBatchCodes

Purpose:

- link table between print batches and codes, with deterministic order inside a batch

Columns:

| Column | Type | Null | Notes |
|---|---|---:|---|
| `id` | `uuid` | no | PK |
| `print_batch_id` | `uuid` | no | FK to batch |
| `marking_code_id` | `uuid` | no | FK to code |
| `sequence_no` | `integer` | no | order of code inside batch |
| `created_at` | `timestamp` | no | audit |

PK:

- `PK_MarkingPrintBatchCodes (id)`

FK:

- `print_batch_id -> MarkingPrintBatches.id`
- `marking_code_id -> MarkingCodes.id`

Indexes:

- `UX_MarkingPrintBatchCodes_BatchId_SequenceNo` unique on `(print_batch_id, sequence_no)`
- `UX_MarkingPrintBatchCodes_BatchId_CodeId` unique on `(print_batch_id, marking_code_id)`
- `IX_MarkingPrintBatchCodes_CodeId` on `marking_code_id`

Uniqueness rules:

- one code cannot appear twice in the same batch
- order inside a batch is explicit and stable

## B. Status Storage Approach

Recommended MVP 1 approach:

- store statuses as `varchar(32)` strings
- keep canonical allowed values in application constants
- optionally add DB-level `CHECK` constraints later if the chosen DB and migration tooling make this easy

Practical recommendation:

- prefer string statuses over DB enums for MVP 1
- do not use `smallint + mapping` yet

Why:

- easiest to debug directly in SQL and logs
- no vendor-specific enum coupling
- lower friction while statuses are still stabilizing
- safer during phased rollout, especially when the domain is still evolving

When to revisit:

- only if status sets become large, highly performance-sensitive, or shared across many services

## C. Dedup / Idempotency Rules at DB Level

### C.1 Import-Level Dedup

Goal:

- prevent the same physical file from being processed twice

Mechanism:

- compute SHA-256 of raw file bytes
- store as `MarkingCodeImports.file_hash`
- enforce `UNIQUE(file_hash)`

Recommendation:

- treat `file_hash` as the authoritative import idempotency key
- do not rely on filename, because filenames are operational and not trustworthy enough

### C.2 Code-Level Dedup

Goal:

- prevent the same physical marking code from being inserted more than once

Mechanism:

- store raw code in `MarkingCodes.code`
- compute SHA-256 in `MarkingCodes.code_hash`
- enforce `UNIQUE(code)`
- add non-unique index on `code_hash` for fast lookup and pre-checks

Recommendation:

- authoritative uniqueness should be on raw `code`
- `code_hash` should help performance and dedup checks, but not replace raw-code integrity

Why:

- this avoids theoretical hash-collision problems
- it keeps business truth readable and auditable

### C.3 Unique Index Set for MVP 1

Recommended unique constraints:

- `MarkingOrders.request_number`
- `MarkingCodeImports.file_hash`
- `MarkingCodes.code`
- `MarkingPrintBatches(marking_order_id, batch_number)`
- `MarkingPrintBatchCodes(print_batch_id, sequence_no)`
- `MarkingPrintBatchCodes(print_batch_id, marking_code_id)`

## D. Relationships

Core relationships:

- one `MarkingOrder` -> many `MarkingCodes`
- one `MarkingCodeImport` -> many `MarkingCodes`
- one `MarkingOrder` -> many `MarkingPrintBatches`
- one `MarkingPrintBatch` -> many `MarkingPrintBatchCodes`
- one `MarkingCode` -> many `MarkingPrintBatchCodes` over time

### D.1 Decision: `print_batch_id` in `MarkingCodes` is not enough

Recommendation:

- use separate link table `MarkingPrintBatchCodes` already in MVP 1

Reason:

- reprint is already a real scenario
- one code may belong to original print batch and later to reprint batch
- single `print_batch_id` in `MarkingCodes` would either lose history or require awkward overwrite logic
- print batch is an operational artifact and should not own code lifecycle state

This is the cleanest minimal model that still supports:

- original print
- controlled reprint
- auditability
- stable order of codes inside a batch

## E. Recommended Fields for Auditability

Keep auditability small but useful.

Minimum recommended timestamps:

- `created_at`
- `updated_at`
- `requested_at`
- `codes_bound_at`
- `processed_at`
- `printed_at`
- future: `applied_at`, `reported_at`, `introduced_at`

Minimum operational trace fields:

- `request_number`
- `original_filename`
- `storage_path`
- `file_hash`
- `error_message`
- `notes`
- `source_row_number`

Recommendation:

- every core table should have `created_at`
- mutable tables should have `updated_at`
- process milestones should have dedicated timestamps instead of only notes

## F. Storage Boundary

Source of truth:

- relational DB

Operational file storage:

- `/opt/flowstock/marking/inbox`
- `/processing`
- `/bound`
- `/failed`
- `/archive`

What files are for:

- intake
- operational processing
- traceability
- recovery and diagnostics

What files are not:

- authoritative lifecycle state
- authoritative matching state
- authoritative duplicate protection

Important rule:

- filename alone must never be treated as source of truth
- DB record plus file hash is the reliable domain boundary

## G. Migration Plan

### Migration 1: Core Tables

Create base tables:

- `MarkingOrders`
- `MarkingCodeImports`
- `MarkingCodes`
- `MarkingPrintBatches`
- `MarkingPrintBatchCodes`

Create only essential FKs and required columns.

Goal:

- establish stable domain shape early

### Migration 2: Indexes and Uniqueness

Add:

- unique index on `MarkingOrders.request_number`
- unique index on `MarkingCodeImports.file_hash`
- unique index on `MarkingCodes.code`
- unique and operational indexes on batch tables
- lookup indexes on status, order, import, request number, and hash columns

Goal:

- lock in idempotency and duplicate protection before import pipeline is built

### Migration 3: Optional Print Batch Refinements

Add only if rollout proves needed:

- extra indexes for heavy print history queries
- refined constraints around reprint chains
- optional derived fields such as `last_printed_at` if first implementation shows the need

Goal:

- keep MVP 1 rollout simple, while leaving room for print-heavy optimization later

## H. Open Questions

- how strict the binding confidence model should be for auto-bind vs manual queue
- whether one business order should always map to exactly one `MarkingOrder`
- whether future lifecycle stages should stay as nullable timestamps on `MarkingCodes` or move into dedicated event tables
- whether print execution history will later require richer batch event tables beyond `MarkingPrintBatchCodes`

## Key Recommendations

- store statuses as strings for MVP 1
- use `file_hash` for import idempotency
- use raw `code` uniqueness as authoritative duplicate protection
- include `MarkingPrintBatchCodes` already in MVP 1
- keep DB as source of truth, filesystem as operational storage only
