# FlowStock.Marking Import Pipeline

## Purpose

This document describes the draft import pipeline for `FlowStock.Marking` MVP 1.

Goal of the pipeline:

- accept a CSV or TSV file with marking codes
- compute a file hash
- parse and normalize rows
- detect duplicate imports and duplicate codes
- identify a target `MarkingOrder`
- either auto-bind conservatively or send the import to manual review
- prepare persistence into the new `Marking` storage

Non-goals for this step:

- no implementation yet
- no `IDataStore` changes yet
- no UI yet
- no print integration yet
- no legacy `Km*` changes

## A. Pipeline Stages

Recommended MVP 1 pipeline stages:

1. detect file
2. move file to processing
3. compute file hash
4. duplicate import check
5. detect source type and delimiter
6. parse rows
7. normalize codes and metadata
8. validate rows
9. derive import-level signals
10. candidate matching against `MarkingOrder`
11. decide `auto-bind` or `manual review`
12. persist import record
13. persist accepted codes
14. update order linkage and statuses
15. move file to `bound`, `failed`, or `archive`

Practical reading of these stages:

- file-system movement is operational
- DB is the source of truth
- matching is conservative
- ambiguity is not solved by clever heuristics in MVP 1

### A.1 Stage Detail

#### 1. Detect File

- watch or scan `/opt/flowstock/marking/inbox`
- pick only regular files
- ignore temporary or partial-upload file names if an operational convention exists

#### 2. Move To Processing

- move file from `inbox` to `/processing`
- rename only if needed for collision avoidance
- from this point, all further processing should use the file in `processing`

#### 3. Compute File Hash

- compute SHA-256 of raw file bytes
- this is the import-level idempotency key

#### 4. Duplicate Import Check

- check whether `file_hash` already exists in `marking_code_import`
- if it does, stop further processing

#### 5. Detect Source Type and Delimiter

- infer `csv` or `tsv`
- use simple delimiter detection, not a smart format engine

#### 6. Parse Rows

- read non-empty lines
- extract first-column code candidates
- read optional GTIN or product-like columns if present

#### 7. Normalize Codes And Metadata

- trim whitespace
- unwrap wrapped quotes if present
- normalize GTIN to the agreed format
- keep raw code identity stable after normalization

#### 8. Validate Rows

- ignore blank lines
- reject empty normalized codes
- count malformed rows explicitly

#### 9. Derive Import-Level Signals

Derive the following signals if possible:

- `detected_request_number`
- `detected_gtin`
- `detected_quantity`

These are not the same thing as final binding. They are matching inputs.

#### 10. Candidate Matching Against MarkingOrder

- try exact request-number match first
- if not available, use conservative fallback matching
- if still ambiguous, stop at manual review

#### 11. Decide Auto-Bind Or Manual Review

- auto-bind only when the result is clearly unambiguous
- otherwise create a manual review state

#### 12. Persist Import Record

- create one `MarkingCodeImport` row
- status should reflect the pipeline result

#### 13. Persist Accepted Codes

- persist only normalized, accepted codes
- keep duplicate rows and invalid rows in counters, not as separate `MarkingCode` records

#### 14. Update Order Linkage And Statuses

- if auto-bound, attach import to `MarkingOrder`
- update order status toward `CodesBound` or `ReadyForPrint`

#### 15. Move File To Final Folder

- `bound` for successful auto-bound imports
- `failed` for invalid or processing-failed imports
- `archive` for long-term retention after normal processing lifecycle
- manual-review files can either stay in `processing` temporarily or move to a dedicated operational bucket later, but MVP 1 can keep them in `processing` plus DB state if needed

## B. File Format Assumptions

MVP 1 assumptions:

- file types: CSV and TSV only
- encoding: UTF-8 or UTF-8 with BOM
- codes are usually in the first column
- blank lines are ignored
- extra columns may exist and should be tolerated
- request number may come from filename or content later, but this is not guaranteed
- format is expected to evolve over time

Practical parser stance:

- be strict about missing code values
- be tolerant about extra columns
- do not try to support arbitrary spreadsheet exports in MVP 1

### B.1 Minimal Supported Shape

Minimum row shape:

- column 1: code

Optional useful columns:

- GTIN
- product name
- request number

These optional columns are hints, not hard requirements for file acceptance.

## C. Duplicate Handling

Duplicate handling must be split into four separate cases.

### C.1 Duplicate File By `file_hash`

Condition:

- the exact same file content was already imported before

Expected behavior:

- stop processing early
- do not create duplicate `MarkingCode` rows
- result should be treated as idempotent replay of an already-seen import

MVP 1 recommendation:

- either do not create a new import row at all
- or create one only if later audit requirements explicitly need duplicate-attempt tracking

For the first implementation, the safer option is:

- no new import row
- return a duplicate-file result from the coordinator

### C.2 Duplicate Row Inside The Same File

Condition:

- the same normalized code appears multiple times inside one file

Expected behavior:

- count it in `duplicate_code_rows`
- persist only the first accepted occurrence
- do not fail the whole import because of repeated rows

