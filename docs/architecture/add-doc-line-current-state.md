# Purpose

This document captures the current implementation of document line addition in FlowStock before any migration to server-centric writes for `AddDocLine`.

Scope of this document:

- current WPF line-add flow;
- current Server/API line-add flow;
- TSD usage of `POST /api/docs/{docUid}/lines`;
- DB reads and writes involved in line insertion and line-side mutations;
- `doc_lines` behavior;
- validation and idempotency semantics;
- differences between WPF, `/api/docs/{docUid}/lines`, `/api/ops`, and legacy import paths;
- migration risks.

This is a current-state inventory only. No target design is proposed here.

# Current WPF flow

## Entry points

Interactive WPF line addition is initiated from code-behind, not from ViewModels or `ICommand`.

ViewModel/command-based line add: not found.

Main WPF interactive entry points:

- `apps/windows/FlowStock.App/OperationDetailsWindow.xaml`
  - button `+ Товар`
  - button `Заполнить из заказа`
- `apps/windows/FlowStock.App/OperationDetailsWindow.xaml.cs`
  - `DocAddLine_Click()`
  - `DocFillFromOrder_Click()`
  - `FillProductionReceiptFromOrder()`
  - `OutboundHuApply_Click()`
  - indirect line mutations through:
    - `DocEditLine_Click()`
    - `AssignHuButton_Click()`
    - `AutoHuButton_Click()`

## WPF interactive local path

Files and roles:

- `apps/windows/FlowStock.App/OperationDetailsWindow.xaml.cs`
  - UI flow and local pre-validation
  - local merge behavior before calling core service
- `apps/windows/FlowStock.App/AppServices.cs`
  - wires WPF directly to `PostgresDataStore`
- `apps/windows/FlowStock.Core/Services/DocumentService.cs`
  - core `AddDocLine()` / `UpdateDocLineQty()` / `AssignDocLineHu()` / order-fill methods
- `apps/windows/FlowStock.Data/PostgresDataStore.cs`
  - direct `doc_lines` insert/update/delete

Current manual line-add flow in `DocAddLine_Click()`:

1. ensure selected document exists, is `DRAFT`, and is not on recount;
2. save dirty header if needed;
3. for outbound with bound order, manual add is blocked;
4. optionally filter selectable items by current source location/HU:
   - `MOVE`: source location + optional source HU
   - `OUTBOUND`: source location + optional source HU
5. open `ItemPickerWindow`;
6. open `QuantityUomDialog`;
7. resolve effective line locations/HU through `TryGetLineLocations()`;
8. locally search an existing same-key line in `IDataStore.GetDocLines()` and:
   - merge via `DocumentService.UpdateDocLineQty()` if same item/location/HU;
   - otherwise append via `DocumentService.AddDocLine()`.

Current WPF merge key:

- `item_id`
- `from_location_id`
- `to_location_id`
- normalized `from_hu`
- normalized `to_hu`

Current WPF merge behavior:

- WPF merges matching lines locally before writing;
- server `AddDocLine()` itself does not merge;
- if UOM matches, `qty_input` is summed and `uom_code` preserved;
- if UOM differs, merged line keeps base quantity but clears input-UOM data.

## WPF order-derived line population

There are separate local flows that populate `doc_lines`, but they are not the `+ Товар` dialog.

Files and roles:

- `apps/windows/FlowStock.Core/Services/DocumentService.cs`
  - `ApplyOrderToDoc()`
  - `ApplyOrderToProductionReceipt()`
- `apps/windows/FlowStock.App/OperationDetailsWindow.xaml.cs`
  - `TryApplyOrderSelection()`
  - `TryApplyReceiptOrderSelection()`
  - `DocFillFromOrder_Click()`

Current behavior:

- `OUTBOUND` order apply deletes current lines and re-adds remaining shipment lines from order;
- `ProductionReceipt` fill-from-order can optionally replace current lines, then add remaining receipt lines;
- these flows write directly through `IDataStore.AddDocLine()` inside `DocumentService`, not through the HTTP line endpoint.

## WPF line split flows

Additional local paths create new `doc_lines` as part of editing/splitting, not as plain add:

- `AssignHuButton_Click()` -> `DocumentService.AssignDocLineHu()`
  - may split one line into two when partial quantity is assigned to HU
- `AutoHuButton_Click()` / `AutoDistributeProductionReceiptHus()`
  - may delete old lines and create replacement lines per HU
- `OutboundHuApply_Click()`
  - may reduce/delete the original outbound line and create a new HU/location-specific line

These are local draft-line mutation paths and bypass the server line endpoint.

