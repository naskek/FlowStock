# Purpose

This document captures the current implementation of document closing/posting in FlowStock before any migration to server-centric writes.

Scope of this document:

- current WPF close/post flow;
- current Server/API close/post flow;
- DB writes and reads involved in close;
- ledger behavior on close;
- validation rules;
- known invariants;
- differences and migration risks.

This is a current-state inventory only. No target design is proposed here.

# Current WPF flow

## Entry points

WPF document closing is initiated from code-behind, not from ViewModels or `ICommand`.

ViewModel/command-based close implementation: not found.

Main WPF entry points:

- `apps/windows/FlowStock.App/MainWindow.xaml`
  - menu item `Закрыть текущий документ`, click handler `DocClose_Click`
- `apps/windows/FlowStock.App/MainWindow.xaml.cs`
  - method `TryCloseSelectedDoc()`
- `apps/windows/FlowStock.App/OperationDetailsWindow.xaml`
  - button `DocCloseButton`, click handler `DocClose_Click`
- `apps/windows/FlowStock.App/OperationDetailsWindow.xaml.cs`
  - method `TryCloseCurrentDoc()`
  - `OperationDetailsWindow_PreviewKeyDown()` also routes `Ctrl+Enter` to close

## WPF runtime path

Common WPF service path:

1. UI event handler in `MainWindow` or `OperationDetailsWindow`
2. `AppServices.Documents`
3. `FlowStock.Core.Services.DocumentService.TryCloseDoc(long docId, bool allowNegative)`
4. `FlowStock.Data.PostgresDataStore` via `IDataStore`
5. `docs` status update + `ledger` writes

Relevant composition:

- `apps/windows/FlowStock.App/AppServices.cs`
  - creates `PostgresDataStore`
  - constructs shared `DocumentService`

## WPF initiator details

### `MainWindow` path

File: `apps/windows/FlowStock.App/MainWindow.xaml.cs`

Method: `TryCloseSelectedDoc()`

Behavior:

- requires selected row in `DocsGrid`
- blocks if document is already `Closed`
- blocks if `doc.IsRecountRequested`
- calls `DocumentService.TryCloseDoc(doc.Id, allowNegative: false)`
- if `result.Errors` is not empty, shows them
- if `result.Warnings` is not empty, asks user confirmation and retries with `allowNegative: true`
- on success reloads docs and stock, and reloads orders for `Outbound`

Notable details:

- no explicit exception handling around `TryCloseDoc()`
- no header-save step, because this screen does not edit the document header directly

### `OperationDetailsWindow` path

File: `apps/windows/FlowStock.App/OperationDetailsWindow.xaml.cs`

Method: `TryCloseCurrentDoc()`

Behavior before service call:

- blocks if `_doc` is null
- blocks if already `Closed`
- blocks if `_doc.IsRecountRequested`
- if `_hasUnsavedChanges`, calls `TrySaveHeader()` first
- blocks if document has zero lines
- blocks if partner is required and missing
- for `Outbound`, runs extra UI-side `TryValidateOutboundStock(doc.Id)`

Service call behavior:

- calls `DocumentService.TryCloseDoc(doc.Id, allowNegative: false)`
- catches exceptions and logs via `AppLogger`
- shows `Errors` if returned
- if `Warnings` are returned:
  - for `Outbound`, shows warning and stops
  - for other types, asks confirmation and retries with `allowNegative: true`
- on success closes the window

Notable detail:

- `TrySaveHeader()` may persist pending header fields before close, so this window has a side effect that `MainWindow` does not have.

## WPF extra pre-close logic that is outside `DocumentService`

These checks are not part of the shared close service itself:

- `OperationDetailsWindow.TrySaveHeader()`
- `OperationDetailsWindow.IsPartnerRequired()`
- `OperationDetailsWindow.TryValidateOutboundStock()`
- `MainWindow` / `OperationDetailsWindow` checks for `IsRecountRequested`

This means part of the current close behavior is UI-specific and duplicated/asymmetric across WPF screens.

# Current Server/API flow

## Shared service layer

Server close uses the same core close service as WPF.

Files:

- `apps/windows/FlowStock.Server/Program.cs`
  - DI registers `PostgresDataStore` as `IDataStore`
  - DI registers `DocumentService`
