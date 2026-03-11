# Purpose

This document captures the current implementation of document draft creation in FlowStock before any migration to server-centric writes for `CreateDocDraft`.

Scope of this document:

- current WPF draft-creation flow;
- current Server/API draft-creation flow;
- TSD usage of the draft API;
- DB writes and reads involved in draft creation;
- `doc_uid` / `doc_ref` behavior;
- `api_docs` behavior;
- validation and idempotency semantics;
- differences between WPF, `/api/docs`, `/api/ops`, and legacy import paths;
- migration risks.

This is a current-state inventory only. No target design is proposed here.

# Current WPF flow

## Entry points

Interactive WPF draft creation is initiated from code-behind, not from ViewModels or `ICommand`.

ViewModel/command-based draft creation: not found.

Main WPF interactive entry points:

- `apps/windows/FlowStock.App/MainWindow.xaml.cs`
  - `NewDocMenu_Click()`
  - `ShowNewDocDialog()`
- `apps/windows/FlowStock.App/NewDocWindow.xaml`
  - dialog with fields `type`, `doc_ref`, `comment`
- `apps/windows/FlowStock.App/NewDocWindow.xaml.cs`
  - `UpdateDocRef()`
  - `Create_Click()`

## WPF interactive flow

Files and roles:

- `apps/windows/FlowStock.App/MainWindow.xaml.cs`
  - opens `NewDocWindow`
  - after success reloads document list and opens details window
- `apps/windows/FlowStock.App/NewDocWindow.xaml.cs`
  - generates a local `doc_ref` suggestion through `DocumentService.GenerateDocRef()`
  - creates the draft through `DocumentService.CreateDoc()`
- `apps/windows/FlowStock.App/AppServices.cs`
  - wires WPF directly to `PostgresDataStore`
- `apps/windows/FlowStock.Core/Services/DocumentService.cs`
  - validates and inserts the draft
- `apps/windows/FlowStock.Data/PostgresDataStore.cs`
  - performs the actual `docs` insert

Interactive flow steps:

1. `MainWindow.ShowNewDocDialog()` opens `NewDocWindow`.
2. `NewDocWindow.UpdateDocRef()` calls `DocumentService.GenerateDocRef(type, DateTime.Now)`.
3. User clicks `Create`.
4. `NewDocWindow.Create_Click()` calls `DocumentService.CreateDoc(type, docRef, comment, null, null, null)`.
5. On success, `CreatedDocId` is returned to `MainWindow`.
6. `MainWindow.ShowNewDocDialog()` reloads docs and opens `OperationDetailsWindow` for the created draft.

Current behavior details:

- partner, order, locations, HU, and `doc_uid` are not part of WPF interactive draft creation;
- WPF does not call `POST /api/docs` for draft creation;
- WPF does not create `api_docs` metadata at draft creation time;
- WPF local docs can later get temporary `api_docs` metadata only through `WpfCloseDocumentService` during the close migration path, not during create.

## Separate local/legacy WPF-adjacent path: JSONL import

There is another local draft-creation-capable path reachable from WPF, but it is not the interactive new-document dialog.

Files and roles:

- `apps/windows/FlowStock.App/MainWindow.xaml.cs`
  - `ImportMenu_Click()`
  - `RunImportDialog()`
  - `RunImport()`
- `apps/windows/FlowStock.Core/Services/ImportService.cs`
  - `ImportJsonl()`
  - `ProcessEvent()`
- `apps/windows/FlowStock.Data/PostgresDataStore.cs`
  - `AddDoc()`
  - `AddImportedEvent()`

Current import flow details:

- import parses JSONL operation events;
- when a `doc_ref` is not found, import creates a new `docs` row with `status = DRAFT`;
- idempotency is handled through `imported_events`, not through `api_events`;
- `api_docs` / `doc_uid` are not used by import draft creation.

This path is not the current online/TSD draft lifecycle, but it still creates local draft documents directly in PostgreSQL.

# Current Server/API flow

