# FlowStock Server MVP Spec

## Scope
- Server-centric workflow. The server is the single source of truth.
- Desktop WPF client connects directly to PostgreSQL as an operator UI.
- TSD PWA works online through the server API (no offline JSONL).
- PostgreSQL is the only supported storage.
- Stock is computed from ledger only.

## Components
- FlowStock.Server: ASP.NET Core Minimal API, DB access, diagnostics, serves TSD/PC web clients.
- FlowStock.App: WPF desktop operator UI with direct PostgreSQL connection (FLOWSTOCK_PG_* env or settings.json postgres).
- TSD PWA: online data capture via API (no direct DB access).
- PC web client: read-only view served by the server.

## Data model (server DB)
- items(id, name, barcode, gtin, base_uom, default_packaging_id, brand, volume, shelf_life_months, tara_id)
- taras(id, name)
- item_requests(id, barcode, comment, device_id, login, status, created_at, resolved_at)
- locations(id, code, name)
- partners(id, name, inn, ... )
- orders(id, order_ref, partner_id, due_date, status, comment, created_at)
- order_lines(id, order_id, item_id, qty_ordered)
- docs(id, doc_ref, type, status, created_at, closed_at, partner_id, order_id, order_ref, shipping_ref, reason_code)
- doc_lines(id, doc_id, item_id, qty, from_location_id, to_location_id, uom, from_hu, to_hu)
- ledger(id, ts, doc_id, item_id, location_id, qty_delta, hu_code)

## Invariants
- Stock is derived only from ledger.
- Closed documents are immutable.
- Corrections are separate documents (out of MVP scope).
- HU code can have stock only in one location at a time.
- Write-off documents require a reason_code before closing.

## TSD online flow
- Drafts are created via API; server assigns `doc_ref`.
- Lines are added via API; closing a document writes to the ledger.
- `doc_ref` format: `TYPE-YYYY-000001` (sequence is zero-padded to 6 digits and is unique across all doc types per year).
- Drafts with `doc_uid` can be resumed on TSD (server draft is reconstructed as a local draft for editing).
- TSD scanner defaults to Keyboard wedge; Intent mode works only via JS bridge (see TSD README). For Chrome/PWA, keep Keyboard mode and disable Intent output.

## Unknown items
- TSD cannot create items directly.
- If a scanned barcode is missing, TSD sends an item request (barcode + comment + device/login).
- Requests are stored in `item_requests` and shown in WPF notifications for operator review.

## UI screens (MVP)
- Status: stock list + search.
- Documents: list + details + close action.
- Items: list + create (name, barcode/SKU, gtin, brand, volume, shelf life months, tara, uom) + Excel import with preview and column mapping.
- Tara: dictionary editor in “Справочники”.
- Locations: list + create (code, name).
- Partners: list + create.
- Orders: list + details (see spec_orders.md).
- Item requests: bell notification + list with resolve action.