## WPF-specific validation before calling core service

Files:

- `apps/windows/FlowStock.App/OperationDetailsWindow.xaml.cs`
  - `TryGetLineLocations()`
  - `ValidateLineLocations()`
  - `TryValidateReceiptQty()`

Current WPF checks:

- document must be editable (`DRAFT`, not recount);
- move/outbound item picker may be filtered by source stock;
- locations must be chosen according to doc type;
- for `MOVE` in internal move mode, same location is required and at least one HU must be set;
- for normal `MOVE`, same location without HU is rejected;
- production receipt edit path validates order remaining qty for `order_line_id`;
- manual add in outbound is blocked when order is bound.

Important current detail:

- for `ProductionReceipt`, `TryGetLineLocations()` forces `toHu = null` for manual add;
- line-level HU is expected later through explicit HU assignment / auto distribution, not at add time.

## Legacy local import path

Files and roles:

- `apps/windows/FlowStock.Core/Services/ImportService.cs`
  - `ProcessEvent()`
- `apps/windows/FlowStock.Data/PostgresDataStore.cs`
  - `AddDocLine()`
  - `AddImportedEvent()`

Current import behavior:

- JSONL import can append `doc_lines` directly to locally found/created documents;
- it does not use `doc_uid`, `api_docs`, or `api_events`;
- idempotency is handled through `imported_events`, not API events.

# Current Server/API flow

## Mapped endpoints

Files and roles:

- `apps/windows/FlowStock.Server/DocumentDraftEndpoints.cs`
  - `POST /api/docs/{docUid}/lines`
- `apps/windows/FlowStock.Server/Program.cs`
  - `GET /api/docs/{docId}/lines`
- `apps/windows/FlowStock.Server/ApiModels.cs`
  - `AddDocLineRequest`
- `apps/windows/FlowStock.Core/Services/DocumentService.cs`
  - shared `AddDocLine()` primitive reused by server wrapper
- `apps/windows/FlowStock.Server/PostgresApiDocStore.cs`
  - `api_events` lookup/write for `DOC_LINE`

## Canonical remote line-add path

Current remote line-add flow is implemented in:

- `apps/windows/FlowStock.Server/DocumentDraftEndpoints.cs`
  - method `HandleAddLineAsync()`

Current flow steps:

1. read raw request body;
2. reject empty body / invalid JSON;
3. require `event_id`;
4. check `api_events`:
   - if same `event_id` already recorded as `DOC_LINE` for the same `doc_uid`, return `ok = true`;
   - otherwise return `EVENT_ID_CONFLICT`;
5. resolve `api_docs` by `doc_uid`;
6. resolve underlying `docs` row by `doc_id`;
7. special recount handling:
   - if comment contains `RECOUNT` and doc is still `DRAFT`, delete all current lines and reset comment to `TSD`;
8. require `api_docs.status = DRAFT`;
9. require `qty > 0`;
10. parse `doc_type` from `api_docs.doc_type`;
11. validate requested line HU codes if present;
12. resolve item by `item_id` or `barcode` (with 13/14 digit barcode variant fallback);
13. derive effective line locations/HU from document type and request/header metadata;
14. call `DocumentService.AddDocLine(...)`;
15. record `DOC_LINE` event in `api_events`;
16. fetch the last inserted line for that item in the document and return it.

## Request shape

`AddDocLineRequest` currently supports:

- `event_id`
- `device_id`
- `barcode`
- `item_id`
- `order_line_id`
- `qty`
- `uom_code`
- `from_location_id`
- `to_location_id`
- `from_hu`
- `to_hu`

Current required fields:

- `event_id`
- `qty > 0`
- either `item_id` or resolvable `barcode`
- required locations may come from document header/api metadata rather than from the line payload

## Effective location/HU derivation by doc type

Current `HandleAddLineAsync()` behavior:

- `INBOUND`
  - `to_location_id` comes from `api_docs.to_location_id`
  - `to_hu` comes from `api_docs.to_hu`
  - request `to_location_id` / `to_hu`: ignored
- `ProductionReceipt`
  - same as inbound
  - request `to_location_id` / `to_hu`: ignored
- `OUTBOUND`
  - `from_location_id` = request override or `api_docs.from_location_id`
  - `from_hu` = request override or `api_docs.from_hu`
- `MOVE`
  - both locations and both HU come from `api_docs`
  - request line-level location/HU overrides are ignored
- `WriteOff`
  - `from_location_id` comes from `api_docs.from_location_id`
  - `from_hu` = request override or `api_docs.from_hu`
- `Inventory`
  - `to_location_id` comes from `api_docs.to_location_id`
  - `to_hu` = request override or `api_docs.to_hu`

