# Purpose

This document defines the canonical server-centric contract for `CloseDocument` in FlowStock, based on the current-state inventory in:

- `docs/architecture/close-document-current-state.md`

Goal of this document:

- define one canonical close business operation;
- separate close business logic from wrapper/UI preparation logic;
- define a practical server/API contract that fits the current architecture;
- provide testable behavior for the next step (integration tests and client compatibility tests).

This document does not change production code. It defines the target contract for migration planning.

# Current-state assumptions used for design

The following assumptions are taken from current code and the current-state document.

1. The main close business logic already exists in one shared core path:
   - `apps/windows/FlowStock.Core/Services/DocumentService.cs`
   - method `TryCloseDoc(long docId, bool allowNegative)`

2. WPF and Server/API already reuse this shared core path.

3. The main differences are in wrapper behavior:
   - WPF code-behind wrappers in:
     - `apps/windows/FlowStock.App/MainWindow.xaml.cs`
     - `apps/windows/FlowStock.App/OperationDetailsWindow.xaml.cs`
   - Server/API wrappers in:
     - `apps/windows/FlowStock.Server/Program.cs`

4. WPF currently has pre-close behavior outside the core close operation:
   - `TrySaveHeader()`
   - recount gating
   - partner presence checks
   - outbound preview checks

5. Server currently has multiple wrapper paths:
   - `POST /api/docs/{docUid}/close`
   - `POST /api/ops` immediate-close path

6. `api_docs` and `api_events` currently exist as API synchronization metadata around document lifecycle.

7. TSD explicit usage of `POST /api/docs/{docUid}/close` was not found in current client code.

8. `allowNegative` / warning semantics currently appear dormant:
   - wrappers handle warnings
   - inspected close code does not populate `Warnings`

9. KM-related close behavior must remain disabled during migration planning.

# Canonical operation definition

The canonical close operation for FlowStock is:

`Close a persisted draft document on the server, writing all authoritative ledger effects and changing the authoritative document status to CLOSED exactly once.`

Canonical close is a business operation, not a UI operation.

Canonical close assumes:

- the draft document already exists on the server;
- all intended document header and line edits are already persisted before close starts;
- the server is responsible for authoritative validation of close eligibility;
- ledger writes and `docs.status` transition are server-authoritative.

Authoritative state for close outcome:

- `docs.status`
- `docs.closed_at`
- `ledger`

Authoritative identity inside the business operation:

- `doc_id`

Canonical remote/API identity:

- `doc_uid` resolved to `doc_id` by server wrapper when applicable

# Boundary: pre-close preparation vs close operation

## Pre-close preparation

Pre-close preparation is any step that makes the persisted draft ready for close, but does not itself close the document.

Examples:

- save pending WPF header changes (`TrySaveHeader()`)
- resolve dirty UI state
- collect user confirmation
- perform non-authoritative previews for UX
- resolve `doc_uid -> doc_id` in HTTP wrapper
- generate `event_id` for API clients

Pre-close preparation may fail without changing document state.

## Close operation

The canonical close operation starts only after the document is already fully persisted and identified.

Canonical close includes:

- authoritative server-side validation
- authoritative ledger writes
- authoritative document status transition to `CLOSED`
- no duplicate ledger writes on repeated calls

Design rule:

- unsaved client state is outside canonical close
- persisted draft validation is inside canonical close

# Proposed application command

Proposed canonical server-side application command:

`CloseDocumentCommand`

Proposed shape:

```text
CloseDocumentCommand
- doc_id: long
- request_id: string?          // idempotency key when wrapper has one
- source: string?              // WPF | API | TSD | OPS
- device_id: string?           // optional sync metadata
```

Design intent:

- `doc_id` is the authoritative business identity
- `request_id` is wrapper-supplied idempotency metadata, not business identity
- the command closes an already persisted document

Practical fit to current architecture:

- `DocumentService.TryCloseDoc()` is the closest current implementation anchor
- wrapper layers should converge toward invoking one application-level close command with one behavior contract

# Proposed HTTP endpoint

## Canonical remote endpoint

Proposed canonical remote close path:

`POST /api/docs/{docUid}/close`

Decision:

- yes, `/api/docs/{docUid}/close` should become the canonical remote close path for server-centric document closing

Reasoning:

- the endpoint already exists
- it already matches current API synchronization model (`api_docs`, `api_events`)
- it is a practical fit for TSD and other remote clients
- it avoids inventing a new external contract when one is already present

Clarification:

- this is the canonical remote/API wrapper path
- the canonical business operation is still the application command keyed by `doc_id`

WPF note:

- WPF compatibility is not automatic, because current WPF-created documents are not guaranteed to have `doc_uid` / `api_docs` mapping
- this is addressed in the compatibility and confirmation sections below

