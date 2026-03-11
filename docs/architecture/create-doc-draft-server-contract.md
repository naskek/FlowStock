# Purpose

This document defines the canonical server-centric contract for `CreateDocDraft` in FlowStock, based on the current-state inventory in:

- `docs/architecture/create-doc-draft-current-state.md`

Goal of this document:

- define one canonical server-centric draft-creation operation;
- preserve the existing practical remote contract around `POST /api/docs`;
- separate strict first-create semantics from draft upsert semantics;
- define authoritative identity, idempotency, and metadata behavior;
- provide testable behavior for the next migration step.

This document does not change production code. It defines the target contract for migration planning.

# Current-state assumptions used for design

The following assumptions are taken from current code and the current-state document.

1. A shared core create primitive already exists:
   - `apps/windows/FlowStock.Core/Services/DocumentService.cs`
   - method `CreateDoc(DocType type, string docRef, string? comment, long? partnerId, string? orderRef, string? shippingRef, long? orderId = null)`

2. A canonical remote path already exists:
   - `apps/windows/FlowStock.Server/DocumentDraftEndpoints.cs`
   - `POST /api/docs`

3. `POST /api/docs` is already used in a real client flow:
   - `apps/android/tsd/storage.js`
   - `apps/android/tsd/app.js`

4. WPF interactive draft creation is still legacy-local:
   - `apps/windows/FlowStock.App/MainWindow.xaml.cs`
   - `apps/windows/FlowStock.App/NewDocWindow.xaml.cs`
   - direct call to `DocumentService.CreateDoc(...)`
   - no `doc_uid`, `api_docs`, or `api_events` at create time

5. `POST /api/docs` currently behaves as create-or-upsert by `doc_uid`, not strict create-only.

6. `doc_uid` is currently client-generated in the API docs flow. Server-side `doc_uid` generation in `POST /api/docs`: not found.

7. `api_docs` and `api_events` already exist as sync/idempotency metadata around the API docs lifecycle:
   - `apps/windows/FlowStock.Server/PostgresApiDocStore.cs`

8. `/api/ops` can create documents, but it is an immediate-create-and-close compatibility wrapper, not a normal draft lifecycle:
   - `apps/windows/FlowStock.Server/OpsEndpoint.cs`

9. JSONL import can create drafts locally through a separate ingestion path:
   - `apps/windows/FlowStock.Core/Services/ImportService.cs`

10. Product invariants still apply:
   - stock is derived from ledger only;
   - draft creation must not affect stock;
   - only close writes stock effects.

# Canonical operation definition

The canonical server-centric operation for draft creation is:

`Create or reconcile a persisted DRAFT document on the server, keyed by client-owned doc_uid, without writing ledger and without changing business status beyond DRAFT.`

Canonical behavior includes two closely related modes under one external contract:

- strict first-create of a new draft identity;
- draft upsert/reconcile for an already known `doc_uid` while the document remains `DRAFT`.

Reasoning:

- this matches the current practical role of `POST /api/docs`;
- TSD already depends on the second mode;
- introducing a new external split endpoint now would create migration work without clear value.

Authoritative business state after canonical create/upsert:

- one persisted row in `docs`
- `docs.status = DRAFT`
- optional `doc_lines` prefilled only when current create semantics already do so
- no ledger writes

# Draft identity model

## Authoritative business identity

Inside the business/application layer, the authoritative persisted identity is:

- `doc_id`

This is the identity used by `DocumentService`, `IDataStore`, and the `docs` table.

## Authoritative remote/synchronization identity

For remote draft lifecycle and client replay semantics, the authoritative external identity is:

- `doc_uid`

Target rule:

- before close, remote draft lifecycle is keyed by `doc_uid`
- after persistence, `doc_uid -> doc_id` mapping must remain stable through `api_docs`

## Practical contract decision

`doc_id` is the authoritative business identity.
`doc_uid` is the authoritative remote draft identity.

These identities are not interchangeable, but both are canonical in their own layer.

# Boundary: strict create vs create-or-upsert

## Strict create

Strict create means:

- no existing `api_docs` row for the requested `doc_uid`
- server creates a new `docs` row in `DRAFT`
- server creates the corresponding `api_docs` row
- server records the accepted create event

Strict create is the first successful claim of a remote draft identity.

## Draft upsert/reconcile

Draft upsert means:

- the requested `doc_uid` already exists;
- the mapped document is still a draft candidate for synchronization;
- the request is treated as reconciliation of the same draft, not as a request for a second draft.

Allowed upsert intent:

- supply more complete header data later;
- fill metadata omitted during an earlier skeletal create;
- replay the same client intent safely;
- reconcile remote metadata drift without changing draft identity.

Not allowed under upsert:

- changing the draft into a different business document;
- changing immutable identity fields in a conflicting way;
- creating a second draft behind the same `doc_uid`.

## Contract decision

`POST /api/docs` should be treated as canonical draft upsert by `doc_uid`, not as strict create-only.

Reasoning:

- this already matches the working TSD flow;
- it avoids forcing a separate update endpoint before migration value is realized;
- it keeps the external contract aligned with current production semantics.

# Proposed application command

Proposed canonical application command:

`CreateOrUpsertDocDraftCommand`

Proposed shape:

```text
CreateOrUpsertDocDraftCommand
- doc_uid: string
- event_id: string?
- device_id: string?
- type: DocType
- doc_ref: string?
- comment: string?
- reason_code: string?
- partner_id: long?
- order_id: long?
- order_ref: string?
- from_location_id: long?
- to_location_id: long?
- from_hu: string?
- to_hu: string?
- draft_only: bool
- source: string?          // TSD | WPF | API | IMPORT bridge
```

Design intent:

- use `doc_uid` as the application-level remote identity for create/upsert;
- reuse `DocumentService.CreateDoc()` only for strict first-create business creation;
- keep reconcile/upsert logic explicit instead of pretending it is strict create.

Pragmatic fit to current code:

- `apps/windows/FlowStock.Server/DocumentDraftEndpoints.cs`
  - `HandleCreateAsync()` is the closest current implementation anchor
- `apps/windows/FlowStock.Core/Services/DocumentService.cs`
  - `CreateDoc()` remains the strict new-doc primitive behind the command

# Proposed HTTP endpoint

## Canonical remote endpoint

Canonical remote endpoint:

`POST /api/docs`

Decision:

- yes, `POST /api/docs` should remain the canonical remote path for server-centric draft creation

Reasoning:

- it already exists;
- it is already used by TSD;
- it already carries `doc_uid`, `event_id`, and header metadata;
- draft lifecycle already orbits around this route.

## Endpoint semantics

Target remote semantics:

- `POST /api/docs` is a canonical create-or-upsert endpoint keyed by `doc_uid`
- first accepted call for a `doc_uid` creates the draft
- later accepted calls for the same `doc_uid` reconcile the same draft while it remains compatible with the contract

This is intentionally not idealized REST. It is a practical server-centric contract for this codebase.

# Request DTO

The canonical request DTO should remain aligned with the existing `CreateDocRequest` shape in:

- `apps/windows/FlowStock.Server/ApiModels.cs`

Target request DTO:

```json
{
  "doc_uid": "client-generated-uuid",
  "event_id": "request-uuid",
  "device_id": "optional-device-id",
  "type": "INBOUND",
  "doc_ref": "optional-doc-ref",
  "comment": "optional-comment",
  "reason_code": "optional-writeoff-reason",
  "partner_id": 123,
  "order_id": 456,
  "order_ref": "optional-order-ref",
  "from_location_id": 10,
  "to_location_id": 20,
  "from_hu": "HU-000001",
  "to_hu": "HU-000002",
  "draft_only": true
}
```

Field rules:

- `doc_uid`
  - required
  - client-generated
  - stable draft identity across retries and enrichments
- `event_id`
  - required
  - idempotency/replay key per request
- `type`
  - required on first create
  - on upsert must match existing draft type
- `doc_ref`
  - optional
  - if omitted, server generates one
  - if requested and unavailable, server may assign replacement and indicate that in response
- `draft_only`
  - validation mode flag
  - does not change final created status from `DRAFT`
- other fields
  - optional at draft skeleton phase
  - validated according to create mode and document type

# Response DTO

Proposed canonical response DTO:

```json
{
  "ok": true,
  "result": "CREATED",
  "doc": {
    "id": 12345,
    "doc_uid": "client-generated-uuid",
    "doc_ref": "IN-2026-000001",
    "status": "DRAFT",
    "type": "INBOUND",
    "doc_ref_changed": false
  },
  "created": true,
  "upserted": false,
  "idempotent_replay": false,
  "errors": []
}
```

Proposed `result` values:

- `CREATED`
- `UPSERTED`
- `IDEMPOTENT_REPLAY`
- `VALIDATION_FAILED`
- `NOT_FOUND`
- `EVENT_CONFLICT`
- `DUPLICATE_DOC_UID`

Field semantics:

- `doc.id`
  - authoritative persisted `doc_id`
- `doc.doc_uid`
  - authoritative remote identity
- `doc.doc_ref`
  - authoritative final document reference
- `doc.status`
  - expected to remain `DRAFT`
- `doc.doc_ref_changed`
  - `true` when requested `doc_ref` was replaced by a generated one
- `created`
  - `true` only when a new `docs` row was inserted
- `upserted`
  - `true` when an existing draft was reconciled
- `idempotent_replay`
  - `true` when same request intent was replayed and produced no new business work
- `errors`
  - validation or contract errors

# Canonical validation rules

Canonical validation rules are the validations that belong to the server-centric create/upsert operation itself.

## Baseline rules to preserve from current server path

These are already implemented or directly implied by current code in:

- `apps/windows/FlowStock.Server/DocumentDraftEndpoints.cs`
- `apps/windows/FlowStock.Core/Services/DocumentService.cs`

Canonical rules:

- request body must be non-empty and valid JSON
- `doc_uid` is required
- `event_id` is required
- `type` must parse into supported `DocType`
- requested `order_id` / `order_ref` must resolve if provided
- `Outbound` with internal order is invalid
- partner must exist if provided
- location ids must exist if provided
- HU values must exist if provided
- HU status must be allowed for draft-create semantics if provided
- conflicting reuse of the same `doc_uid` for a different business draft is invalid
- `doc_ref` uniqueness must be preserved
- if `draft_only = false`:
  - `Inbound` / `Outbound` require partner
  - `Move` / `Outbound` / `WriteOff` require source location
  - `Move` / `Inbound` / `Inventory` / `ProductionReceipt` require target location

## Additional canonical contract rules

These rules should be part of the target contract even if current implementation details need later hardening:

- successful create/upsert must leave the authoritative document in `DRAFT`
- create/upsert must not write ledger
- create/upsert must not close the document
- repeated accepted calls for the same `doc_uid` must never create a second logical draft

## Rules that should remain outside canonical create

These checks should remain outside the canonical create contract:

- local WPF dialog validation beyond request shaping
- local UX prompts around replacing/regenerating `doc_ref`
- client-side convenience generation of provisional `doc_ref`

Reason:

- server must own correctness of the persisted draft contract
- UI-only checks may improve UX but must not define server truth

# Idempotency / replay behavior

## Same `event_id`, same logical request

Canonical behavior:

- treat as idempotent replay
- return the previously accepted outcome for that create/upsert intent
- do not create a second `docs` row
- do not create a second `api_docs` row
- do not duplicate downstream side effects

## Same `event_id`, conflicting logical request

Canonical behavior:

- reject as `EVENT_CONFLICT`

## Same `doc_uid`, different `event_id`, compatible draft enrichment

Canonical behavior:

- treat as accepted draft upsert/reconcile
- return existing draft identity
- update compatible missing or incomplete metadata as allowed by contract
- do not create a second `docs` row

## Same `doc_uid`, different `event_id`, conflicting business identity

Canonical behavior:

- reject as `DUPLICATE_DOC_UID`

## Repeated create after document is already closed

Recommended target behavior:

- reject as incompatible state for draft create/upsert
- do not reopen or silently reshape a closed document

`decision needed`:

- whether the response should be a dedicated `ALREADY_CLOSED` contract result or reuse `DUPLICATE_DOC_UID` / validation failure semantics

# doc_uid behavior

## Ownership model

Decision:

- `doc_uid` should remain client-generated

Reasoning:

- current TSD flow already depends on client-generated UUIDs;
- local/offline-first clients need stable identity before the first round-trip;
- changing ownership to server-generated would complicate TSD and WPF bridge semantics without clear benefit.

## Canonical behavior

- client generates `doc_uid`
- first accepted `POST /api/docs` claims that `doc_uid`
- server persists one stable mapping from `doc_uid` to `doc_id`
- future accepted requests with the same `doc_uid` target the same draft only

## WPF implication

When WPF migrates to server-centric create, it must start generating `doc_uid` before or at create time.

# api_docs behavior

Decision:

- `api_docs` should remain synchronization metadata for the remote draft lifecycle

Practical role:

- stores `doc_uid -> doc_id` mapping
- stores remote-header synchronization metadata
- stores mirrored lifecycle status for API clients

Contract rules:

- strict first-create must create one `api_docs` row
- upsert must reconcile the existing `api_docs` row, not create another one
- `api_docs.status` should mirror `DRAFT` during draft lifecycle
- `docs.status` remains the authoritative business status