## Mapped endpoints

Files and roles:

- `apps/windows/FlowStock.Server/Program.cs`
  - registers `DocumentDraftEndpoints.Map(app)`
  - registers `OpsEndpoint.Map(app)`
  - exposes helper `GET /api/docs/next-ref`
- `apps/windows/FlowStock.Server/DocumentDraftEndpoints.cs`
  - `POST /api/docs`
  - `POST /api/docs/{docUid}/lines`
- `apps/windows/FlowStock.Server/OpsEndpoint.cs`
  - `POST /api/ops`
- `apps/windows/FlowStock.Server/ApiModels.cs`
  - request DTOs including `CreateDocRequest`
- `apps/windows/FlowStock.Server/PostgresApiDocStore.cs`
  - `api_docs` / `api_events` store
- `apps/windows/FlowStock.Core/Services/DocumentService.cs`
  - core `CreateDoc()` and `GenerateDocRef()` logic reused by server

## Canonical API docs flow: `POST /api/docs`

Current server draft creation for the API docs flow is implemented in:

- `apps/windows/FlowStock.Server/DocumentDraftEndpoints.cs`
  - method `HandleCreateAsync()`

### Request shape

`CreateDocRequest` currently supports:

- `doc_uid`
- `event_id`
- `device_id`
- `type`
- `doc_ref`
- `comment`
- `reason_code`
- `partner_id`
- `order_id`
- `order_ref`
- `from_location_id`
- `to_location_id`
- `from_hu`
- `to_hu`
- `draft_only`

### New draft creation path

Current new-draft flow in `HandleCreateAsync()`:

1. reads raw body and deserializes JSON;
2. requires non-empty `doc_uid` and `event_id`;
3. parses `type` into `DocType`;
4. optionally resolves `order_id` / `order_ref`;
5. checks `api_events` for `event_id` idempotency/conflict;
6. checks `api_docs` for existing `doc_uid`;
7. validates partner/location/HU/order constraints;
8. determines `doc_ref`:
   - requested value if free;
   - otherwise generates via `DocumentService.GenerateDocRef()`;
9. creates the draft via `DocumentService.CreateDoc()`;
10. for write-off, optionally persists `reason_code` via `DocumentService.UpdateDocReason()`;
11. inserts `api_docs` row via `IApiDocStore.AddApiDoc(..., status = "DRAFT", ...)`;
12. records `DOC_CREATE` event in `api_events`;
13. returns `{ ok, doc: { id, doc_uid, doc_ref, status, type, doc_ref_changed } }`.

### Existing `doc_uid` path

`POST /api/docs` is not currently a strict create-only endpoint.

If `api_docs` already contains the same `doc_uid`, `HandleCreateAsync()` behaves like a draft upsert/reconcile path:

- loads the existing `api_docs` row and the underlying `docs` row;
- rejects mismatched type / `doc_ref` / partner / locations / HU with `DUPLICATE_DOC_UID`;
- may update missing order, partner, locations, and HU metadata;
- may update draft comment and write-off reason;
- may update `docs.shipping_ref` to match resolved HU context;
- returns the existing draft instead of creating a new `docs` row.

Important current-state observation:

- in this existing-`doc_uid` path, no `DOC_CREATE` event is recorded for the new request when `event_id` was not already present.

## `draft_only` behavior

`draft_only` does not change the final draft status.

Current effect of `draft_only` in `POST /api/docs`:

- `draft_only = true`
  - allows incomplete draft header metadata;
  - skips partner/location required-field checks that would otherwise be applied at create time.
- `draft_only = false`
  - enforces stricter partner/location presence checks depending on document type;
  - still creates or returns a `DRAFT` document;
  - does not close the document.

So today `draft_only` means validation mode, not state mode.

## Helper endpoint: `GET /api/docs/next-ref`

File:

- `apps/windows/FlowStock.Server/Program.cs`

Behavior:

- returns a server-generated next `doc_ref` for a requested type;
- this is auxiliary only;
- current TSD create flow does not depend on this helper.

