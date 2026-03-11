# Purpose

This document defines the canonical server-centric contract for `AddDocLine` in FlowStock, based on the current-state inventory in:

- `docs/architecture/add-doc-line-current-state.md`

Goal of this document:

- define one canonical remote and server-side contract for adding draft document lines;
- preserve the practical existing endpoint shape around `POST /api/docs/{docUid}/lines`;
- formalize append-only behavior and idempotency semantics;
- separate client-side merge behavior from server-authoritative line persistence;
- provide testable behavior for the next migration step.

This document does not change production code. It defines the target contract for migration planning.

# Current assumptions

The following assumptions are taken from current code and the current-state document.

1. A shared core line-add primitive already exists:
   - `apps/windows/FlowStock.Core/Services/DocumentService.cs`
   - method `AddDocLine(long docId, long itemId, double qty, long? fromLocationId, long? toLocationId, double? qtyInput = null, string? uomCode = null, string? fromHu = null, string? toHu = null, long? orderLineId = null)`

2. A canonical remote line-add path already exists:
   - `apps/windows/FlowStock.Server/DocumentDraftEndpoints.cs`
   - `POST /api/docs/{docUid}/lines`

3. TSD already uses the remote line-add path as part of a real upload flow:
   - `apps/android/tsd/app.js`
   - `apps/android/tsd/storage.js`

4. WPF interactive line add is still legacy-local:
   - `apps/windows/FlowStock.App/OperationDetailsWindow.xaml.cs`
   - direct calls into `DocumentService.AddDocLine()`, `UpdateDocLineQty()`, `AssignDocLineHu()`, `ApplyOrderToDoc()`, and related local draft mutation paths

5. Current server line-add behavior is effectively append-only for accepted new events.

6. Current WPF behavior is not append-only:
   - WPF manually merges same-key lines before writing;
   - WPF also has local split, delete/recreate, and order-fill flows.

7. `api_docs` already provides the remote mapping and header metadata needed to resolve `doc_uid -> doc_id` and effective locations/HU for the line endpoint.

8. `api_events` already provides event logging for `DOC_LINE`, but current replay semantics are weaker than the target contract:
   - exact same `event_id` currently returns `ok = true`;
   - payload comparison for `same event_id + different payload`: not found.

9. Product invariants still apply:
   - stock is derived from ledger only;
   - adding a line must not affect stock;
   - only close writes stock effects.

# Canonical operation definition

The canonical server-centric `AddDocLine` operation is:

`Append one validated line intent to an existing persisted DRAFT document on the server, keyed by doc_uid, without merging lines, without writing ledger, and without changing the authoritative document status.`

Canonical `AddDocLine` is a draft-mutation operation, not a stock operation.

Canonical assumptions:

- the target draft already exists on the server;
- the remote draft is identified by `doc_uid`;
- the document is still in `DRAFT`;
- the server is responsible for authoritative line validation;
- the server persists one new `doc_lines` row for each accepted new line event.

Authoritative outcome state:

- one appended row in `doc_lines`
- unchanged `docs.status = DRAFT`
- no ledger writes

# Append-only line model

Canonical server behavior is append-only.

Decision:

- every accepted `AddDocLine` request with a new logical event appends a new `doc_lines` row;
- server-side merge behavior is explicitly out of scope for the canonical contract.

Reasoning:

- current remote endpoint already behaves this way;
- `doc_lines` has no semantic uniqueness constraint that would support canonical merge semantics;
- TSD already performs local merge before upload, so append-only server behavior is practical today;
- keeping merge out of the server contract reduces ambiguity between remote idempotency and business aggregation.

Important consequence:

- a semantic "line" in the UI is not necessarily the same thing as a persisted `doc_lines` row;
- server persistence is event-shaped and append-only;
- client merge is allowed as a convenience layer but is not authoritative business behavior.

# Proposed application command

Proposed canonical application command:

`AddDocLineCommand`

Proposed shape:

```text
AddDocLineCommand
- doc_id: long
- doc_uid: string
- event_id: string
- device_id: string?
- item_id: long?
- barcode: string?
- order_line_id: long?
- qty: double
- uom_code: string?
- from_location_id: long?
- to_location_id: long?
- from_hu: string?
- to_hu: string?
- source: string?          // TSD | WPF | API | OPS bridge
```

Design intent:

- `doc_id` is the authoritative business identity for the application layer;
- `doc_uid` is the authoritative remote identity used by the HTTP wrapper;
- `event_id` is the idempotency key for line requests;
- the command appends one line intent or resolves idempotent replay.

Practical fit to current architecture:

- `apps/windows/FlowStock.Server/DocumentDraftEndpoints.cs`
  - `HandleAddLineAsync()` is the closest current wrapper anchor
- `apps/windows/FlowStock.Core/Services/DocumentService.cs`
  - `AddDocLine()` remains the strict row-append business primitive behind the command

# HTTP endpoint contract

## Canonical remote endpoint

Canonical remote endpoint:

`POST /api/docs/{docUid}/lines`

Decision:

- yes, `POST /api/docs/{docUid}/lines` should remain the canonical remote path for server-centric line addition

Reasoning:

- it already exists;
- it is already used by TSD;
- it already fits the `doc_uid`-based remote draft lifecycle established by `POST /api/docs`;
- it naturally composes with canonical create and canonical close.

## Endpoint semantics

Target semantics:

- resolve `doc_uid` through `api_docs`;
- validate that the mapped document is still editable in `DRAFT`;
- validate line payload and effective location/HU context;
- append exactly one `doc_lines` row for an accepted new event;
- record accepted remote line events in `api_events`;
- return canonical response data for the accepted appended line or for idempotent replay.

This is intentionally not a generalized line-update endpoint. It is a practical append contract for the current architecture.

# Request DTO

The canonical request DTO should remain aligned with `AddDocLineRequest` in:

- `apps/windows/FlowStock.Server/ApiModels.cs`

Target request DTO:

```json
{
  "event_id": "request-uuid",
  "device_id": "optional-device-id",
  "barcode": "optional-barcode",
  "item_id": 123,
  "order_line_id": 456,
  "qty": 10.0,
  "uom_code": "BOX",
  "from_location_id": 11,
  "to_location_id": 22,
  "from_hu": "HU-000001",
  "to_hu": "HU-000002"
}
```

Field rules:

- `event_id`
  - required
  - idempotency/replay key per line request
- `item_id` / `barcode`
  - at least one item resolver is required
  - if both are present, they must resolve to the same item under canonical validation
- `qty`
  - required
  - must be `> 0`
- `order_line_id`
  - optional
  - treated as line metadata unless stronger validation is explicitly added later
- `uom_code`
  - optional input/UOM metadata
- location/HU fields
  - optional in transport
  - effective requiredness depends on document type and on allowed header inheritance rules

Current practical rule to preserve:

- for several document types, effective locations/HU come from draft header metadata already persisted through `POST /api/docs`;
- therefore line payload is not fully free-form even when optional fields exist in the request DTO.

# Response DTO

Proposed canonical response DTO:

```json
{
  "ok": true,
  "result": "APPENDED",
  "doc_uid": "client-generated-doc-uid",
  "doc_status": "DRAFT",
  "line": {
    "id": 98765,
    "item_id": 123,
    "qty": 10.0,
    "uom_code": "BOX",
    "order_line_id": 456,
    "from_location_id": 11,
    "to_location_id": 22,
    "from_hu": "HU-000001",
    "to_hu": "HU-000002"
  },
  "appended": true,
  "idempotent_replay": false,
  "errors": []
}
```

Proposed `result` values:

- `APPENDED`
- `IDEMPOTENT_REPLAY`
- `VALIDATION_FAILED`
- `NOT_FOUND`
- `EVENT_ID_CONFLICT`
- `DOC_NOT_DRAFT`

Field semantics:

- `doc_uid`
  - echoed canonical remote draft identity
- `doc_status`
  - authoritative resulting document status from `docs.status`
  - expected to remain `DRAFT`
- `line.id`
  - authoritative persisted `doc_lines.id`
- `appended`
  - `true` only when a new `doc_lines` row was inserted
- `idempotent_replay`
  - `true` when the request matches a previously accepted event and no new row is appended
- `errors`
  - validation or contract errors

Recommended replay behavior:

- same `event_id` + same payload should return the same canonical line payload, not only bare `ok = true`

`decision needed`:

- whether the first migration step must reconstruct the original response payload from stored event/raw request or whether replay may temporarily return a reduced success payload while preserving no-duplicate guarantees

# Canonical validation rules

Canonical validation rules are the rules that belong to server-authoritative `AddDocLine` behavior itself.

## Baseline rules to preserve from current code

These are already implemented or directly implied by:

- `apps/windows/FlowStock.Server/DocumentDraftEndpoints.cs`
- `apps/windows/FlowStock.Core/Services/DocumentService.cs`

Canonical rules:

- request body must be non-empty and valid JSON
- `event_id` is required
- target `doc_uid` must resolve through `api_docs`
- target document must exist
- target document must be in `DRAFT`
- item must resolve by `item_id` or `barcode`
- `qty > 0`
- effective location set must satisfy `DocumentService.ValidateLineLocations()` for the document type
- any explicitly provided HU code must exist
- any explicitly provided HU code must be in an allowed draft-edit status

## Document-type location rules

Canonical location rules follow the existing business primitive:

- `INBOUND`
  - requires effective target location
- `ProductionReceipt`
  - requires effective target location
- `WRITE_OFF`
  - requires effective source location
- `OUTBOUND`
  - requires effective source location
- `MOVE`
  - requires both effective source and target locations
  - same location without any HU context is invalid
- `INVENTORY`
  - requires effective target location

## Header inheritance rules

Canonical rule:

- the server may derive effective line locations/HU from persisted draft header metadata when that is the established document-type behavior

Pragmatic contract direction:

- preserve current inheritance-based server behavior during migration;
- do not redefine line payload freedom at this step.

This means:

- remote line add is validated against persisted draft context, not against arbitrary client-selected line coordinates.

## Rules intentionally outside canonical AddDocLine

These checks should remain outside the canonical line-add contract unless later promoted deliberately:

- WPF item-picker stock filtering
- WPF local merge behavior
- WPF blocking of outbound manual add on bound order
- TSD local HU/location prechecks against cached stock snapshot
- UX-only quantity hints or dialogs

Reason:

- these checks improve local UX or data entry ergonomics;
- they are not currently part of the shared line-add business primitive.

## Validation gaps explicitly not standardized yet

The following were not found as canonical add-time validation and should not be silently invented in this contract:

- stock sufficiency checks at line-add time
- authoritative validation that `order_line_id` exists
- authoritative validation that `order_line_id` belongs to the document order
- authoritative validation of order remaining quantity at add time

These remain migration questions, not accepted current contract facts.

# Idempotency / replay behaviour

Canonical replay semantics should align with the already documented `CreateDocDraft` contract.

## Same `event_id`, same logical request

Canonical behavior:

- treat as idempotent replay;
- return success with `result = IDEMPOTENT_REPLAY`;
- do not append a second `doc_lines` row;
- do not record a duplicate `api_events` row;
- leave `docs.status = DRAFT`;
- return the canonical response for the previously accepted appended line.

## Same `event_id`, different logical request

Canonical behavior:

- reject as `EVENT_ID_CONFLICT`

This includes at least:

- same `event_id` but different `doc_uid`
- same `event_id` but different resolved item
- same `event_id` but different qty
- same `event_id` but materially different location/HU/order-line intent