Important current detail:

- `POST /api/docs/{docUid}/lines` is not a generic free-form line insert;
- for most types it depends on header metadata already persisted through `POST /api/docs`.

## Server-side read path used by TSD resume

Files:

- `apps/windows/FlowStock.Server/Program.cs`
  - `GET /api/docs/{docId}/lines`

Current behavior:

- resolves `docs` by numeric `doc_id`;
- reads `IDataStore.GetDocLineViews(docId)`;
- maps each row through `MapDocLine()`.

This is read-side only, but it is part of current line lifecycle because TSD uses it to reconstruct editable drafts.

# Current DB interactions

## Core tables

Primary write table for line add:

- `doc_lines`

Related reads/writes:

- `docs`
  - read to ensure draft/editable state
  - comment may be updated from recount marker to `TSD` in API line flow
- `api_docs`
  - read for `doc_uid -> doc_id`, status, doc type, header metadata
- `api_events`
  - read/write for `DOC_LINE` idempotency
- `imported_events`
  - legacy JSONL idempotency, outside API flow
- `ledger`
  - not written by line add

## `doc_lines` schema and writes

Files:

- `apps/windows/FlowStock.Data/PostgresDataStore.cs`

Current insert shape:

- `doc_id`
- `order_line_id`
- `item_id`
- `qty`
- `qty_input`
- `uom_code`
- `from_location_id`
- `to_location_id`
- `from_hu`
- `to_hu`

Current related DB methods:

- `GetDocLines(long docId)`
- `GetDocLineViews(long docId)`
- `AddDocLine(DocLine line)`
- `UpdateDocLineQty(long docLineId, ...)`
- `UpdateDocLineHu(long docLineId, ...)`
- `UpdateDocLineOrderLineId(long docLineId, ...)`
- `DeleteDocLine(long docLineId)`
- `DeleteDocLines(long docId)`

Current API line-add side effects on DB:

- `doc_lines`: append one new row
- `api_events`: insert `DOC_LINE`
- `docs.comment`: may change from recount comment to `TSD`
- `doc_lines`: may all be deleted first when recount draft is resumed through TSD

Current WPF line-add side effects on DB:

- may append one row;
- may update an existing row instead of appending;
- may delete/recreate rows during HU split/distribution/order fill;
- does not write `api_events` / `api_docs`.

# doc_lines behavior

## Server/API path

Current server line-add behavior is append-only per accepted event:

- no line merge by same item/location/HU;
- no line update/upsert by semantic key;
- each accepted new `DOC_LINE` event appends a new `doc_lines` row;
- exact replay by same `event_id` returns success without appending a second row.

Current return payload:

- `ok = true`
- `line = { id, item_id, qty, uom_code }`

Current line lookup for response:

- fetch all lines for the doc;
- filter by `item_id`;
- return the latest by highest `id`.

This means the response is not tied to `event_id` directly; it assumes the last line for the same item is the inserted one.

## WPF local path

Current WPF manual-add behavior is merge-or-append:

- merge matching line locally when same item/location/HU key is found;
- otherwise append;
- line quantity edits happen through separate update path.

## TSD local draft behavior

Files:

- `apps/android/tsd/app.js`
  - `addLineWithQuantity()`
  - `buildLineData()`
  - `buildLineKey()`
  - `findLineIndex()`
  - `submitDocToServer()`

Current TSD behavior has two stages:

1. local draft capture in IndexedDB:
   - scan/manual add merges lines locally by `buildLineKey()`
2. remote upload:
   - iterate local lines and send one `POST /api/docs/{docUid}/lines` per local line

Current TSD local merge key:

- `INBOUND`: barcode + target location label
- `OUTBOUND`: barcode + source location label
- `MOVE`: barcode + source label + target label + from_hu + to_hu
- `WRITE_OFF`: barcode + source label + reason
- `INVENTORY`: barcode + location label + to_hu

Important current detail:

- TSD merges locally before upload;
- server endpoint does not repeat that merge logic.

# Validation rules

## Core business validation in `DocumentService.AddDocLine()`

Files:

- `apps/windows/FlowStock.Core/Services/DocumentService.cs`
  - `AddDocLine()`
  - `ValidateLineLocations()`

Current core checks:

- `qty > 0`
- document exists
- document status is `DRAFT`
- item exists
- locations/HU combination is valid for document type

Current `ValidateLineLocations()` rules:

- `INBOUND`: requires target location
- `ProductionReceipt`: requires target location
- `WRITE_OFF`: requires source location
- `OUTBOUND`: requires source location
- `MOVE`: requires both locations; same location without any HU is rejected
- `INVENTORY`: requires target location