- `apps/windows/FlowStock.Core/Services/DocumentService.cs`
  - same `TryCloseDoc()` method is reused

This means current close business logic is mostly shared between WPF and Server.

## Explicit API close endpoint

Files:

- `apps/windows/FlowStock.Server/Program.cs`
  - endpoint `POST /api/docs/{docUid}/close`
- `apps/windows/FlowStock.Server/ApiModels.cs`
  - model `CloseDocRequest`
- `apps/windows/FlowStock.Server/IApiDocStore.cs`
  - API doc/event metadata abstraction
- `apps/windows/FlowStock.Server/PostgresApiDocStore.cs`
  - concrete `api_docs` / `api_events` store

Current route behavior:

1. reads raw JSON body
2. deserializes `CloseDocRequest`
3. requires `event_id`
4. checks `api_events` for idempotency/conflict
5. loads `api_docs` row by `docUid`
6. calls `DocumentService.TryCloseDoc(docInfo.DocId, allowNegative: false)`
7. if core close succeeds:
   - updates `api_docs.status` to `CLOSED`
   - records `DOC_CLOSE` event in `api_events`
8. returns `{ ok, closed, doc_ref, warnings }`

Important observation:

- `allowNegative: true` path is not exposed in the API close route
- the route uses `api_docs.doc_uid -> docs.id` mapping before calling core close

## Online immediate-close API flow

File: `apps/windows/FlowStock.Server/Program.cs`

Endpoint: `POST /api/ops`

Current behavior:

1. resolves or creates a `docs` row
2. adds a line via `docs.AddDocLine(...)`
3. immediately calls `docs.TryCloseDoc(docId, allowNegative: false)`
4. records API event via `apiStore.RecordOpEvent(...)`

This is a separate server path from `/api/docs/{docUid}/close`.

Important observation:

- online `/api/ops` does not use `api_docs`
- it closes immediately after line insert

## Draft create + line add API flow

Files:

- `apps/windows/FlowStock.Server/Program.cs`
  - `POST /api/docs`
  - `POST /api/docs/{docUid}/lines`

Current behavior:

- `/api/docs` creates server-side draft metadata and underlying `docs` row
- `/api/docs/{docUid}/lines` appends lines to the mapped draft
- explicit close is expected via `/api/docs/{docUid}/close`

Observation:

- `draft_only` exists on `CreateDocRequest`
- current code uses `draft_only` for header/required-field checks
- automatic close inside `/api/docs` was not found

## TSD usage

TSD explicit usage of `POST /api/docs/{docUid}/close`: not found.

Files inspected:

- `apps/android/tsd/storage.js`
- `apps/android/tsd/app.js`

What was found instead:

- TSD can call `POST /api/docs`
- TSD can call `POST /api/docs/{docUid}/lines`
- TSD JSONL export path marks local documents `READY -> EXPORTED`

Observed server-upload path in TSD:

- `apps/android/tsd/app.js`
  - posts document header to `/api/docs`
  - posts each line to `/api/docs/{docUid}/lines`
  - no subsequent `/close` call found in the same flow

Conclusion for TSD:

- explicit close via API is not currently observable in the TSD client code
- current TSD close/post scenario via server API: not found

# Current DB interactions

## Core close transaction

File: `apps/windows/FlowStock.Data/PostgresDataStore.cs`

Core transaction entry:

- `ExecuteInTransaction(Action<IDataStore> work)`
  - opens `NpgsqlConnection`
  - starts SQL transaction
  - creates scoped `PostgresDataStore(connection, transaction)`
  - commits on success

Important detail:

- `CreateCommand()` attaches `_transaction` when present
- therefore all `store.*` calls inside `DocumentService.TryCloseDoc()` run in one DB transaction

## Reads used during validation and close

Primary read methods used by `DocumentService`:

- `GetDoc(long id)`
  - reads current `docs` row
- `GetDocLines(long docId)`
  - reads `doc_lines`
- `GetItems(null)`
  - reads `items`
- `GetLocations()`
  - reads `locations`
- `GetOrderReceiptRemaining(long orderId)`
  - computes closed production receipts by `docs + doc_lines`