### C.3 Already-Imported Code From Another File

Condition:

- normalized code already exists in `MarkingCode`

Expected behavior:

- do not insert duplicate `MarkingCode`
- count it as duplicate code
- do not fail the whole import unless business policy later requires full-file rejection

MVP 1 recommendation:

- skip duplicate code
- continue processing remaining rows

### C.4 Duplicate Code Value With Different Formatting

Condition:

- same logical code appears with wrapped quotes, whitespace, or other trivial formatting differences

Expected behavior:

- normalization should collapse these into the same canonical value before duplicate checks

## D. Matching Strategy

MVP 1 matching must be conservative.

Exact matching order:

1. `request_number` exact match
2. fallback by `GTIN + quantity + candidate open orders`
3. if the result is unambiguous -> auto-bind
4. otherwise -> manual review queue

### D.1 Request Number Exact Match

Primary matching key:

- internal request number from filename or parsed content

Rule:

- exact match only
- no fuzzy request-number matching

If exactly one open or relevant `MarkingOrder` matches:

- auto-bind

If multiple match:

- manual review

If none match:

- continue to fallback matching

### D.2 Fallback Matching

Fallback inputs:

- detected GTIN
- detected quantity
- candidate open `MarkingOrder` records
- optionally recent time window as a weak tie-breaker

MVP 1 rule:

- fallback may suggest
- fallback must not guess aggressively

Auto-bind from fallback only if:

- exactly one candidate order fits the available evidence
- there is no credible second candidate

Otherwise:

- manual review

### D.3 Ambiguity Policy

Ambiguity means manual review.

Examples:

- multiple open orders with same GTIN and same quantity
- request number absent
- mixed GTIN file with no clear request number
- quantity mismatch versus all candidates

MVP 1 should prefer false negatives over false positive auto-binding.

## E. Suggested Statuses And Transitions

### E.1 `MarkingCodeImport.status`

Suggested lifecycle in MVP 1:

- `New`
  file detected, import row not yet fully processed or just created
- `Processing`
  parser and duplicate checks are running
- `Bound`
  import successfully matched to one `MarkingOrder`
- `ManualReview`
  parsed successfully but target order is ambiguous or missing
- `Failed`
  malformed file or processing failure
- `Archived`
  optional later operational terminal state after retention handling

Suggested transitions:

- `New -> Processing`
- `Processing -> Bound`
- `Processing -> ManualReview`
- `Processing -> Failed`
- `Bound -> Archived`
- `ManualReview -> Bound` after operator decision

### E.2 `MarkingOrder.status`

Relevant MVP 1 transitions:

- `Draft -> WaitingForCodes`
- `WaitingForCodes -> CodesBound`
- `CodesBound -> ReadyForPrint`

Notes:

- `CodesBound` means storage and binding succeeded
- `ReadyForPrint` means codes are ready for print selection and batch preparation

MVP 1 simplification:

- initial import pipeline may move directly from `WaitingForCodes` to `ReadyForPrint` if there is no useful intermediate runtime step
- keeping `CodesBound` is still recommended because it reflects domain progress clearly

### E.3 `MarkingCode.status`

Suggested MVP 1 import-time flow:

- create code as `Imported`
- once bound to an order and considered part of active domain inventory, move to `Reserved`

Practical note:

- if every persisted `MarkingCode` is always created already under a known `MarkingOrder`, it is acceptable for the first implementation to store new codes directly as `Reserved`
- the document-first design still benefits from keeping both states available

## F. Persistence Order

Recommended persistence order:

1. create import record
2. update import status to `Processing`
3. persist accepted codes
4. bind import to order if unambiguous
5. update order status
6. finalize import status

### F.1 Recommended Transaction Boundaries

Use two boundaries.

#### Boundary 1: File-System Preparation

Outside DB transaction:

- move file to `processing`
- compute hash
- parse file

Reason:

- file IO should not hold DB transaction open

#### Boundary 2: DB Persistence

Inside DB transaction:

- insert or update `MarkingCodeImport`
- insert accepted `MarkingCode` rows
- bind import to `MarkingOrder` if applicable
- update `MarkingOrder.status`
- finalize `MarkingCodeImport.status`

Reason:

- import row, codes, and binding should converge atomically

### F.2 Suggested Sequence In Practice

Practical implementation sequence:

1. file moved to `processing`
2. hash computed
3. duplicate-file precheck
4. parse and build normalized in-memory import result
5. start DB transaction
6. create `MarkingCodeImport` as `Processing`
7. insert accepted codes
8. decide and store `Bound` or `ManualReview`
9. if bound, update order status and code statuses
10. commit transaction
11. move file to final folder

## G. Failure Handling

### G.1 Malformed File

Examples:

- unreadable encoding
- no parseable rows
- missing usable code values in all rows

Expected result:

- import status = `Failed`
- file moved to `failed`
- no `MarkingCode` rows inserted

### G.2 Duplicate File

Expected result:

- short-circuit before persistence of new codes
- no new `MarkingCode` rows
- file may be moved directly to `archive` or rejected operationally

MVP 1 note:

- if no import row is created for duplicates, the coordinator still needs a structured duplicate result

### G.3 Partial Parse Failure

Condition:

- some rows valid, some rows invalid

Expected result:

- valid rows persisted
- invalid row counts recorded
- import does not fail if enough valid structure remains

Rule:

- partial row failure is acceptable
- whole-file failure is only for structurally unusable input

### G.4 Unknown Target Order

Condition:

- file parsed but no safe target order found

Expected result:

- import status = `ManualReview`
- codes may still be persisted if the design wants manual review on already-stored import content

Recommended MVP 1 behavior:

- persist import record
- persist codes only if they can safely exist under a temporary unresolved import record
- otherwise defer code persistence until manual binding

Safer first implementation:

- persist import record first
- persist parsed codes only after there is either a bound order or an explicit decision that unresolved codes are allowed in storage

### G.5 Mixed GTIN Ambiguity

Condition:

- file contains multiple GTIN groups or conflicting metadata

Expected result:

- import status = `ManualReview` or `Failed` depending on severity

Conservative MVP 1 rule:

- if mixed GTIN clearly violates expected file semantics, prefer `Failed`
- if mixed GTIN may still be legitimate but unclear, prefer `ManualReview`

### G.6 DB Failure During Import

Expected result:

- DB transaction rolled back
- import status remains absent or not committed
- file stays in `processing` or moves to `failed` after coordinator-level handling

Practical recommendation:

- move to `failed`
- log exact failure
- avoid leaving the file silently stuck in `processing`

### G.7 File Move Failure

Case 1: move to `processing` fails

- do not start DB processing
- operational error only

Case 2: final move after DB commit fails

- DB remains source of truth
- import may already be committed successfully
- file operation should be retried or manually reconciled

This is one of the few places where filesystem state may lag behind DB state.

## H. Minimal Service Shape

Do not build a large service zoo. A small decomposition is enough.

### H.1 Recommended Components

#### `MarkingImportCoordinator`

Responsibilities:

- orchestrates the whole pipeline
- file movement
- hash computation
- parser call
- matcher call
- persistence orchestration
- final import result

#### `MarkingFileParser`

Responsibilities:

- detect delimiter and source type
- parse rows
- normalize code values
- derive GTIN and quantity hints
- collect row-level statistics

#### `MarkingOrderMatcher`

Responsibilities:

- apply matching order
- exact request number first
- conservative fallback matching
- produce `AutoBind` or `ManualReview`

### H.2 Suggested DTOs

#### `MarkingParsedFile`

Contains:

- source type
- file hash
- normalized rows
- detected request number
- detected GTIN set
- detected quantity
- duplicate row count
- invalid row count

#### `MarkingImportDecision`

Contains:

- decision type: `Bound`, `ManualReview`, `Failed`, `DuplicateFile`
- target `MarkingOrderId` if available
- match confidence if available
- explanation or reason

#### `MarkingImportResult`

Contains:

- import id if created
- final status
- counts
- duplicate file flag
- manual review flag
- bound order id if any

## I. Storage Integration Touchpoints

These are the storage methods the pipeline truly needs first.

### I.1 Required First

`MarkingOrder`

- get by request number
- get candidate orders by order state / order id / GTIN query shape
- update order status after successful binding

`MarkingCodeImport`

- find import by file hash
- add import row
- update import row status and matching outcome

`MarkingCode`

- existence check by raw code
- bulk insert accepted codes
- count codes by order for readiness

### I.2 Probably Needed Immediately

- bind import to order
- get unresolved imports for manual review backlog
- update code statuses after successful bind

### I.3 Not Needed In First Pipeline Cut

- print batch persistence
- print readiness query beyond a simple count/status check
- any transport-related write methods

### I.4 Practical Correction To Storage Plan

The storage plan should be interpreted with these pipeline-first priorities:

- `FindMarkingCodeImportByHash(...)` is mandatory in the first cut
- `ExistsMarkingCodeByRaw(...)` is mandatory in the first cut
- `AddMarkingCodes(...)` bulk insert is more useful than only single-row insert
- a candidate-order query for matching will likely be needed early, even if it was not fully specified yet

Suggested addition to the future storage surface:

- `IReadOnlyList<MarkingOrder> GetCandidateMarkingOrders(string? requestNumber, string? gtin, int? quantity, int take);`

This should remain conservative and query-oriented, not "smart" by itself.

## J. Rollout Plan

Recommended rollout:

1. parser + hash + source detection
2. duplicate-file and duplicate-code checks
3. import persistence draft
4. order matching draft
5. manual review state support
6. only then print readiness

Practical sequencing rule:

- first prove that files can be parsed and normalized safely
- then prove that persistence is idempotent
- only then add matching and downstream workflow

## Recommendation For Next Step

Next implementation step should be:

- parser + import service draft

Do not start with UI or print integration. The first real code should focus on:

- parsing
- normalization
- duplicate handling
- import decision shaping