What is not validated in `AddDocLine()`:

- stock availability
- `order_line_id` existence
- `order_line_id` belonging to current doc order
- qty remaining vs order limits at add time
- HU existence/status

These checks are handled elsewhere or not handled in current add path.

## Server wrapper validation in `HandleAddLineAsync()`

Additional current server checks:

- valid JSON and non-empty body
- `event_id` required
- `api_docs` mapping must exist
- `api_docs.status` must be `DRAFT`
- `qty > 0`
- `doc_type` from `api_docs` must parse
- requested HU codes must exist and be in allowed status (`ACTIVE` or `OPEN`)
- item must resolve by `item_id` or `barcode`
- required effective location must be resolvable from doc header and/or allowed override

Special current behavior:

- recount draft line upload clears all previous lines on first add after recount marker

## WPF local pre-validation

Additional current WPF checks:

- document must not be on recount
- source-stock filtering for item picker in `MOVE` / `OUTBOUND`
- outbound manual add blocked when order is bound
- production-receipt edit path validates order remaining for bound order lines
- UI-specific move/internal-move checks around same-location moves and HU presence

## TSD local pre-validation

Files:

- `apps/android/tsd/app.js`
  - `addLineWithQuantity()`
  - `validateHuLocationsForSubmit()`

Current TSD checks:

- line qty must be positive
- for internal move from HU, local quantity cannot exceed stock on the selected HU
- before submit, selected HU/location combinations are checked against exported HU stock snapshot

These are client-side guards, not authoritative server rules.

# Known invariants

- stock is derived from ledger only;
- line add does not write ledger;
- line add does not close the document;
- line add requires a persisted draft context;
- closed documents are immutable for line add;
- current canonical remote line lifecycle depends on `doc_uid -> doc_id` mapping in `api_docs`;
- `POST /api/docs/{docUid}/lines` is coupled to header metadata already persisted via `POST /api/docs`.

# Idempotency / event semantics

## Server/API `DOC_LINE`

Files:

- `apps/windows/FlowStock.Server/DocumentDraftEndpoints.cs`
- `apps/windows/FlowStock.Server/PostgresApiDocStore.cs`

Current semantics:

- `event_id` is required;
- if the same `event_id` already exists as `DOC_LINE` for the same `doc_uid`, server returns `ok = true`;
- server does not compare replay payload for conflicts in the current line endpoint;
- if the same `event_id` exists for another event type or another `doc_uid`, server returns `EVENT_ID_CONFLICT`;
- accepted new line request records `DOC_LINE` in `api_events`.

Current replay response detail:

- replay returns plain `ApiResult(true)`, not the previously created line payload.

## WPF local path

Idempotency/event semantics:

- not found

WPF local line add has no `event_id` or replay log.

## TSD local + remote

Current TSD semantics:

- local draft has no remote idempotency until submit;
- on submit each uploaded line gets a fresh generated `event_id`;
- TSD depends on server `DOC_LINE` replay semantics only during HTTP upload.

## Legacy import

Current import semantics:

- idempotency handled through `imported_events`;
- no `api_events` / `DOC_LINE`.

# Differences between WPF and Server

1. Identity model differs.
   - WPF uses local `doc_id` and direct DB writes.
   - Server line add uses `doc_uid` + `api_docs` mapping.

2. Merge behavior differs.
   - WPF merges matching lines locally before writing.
   - Server endpoint appends one line per accepted event and does not merge.

3. Event semantics differ.
   - WPF: no `event_id`.
   - Server: `DOC_LINE` replay/idempotency via `api_events`.

4. Recount behavior differs.
   - WPF blocks editing on recount drafts.
   - Server/TSD can reopen recount flow by clearing lines and continuing upload.

5. HU behavior differs.
   - WPF production receipt manual add forces no line HU at add time.
   - Server production receipt line add uses `api_docs.to_hu` if header has one.

6. Order-bound behavior differs.
   - WPF blocks manual outbound add when order is selected.
   - Server line endpoint does not explicitly block adding outbound lines to order-bound drafts.

7. Line payload freedom differs.
   - WPF derives locations/HU from current UI and can update existing lines.
   - Server line endpoint accepts only limited per-line overrides depending on doc type.

8. Order validation differs.
   - WPF edit flow validates receipt qty against order remaining for bound receipt lines.
   - server `AddDocLine()` does not validate `order_line_id` or remaining qty at add time.

# Differences between API lines flow and /api/ops

## API lines flow (`POST /api/docs/{docUid}/lines`)

