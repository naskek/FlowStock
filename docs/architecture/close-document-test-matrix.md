# Purpose

This document defines the test matrix for the server-centric `CloseDocument` migration in FlowStock.

Scope:

- freeze expected behavior before implementation changes;
- cover canonical close behavior and wrapper compatibility;
- identify which scenarios are executable now and which remain pending;
- align test coverage with:
  - `docs/architecture/close-document-current-state.md`
  - `docs/architecture/close-document-server-contract.md`

Notes:

- authoritative state is always verified against `docs.status`, `docs.closed_at`, `ledger`, and where relevant `api_docs` / `api_events`;
- `KM` remains disabled as a migration invariant;
- pending rows are intentional when current infrastructure is not yet sufficient for automation without production refactoring.

# Test matrix

| Test ID | Category | Scenario name | Preconditions | Action | Expected result | Authoritative state to verify | Client relevance | Automation level |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `CD-CAN-001` | Canonical close | Successful close changes status to `CLOSED` | Persisted `DRAFT` inbound document with one valid line and target location | Invoke canonical close on persisted `doc_id` | Close succeeds | `docs.status = CLOSED`, `docs.closed_at != null` | Server / WPF / OPS | `integration` |
| `CD-CAN-002` | Canonical close | Successful close writes ledger exactly once | Persisted `DRAFT` inbound document with one valid line | Invoke canonical close once | Exactly one ledger effect for the line | `ledger` row count, `qty_delta`, `doc_id` | Server / WPF / OPS | `integration` |
| `CD-CAN-003` | Canonical close | Inventory close writes delta, not raw counted qty | Persisted `DRAFT` inventory document with existing stock already on hand | Invoke canonical close | Close succeeds with delta write only | `ledger.qty_delta`, `docs.status`, `docs.closed_at` | Server / WPF | `integration` |
| `CD-CAN-004` | Canonical close | Validation failure leaves draft unchanged | Persisted invalid draft document | Invoke canonical close | Close fails, no posting side effects | `docs.status = DRAFT`, `docs.closed_at = null`, no new `ledger` rows | Server / WPF / TSD / OPS | `integration` |
| `CD-VAL-001` | Validation | Close with no lines fails | Persisted `DRAFT` document without lines | Invoke canonical close | Validation error | `docs.status = DRAFT`, no new `ledger` rows | Server / WPF / TSD / OPS | `integration` |
| `CD-VAL-002` | Validation | Write-off without reason fails | Persisted `DRAFT` write-off with valid source line and stock, but no `reason` | Invoke canonical close | Validation error | `docs.status = DRAFT`, no new `ledger` rows | Server / WPF / OPS | `integration` |
| `CD-VAL-003` | Validation | Production receipt without HU fails | Persisted `DRAFT` production receipt with valid target location but missing line HU | Invoke canonical close | Validation error | `docs.status = DRAFT`, no new `ledger` rows | Server / WPF / OPS | `integration` |
| `CD-VAL-004` | Validation | Production receipt exceeding `max_qty_per_hu` fails | Persisted `DRAFT` production receipt, item has `max_qty_per_hu`, line qty exceeds limit | Invoke canonical close | Validation error | `docs.status = DRAFT`, no new `ledger` rows | Server / WPF / OPS | `integration` |
| `CD-VAL-005` | Validation | Recount-gated close is rejected | Persisted `DRAFT` document with recount flag | Invoke canonical close | Rejected by canonical validation | `docs.status = DRAFT`, no new `ledger` rows | Server / WPF / TSD / OPS | `pending` |
| `CD-IDEM-001` | Idempotency | Repeated close does not duplicate ledger | Persisted valid `DRAFT` document | Invoke canonical close twice | No duplicate ledger rows after second call | `ledger` row count remains unchanged after first close | Server / WPF / TSD / OPS | `integration` |
| `CD-IDEM-002` | Idempotency | Already closed returns canonical no-op semantics | Persisted already-closed document | Invoke canonical close again | Response reports successful `ALREADY_CLOSED` no-op outcome; no new ledger rows | `docs.status = CLOSED`, `ledger` unchanged | Server / WPF / TSD / OPS | `integration` |
| `CD-IDEM-003` | Idempotency | Same `event_id` replay is idempotent | HTTP/API close already processed once for `docUid` | Replay same `POST /api/docs/{docUid}/close` request with same `event_id` | Success/no-op replay | `docs.status = CLOSED`, `ledger` unchanged, replay metadata consistent | Server / TSD | `integration` |
| `CD-META-001` | API metadata | HTTP close updates `api_docs.status` to `CLOSED` | Existing `api_docs` mapping for draft document | Call `POST /api/docs/{docUid}/close` | Business close succeeds and metadata is reconciled | `docs.status = CLOSED`, `api_docs.status = CLOSED` | Server / TSD | `integration` |
| `CD-META-002` | API metadata | HTTP close records `DOC_CLOSE` event | Existing `api_docs` mapping, valid `event_id` | Call `POST /api/docs/{docUid}/close` | Event recorded exactly once | `api_events` contains one processed close event, `ledger` not duplicated | Server / TSD | `integration` |
| `CD-META-003` | API metadata | Metadata replay reconciles stale `api_docs` without reposting | `docs.status = CLOSED`, `api_docs.status` stale, new or replayed request arrives | Call `POST /api/docs/{docUid}/close` | No reposting; metadata converges | `docs.status = CLOSED`, `ledger` unchanged, `api_docs.status = CLOSED` | Server / TSD | `integration` |
| `CD-WPF-001` | WPF compatibility | Details window saves header before close | WPF details window has dirty header state and valid lines | Trigger close from details window | Header is persisted before canonical close is invoked | Saved header fields, then `docs.status = CLOSED` | WPF | `manual` |
| `CD-WPF-002` | WPF compatibility | Main window and details window converge to same close outcome | Same persisted draft document state available from both wrappers | Close via main window and via details window in separate runs | Same authoritative close result | `docs.status`, `docs.closed_at`, `ledger` | WPF | `manual` |
| `CD-WPF-003` | WPF compatibility | Outbound preview remains UX-only | Outbound doc that fails authoritative stock validation | Trigger WPF preview and then canonical close | Preview may warn, but canonical result still determined by server close | `docs.status`, `ledger` | WPF | `manual` |
| `CD-TSD-001` | TSD compatibility | TSD `create -> lines -> close` ends in `CLOSED` | TSD-created server draft, all lines uploaded | Call `POST /api/docs`, then `/lines`, then `/close` | Close succeeds with canonical `CloseDocResponse` | `docs.status = CLOSED`, `docs.closed_at != null`, `ledger` posted, `api_docs.status = CLOSED`, `DOC_CREATE`/`DOC_LINE`/`DOC_CLOSE` recorded | TSD / Server | `integration` |
| `CD-TSD-002` | TSD compatibility | TSD `create -> lines` without `/close` remains `DRAFT` | TSD-created server draft with uploaded lines only | Call `POST /api/docs`, then `/lines`, but skip `/close` | Draft remains unposted | `docs.status = DRAFT`, `docs.closed_at = null`, no close-side ledger changes, `api_docs.status = DRAFT` | TSD / Server | `integration` |
| `CD-TSD-003` | TSD compatibility | Repeated TSD close is idempotent | TSD already closed the server draft once | Replay `/api/docs/{docUid}/close` with the same `event_id` | Success/no-op replay | `docs.status = CLOSED`, `ledger` unchanged, `DOC_CLOSE` count unchanged | TSD / Server | `integration` |
| `CD-TSD-004` | TSD compatibility | Already-closed full flow returns canonical no-op | TSD full flow already closed the draft once | Call `/api/docs/{docUid}/close` with a new `event_id` | Successful `ALREADY_CLOSED` no-op | `docs.status = CLOSED`, `docs.closed_at` unchanged, `ledger` unchanged, `api_docs.status = CLOSED`, new `DOC_CLOSE` event recorded | TSD / Server | `integration` |
| `CD-TSD-005` | TSD compatibility | Write-off without reason fails through full flow | TSD-created write-off draft with valid source line and stock, but no `reason_code` | Call `POST /api/docs`, then `/lines`, then `/close` | Close returns canonical validation failure | `docs.status = DRAFT`, `docs.closed_at = null`, `ledger` unchanged, `api_docs.status = DRAFT`, no `DOC_CLOSE` event | TSD / Server | `integration` |
| `CD-OPS-001` | `/api/ops` compatibility | `/api/ops` converges to canonical close semantics | Valid `/api/ops` payload that creates/adds line and closes | Call `POST /api/ops` | Final authoritative state matches canonical close rules | `docs.status = CLOSED`, `docs.closed_at`, `ledger` | OPS / Server | `integration` |
| `CD-OPS-002` | `/api/ops` compatibility | `/api/ops` repeated processing does not duplicate posting | Same logical op retried after successful close | Replay `POST /api/ops` | No duplicate ledger writes | `ledger` unchanged after first success | OPS / Server | `integration` |
| `CD-OPS-003` | `/api/ops` compatibility | `/api/ops` new event on already-closed doc returns success/no-op | Document already closed by prior `/api/ops` request; new `event_id` arrives with same logical doc ref | Call `POST /api/ops` with new `event_id` | Successful `ALREADY_CLOSED` no-op without reposting | `docs.status = CLOSED`, `docs.closed_at` unchanged, `ledger` unchanged, processed `OP` event recorded | OPS / Server | `integration` |
| `CD-INV-001` | Migration invariants | KM remains disabled during close | Valid close document with marked item and no KM assignment side data | Invoke canonical close | Close behavior does not require KM assignment | `docs.status`, `ledger`, no KM-driven validation errors | Server / WPF / TSD / OPS | `integration` |
| `CD-INV-002` | Migration invariants | `warnings` remain empty under migration contract | Any close attempt under current migration contract | Invoke canonical close | `warnings = []` unless future design explicitly changes it | `CloseDocument` result payload, no warning-driven side path | Server / WPF / TSD / OPS | `integration` |

# Coverage notes

- Executable now:
  - application-level close semantics around `DocumentService.TryCloseDoc()`;
  - authoritative `docs.status`, `docs.closed_at`, and `ledger` verification;
  - validation and migration invariants that do not require HTTP or a real `api_docs` / `api_events` store;
  - HTTP wrapper tests for `/api/docs/{docUid}/close`, including same-event replay, already-closed no-op, and metadata reconciliation;
  - HTTP TSD lifecycle tests for `/api/docs` -> `/lines` -> `/close`, including draft-only state, replay/no-op close semantics, and write-off validation failure;
  - HTTP `/api/ops` compatibility tests for successful close, replay without reposting, and already-closed success/no-op.

- Deferred:
  - WPF compatibility automation.
