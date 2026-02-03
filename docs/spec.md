# FlowStock MVP Spec

## Scope
- Windows WPF app with local SQLite.
- No servers, no Docker, no network sync, no auth/roles/integrations.
- Stock is computed from ledger only.

## Data model (SQLite)
- items(id, name, barcode, gtin, uom)
- locations(id, code, name)
- docs(id, doc_ref, type, status, created_at, closed_at)
- doc_lines(id, doc_id, item_id, qty, from_location_id, to_location_id)
- ledger(id, ts, doc_id, item_id, location_id, qty_delta)
- imported_events(event_id, imported_at, source_file, device_id)
- import_errors(id, event_id, reason, raw_json, created_at)

## Invariants
- Stock is derived only from ledger.
- Closed documents are immutable.
- Corrections are separate documents (out of MVP scope).

## JSONL event format
One line = one JSON object:
```json
{
  "event_id": "UUID",
  "ts": "YYYY-MM-DDTHH:MM:SS",
  "device_id": "TSD-01",
  "op": "INBOUND|WRITE_OFF|MOVE|INVENTORY",
  "doc_ref": "SHIFT-YYYY-MM-DD-OP1-INBOUND",
  "barcode": "string",
  "qty": 10,
  "from": "LOC-CODE-or-null",
  "to": "LOC-CODE-or-null"
}
```

## Import rules
- Idempotency: if event_id already in imported_events, skip.
- Unknown barcode: add to import_errors with reason UNKNOWN_BARCODE; do not crash.
- Unknown location: add to import_errors with reason UNKNOWN_LOCATION.
- Invalid JSON or missing required fields: add to import_errors (INVALID_JSON or MISSING_FIELD).
- Documents are grouped by (doc_ref + op) and created as DRAFT.
- Each event appends a doc_line.
- Ledger entries are created only when the document is closed.

## Ledger rules on close
- INBOUND: +qty to to-location.
- WRITE_OFF: -qty from from-location.
- MOVE: -qty from from-location and +qty to to-location.
- INVENTORY: ledger logic deferred in MVP (document can be closed, but no ledger entries yet).

## UI screens (MVP)
- Status: stock list + search.
- Documents: list with import panel and access to import errors.
- Document: lines + "Close" action.
- Items: list + create (name, barcode, gtin, uom).
- Locations: list + create (code, name).

Import errors are shown in a modal window that lets you bind or create items for UNKNOWN_BARCODE and reapply errors.