Current characteristics:

- draft lifecycle path
- requires `doc_uid`
- requires prior `api_docs` mapping
- records `DOC_LINE` in `api_events`
- appends line to an existing draft
- does not close document

## `/api/ops`

Files:

- `apps/windows/FlowStock.Server/OpsEndpoint.cs`

Current characteristics:

- compatibility wrapper, not resumable draft line lifecycle
- no `doc_uid`
- no `api_docs`
- uses `OP` event type, not `DOC_LINE`
- may create document if `doc_ref` does not exist
- adds one line and immediately proceeds to close

# Risks of migration

1. There are multiple line-creation paths, not one.
   Current state includes WPF manual add, WPF order-fill/split flows, API draft lines flow, `/api/ops`, and JSONL import.

2. WPF local semantics already include merge behavior.
   Migrating only server path without deciding merge policy will change user-visible behavior.

3. TSD also merges locally before upload.
   Server append-only behavior may diverge from local draft semantics if clients change or retries happen differently.

4. Current line replay semantics are weak.
   Same `event_id` replay for `DOC_LINE` returns success without payload comparison and without reconstructing the created line payload.

5. Response correlation is heuristic.
   Server returns the latest line for the same item, not a line explicitly bound to `event_id`.

6. Recount behavior is spread across wrapper and client flows.
   WPF blocks recount edit, while server/TSD clears lines and resumes through API.

7. Order-line semantics are not centralized at add time.
   `order_line_id` existence and remaining-qty checks are partial and path-dependent.

8. Header/line coupling is strong in API flow.
   Effective locations/HU often come from `api_docs`, so line add behavior depends on prior create/upsert semantics.

9. Production receipt HU semantics differ by client.
   WPF manual add expects later HU assignment; server line add can inherit header HU.

10. `/api/ops` still adds lines outside the `doc_uid` lifecycle.
    Migrating `POST /api/docs/{docUid}/lines` alone will not migrate all server-side line writes.

11. Import JSONL still creates lines through `imported_events`.
    This is another legacy write path outside canonical remote draft lifecycle.

# Golden test scenarios

1. WPF manual add merges into an existing same-key line instead of inserting a second row.
2. WPF manual add for outbound with bound order is blocked.
3. WPF fill-from-order for production receipt creates lines with `order_line_id` and target location/HU from header.
4. `POST /api/docs/{docUid}/lines` with missing `event_id` fails.
5. `POST /api/docs/{docUid}/lines` with `qty <= 0` fails.
6. `POST /api/docs/{docUid}/lines` for unknown `doc_uid` returns `DOC_NOT_FOUND`.
7. `POST /api/docs/{docUid}/lines` exact replay with same `event_id` returns success without duplicating `doc_lines`.
8. `POST /api/docs/{docUid}/lines` same `event_id` for different doc/event type returns `EVENT_ID_CONFLICT`.
9. `POST /api/docs/{docUid}/lines` with unknown item fails.
10. `POST /api/docs/{docUid}/lines` appends a new row and records `DOC_LINE`.
11. `POST /api/docs/{docUid}/lines` for recount draft clears previous lines and then accepts the new line.
12. TSD local merge plus remote upload results in one API line call per merged local line.
13. `/api/ops` adds a line and immediately closes, without `api_docs` or `DOC_LINE`.
14. JSONL import appends a line and records `imported_events`, not `api_events`.

# Open questions

1. Should canonical server-centric `AddDocLine` preserve WPF/TSD merge behavior, or standardize on append-only semantics?

2. Should `POST /api/docs/{docUid}/lines` remain an append endpoint only, or evolve toward line upsert/update semantics?

3. Is it intentional that `DOC_LINE` replay does not compare payload for conflicts?

4. Should replay of `DOC_LINE` return the original created line payload instead of plain `ok = true`?

5. Should `order_line_id` be validated at add time against the current draft order and remaining quantities?

6. Is the current difference between WPF outbound manual-add block and server line-add permissiveness intentional?

7. For `ProductionReceipt`, should line add inherit header HU in the canonical remote contract, or should HU remain a later line-assignment step as in current WPF manual add?

8. Should recount line-reset behavior remain inside the line endpoint, or move to an explicit recount-resume operation?

9. Should `/api/ops` remain allowed to create/add lines outside the `doc_uid` draft lifecycle?

10. Should JSONL import remain outside the canonical line-add migration scope?

11. Dedicated AddDocLine integration tests in a separate suite: not found.
    Current automated coverage is indirect through:
    - `apps/windows/FlowStock.Server.Tests/CloseDocument/TsdCompatibilityTests.cs`
