# Purpose

This document defines the test matrix for the server-centric `AddDocLine` migration in FlowStock.

Scope:

- freeze expected behavior before implementation changes;
- cover canonical `POST /api/docs/{docUid}/lines` behavior and lifecycle boundaries;
- identify which scenarios are executable now and which remain pending;
- align test coverage with:
  - `docs/architecture/add-doc-line-current-state.md`
  - `docs/architecture/add-doc-line-server-contract.md`

Notes:

- authoritative state is verified against `doc_lines`, `docs.status`, `ledger`, and where relevant `api_events` / `api_docs`;
- canonical remote semantics are append-only by `event_id` under one persisted `doc_uid` draft;
- pending rows are intentional only where target contract is still ahead of the current implementation or infrastructure.

# Test matrix

| Test ID | Scenario | Preconditions | Request | Expected response | Authoritative state | Automation level |
| --- | --- | --- | --- | --- | --- | --- |
| `ADL-CAN-001` | First add-line creates one `doc_lines` row | Existing canonical draft for `doc_uid`, item exists, effective location is resolvable | `POST /api/docs/{docUid}/lines` with new `event_id`, valid item, `qty > 0` | `200 OK`, `APPENDED`, accepted line payload | exactly one new `doc_lines` row, `docs.status = DRAFT`, no `ledger` writes | `integration` |
| `ADL-CAN-002` | Add-line does not write ledger | Existing canonical draft and valid line payload | Valid `POST /api/docs/{docUid}/lines` | `200 OK` | no new `ledger` rows | `integration` |
| `ADL-CAN-003` | Add-line does not change `docs.status` | Existing canonical draft and valid line payload | Valid `POST /api/docs/{docUid}/lines` | `200 OK` | `docs.status` stays `DRAFT`, `closed_at` stays `null` | `integration` |
| `ADL-CAN-004` | Add-line writes `DOC_LINE` event | Existing canonical draft and valid line payload | Valid `POST /api/docs/{docUid}/lines` | `200 OK` | one processed `DOC_LINE` in `api_events` for the same `doc_uid` | `integration` |
| `ADL-VAL-001` | Missing `event_id` fails | Existing canonical draft | `POST /api/docs/{docUid}/lines` without `event_id` | `400 BadRequest`, `MISSING_EVENT_ID` | no new `doc_lines`, no new `DOC_LINE` event | `integration` |
| `ADL-VAL-002` | Unknown `doc_uid` fails | No `api_docs` mapping for `doc_uid` | `POST /api/docs/{docUid}/lines` with otherwise valid payload | `404 NotFound`, `DOC_NOT_FOUND` | no new `doc_lines`, no new `DOC_LINE` event | `integration` |
| `ADL-VAL-003` | Non-draft document rejects line add | Existing mapped document is not `DRAFT` | `POST /api/docs/{docUid}/lines` with valid payload | `400 BadRequest`, `DOC_NOT_DRAFT` | no new `doc_lines`, no new `ledger`, no status change | `integration` |
| `ADL-VAL-004` | Unknown item fails | Existing canonical draft, no resolvable item | `POST /api/docs/{docUid}/lines` with unknown `item_id` / `barcode` | `400 BadRequest`, `UNKNOWN_ITEM` | no new `doc_lines`, no new `DOC_LINE` event | `integration` |
| `ADL-VAL-005` | `qty <= 0` fails | Existing canonical draft and resolvable item | `POST /api/docs/{docUid}/lines` with `qty = 0` | `400 BadRequest`, `INVALID_QTY` | no new `doc_lines`, no new `DOC_LINE` event | `integration` |
| `ADL-IDEM-001` | Same `event_id` plus same payload is idempotent replay | First add-line already accepted for this `event_id` | Replay same `POST /api/docs/{docUid}/lines` body | `200 OK`, `IDEMPOTENT_REPLAY`, stable line payload | still one `doc_lines` row, still one `DOC_LINE` event, `docs.status = DRAFT`, no `ledger` | `integration` |
| `ADL-IDEM-002` | Same `event_id` plus different payload returns conflict | First add-line already accepted for this `event_id` | Replay with same `event_id` but changed qty/item/location intent | `400 BadRequest`, `EVENT_ID_CONFLICT` | still one `doc_lines` row, still one `DOC_LINE` event, `docs.status = DRAFT`, no `ledger` | `integration` |
| `ADL-APP-001` | Two different `event_id` values for the same semantic line create two rows | Existing canonical draft and valid payload | Send the same semantic line twice with different `event_id` values | two successful `APPENDED` responses | two `doc_lines` rows, two `DOC_LINE` events, still no `ledger` | `integration` |
| `ADL-META-001` | Metadata stores one `DOC_LINE` event for one accepted add-line | Existing canonical draft and valid payload | Valid `POST /api/docs/{docUid}/lines` | `200 OK` | one `DOC_LINE` event stored for that `doc_uid` | `integration` |
| `ADL-TSD-001` | TSD flow `create -> add-lines` keeps server document in `DRAFT` | Draft created through canonical `POST /api/docs`, item resolvable by barcode | `POST /api/docs/{docUid}/lines` using TSD-style payload | `200 OK` | `docs.status = DRAFT`, one appended `doc_lines` row, no `ledger` writes | `integration` |
| `ADL-LEG-001` | `/api/ops` remains outside canonical add-line lifecycle | `/api/ops` request with its own operation payload | `POST /api/ops` | request may succeed, but not as canonical `DOC_LINE` append | no `api_docs` mapping, no `DOC_LINE` events, compatibility flow remains separate | `integration` |
| `ADL-LEG-002` | JSONL import remains outside canonical add-line lifecycle | Import input produces line append through local import path | Run import through `ImportService` | import succeeds through legacy path | `imported_events` used, not `api_events`; no canonical `doc_uid` line lifecycle | `pending` |

# Coverage notes

- Executable now:
  - first canonical add-line through `POST /api/docs/{docUid}/lines`;
  - no-ledger and no-status-change invariants;
  - line-write `DOC_LINE` metadata;
  - baseline validation failures;
  - canonical replay for `same event_id + same payload`;
  - canonical conflict for `same event_id + different payload`;
  - append-only behavior for different `event_id` values;
  - TSD-compatible `create -> add-lines -> still DRAFT` flow;
  - `/api/ops` boundary as non-canonical lifecycle.

- Deferred:
  - JSONL import harness inside `FlowStock.Server.Tests`.