Authoritative status rule:

- `docs.status` wins if `api_docs.status` drifts

# api_events behavior

Decision:

- `api_events` should remain the idempotency/event log for remote create/upsert requests

Contract rules:

- every accepted new request intent should be representable in `api_events`
- exact replay by `event_id` should be detectable through `api_events`
- accepted upsert calls with a new `event_id` should also record that accepted event

Important target clarification:

- current code does not always record a new `DOC_CREATE` event in the existing-`doc_uid` branch
- target contract should treat this as a gap to fix, not as desired long-term behavior

Recommended target behavior:

- same `event_id` replay returns idempotently and does not create another event record
- new accepted `event_id` for compatible upsert records a new processed create/upsert event against the same `doc_uid`

`decision needed`:

- whether event type should remain `DOC_CREATE` for both strict create and accepted draft upsert, or whether a distinct event type like `DOC_UPSERT` should be introduced later

Pragmatic recommendation:

- keep one event family initially for compatibility; do not widen protocol unless tests show a need

# Compatibility with TSD

Target rule:

- TSD remains a first-class client of the canonical `POST /api/docs` contract

Expected TSD-compatible behavior:

1. TSD generates `doc_uid` and `event_id`
2. TSD sends an early skeletal create with `draft_only = true`
3. server creates the remote draft in `DRAFT`
4. TSD later posts the same `doc_uid` with a new `event_id` and richer header fields
5. server treats that as accepted draft upsert
6. TSD appends lines through `/api/docs/{docUid}/lines`
7. later close happens through canonical close flow

Explicit contract consequence:

- same `doc_uid` with more complete header data must remain supported
- otherwise current TSD flow would break

# Compatibility with WPF

Decision:

- WPF should migrate toward API semantics, not weaken API semantics to match legacy local create

Reasoning:

- server-centric writes need one authoritative contract;
- TSD already uses the richer remote semantics;
- shrinking API semantics down to current WPF local behavior would remove idempotency and draft identity guarantees.

## WPF bridge semantics

Target WPF behavior after migration:

- WPF generates `doc_uid`
- WPF calls canonical `POST /api/docs`
- WPF receives authoritative `doc_ref` from server
- WPF treats server response as the source of truth for draft identity

## What becomes incompatible in current WPF create path

Current WPF create path differs from the target contract in these ways:

- no `doc_uid`
- no `event_id`
- no `api_docs`
- no `api_events`
- local-only `doc_ref` pre-generation is treated as primary, not provisional
- no server replay semantics
- direct DB write bypasses remote metadata and synchronization model
- create dialog currently captures only `type`, `doc_ref`, `comment`

## Practical migration stance

- WPF local create is legacy and should become a wrapper over canonical API semantics
- API should not be weakened to preserve local WPF minimalism

# Treatment of JSONL import

Decision:

- JSONL import should remain a separate legacy ingestion flow, not part of canonical draft create

Reasoning:

- it uses `imported_events`, not `api_events`;
- it creates drafts locally by `doc_ref`, not by `doc_uid`;
- it behaves like offline/batch ingestion, not remote interactive draft lifecycle.

Target migration stance:

- do not fold JSONL import into the canonical `POST /api/docs` contract at this step
- treat it as a separate compatibility/legacy path until explicitly redesigned

# Treatment of /api/ops

Decision:

- `/api/ops` remains outside the normal draft lifecycle

Reasoning:

- it does not use `doc_uid`
- it does not create `api_docs`
- it creates or reuses a doc by `doc_ref` and immediately closes it
- it is an operation wrapper, not a resumable draft workflow

Target stance:

- `/api/ops` may remain as a compatibility wrapper
- it should not define canonical draft-create semantics
- future migration work may make it delegate to canonical create/line/close operations internally, but it is still outside the standard draft lifecycle contract

# Test specification

This section defines tests for the next step.

## Canonical create/upsert integration tests

1. `POST /api/docs` first call with new `doc_uid` creates one `docs` row with `status = DRAFT`.
2. Successful first create also creates one `api_docs` row mapped to the same `doc_id`.
3. Successful first create writes no ledger rows.
4. `POST /api/docs` without `doc_ref` generates a server `doc_ref`.
5. `POST /api/docs` with colliding requested `doc_ref` returns a replacement `doc_ref` and `doc_ref_changed = true`.
6. `POST /api/docs` with `order_id` on first create pre-populates draft lines through current `DocumentService.CreateDoc()` semantics.
7. `POST /api/docs` with `draft_only = false` still results in `docs.status = DRAFT`.

