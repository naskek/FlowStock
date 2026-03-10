# Purpose

This note captures the current production implementation decisions for the canonical HTTP close behavior used by:

- `POST /api/docs`
- `POST /api/docs/{docUid}/lines`
- `POST /api/docs/{docUid}/close`
- `POST /api/ops`

Scope:

- exact success/no-op semantics;
- current metadata behavior for `api_docs` and `api_events`;
- how the wrappers distinguish first close, replay, and already-closed no-op outcomes;
- current WPF feature-flag migration path.

# Current production wrapper decisions

Current production code now shares one close-phase helper:

- `apps/windows/FlowStock.Server/CanonicalCloseBehavior.cs`

Used by:

- `apps/windows/FlowStock.Server/DocumentDraftEndpoints.cs`
- `apps/windows/FlowStock.Server/CloseDocumentEndpoint.cs`
- `apps/windows/FlowStock.Server/OpsEndpoint.cs`

This means:

- the TSD-style server draft lifecycle now lives in a reusable production endpoint mapper instead of an inline `Program.cs` block;
- `/api/ops` no longer keeps its own separate close-result semantics;
- `/api/ops` create/add-line phase is still a compatibility facade, but its close phase converges to the same canonical success/no-op rules as `/api/docs/{docUid}/close`.

## Success/no-op response shape

Successful close-wrapper responses now use the explicit DTO:

```json
{
  "ok": true,
  "closed": true,
  "doc_uid": "doc-uid",
  "doc_ref": "DOC-2026-000001",
  "doc_status": "CLOSED",
  "result": "CLOSED | ALREADY_CLOSED",
  "errors": [],
  "warnings": [],
  "idempotent_replay": false,
  "already_closed": false
}
```

`success/no-op` means:

- HTTP request is accepted under the close contract;
- end authoritative state is still `docs.status = CLOSED`;
- no duplicate ledger rows are written;
- response `ok = true` and `closed = true`.

For `/api/ops`:

- `doc_uid` is currently `null`, because `/api/ops` does not participate in the `api_docs` draft lifecycle;
- the remaining fields follow the same canonical close semantics.

For the TSD-style full flow:

- `POST /api/docs` creates the server draft and `api_docs` mapping with `status = DRAFT`;
- `POST /api/docs/{docUid}/lines` appends persisted draft lines and records `DOC_LINE`;
- `POST /api/docs/{docUid}/close` returns the same `CloseDocResponse` contract already used by the standalone canonical close wrapper tests.

For WPF when `UseServerCloseDocument` is enabled:

- `MainWindow` and `OperationDetailsWindow` call a small WPF close service instead of `DocumentService.TryCloseDoc(...)`;
- that service ensures a temporary `api_docs` mapping exists for the local `doc_id`;
- WPF then calls the canonical server endpoint `POST /api/docs/{docUid}/close`;
- legacy in-process close remains available behind the feature flag.

## Distinguishing first close vs no-op close

The wrapper distinguishes close outcomes with `result`, `idempotent_replay`, and `already_closed`.

### First successful close

- `result = CLOSED`
- `idempotent_replay = false`
- `already_closed = false`

Meaning:

- this request performed the authoritative close transition.

### Same `event_id` replay

- `result = CLOSED`
- `idempotent_replay = true`
- `already_closed = false`

Meaning:

- this is a replay of the same logical close request;
- the wrapper returns the current closed state without new business work.

### New `event_id` against an already-closed document

- `result = ALREADY_CLOSED`
- `idempotent_replay = false`
- `already_closed = true`

Meaning:

- this is a distinct accepted close request;
- the document was already closed when this request was processed;
- wrapper returns canonical success/no-op.

# Metadata behavior

## `api_docs.status`

Current rule:

- any successful close response, including success/no-op, reconciles `api_docs.status` to `CLOSED`.

This applies to:

- first successful close;
- same `event_id` replay when mapping exists;
- new `event_id` against an already-closed document.

For `/api/ops`:

- `api_docs.status` is not used, because `/api/ops` does not create `api_docs` mappings.

For the TSD-style flow:

- `api_docs.status` remains `DRAFT` after `POST /api/docs` and `/lines`;
- any successful close response, including `ALREADY_CLOSED` no-op, reconciles `api_docs.status` to `CLOSED`;
- validation failures during `/close` leave `api_docs.status = DRAFT`.

For the WPF feature-flagged compatibility path:

- WPF temporarily bootstraps or reuses `api_docs` metadata directly from the local PostgreSQL write model before the HTTP close call;
- this metadata bridge exists only because the WPF create/edit lifecycle is not yet migrated to `/api/docs` and `/lines`;
- once the server close request succeeds, `api_docs.status` follows the same canonical server semantics as the TSD/API path.

## `api_events`

Current rule:

- a new accepted close request writes a `DOC_CLOSE` event to `api_events`, even if the document is already closed.

Practical effect:

- same `event_id` replay does not create a duplicate event;
- new `event_id` against an already-closed document is recorded as a processed no-op close attempt;
- later replay of that new `event_id` is idempotent.

For `/api/ops`:

- accepted requests are recorded as `OP` events, not `DOC_CLOSE`;
- same `event_id` replay does not create a duplicate `OP` event;
- new `event_id` against an already-closed `/api/ops` document is recorded as a processed `OP` no-op;
- this preserves compatibility event logging while keeping canonical close/no-op business semantics.

For the TSD-style flow:

- `POST /api/docs` records `DOC_CREATE`;
- `POST /api/docs/{docUid}/lines` records `DOC_LINE`;
- accepted `/close` requests record `DOC_CLOSE`;
- same `event_id` close replay does not create a duplicate `DOC_CLOSE`;
- validation failure on `/close` does not record `DOC_CLOSE`.

For the WPF feature-flagged path:

- WPF does not write `api_events` directly;
- WPF writes only the temporary `api_docs` mapping needed to address the canonical close endpoint;
- accepted close requests are still recorded by the server as `DOC_CLOSE`;
- validation failures or transport failures do not create `DOC_CLOSE`.

# Ledger and authoritative state

Current invariant preserved by the wrapper:

- authoritative business state remains `docs.status`, `docs.closed_at`, and `ledger`;
- no-op close responses do not change `docs.closed_at`;
- no-op close responses do not add ledger rows.

For `/api/ops`, final authoritative state after a valid request is expected to match canonical close semantics:

- `docs.status = CLOSED`
- `docs.closed_at != null`
- one authoritative set of `ledger` writes only

The wrapper still creates the draft and line inline for compatibility, but it no longer owns a distinct close-result branch.

For the TSD-style server flow, the executable lifecycle now confirms:

- `POST /api/docs` + `/lines` without `/close` leaves `docs.status = DRAFT`, `docs.closed_at = null`, and no close-side ledger rows;
- first `/close` after that lifecycle produces canonical `CLOSED`;
- same-`event_id` replay produces canonical replay success with no new ledger rows;
- new `event_id` after closure produces canonical `ALREADY_CLOSED` success/no-op with no new ledger rows;
- write-off without `reason_code` produces canonical `VALIDATION_FAILED`, leaves the draft unchanged, and does not record `DOC_CLOSE`.

For the WPF feature-flagged path, current client behavior is intentionally thin:

- client-side pre-close checks remain in WPF where they already existed;
- authoritative close success/failure after the HTTP call is taken from the server response;
- WPF treats `CLOSED`, `ALREADY_CLOSED`, and `idempotent_replay` as successful end states and refreshes UI accordingly;
- WPF does not silently fall back to legacy close when the server path is enabled but unavailable.

# Remaining gaps after this step

Still outside this implementation note:

- broader response-contract normalization for non-success cases such as `NOT_FOUND` and `EVENT_CONFLICT`;
- `/api/ops` still creates draft + line inline instead of delegating through `/api/docs` and `/api/docs/{docUid}/close`;
- `/api/ops` replay acceptance is keyed by processed `OP` event id and does not currently re-validate full payload equivalence for a same-`event_id` retry;
- TSD client-UI orchestration is still not automated above the HTTP endpoint layer;
- WPF still uses a temporary local `api_docs` bootstrap because its document lifecycle is not yet server-managed;
- WPF automation remains manual/documented and does not yet have adapter-level executable coverage;
- the legacy WPF in-process close path is still present as rollback.
