# Purpose

This document records the minimal production hardening that brought `POST /api/docs/{docUid}/lines` up to the current replay/idempotency contract.

Reference inputs:

- `docs/architecture/add-doc-line-server-contract.md`
- `docs/architecture/add-doc-line-test-matrix.md`
- `docs/architecture/add-doc-line-test-spec.md`

# Implementation summary

Production hardening was intentionally kept narrow and local to the server wrapper around `AddDocLine`.

Primary implementation anchor:

- `apps/windows/FlowStock.Server/DocumentDraftEndpoints.cs`
  - `HandleAddLineAsync()`

No WPF code, no TSD UI code, and no JSONL import code were changed on that replay-hardening step.

A later WPF bridge step can consume this contract without changing these server semantics.

# Exact replay semantics

## First accepted add-line

When `POST /api/docs/{docUid}/lines` receives a new accepted line request:

- server validates the request;
- server appends exactly one new `doc_lines` row;
- server does not write `ledger`;
- server does not change `docs.status`;
- server records one `DOC_LINE` event in `api_events`;
- server stores replay metadata in `api_events.raw_json` containing:
  - normalized request payload
  - authoritative accepted line response
  - resulting document status at acceptance time.

Canonical response:

- `ok = true`
- `result = APPENDED`
- `doc_uid = ...`
- `doc_status = DRAFT`
- `appended = true`
- `idempotent_replay = false`
- `line = accepted authoritative line payload`

## Same `event_id` plus same normalized payload

Canonical behavior:

- no second `doc_lines` row is appended;
- no second `DOC_LINE` event is recorded;
- server returns an idempotent replay response using the stored authoritative line payload.

Canonical response:

- `ok = true`
- `result = IDEMPOTENT_REPLAY`
- `doc_uid = ...`
- `doc_status = stored/current authoritative status`
- `appended = false`
- `idempotent_replay = true`
- `line = originally accepted authoritative line payload`

## Same `event_id` plus different normalized payload

Canonical behavior:

- request is rejected as replay conflict;
- no second `doc_lines` row is appended;
- no second `DOC_LINE` event is recorded;
- `docs.status` and `docs.closed_at` remain unchanged;
- `ledger` remains untouched.

Canonical error:

- `400 BadRequest`
- `ApiResult(false, "EVENT_ID_CONFLICT")`

## Different `event_id` plus same semantic line intent

Canonical behavior remains append-only:

- server treats the request as a new accepted line intent;
- server appends a second `doc_lines` row;
- server records a second `DOC_LINE` event.

This is not replay, even if item, qty, and visible business meaning are the same.

# Normalized payload comparison

The replay comparison is done on a normalized request payload, not on raw JSON text.

Fields currently included in normalized comparison:

- `doc_uid`
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

Normalization rules:

- string fields are trimmed;
- HU fields are normalized through the same HU normalization helper used elsewhere in the endpoint;
- comparison is case-insensitive for normalized string fields;
- numeric fields are compared by their parsed values.

Important current scope choice:

- replay comparison is request-normalized, not header-state-normalized;
- this keeps replay semantics tied to the original accepted request intent instead of current mutable header state.

# Replay response shape

Current replay response shape for canonical idempotent replay:

```json
{
  "ok": true,
  "result": "IDEMPOTENT_REPLAY",
  "doc_uid": "...",
  "doc_status": "DRAFT",
  "appended": false,
  "idempotent_replay": true,
  "line": {
    "id": 123,
    "item_id": 100,
    "qty": 5.0,
    "uom_code": "BOX",
    "order_line_id": null,
    "from_location_id": null,
    "to_location_id": 10,
    "from_hu": null,
    "to_hu": null
  }
}
```

Current accepted-new-line response shape:

```json
{
  "ok": true,
  "result": "APPENDED",
  "doc_uid": "...",
  "doc_status": "DRAFT",
  "appended": true,
  "idempotent_replay": false,
  "line": {
    "id": 123,
    "item_id": 100,
    "qty": 5.0,
    "uom_code": "BOX",
    "order_line_id": null,
    "from_location_id": null,
    "to_location_id": 10,
    "from_hu": null,
    "to_hu": null
  }
}
```

# Implementation notes

1. The hardening stays compatible with the current append-only model.
   - no merge was added;
   - no line update/upsert path was introduced.

2. Replay storage is pragmatic.
   - `api_events.raw_json` stores replay metadata for new `DOC_LINE` events;
   - this avoided schema work and kept the change local to the existing endpoint and event store contract.

3. Legacy tolerance is preserved.
   - if an older `DOC_LINE` event contains only raw request JSON, replay handling falls back to legacy parsing;
   - stable line identity on replay is guaranteed for events recorded after this hardening step.

4. Business invariants were preserved.
   - no `ledger` writes at add-line time;
   - no `docs.status` transition;
   - no `docs.closed_at` mutation.

5. WPF bridge migration consumes this contract as-is.
   - WPF add-line under feature flag does not change replay semantics;
   - WPF remains responsible only for client-side preparation and temporary `api_docs` bridge metadata until draft creation is also migrated.

# Remaining gaps after this step

- WPF line operations still need broader migration beyond manual `+ Товар`.
- JSONL import remains outside canonical add-line lifecycle.
- `/api/ops` remains a compatibility wrapper outside canonical draft line lifecycle.
- Legacy `DOC_LINE` events recorded before this hardening step may replay without a full authoritative line payload, because only new events carry the stored replay envelope.