## Validation tests

8. Missing body fails.
9. Invalid JSON fails.
10. Missing `doc_uid` fails.
11. Missing `event_id` fails.
12. Invalid `type` fails.
13. Unknown `partner_id` fails.
14. Unknown `order_id` or `order_ref` fails.
15. `Outbound` with internal order fails.
16. Unknown location id fails.
17. Unknown HU fails.
18. Disallowed HU status fails.
19. `draft_only = false` enforces partner/location requirements by doc type.
20. Same `doc_uid` with conflicting type or conflicting identity fields fails as `DUPLICATE_DOC_UID`.

## Idempotency and replay tests

21. Same `event_id` replay of a successful first create is idempotent and does not create another `docs` row.
22. Same `event_id` replay does not create another `api_docs` row.
23. Same `event_id` with conflicting payload fails as `EVENT_CONFLICT`.
24. Same `doc_uid` with a new `event_id` and richer compatible header data returns the same draft and updates allowed metadata.
25. Same `doc_uid` with a new `event_id` and incompatible header data fails as `DUPLICATE_DOC_UID`.
26. Repeated accepted upsert with same effective data remains stable and does not create duplicate side effects.

## api_events tests

27. Successful first create records a processed create event.
28. Same `event_id` replay does not create a duplicate event record.
29. Successful accepted upsert with a new `event_id` also records a processed event for that accepted request.
30. Event lookup can reconstruct idempotent response for replay.

## TSD compatibility tests

31. TSD skeletal create with `draft_only = true` succeeds and returns a server `doc_ref`.
32. TSD second `POST /api/docs` for the same `doc_uid` with richer header data is accepted as upsert.
33. `create -> upsert header -> add lines` leaves the document in `DRAFT` until explicit close.
34. Remote draft list/resume still exposes stable `doc_uid` mapping for TSD.

## WPF compatibility tests

35. WPF bridge create generates `doc_uid` and uses canonical `POST /api/docs`.
36. WPF bridge create accepts server-authored `doc_ref` replacement.
37. WPF duplicate local retry logic is retired or reduced to wrapper UX, not correctness logic.
38. WPF no longer creates drafts directly in PostgreSQL in the migrated path.

## Legacy-path tests

39. JSONL import remains independent from `api_docs` and `api_events`.
40. `/api/ops` remains outside normal draft lifecycle and does not require `doc_uid`.

## State/invariant tests

41. Create/upsert never writes ledger.
42. Create/upsert never changes draft status to `CLOSED`.
43. One `doc_uid` never maps to more than one `doc_id`.
44. Closed documents are not silently reshaped by later draft-create requests.

# Migration notes

1. The first migration target should be wrapper convergence, not business redefinition.
   - keep `POST /api/docs` as the canonical remote contract
   - keep `DocumentService.CreateDoc()` as the strict new-doc primitive behind it

2. WPF should migrate toward the remote contract in a bridge step.
   - generate `doc_uid`
   - send `event_id`
   - accept server-authored `doc_ref`
   - stop treating direct `docs` insert as the primary path

3. TSD should remain unchanged at the contract level.
   - target contract deliberately preserves its existing two-step create/upsert behavior

4. JSONL import should be explicitly left out of canonical draft-create migration scope.

5. `/api/ops` should be treated as compatibility surface outside the normal draft lifecycle.

6. The current gap in event recording for accepted existing-`doc_uid` upserts should be treated as migration work, not as intended target behavior.

7. `api_docs` and `api_events` should be treated as required metadata for the canonical remote path, but `docs.status` remains the business authority.

# Decisions requiring confirmation

1. `decision needed`: For same `doc_uid` after the document is already closed, should canonical create/upsert return a dedicated `ALREADY_CLOSED` result or a validation-style rejection?

2. `decision needed`: Should accepted existing-`doc_uid` reconciliations keep using event type `DOC_CREATE`, or should the protocol later distinguish `DOC_UPSERT`?

3. `decision needed`: Which exact header fields should be updatable in accepted upsert once the draft already exists?
   - partner
   - order
   - locations
   - HU
   - comment
   - write-off reason

4. `decision needed`: Should assigning `order_id` during an upsert populate order lines for an existing draft, or should that remain first-create-only behavior until explicitly redesigned?

5. `decision needed`: Should the create business write and `api_docs` / `api_events` metadata be moved into one physical DB transaction in the first migration step, or later?

6. `decision needed`: Does WPF need a temporary in-process bridge first, or should it move directly to HTTP `POST /api/docs`?