- `GetOrderShipmentRemaining(long orderId)`
  - computes closed shipments by `docs + doc_lines`
- `GetAvailableQty(...)`
  - currently delegates to `GetLedgerBalance(...)`
- `GetLedgerBalance(...)`
  - `SELECT COALESCE(SUM(qty_delta), 0) FROM ledger ...`
- `GetHuStockRows()`
  - aggregates `ledger` by `HU + item + location`
- `GetLedgerTotalsByItem()`
  - aggregates `ledger` by item
- `GetLedgerTotalsByHu()`
  - aggregates `ledger` by HU

Tables touched by current close validation:

- `docs`
- `doc_lines`
- `items`
- `locations`
- `orders`
- `order_lines`
- `ledger`
- `hus`
- `km_code` logic exists in code but is currently disabled for close by `KmWorkflowEnabled => false`

## Writes used during close

Inside `DocumentService.TryCloseDoc()`:

- optional `UpdateDocHeader(...)`
  - used only to infer and persist `shippingRef` from lines when header HU is empty
- multiple `AddLedgerEntry(...)`
  - `INSERT INTO ledger(ts, doc_id, item_id, location_id, qty_delta, hu_code, hu)`
- `UpdateDocStatus(...)`
  - `UPDATE docs SET status = @status, closed_at = @closed_at WHERE id = @id`

## Server-only metadata writes around close

Only on the HTTP close path:

- `PostgresApiDocStore.UpdateApiDocStatus(...)`
  - `UPDATE api_docs SET status = @status ...`
- `PostgresApiDocStore.RecordEvent(...)`
  - `INSERT INTO api_events(...) ... ON CONFLICT DO NOTHING`

Important observation:

- these `api_docs` / `api_events` writes are not part of the same DB transaction as `DocumentService.TryCloseDoc()`

## DB interactions not found in close flow

- `stock_reservation_lines` usage in close path: not found
- `IApiDocStore.ClearReservations(...)` in close path: not found

# Ledger behavior

Current source of truth:

- stock is derived from `ledger`
- documents affect stock only on close/post

Core implementation file:

- `apps/windows/FlowStock.Core/Services/DocumentService.cs`
  - method `TryCloseDoc()`
  - helper `ResolveLedgerHu()`
  - helper `ResolveShippingRefFromLines()`

Ledger behavior by document type:

| Doc type | Current ledger behavior on close |
| --- | --- |
| `Inbound` | writes one positive ledger entry per line to `ToLocationId` and resolved `toHu` |
| `ProductionReceipt` | same as `Inbound`; positive entry to `ToLocationId` and `toHu` |
| `WriteOff` | writes one negative entry per line from `FromLocationId` and resolved `fromHu` |
| `Move` | writes one negative entry from source and one positive entry to destination |
| `Inventory` | does not write raw counted qty directly; aggregates target totals by `(item, location, HU)` and writes only delta versus current available qty |
| `Outbound` with explicit source location | writes one negative entry from selected source location and resolved `fromHu` |
| `Outbound` without explicit source location | allocates from available ledger balances; if HU is specified, consumes that HU across locations ordered by location code; otherwise consumes non-HU balances first, then HU balances, sorted by location code and HU code |

## HU resolution behavior

Current helper:

- `DocumentService.ResolveLedgerHu(Doc doc, DocLine line, string? docHu)`

Current behavior:

- line HU has priority
- document header HU is fallback only
- for `Inbound` / `Inventory` / `ProductionReceipt`, close uses `line.ToHu ?? docHu`
- for `Outbound` / `WriteOff`, close uses `line.FromHu ?? docHu`
- for `Move`, line HU rules are more specific and may mirror source HU to destination in one case

## Header HU inference during close

Current helper:

- `DocumentService.ResolveShippingRefFromLines(...)`

If document header HU is empty and all relevant lines contain the same non-empty HU:

- close flow updates document header before writing ledger

# Validation rules

## Core validation in `DocumentService.BuildCloseDocCheck()`

File:

- `apps/windows/FlowStock.Core/Services/DocumentService.cs`

Current core checks:

- document exists
- document is not already `Closed`
- `WriteOff` requires `ReasonCode`
- document has at least one line
- per-line quantity must be `> 0`
- required locations by type:
  - `Inbound`: `ToLocationId`
  - `ProductionReceipt`: `ToLocationId`
  - `WriteOff`: `FromLocationId`
  - `Move`: both locations
  - `Inventory`: `ToLocationId`
- `ProductionReceipt` requires HU on each line at close time
- `Move` with same source/destination location requires HU context
- `ProductionReceipt` enforces `item.MaxQtyPerHu` if set
- `ProductionReceipt` checks order receipt remaining by `order_line_id`
- `Outbound` checks order shipment remaining by `order_line_id`
- negative stock checks:
  - explicit source checks via `GetAvailableQty(...)`
  - `Outbound` without explicit source checks total availability
- HU/location consistency check:
  - one HU must not end up in multiple locations after applying the document
- `Outbound` requires document partner

## KM-related validation

KM validation code exists in `BuildCloseDocCheck()` and in `AutoAssignOutboundKmCodes()`, but current repo state disables it for close/post:

- `DocumentService.KmWorkflowEnabled => false`

Current effect:

- close validation does not require KM assignment
- close path does not auto-ship KM codes
- HU assignment checks are not blocked by existing KM links during close-related operations

## WPF-only validation around close

Additional WPF-side checks that are not part of `TryCloseDoc()`:

- pending header save in `OperationDetailsWindow.TrySaveHeader()`
- `IsRecountRequested` gate in both WPF entry points
- partner presence check in `OperationDetailsWindow`
- `OperationDetailsWindow.TryValidateOutboundStock()` UI preview check

## Server-only validation around close endpoint

Additional API checks in `POST /api/docs/{docUid}/close`:

- request body must be non-empty
- JSON must parse into `CloseDocRequest`
- `event_id` is required
- duplicate/conflicting `event_id` is rejected
- `api_docs` entry for `docUid` must exist

# Known invariants

Source files:

- `AGENTS.md`
- `docs/spec.md`

Current invariants explicitly documented in repo:

- stock is derived from `ledger` only
- documents affect stock only on close/post
- closed documents are immutable
- corrections must be separate documents

Observed code alignment:

- `DocumentService.TryCloseDoc()` is the point that writes to `ledger`
- write/update methods such as `UpdateDocHeader()` enforce `Draft` status before edits

# Differences between WPF and Server

## Same core logic

Shared close core:

- both WPF and Server call the same `DocumentService.TryCloseDoc()`
- both ultimately use `PostgresDataStore`

## Different wrappers around the same core

WPF:

- direct in-process call from code-behind
- no HTTP layer
- no `api_docs` or `api_events`

Server:

- HTTP endpoint wrapper
- `docUid -> docId` resolution through `api_docs`
- event idempotency through `api_events`
- additional API metadata update after successful close

## WPF initiators are asymmetric

`MainWindow.TryCloseSelectedDoc()` and `OperationDetailsWindow.TryCloseCurrentDoc()` are not equivalent:

- only `OperationDetailsWindow` saves pending header changes before close
- only `OperationDetailsWindow` catches/logs close exceptions
- only `OperationDetailsWindow` runs extra outbound stock preview before the service call
- post-success behavior differs:
  - `MainWindow` reloads lists
  - `OperationDetailsWindow` closes the window

## Server close vs WPF negative-stock handling

Current WPF wrapper:

- contains a retry path with `allowNegative: true`

Current Server/API wrapper:

- always uses `allowNegative: false`
- no retry/override path exposed via HTTP

Important observation:

- `CloseDocCheck.Warnings` is currently never populated in the inspected code
- therefore `allowNegative` appears to be present in API and WPF wrappers, but warning-based retry logic appears effectively inactive in current code

## Server has an extra close path that WPF does not use

`POST /api/ops`:

- creates/extends operation data
- immediately closes document server-side

This path bypasses the explicit `/api/docs/{docUid}/close` route.

## TSD/API mismatch

Explicit TSD use of `/api/docs/{docUid}/close`: not found.

Observed TSD behavior:

- create draft via `/api/docs`
- append lines via `/api/docs/{docUid}/lines`
- no explicit close call found

This means the documented API close route exists, but active TSD usage of it is not visible in current client code.

# Risks of migration