## Different `event_id`, same `doc_uid`

Canonical behavior:

- if validation succeeds, append a new `doc_lines` row;
- do not treat semantic similarity as replay;
- do not merge with previous rows.

Important consequence:

- append-only line history is event-based, not semantic-key-based.

# Line identity model

## Authoritative persisted identity

Inside the business/application layer, the authoritative persisted line identity is:

- `doc_lines.id`

## Remote correlation identity

For remote request deduplication, the authoritative request identity is:

- `event_id`

## Contract rule

- `event_id` is not the business identity of a line;
- `event_id` identifies one remote request intent;
- successful new request intent produces one new authoritative `doc_lines.id`;
- idempotent replay returns the previously established line identity.

There is no canonical remote `line_uid` in the current architecture.

# doc_lines authoritative fields

Canonical server write shape for an appended line remains aligned with the current `doc_lines` row:

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

Authoritative write rules:

- `doc_id` is resolved from `doc_uid` through `api_docs`
- `item_id` is the resolved business item identity
- effective locations/HU are the final server-resolved values after header inheritance and validation
- `qty_input` and `uom_code` remain input metadata, not ledger quantities

Canonical invariant:

- `doc_lines` rows are authoritative draft line state for later close;
- line-add itself does not derive or write ledger effects.

# api_events behaviour

Decision:

- `api_events` remains the idempotency and replay log for remote `AddDocLine` requests

Canonical rules:

- each accepted new line intent records one processed `DOC_LINE` event
- exact replay by `event_id` must be detectable through `api_events`
- exact replay must not produce another `doc_lines` row
- conflicting replay must return `EVENT_ID_CONFLICT`

Recommended target behavior:

- the event log should be sufficient to distinguish:
  - first accepted append
  - idempotent replay
  - conflicting replay

Current gap to close in migration:

- same `event_id + different payload` conflict comparison is not yet implemented in the current line endpoint

Pragmatic recommendation:

- keep event type `DOC_LINE`
- do not widen protocol unless later tests prove a need for a more granular event taxonomy

# Interaction with draft lifecycle

Canonical rule:

- `AddDocLine` operates only within an already created server draft lifecycle

Expected sequence:

1. client creates or upserts draft through `POST /api/docs`
2. server persists `doc_uid -> doc_id` mapping in `api_docs`
3. client appends lines through `POST /api/docs/{docUid}/lines`
4. document remains `DRAFT`
5. canonical close later consumes persisted `doc_lines`

Important lifecycle rule:

- `AddDocLine` must not implicitly create drafts
- `AddDocLine` must not implicitly close drafts

This keeps draft creation, draft mutation, and close as separate canonical operations.

# Interaction with close

Canonical rule:

- `AddDocLine` never writes ledger
- `AddDocLine` never changes `docs.status`
- `AddDocLine` only shapes the draft state that canonical close will later consume

Close interaction consequence:

- all stock effects remain deferred until `POST /api/docs/{docUid}/close`
- repeated line-add calls before close only change draft composition
- once the document is no longer `DRAFT`, canonical line add must reject further mutations

Migration invariant:

- add-line migration must not change close math, ledger logic, or close validation ownership

# Client merge vs server append behaviour

Decision:

- client merge and server append are different layers and should stay different in the canonical contract

Canonical server rule:

- server appends one row per accepted new event

Allowed client behavior:

- client may merge or aggregate local capture before upload
- client may present merged read models in UI

Disallowed implication:

- client merge behavior must not be assumed by the server as a correctness mechanism

Pragmatic consequence for migration:

- WPF local merge is legacy client behavior
- TSD local merge is compatible client behavior
- neither should force the canonical server contract to become merge-aware

# Compatibility with TSD

Target rule:

- TSD remains a first-class client of the canonical `POST /api/docs/{docUid}/lines` contract