## `/api/ops` path

File:

- `apps/windows/FlowStock.Server/OpsEndpoint.cs`

`/api/ops` is not a normal draft-lifecycle endpoint.

Current behavior:

1. parses operation event payload;
2. requires `event_id`, `doc_ref`, `barcode`, and `qty`;
3. resolves item and locations;
4. finds existing draft by `doc_ref` or creates one through `DocumentService.CreateDoc()`;
5. immediately adds a line;
6. immediately executes close/posting through the canonical close helper;
7. records an `OP` event.

Important observation:

- `/api/ops` can create a `docs` row, but it does not expose a persisted remote draft lifecycle;
- `/api/ops` does not use `doc_uid` or `api_docs` for draft creation;
- `/api/ops` is a compatibility path, not the API docs draft path.

## TSD flow

Files and roles:

- `apps/android/tsd/storage.js`
  - `apiCreateDocDraft()`
- `apps/android/tsd/app.js`
  - `createDocAndOpen()`
  - `submitDocToServer()`
  - draft resume logic in docs list / document routing

Current TSD draft lifecycle:

### Step 1: reserve/create remote draft early

`createDocAndOpen()`:

1. creates local UUID values for `docId` and `eventId`;
2. calls `TsdStorage.apiCreateDocDraft(op, docId, eventId, deviceId)`;
3. `apiCreateDocDraft()` sends:
   - `doc_uid = local UUID`
   - `event_id = UUID`
   - `type = op`
   - `draft_only = true`
   - `comment = "TSD"`
4. server returns `doc_ref`;
5. TSD saves a local document with:
   - local `id = docUid`
   - returned `doc_ref`
   - local `status = DRAFT`

### Step 2: enrich the existing remote draft before line upload

Later, `submitDocToServer()` calls `POST /api/docs` again for the same `doc_uid`, now with full header data:

- same `doc_uid`
- a new `event_id`
- `type`
- current `doc_ref`
- partner/order/location/HU/reason fields
- no `draft_only` flag, so `draft_only = false` by default

This means current TSD behavior depends on the existing-`doc_uid` upsert semantics of `POST /api/docs`.

### Step 3: append lines through `/api/docs/{docUid}/lines`

After the header-upsert call, `submitDocToServer()` appends each line via `POST /api/docs/{docUid}/lines`.

### Resume behavior

Server docs list responses expose `doc_uid`, and TSD uses that to resume server drafts.

Files:

- `apps/windows/FlowStock.Server/Program.cs`
  - `MapDoc()` returns `doc_uid = doc.ApiDocUid`
- `apps/android/tsd/storage.js`
  - normalizes `doc_uid`
- `apps/android/tsd/app.js`
  - if server doc is `DRAFT` and has `doc_uid`, reconstructs local editable draft state

## WPF server/API draft creation path

Explicit WPF HTTP draft creation path: not found.

Feature flag for server-centric WPF draft creation: not found.

WPF still creates drafts directly in PostgreSQL.

# Current DB interactions

## Shared core creation primitive

Files:

- `apps/windows/FlowStock.Core/Services/DocumentService.cs`
  - `CreateDoc()`
- `apps/windows/FlowStock.Core/Services/DocRefGenerator.cs`
  - `Generate()`
- `apps/windows/FlowStock.Data/PostgresDataStore.cs`
  - `AddDoc()`

Current shared primitive behavior:

- validates `doc_ref` uniqueness;
- validates optional partner/order existence;
- inserts `docs` row with `status = DRAFT`;
- if `order_id` is present:
  - creates draft and pre-populates lines in one transaction;
  - `Outbound` uses `GetOrderShipmentRemaining()`;
  - `ProductionReceipt` uses `GetOrderReceiptRemaining()`;
  - other order-linked doc types use `GetOrderLines()`.

## WPF interactive create DB interactions

Current WPF local create uses:

- `DocumentService.GenerateDocRef()`
  - `IDataStore.GetMaxDocRefSequenceByYear()`
  - `IDataStore.FindDocByRef()`
- `DocumentService.CreateDoc()`
  - `IDataStore.FindDocByRef()`
  - `IDataStore.IsDocRefSequenceTaken()`
  - optional `IDataStore.GetPartner()` / `IDataStore.GetOrder()`
  - `IDataStore.AddDoc()`

With current WPF UI, practical DB writes are minimal:

- one `docs` insert;
- no `api_docs` insert;
- no `api_events` insert.

## Server `POST /api/docs` DB interactions

Current API create path uses:

- `IApiDocStore.GetEvent()`
- `IApiDocStore.GetApiDoc()`
- `IDataStore.GetOrder()` / `IDataStore.GetOrders()`
- `IDataStore.GetPartner()`
- `IDataStore.FindLocationById()`
- `IDataStore.GetHuByCode()`
- `IDataStore.FindDocByRef()`
- `DocumentService.GenerateDocRef()`
- `DocumentService.CreateDoc()`
- `DocumentService.UpdateDocReason()`
- `IApiDocStore.AddApiDoc()`
- `IApiDocStore.RecordEvent()`

For existing `doc_uid` update/reconcile path, it may additionally use:

- `IDataStore.GetDoc()`
- `IDataStore.UpdateDocOrder()`
- `IDataStore.UpdateDocHeader()`
- `IDataStore.UpdateDocComment()`
- `IDataStore.UpdateDocReason()`
- `IApiDocStore.UpdateApiDocHeader()`

## `/api/ops` DB interactions

Current `/api/ops` path uses:

- `IApiDocStore.GetEvent()` / `RecordOpEvent()`
- `IDataStore.FindDocByRef()`
- `IDataStore.FindItemByBarcode()` / `FindLocationById()` / `GetLocations()` / `GetHuByCode()`
- `DocumentService.CreateDoc()`
- `DocumentService.AddDocLine()`
- canonical close path afterward

## Import JSONL DB interactions

Current import path uses:

- `IDataStore.IsEventImported()` / `AddImportedEvent()`
- `IDataStore.FindDocByRef()`
- `IDataStore.AddDoc()` with `status = DRAFT`
- no `api_docs`
- no `api_events`

## Transaction boundaries

Current-state transaction behavior is split.

- `DocumentService.CreateDoc()` wraps `docs + doc_lines` in a transaction only when `order_id` is present;
- `DocumentDraftEndpoints.HandleCreateAsync()` does not wrap `CreateDoc()`, `AddApiDoc()`, and `RecordEvent()` in one shared transaction;
- `PostgresApiDocStore` always opens its own direct connection;
- import path uses `_data.ExecuteInTransaction()` around event processing and draft creation;
- `/api/ops` create, line add, and close are spread across distinct operations/helpers.

# doc_uid / doc_ref behavior

## `doc_uid`

Current behavior:

- server-side `doc_uid` generation in `POST /api/docs`: not found;
- `POST /api/docs` requires client-supplied `doc_uid`;
- TSD generates `doc_uid` client-side as a UUID and reuses it as local draft id;
- WPF local draft creation has no `doc_uid` at create time;
- WPF can derive/insert a temporary `doc_uid` later only in `WpfCloseDocumentService` for close migration compatibility.

## `doc_ref`

### Shared generator

Files:

- `apps/windows/FlowStock.Core/Services/DocRefGenerator.cs`
- `apps/windows/FlowStock.Core/Services/DocumentService.cs`

Current generator behavior:

- format: `PREFIX-YYYY-000001`
- year-global numeric sequence across all doc types
- uniqueness checked through current max sequence and `FindDocByRef()`

### WPF local behavior

- WPF suggests a `doc_ref` locally before create;
- if create fails with duplicate `doc_ref`, WPF regenerates locally and asks user to retry with the suggested value.

### API docs flow behavior