1. UI behavior is not fully inside `DocumentService`.
   Missing `TrySaveHeader()`, partner UI checks, recount gating, or outbound stock preview may change behavior during migration.

2. WPF has two different close initiators.
   Migrating only one path can leave inconsistent behavior between `MainWindow` and `OperationDetailsWindow`.

3. Core validation runs before the write transaction.
   `BuildCloseDocCheck()` reads outside the later `ExecuteInTransaction()` block, so concurrent changes can invalidate the check before ledger/status writes happen.

4. Server close metadata is not atomic with core close.
   `docs/ledger` writes happen inside `DocumentService` transaction, but `api_docs` and `api_events` are updated separately afterward.

5. `api_docs.status` can drift from `docs.status`.
   The API layer stores its own close status independently.

6. `/api/ops` is a second server-side close mechanism.
   Migrating only `/api/docs/{docUid}/close` does not migrate the full server closing surface.

7. TSD explicit close usage is not visible.
   Migrating close to server-centric execution may not affect the active TSD path if TSD still never calls `/close`.

8. Outbound ledger allocation is complex and easy to regress.
   The no-source outbound path allocates across location/HU balances using non-trivial ordering rules.

9. Inventory close uses delta semantics, not raw snapshot writes.
   A migration that treats inventory like a regular inbound/outbound move would break stock correctness.

10. HU behavior is spread across multiple helpers.
    `ResolveLedgerHu()`, `ResolveShippingRefFromLines()`, and `BuildHuLocationErrors()` all affect the final outcome.

11. Warning/negative-stock semantics are unclear.
    `allowNegative` exists, but warning generation was not found. A migration could accidentally “fix” or remove currently dormant behavior.

12. KM code path is present but disabled.
    Migration must preserve the current disabled state, not accidentally re-enable KM validation or shipment side effects.

# Golden test scenarios

1. Inbound close from WPF details window.
   Draft with one line, `ToLocationId` set, optional HU, expect one positive ledger row and `docs.status = CLOSED`.

2. Production receipt close with order-linked lines.
   Respect order remaining and `max_qty_per_hu`; expect positive ledger rows with line HU.

3. Write-off close.
   Missing reason must fail; valid reason must produce negative ledger rows.

4. Move close with different locations.
   Expect one negative and one positive ledger row per line.

5. Move close with same location and no HU.
   Must fail validation.

6. Inventory close.
   Expect ledger delta versus current balance, not raw counted quantity replay.

7. Outbound close with explicit source location and HU.
   Expect negative ledger rows only from selected source.

8. Outbound close without explicit source location.
   Expect allocation across available ledger sources in current ordering rules.

9. Re-close already closed document.
   Must fail and produce current “already closed” behavior.

10. API close idempotency.
   Same `event_id` retried against `/api/docs/{docUid}/close` should be idempotent.

11. API close with new `event_id` after document already closed.
   Verify current behavior of `api_docs` / `docs.status` synchronization.

12. `/api/ops` immediate close.
   Verify this path still writes ledger and closes docs without using `/api/docs/{docUid}/close`.

13. TSD upload flow.
   Verify whether current TSD flow leaves server docs in `DRAFT` after `/api/docs` + `/lines`, since explicit `/close` usage was not found.

# Open questions

1. `CloseDocCheck.Warnings` exists, but no code populating it was found. Is this dead code, incomplete work, or logic removed elsewhere?

2. Is any active client currently calling `POST /api/docs/{docUid}/close`? TSD explicit usage was not found in the inspected client code.

3. Should `/api/ops` remain a distinct immediate-close path, or should it converge with draft + explicit close semantics?

4. Should `api_docs.status` continue to be stored separately from `docs.status`, or should it be derived to avoid drift?

5. When moving WPF close to server-centric execution, where should current WPF-only pre-close behavior live:
   `TrySaveHeader()`, recount gating, partner requirement UI, outbound stock preview?

6. Should close validation be moved inside the same DB transaction as ledger/status writes to remove race conditions?

7. Is the current absence of `allowNegative` in HTTP/API intentional, or just an unimplemented branch?

8. Should the current disabled KM behavior (`KmWorkflowEnabled => false`) be treated as a hard invariant for migration tests?