Expected TSD-compatible behavior:

1. TSD creates or upserts a draft via `POST /api/docs`
2. TSD collects scans locally and may merge them in IndexedDB
3. TSD submits one line request per locally merged line
4. server appends one `doc_lines` row per accepted new request
5. document remains `DRAFT` until explicit close

Important compatibility note:

- TSD local merge remains allowed and useful;
- canonical server semantics remain append-only regardless of that local merge.

Replay contract for TSD:

- retries with the same `event_id` must be safe;
- retries with the same `event_id` but changed payload must fail as `EVENT_ID_CONFLICT`.

# Compatibility with WPF

Decision:

- WPF should migrate toward canonical API line-add semantics, not weaken the canonical API to preserve legacy local merge semantics

Reasoning:

- create and close already establish `doc_uid`-centric remote draft lifecycle as the target direction;
- append-only semantics are clearer and easier to make idempotent than local merge semantics;
- keeping server line-add merge-free avoids mixing UI conveniences with authoritative write behavior.

## WPF bridge semantics

Target WPF behavior after migration:

- WPF operates on drafts that have canonical remote identity (`doc_uid`)
- WPF line add calls canonical `POST /api/docs/{docUid}/lines`
- WPF may still merge locally before issuing the request if that remains a desired UX behavior
- authoritative persistence result comes from the server response

## What becomes incompatible in current WPF add-line path

Current WPF add-line path differs from the target contract in these ways:

- no `event_id`
- direct DB write by `doc_id`, not canonical remote write by `doc_uid`
- local merge is treated as the default write behavior
- line edits/splits are mixed into the same local mutation surface
- outbound manual add is blocked in some order-bound cases that server line-add does not currently enforce
- production receipt manual add forces `to_hu = null`, while canonical server line-add may inherit header HU

## Practical migration stance

- WPF direct local add is legacy
- canonical API contract should stay append-only
- WPF compatibility work is a separate migration step

# Treatment of /api/ops

Decision:

- `/api/ops` remains outside the normal draft line lifecycle

Reasoning:

- it does not use `doc_uid`
- it does not depend on `api_docs`
- it records `OP`, not `DOC_LINE`
- it adds a line and immediately proceeds to close

Target stance:

- do not treat `/api/ops` as canonical `AddDocLine`
- it may remain a compatibility wrapper
- future migration may make it delegate internally to canonical create + line add + close, but it remains outside normal draft lifecycle

# Treatment of JSONL import

Decision:

- JSONL import remains a separate legacy ingestion flow, not part of canonical remote `AddDocLine`

Reasoning:

- it uses `imported_events`, not `api_events`
- it writes directly through local store methods
- it identifies document context outside the `doc_uid` API draft lifecycle

Target migration stance:

- do not fold JSONL import into canonical `POST /api/docs/{docUid}/lines` at this step
- test it as a legacy boundary, not as part of canonical line-add behavior

# Test specification

This section defines tests for the next migration step.

## Canonical line-add integration tests

1. `POST /api/docs/{docUid}/lines` with a valid new `event_id` appends one `doc_lines` row.
2. Successful line add leaves `docs.status = DRAFT`.
3. Successful line add writes no ledger rows.
4. Successful line add records one `DOC_LINE` event in `api_events`.
5. Successful line add returns the authoritative appended `doc_lines.id`.
6. For `INBOUND`, effective target location is inherited from the draft header when that is the canonical current behavior.
7. For `OUTBOUND`, allowed source location/HU override behavior matches the canonical contract.
8. For `MOVE`, effective source and target coordinates come from persisted draft context as defined by the contract.

## Validation tests

9. Missing body fails.
10. Invalid JSON fails.
11. Missing `event_id` fails.
12. Unknown `doc_uid` fails.
13. Document not in `DRAFT` fails.
14. Missing item resolver (`item_id` and `barcode`) fails.
15. Unknown item fails.
16. `qty <= 0` fails.
17. Invalid effective location set for the document type fails.
18. Unknown explicitly provided HU fails.
19. Disallowed explicitly provided HU status fails.
20. Same-location `MOVE` without any HU context fails.