- client may send `doc_ref`, but it is optional;
- if missing, server generates it;
- if the requested `doc_ref` already exists, server silently switches to a generated one and returns `doc_ref_changed = true`;
- if a race still causes duplicate on insert, server retries generation once, then may return `DOC_REF_EXISTS`.

### `/api/ops` behavior

- `doc_ref` is required in the request;
- no automatic server replacement with `doc_ref_changed` behavior;
- existing draft with same `doc_ref` may be reused;
- conflicting existing document type returns `DOC_REF_EXISTS`.

### Import JSONL behavior

- uses `doc_ref` from the imported event;
- no server-side regeneration;
- type mismatch on existing `doc_ref` becomes import error `DOC_REF_EXISTS`.

# api_docs behavior

Files:

- `apps/windows/FlowStock.Server/IApiDocStore.cs`
- `apps/windows/FlowStock.Server/PostgresApiDocStore.cs`
- `apps/windows/FlowStock.Server/DocumentDraftEndpoints.cs`
- `apps/windows/FlowStock.Data/PostgresDataStore.cs`
- `apps/windows/FlowStock.Server/Program.cs`

Current behavior:

- `api_docs` is created only by the API docs flow and by the temporary WPF close bootstrap;
- new `POST /api/docs` draft creation inserts one `api_docs` row with:
  - `doc_uid`
  - `doc_id`
  - `status = DRAFT`
  - `doc_type`
  - `doc_ref`
  - partner/location/HU/device metadata
- repeated `POST /api/docs` for an existing `doc_uid` can update header metadata through `UpdateApiDocHeader()`;
- `api_docs.status` remains `DRAFT` during create and line-add phases;
- `DocSelectBase` in `PostgresDataStore` left-joins `api_docs` to expose `doc.ApiDocUid` in normal `GetDocs()` / `GetDoc()` reads;
- `/api/ops` does not create `api_docs` rows;
- WPF local interactive create does not create `api_docs` rows.

# Validation rules

## Shared `DocumentService.CreateDoc()` validation

Current shared create validation:

- `doc_ref` is required;
- duplicate `doc_ref` is rejected;
- duplicate parsed year/sequence is rejected;
- provided partner must exist;
- provided order must exist;
- `Outbound` with internal order is rejected;
- if `order_id` is present, draft lines may be auto-created from the order.

## WPF interactive create validation

Current WPF UI-level validation:

- document type must be selected;
- duplicate `doc_ref` is handled through exception mapping and local regeneration;
- no partner/order/location/HU validation happens in the create dialog itself.

## API `POST /api/docs` validation

Current API create validation includes:

- request body must be non-empty;
- JSON must parse;
- `doc_uid` is required;
- `event_id` is required;
- `type` must parse into supported document type;
- referenced order must exist;
- `Outbound` cannot use internal order;
- existing `event_id` with different semantic target returns `EVENT_ID_CONFLICT`;
- existing `doc_uid` with conflicting type/ref/header returns `DUPLICATE_DOC_UID`;
- provided partner must exist;
- provided locations must exist;
- provided HU must exist and be in allowed status (`ACTIVE` or `OPEN`);
- if order partner and requested partner disagree on new create, returns `ORDER_PARTNER_MISMATCH`;
- if `draft_only = false`:
  - `Inbound` / `Outbound` require partner;
  - `Move` / `Outbound` / `WriteOff` require source location;
  - `Move` / `Inbound` / `Inventory` / `ProductionReceipt` require target location.

Important current-state observations:

- create does not require any lines;
- create does not require write-off reason;
- create does not validate close-time stock rules;
- create does not validate `max_qty_per_hu`;
- existing `doc_uid` update path accepts new requests even when no prior `DOC_CREATE` event exists for that request id.

## `/api/ops` validation

Current `/api/ops` validation includes:

- body / JSON / parsed operation validity;
- `event_id`, `doc_ref`, `barcode`, and positive `qty` are required;
- operation must be one of supported online op names;
- item and locations must resolve;
- optional HU values must exist;
- existing closed doc with same `doc_ref` is treated as already-closed replay/no-op rather than a new draft lifecycle.

