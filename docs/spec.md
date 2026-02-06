# FlowStock Server MVP Spec

## Scope
- Server-centric workflow. The server is the single source of truth.
- Desktop WPF client connects directly to PostgreSQL as an operator UI.
- TSD PWA works offline and syncs via JSONL files through the server.
- PostgreSQL is the only supported storage.
- JSON (non-JSONL) exchange is not used.
- Stock is computed from ledger only.

## Components
- FlowStock.Server: ASP.NET Core Minimal API, DB access, diagnostics, JSONL import/export.
- FlowStock.App: WPF desktop operator UI with direct PostgreSQL connection (FLOWSTOCK_PG_* env or settings.json postgres).
- TSD PWA: offline data capture and JSONL export/import (no direct DB access).

## Data model (server DB)
- items(id, name, barcode, gtin, uom)
- locations(id, code, name)
- partners(id, name, inn, ... )
- orders(id, order_ref, partner_id, due_date, status, comment, created_at)
- order_lines(id, order_id, item_id, qty_ordered)
- docs(id, doc_ref, type, status, created_at, closed_at, partner_id, order_id, order_ref, shipping_ref)
- doc_lines(id, doc_id, item_id, qty, from_location_id, to_location_id, uom, from_hu, to_hu)
- ledger(id, ts, doc_id, item_id, location_id, qty_delta, hu_code)
- imported_events(event_id, imported_at, source_file, device_id)
- import_errors(id, event_id, reason, raw_json, created_at)

## Invariants
- Stock is derived only from ledger.
- Closed documents are immutable.
- Corrections are separate documents (out of MVP scope).
- HU code can have stock only in one location at a time.

## JSONL exchange (offline sync)
- JSONL is the only offline format.
- One line = one JSON object.
- Operations import JSONL (events).
- Reference data export uses JSONL (items, locations, partners) when needed.
- JSON (non-JSONL) files are deprecated and not used.

## JSONL event format (operations)
```json
{
  "event_id": "UUID",
  "ts": "YYYY-MM-DDTHH:MM:SS",
  "device_id": "TSD-01",
  "op": "INBOUND|WRITE_OFF|MOVE|INVENTORY|OUTBOUND",
  "doc_ref": "IN-2025-000001",
  "barcode": "string",
  "qty": 10,
  "from": "LOC-CODE-or-null",
  "to": "LOC-CODE-or-null",
  "from_hu": "HU-or-null",
  "to_hu": "HU-or-null"
}
```
- doc_ref format: `TYPE-YYYY-000001` (sequence is zero-padded to 6 digits and is unique across all doc types per year).

## Import rules
- Idempotency: if event_id already in imported_events, skip.
- Unknown barcode: add to import_errors with reason UNKNOWN_BARCODE; do not crash.
- Unknown location: add to import_errors with reason UNKNOWN_LOCATION.
- Invalid JSONL or missing required fields: add to import_errors (INVALID_JSON or MISSING_FIELD).
- Documents are grouped by doc_ref (unique per year across all types) and created as DRAFT.
- Each event appends a doc_line.
- Ledger entries are created only when the document is closed.

## Ledger rules on close
- INBOUND: +qty to to-location.
- WRITE_OFF: -qty from from-location.
- MOVE: -qty from from-location and +qty to to-location.
- OUTBOUND: -qty from from-location.
- INVENTORY: for each item+location(+HU) in the document, adjust ledger by (counted qty - current qty). Only the specified locations are affected.

## TSD online docs
- Inventory lines can include HU per line (to_hu) when multiple HU exist in one location.
- Recount is marked by setting comment to `TSD:RECOUNT`.
- While comment includes `RECOUNT`, WPF shows a recount status label and locks editing until new TSD data clears the flag.

## Accounts
- Logins for web clients are stored in `tsd_devices` with a platform flag (`TSD` or `PC`).
- `/api/tsd/login` returns `device_id` and `platform`; UI routes to the matching web client.
- PC web client is served on a separate HTTPS port (default `7154`).

## UI screens (MVP)
- Status: stock list + search.
- Documents: list with JSONL import panel and access to import errors.
- Document: lines + "Close" action.
- Items: list + create (name, barcode, gtin, uom) + Excel import (SKU/GTIN, name).
- Locations: list + create (code, name).
- Partners: list + create.
- Orders: list + details (see spec_orders.md).

Import errors are shown in a modal window that lets you bind or create items for UNKNOWN_BARCODE and reapply errors.