## Idempotency and replay tests

21. Same `event_id` + same payload returns `IDEMPOTENT_REPLAY` and does not append a second row.
22. Same `event_id` + same payload does not create a duplicate `DOC_LINE` event.
23. Same `event_id` + different payload returns `EVENT_ID_CONFLICT`.
24. Same `event_id` reused for a different `doc_uid` returns `EVENT_ID_CONFLICT`.
25. Different `event_id` with semantically same line intent appends a second row and is not treated as replay.

## Response-shape tests

26. Idempotent replay returns the same canonical line identity as the first accepted request.
27. Response line payload reflects final server-resolved location/HU values.

## TSD compatibility tests

28. `create draft -> add lines -> close later` keeps the server document in `DRAFT` after line add.
29. TSD local merge plus remote upload results in one appended server row per uploaded local merged line.
30. TSD retry of the same line request with same `event_id` is idempotent.
31. TSD retry with changed payload under the same `event_id` returns `EVENT_ID_CONFLICT`.

## WPF compatibility tests

32. WPF bridge line add issues `event_id` and uses canonical `POST /api/docs/{docUid}/lines`.
33. WPF local merge, if retained, happens before the canonical server call and not inside server logic.
34. WPF no longer writes `doc_lines` directly in the migrated path.

## Legacy-boundary tests

35. `/api/ops` remains outside the canonical draft line lifecycle and does not create `DOC_LINE` events.
36. JSONL import remains independent from `api_docs` and `api_events`.

## Invariant tests

37. Line add never writes ledger.
38. Line add never changes document status from `DRAFT`.
39. Accepted new line events always append exactly one new row.
40. Closed documents reject further canonical line-add requests.

# Migration notes

1. The first migration target should be wrapper convergence, not business redefinition.
   - keep `POST /api/docs/{docUid}/lines` as the canonical remote path
   - keep `DocumentService.AddDocLine()` as the strict row-append business primitive behind it

2. Server contract should be hardened around replay semantics before any WPF bridge work.
   - add payload comparison for `same event_id + different payload`
   - make replay response canonical and stable

3. WPF should migrate toward the remote append-only contract in a later bridge step.
   - generate `event_id`
   - operate on canonical `doc_uid`
   - stop treating direct `doc_lines` insert/update as the authoritative path

4. TSD contract should remain stable.
   - local merge is compatible with canonical append-only upload
   - no client UI redesign is required to adopt this contract

5. JSONL import should stay out of canonical `AddDocLine` migration scope.

6. `/api/ops` should be treated as compatibility surface outside normal draft line lifecycle.

7. Add-line migration must not redefine close behavior.
   - no ledger writes
   - no status transitions
   - no implicit close

# Decisions requiring confirmation

1. `decision needed`: Should idempotent replay return the original canonical line payload in the first hardening step, or is a reduced success payload temporarily acceptable if no duplicates are guaranteed?

2. `decision needed`: If both `item_id` and `barcode` are supplied and resolve differently, should canonical behavior be strict conflict failure or "prefer item_id"?

3. `decision needed`: Should `order_line_id` remain passive metadata at add time, or should future canonical validation ensure it belongs to the current draft order and remaining quantity?

4. `decision needed`: Should recount reset behavior remain embedded in `POST /api/docs/{docUid}/lines`, or move later to an explicit recount-resume operation?

5. `decision needed`: For `ProductionReceipt`, should canonical remote line add continue to inherit header HU when present, or should receipt line HU always be assigned later as in current WPF manual add?

6. `decision needed`: Should the first migration step keep current document-type-specific inheritance rules exactly as they are, or should line payload freedom be normalized later under a separate design change?