## Import JSONL validation

Current import validation includes:

- imported event id idempotency through `imported_events`;
- operation parsing;
- item resolution;
- location resolution;
- HU mismatch checks;
- doc-ref type mismatch import error.

# Known invariants

Sources:

- `AGENTS.md`
- `docs/spec.md`
- `docs/spec_orders.md`

Current explicit invariants relevant to draft creation:

- stock is derived from `ledger` only;
- documents affect stock only on close;
- closed documents are immutable;
- WPF connects directly to PostgreSQL;
- TSD creates drafts through API;
- server assigns `doc_ref` when API clients do not provide one;
- `doc_ref` format is year-global and unique across all doc types;
- drafts with `doc_uid` can be resumed by TSD.

Observed code alignment:

- WPF local interactive create still bypasses the API draft path;
- TSD online flow uses the API draft path and later resumes drafts by `doc_uid`;
- `/api/ops` is not part of the resumable `doc_uid` draft lifecycle.

# Differences between WPF and Server

## Same core primitive, different wrappers

Shared primitive:

- both WPF and Server ultimately rely on `DocumentService.CreateDoc()` and `PostgresDataStore.AddDoc()` for the `docs` insert.

Different wrappers:

- WPF interactive create is local and direct to PostgreSQL;
- Server/API create wraps draft creation with `doc_uid`, `api_docs`, and `api_events` metadata;
- WPF has no `event_id` or `doc_uid` at draft creation time.

## WPF create is minimal, server create is metadata-rich

WPF interactive create collects only:

- type
- `doc_ref`
- comment

Server `POST /api/docs` can additionally accept and validate:

- partner
- order
- locations
- HU
- write-off reason
- `doc_uid`
- `event_id`
- `device_id`
- `draft_only`

## WPF local create has no API idempotency

WPF interactive create:

- no `doc_uid`
- no `api_docs`
- no `api_events`
- no replay semantics

Server create:

- `event_id`-based replay/conflict handling
- `doc_uid` identity
- resumable metadata in `api_docs`

## Order-linked behavior differs by call shape

Server create can create order-linked draft lines on first create if `order_id` is passed into `DocumentService.CreateDoc()`.

WPF interactive create does not pass `order_id`.

Also, current API existing-`doc_uid` update path can attach an order to an already-created draft, but does not auto-populate order lines in that branch.

# Differences between API docs flow and /api/ops

## API docs flow (`POST /api/docs`)

Current characteristics:

- persistent `DRAFT` lifecycle
- requires `doc_uid`
- uses `api_docs`
- uses `DOC_CREATE` / `DOC_LINE` events in `api_events`
- supports resumable draft semantics
- may be called first with `draft_only = true`, then again with richer metadata for the same draft

## `/api/ops`

Current characteristics:

- compatibility path, not resumable draft lifecycle
- no `doc_uid`
- no `api_docs`
- uses `OP` event type
- request must include `doc_ref`
- creates/reuses draft and immediately closes it
- does not leave a remote draft waiting for `/lines` or `/close`

# Risks of migration

1. There are multiple draft-creation paths, not one.
   Current state includes WPF interactive create, API docs flow, `/api/ops`, and JSONL import.

2. WPF local create still bypasses `doc_uid` / `api_docs`.
   Migrating only the server path will leave local WPF create as a legacy direct-write path.

3. `POST /api/docs` is create-or-upsert, not strict create.
   Existing `doc_uid` requests may mutate metadata on an existing draft instead of failing or being handled by a separate update endpoint.

4. Existing-`doc_uid` create requests are not fully event-recorded.
   Current code does not record a new `DOC_CREATE` event in the existing-draft branch.

5. Draft create and metadata create are not atomic.
   `docs`, `api_docs`, and `api_events` are not written in one shared transaction.

6. `doc_uid` is client-generated for the API docs flow.
   The server currently trusts client-supplied identity and does not generate canonical `doc_uid` values itself.