# Request DTO

Proposed canonical remote request DTO:

```json
{
  "event_id": "uuid-string",
  "device_id": "optional-device-id"
}
```

Field rules:

- `event_id`
  - required for HTTP/API path
  - used for idempotency and event recording
- `device_id`
  - optional
  - metadata only

Not included in canonical request DTO:

- `allow_negative`
- close-time business overrides
- unsaved header changes

Reason:

- close should run on persisted draft state only
- `allowNegative` behavior is dormant and unclear in current code, so it should not be exposed as canonical contract now

# Response DTO

Proposed canonical remote response DTO:

```json
{
  "ok": true,
  "closed": true,
  "doc_uid": "doc-uid",
  "doc_ref": "DOC-2026-000001",
  "doc_status": "CLOSED",
  "result": "CLOSED",
  "errors": [],
  "warnings": [],
  "idempotent_replay": false,
  "already_closed": false
}
```

Proposed field semantics:

- `ok`
  - request accepted and processed under canonical contract
- `closed`
  - document is closed at the end of request processing
- `doc_uid`
  - echoed API identity
- `doc_ref`
  - server authoritative document reference
- `doc_status`
  - authoritative resulting status from `docs.status`
- `result`
  - one of:
    - `CLOSED`
    - `ALREADY_CLOSED`
    - `VALIDATION_FAILED`
    - `NOT_FOUND`
    - `EVENT_CONFLICT`
- `errors`
  - canonical validation or request errors
- `warnings`
  - reserved for compatibility; expected empty under current planned migration
- `idempotent_replay`
  - `true` when same logical request is replayed and no new business work is performed
- `already_closed`
  - `true` when the document was already closed before this request began

# Canonical validation rules

Canonical validation rules are validations that belong to the close business operation itself and therefore must execute on the server.

## Baseline rules to preserve from current core path

These are already in or directly aligned with `DocumentService.BuildCloseDocCheck()` and should remain canonical:

- document must exist
- document must be closable from draft state
- document must contain at least one line
- `WriteOff` requires reason
- per-line quantity must be `> 0`
- required source/target locations by document type
- `ProductionReceipt` requires line-level HU at close
- `Move` same-location without HU context is invalid
- `ProductionReceipt` must respect `max_qty_per_hu` when defined
- order remaining checks for receipt/shipment lines
- stock sufficiency / non-negative availability checks
- HU must not end in multiple locations after applying the document
- `Outbound` requires partner

## Rules that should move into canonical close

The following current wrapper-side rule should move into canonical close:

- recount gating

Reason:

- recount is a persisted document state and affects close eligibility
- it should not depend on which wrapper initiated close

Proposed canonical rule:

- if document is flagged as being on recount / waiting for TSD recount response, close must be rejected by the server business operation

## Partner validation

Current state is inconsistent:

- `OperationDetailsWindow` treats partner as required for `Inbound` and `Outbound`
- current core close explicitly enforces partner for `Outbound`
- current server create path also requires partner for non-draft `Inbound` / `Outbound`

Proposed canonical treatment:

- `Outbound` partner requirement is canonical
- `Inbound` partner requirement is a `decision needed`

Pragmatic recommendation:

- do not strengthen canonical close for `Inbound` until confirmed
- keep this as a migration confirmation item, not an implicit contract change

# Non-canonical / UI-side checks

These checks should remain outside canonical close.

## Must remain outside canonical close

- `TrySaveHeader()`
  - this is preparation/persistence of pending client changes
- local dirty-state handling
- user confirmation dialogs
- outbound preview-only shortage UI (`TryValidateOutboundStock()`)
  - this is a user convenience preview, not the authoritative close validation

## Can remain duplicated per client if purely UX

- local button enable/disable logic
- immediate informational dialogs before the server call
- optimistic previews or hints

Rule:

- UI-side checks may make UX better
- but canonical correctness must not depend on them

# State transition rules

Canonical state rules:

1. Close operates on persisted draft documents only.
2. Successful close changes authoritative state:
   - `docs.status: DRAFT -> CLOSED`
   - `docs.closed_at: null -> timestamp`
3. Ledger effects are written exactly once.
4. After a successful close, the document is immutable for normal edits.
5. Validation failure leaves document in `DRAFT` and must not write ledger.

Repeated call rule:

- repeated close against an already closed document must be treated as an idempotent no-op at the canonical contract level

Practical meaning:

- no new ledger writes
- no second close transition
- response indicates `already_closed = true`

# Ledger behavior

Migration invariant:

- ledger semantics must remain identical to current `DocumentService.TryCloseDoc()` behavior during close migration

Authoritative reference implementation today:

- `apps/windows/FlowStock.Core/Services/DocumentService.cs`
  - `TryCloseDoc()`
  - `ResolveLedgerHu()`

Canonical ledger behavior to preserve:

- `Inbound`: positive ledger write to target location / target HU
- `ProductionReceipt`: same positive write semantics as current core path
- `WriteOff`: negative ledger write from source location / source HU
- `Move`: one negative source entry and one positive target entry
- `Inventory`: writes delta versus current balance, not raw snapshot rows
- `Outbound` with explicit source: consume from selected source
- `Outbound` without explicit source: preserve current allocation semantics across source balances and HU ordering

Important migration rule:

- close migration must not change ledger math or source selection behavior

# Transaction boundary

Proposed canonical business transaction boundary:

- all authoritative close business writes must happen in one DB transaction

Must be inside canonical business transaction:

- authoritative close validation that depends on mutable DB state
- any inferred authoritative header write needed by close itself
- all `ledger` inserts
- `docs.status` update
- `docs.closed_at` update

Pragmatic note:

- current code performs `BuildCloseDocCheck()` before `ExecuteInTransaction()`
- target contract should treat this as a migration risk
- eventual server-centric implementation should move close-critical mutable-state validation inside the same business transaction, or otherwise prove that races are acceptable

`decision needed`:

- whether all mutable validation is moved fully inside the transaction in the first migration step, or only documented/tested first

# Idempotency / repeated call behavior

Canonical repeated-call behavior:

## Same `event_id`, same `docUid`

- treat as idempotent replay
- return success/no-op if prior call already closed the document or already completed successfully
- do not write duplicate ledger rows

## Different `event_id`, document already closed

Proposed canonical behavior:

- return success/no-op with `result = ALREADY_CLOSED`
- do not write duplicate ledger rows
- wrapper may still record the new close attempt as processed metadata

Reason:

- close is naturally idempotent by final authoritative document state
- clients should not receive a business failure for retrying an already-completed close

## Event ID conflict across different document intents

- must be rejected as request conflict

# API metadata handling (api_docs / api_events)

## `api_docs`

Proposed treatment:

- `api_docs` should be treated as synchronization metadata only
- `docs.status` remains authoritative

Decision:

- `api_docs.status` should not be a second business-authoritative status
- it should mirror synchronization state for remote clients

Practical contract:

- on successful close or idempotent already-closed response, `api_docs.status` should be reconciled to `CLOSED` if mapping exists
- if `api_docs.status` drifts, `docs.status` wins

## `api_events`

Proposed treatment:

- `api_events` remains the idempotency/event log mechanism for HTTP/API calls
- accepted close requests should be recordable as processed events

Pragmatic note under current architecture:

- `api_docs` / `api_events` are wrapper metadata around business close
- they should not control ledger semantics

`decision needed`:

- whether metadata writes must be included in the same physical DB transaction as business close in the first migration step
- practical recommendation: not required for first migration step, as long as repeated calls reconcile metadata and never duplicate ledger

# Compatibility with WPF

Canonical compatibility rule:

- WPF should eventually use the canonical server close operation, but only after pre-close preparation is complete

## What WPF must do before canonical close

- persist pending header changes
- resolve local dirty state
- keep local UX checks if desired

## What WPF must not own after migration

- authoritative close validation
- authoritative ledger posting
- authoritative document status transition

## Practical migration note

Current WPF issue:

- WPF-created documents are identified locally by `doc_id`
- canonical remote endpoint is keyed by `doc_uid`
- current WPF flow does not guarantee `api_docs` / `doc_uid` mapping for all local documents

Practical migration options:

1. WPF keeps calling the canonical application operation in-process temporarily, while still moving close authority conceptually to the server-side application layer.
2. WPF gets a temporary compatibility wrapper keyed by `doc_id`.
3. WPF document lifecycle is migrated earlier so that WPF documents also have canonical `doc_uid` before close migration.

Recommended position for planning:

- canonical remote path remains `/api/docs/{docUid}/close`
- WPF compatibility path is a separate migration concern

`decision needed`:

- which of the three WPF compatibility options will be used

# Compatibility with TSD

Target compatibility rule:

- TSD should explicitly close uploaded server drafts through the canonical close endpoint

Current observed gap:

- explicit TSD usage of `/api/docs/{docUid}/close` was not found

Target behavior:

1. TSD creates server draft via `/api/docs`
2. TSD uploads all lines via `/api/docs/{docUid}/lines`
3. TSD explicitly calls `/api/docs/{docUid}/close`
4. only after successful close response may TSD treat server-side document as closed/synchronized

Important implication:

- local TSD states like `READY` / `EXPORTED` are not authoritative server close states

Client contract rule:

- absence of the explicit close call must mean the server document remains draft

# Migration handling for /api/ops

Decision:

- `/api/ops` should not remain a separate business implementation of close

Target role for `/api/ops`:

- compatibility facade only

Target behavior:

- `/api/ops` may continue to exist for client compatibility
- but internally it should delegate to the same canonical server application operations:
  - resolve/create draft
  - add line(s)
  - invoke canonical close operation

Migration rule:

- no separate posting logic is allowed to remain in `/api/ops`
- it must become a wrapper over the same close command/application service

# Treatment of allowNegative / warnings

Decision:

- current dormant `allowNegative` / warning branch should not be promoted into the canonical contract during this migration

Reasoning:

- current wrapper logic expects warnings
- inspected core logic does not populate them
- semantics are unclear and not testable enough to standardize now

Target contract for this migration step:

- request DTO does not expose `allow_negative`
- response may still include `warnings` for compatibility, but tests should expect it to be empty unless code proves otherwise later
- any future reactivation of negative-stock override semantics should be handled as a separate design/test change

# KM migration invariant

Decision:

- yes, disabled KM close behavior must be preserved as an explicit migration invariant

Migration invariant:

- canonical close must behave as if KM close enforcement is disabled
- migration must not reintroduce KM validation or KM close side effects unless explicitly designed and tested later

Authoritative current signal:

- current close path is planned under disabled KM behavior in repo state

# Test specification

This section defines tests that should be implemented in the next step.

## Canonical close integration tests

1. `Inbound` draft closes successfully and writes exactly one positive ledger entry per line.
2. `ProductionReceipt` draft closes successfully with preserved current HU semantics.
3. `ProductionReceipt` fails when required line HU is missing.
4. `ProductionReceipt` fails when `max_qty_per_hu` is exceeded.
5. `WriteOff` fails without reason.
6. `WriteOff` closes successfully with negative ledger writes.
7. `Move` closes successfully with matching negative and positive ledger writes.
8. `Move` fails when source and target locations are the same and HU context is missing.
9. `Inventory` closes by writing delta versus current stock, not raw quantity.
10. `Outbound` with explicit source closes using only selected source balances.
11. `Outbound` without explicit source preserves current allocation semantics across locations/HUs.
12. close fails when document has no lines.
13. close fails when document is on recount.
14. close fails without duplicate ledger writes and leaves status unchanged on validation error.
15. successful close changes `docs.status` to `CLOSED` and sets `closed_at`.

## Idempotency tests

16. repeated close with same `event_id` returns idempotent success and does not duplicate ledger.
17. repeated close with different `event_id` on already closed document returns `ALREADY_CLOSED` success and does not duplicate ledger.
18. conflicting reuse of `event_id` for another document is rejected.

## API metadata tests

19. successful HTTP close updates `api_docs.status` to `CLOSED`.
20. successful HTTP close records a `DOC_CLOSE` event in `api_events`.
21. repeated HTTP close reconciles metadata without new ledger writes.
22. if business close has already happened, repeated close can still reconcile stale `api_docs.status`.

## WPF compatibility tests

23. WPF details-window path saves dirty header state before invoking canonical close.
24. WPF main-window and details-window wrappers produce the same final server-side close outcome when document state is already persisted.
25. WPF recount gating matches canonical server recount rejection.
26. WPF outbound preview check remains UX-only and does not replace canonical server validation.

## TSD compatibility tests

27. TSD upload flow `create -> add lines -> close` results in server `docs.status = CLOSED`.
28. TSD upload flow without explicit close leaves server document in draft.
29. repeated TSD close call is idempotent.

## `/api/ops` migration tests

30. `/api/ops` still succeeds for compatible clients after migration.
31. `/api/ops` uses canonical close semantics and does not produce different ledger/status outcomes from the canonical close operation.

## Compatibility / invariant tests

32. KM-disabled behavior remains preserved during close.
33. `warnings` field remains empty under current migration contract unless code changes explicitly reintroduce warning semantics.

# Decisions requiring confirmation

1. `decision needed`: Should `Inbound` partner requirement be elevated into canonical close validation, or remain wrapper/UI-side until explicitly confirmed?

2. `decision needed`: Which WPF compatibility path is preferred during migration?
   - temporary in-process application command
   - temporary `doc_id` server wrapper
   - earlier WPF migration to server-managed `doc_uid`

3. `decision needed`: Should mutable close validation move fully inside the same DB transaction in the first migration step, or be deferred to a later hardening step?

4. `decision needed`: Should `api_docs` / `api_events` metadata writes be included in the same physical DB transaction as close in the first migration step, or remain post-commit metadata with reconciliation on retry?

5. `decision needed`: For a new `event_id` against an already closed document, should the server record the request in `api_events` as a processed no-op?

6. `decision needed`: Should `/api/ops` remain indefinitely as a compatibility facade, or should it be scheduled for deprecation after clients adopt canonical draft + close flow?