7. `doc_ref` generation semantics are split across clients and server.
   WPF pre-generates locally, API may override requested refs, `/api/ops` requires caller-provided ref, and import keeps imported refs.

8. Order-linked semantics differ between new draft and existing draft update.
   First create with `order_id` can prefill lines; later `doc_uid` upsert with order assignment does not.

9. `/api/ops` can still create documents outside the `doc_uid` lifecycle.
   Migrating only `/api/docs` does not migrate all server-side document creation.

10. Import JSONL still creates local drafts through `imported_events`.
    This is another legacy write path that can diverge from server-centric design.

11. TSD currently depends on the upsert-like behavior of `POST /api/docs`.
    It creates a skeletal draft first and enriches it later with a second `POST /api/docs` before line upload.

12. WPF close migration already bootstraps temporary `api_docs` for locally created docs.
    Future create migration must decide whether to preserve, replace, or retire that bootstrap.

# Golden test scenarios

1. WPF interactive create creates a local `DRAFT` doc with no `doc_uid` and no `api_docs` row.
2. WPF duplicate `doc_ref` create suggests a regenerated local `doc_ref`.
3. `POST /api/docs` with missing `doc_uid` fails.
4. `POST /api/docs` with missing `event_id` fails.
5. `POST /api/docs` with missing `doc_ref` generates a server `doc_ref` and returns `DRAFT`.
6. `POST /api/docs` with a colliding requested `doc_ref` returns a generated replacement and `doc_ref_changed = true`.
7. `POST /api/docs` successful new create inserts both `docs.status = DRAFT` and `api_docs.status = DRAFT`.
8. `POST /api/docs` same `event_id` replay for the same `doc_uid` returns the existing draft idempotently.
9. `POST /api/docs` with a new `event_id` and the same `doc_uid` updates missing header metadata on the existing draft.
10. `POST /api/docs` with conflicting `doc_uid` metadata returns `DUPLICATE_DOC_UID`.
11. `POST /api/docs` with `draft_only = false` enforces partner/location presence but still creates only `DRAFT`.
12. `POST /api/docs` with `order_id` on first create pre-populates lines through `DocumentService.CreateDoc()`.
13. Existing-`doc_uid` order assignment through `POST /api/docs` does not auto-populate lines.
14. TSD first `createDocAndOpen()` call creates a skeletal server draft with `draft_only = true`.
15. TSD later `submitDocToServer()` call reuses the same `doc_uid` and enriches header metadata before `/lines`.
16. `/api/ops` with a new `doc_ref` creates a doc and immediately closes it without `api_docs`.
17. JSONL import creates a `DRAFT` doc directly through `ImportService` and `AddDoc()` when `doc_ref` is new.

# Open questions

1. Should `POST /api/docs` remain an upsert-like endpoint for existing `doc_uid`, or should create and header-update become separate operations?

2. Is it intentional that the existing-`doc_uid` branch in `POST /api/docs` does not record a `DOC_CREATE` event for the new request id?

3. Should `POST /api/docs` accept requests for already-closed `doc_uid` values and return the existing document, or should that be rejected explicitly?

4. Should server-side draft creation eventually generate canonical `doc_uid` values instead of requiring client-generated ones?

5. When WPF migrates draft creation to server-centric execution, should it call `POST /api/docs`, a new application command keyed by `doc_id`, or some temporary compatibility wrapper?

6. Should the temporary WPF `api_docs` bootstrap in `WpfCloseDocumentService` survive once create moves to the server path?

7. Is the current difference between first-create-with-order and existing-draft-order-update intentional, or should both paths populate order lines consistently?

8. Should `/api/ops` remain allowed to create documents outside the `doc_uid` / `api_docs` draft lifecycle?

9. Should JSONL import remain a supported draft-creation path after the server-centric migration, or be treated as a legacy/offline exception?

10. Is `GET /api/docs/next-ref` still intended to be part of the long-term contract, or only a helper/legacy convenience endpoint?
