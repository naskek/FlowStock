# FlowStock Server MVP Spec

## Scope
- Server-centric workflow. The server is the single source of truth.
- Desktop WPF client connects directly to PostgreSQL as an operator UI.
- TSD PWA works online through the server API (no offline JSONL).
- PostgreSQL is the only supported storage.
- Production schema changes are applied through versioned SQL migrations; postgres init scripts are bootstrap-only for empty data directories.
- Stock is computed from ledger only.

## Components
- FlowStock.Server: ASP.NET Core Minimal API, DB access, diagnostics, serves TSD/PC web clients.
  - Exposes `/health/live` and `/health/ready` for container liveness/readiness checks.
- FlowStock.App: WPF desktop operator UI with direct PostgreSQL connection (FLOWSTOCK_PG_* env or settings.json postgres).
  - If PostgreSQL is unavailable at app startup, WPF still opens and reports DB errors in UI/logs (non-fatal startup).
- TSD PWA: online data capture via API (no direct DB access).
- PC web client: stock is read-only; order create/status changes are submitted as requests and applied only after WPF confirmation.
  - Request submission is allowed only for active accounts with PC access (`tsd_devices.platform=PC` or `BOTH`).
  - Provides three operator screens on port `7154`: `Остатки`, `Каталог`, and `Заказы`.
- WPF admin stores global web block switches in PostgreSQL. If a block is disabled, it is hidden for all users of the corresponding web client and direct navigation to it is blocked after page reload.

## Data model (server DB)
- items(id, name, barcode, gtin, base_uom, default_packaging_id, brand, volume, shelf_life_months, max_qty_per_hu, tara_id, is_marked)
- taras(id, name)
- item_requests(id, barcode, comment, device_id, login, status, created_at, resolved_at)
- order_requests(id, request_type, payload_json, status, created_at, created_by_login, created_by_device_id, resolved_at, resolved_by, resolution_note, applied_order_id)
- locations(id, code, name)
- partners(id, name, inn, ... )
- orders(id, order_ref, order_type, partner_id NULL, due_date, status, comment, created_at)
- order_lines(id, order_id, item_id, qty_ordered)
- docs(id, doc_ref, type, status, created_at, closed_at, partner_id, order_id, order_ref, shipping_ref, reason_code, comment, production_batch_no)
- doc_lines(id, doc_id, replaces_line_id NULL, order_line_id, item_id, qty, qty_input, uom_code, from_location_id, to_location_id, from_hu, to_hu)
- ledger(id, ts, doc_id, item_id, location_id, qty_delta, hu_code)
- km_code_batch(id, order_id, file_name, file_hash, imported_at, imported_by, total_codes, error_count)
- km_code(id, batch_id, code_raw, gtin14, sku_id, product_name, status, receipt_doc_id, receipt_line_id, hu_id, location_id, ship_doc_id, ship_line_id, order_id)
- client_blocks(block_key, is_enabled, updated_at)

## Invariants
- Stock is derived only from ledger.
- Closed documents are immutable.
- Corrections are separate documents (out of MVP scope).
- HU code can have stock only in one location at a time.
- Write-off documents require a reason_code before closing.
- Draft line edits on the server are append-only: replacing a draft line creates a new `doc_lines` row linked by `replaces_line_id`; active draft/read projections ignore superseded rows.

## TSD online flow
- Drafts are created via API; server assigns `doc_ref`.
- Lines are added via API; closing a document writes to the ledger.
- `doc_ref` format: `TYPE-YYYY-000001` (sequence is zero-padded to 6 digits and is unique across all doc types per year).
- Drafts with `doc_uid` can be resumed on TSD (server draft is reconstructed as a local draft for editing).
- TSD scanner defaults to Keyboard wedge; Intent mode works only via JS bridge (see TSD README). For Chrome/PWA, keep Keyboard mode and disable Intent output.
- TSD home menu is grouped into `Операции`, `Остатки`, `Каталог`, and `Заказы`; `История операций` is no longer a separate top-level block.
- `Операции` on TSD contain: `Приемка`, `Выпуск продукции`, `Отгрузка`, `Перемещение`, `Списание`, `Инвентаризация`.

## Unknown items
- TSD cannot create items directly.
- If a scanned barcode is missing, TSD sends an item request (barcode + comment + device/login).
- Requests are stored in `item_requests` and shown in WPF notifications for operator review.

## UI screens (MVP)
- Status: stock list + search.
- Documents: list + details + close action.
- Items: list with ID + modal create/edit (name, barcode/SKU, gtin, brand, volume, shelf life months, max qty per HU, tara, uom, is_marked) + Excel import with preview and column mapping.
- Tara: dictionary editor in “Справочники”.
- Locations: list with ID + modal create/edit (code, name).
- Partners: list with ID + modal create/edit.
- Orders: list + details (see spec_orders.md).
- Incoming requests: single WPF inbox window (from bell/menu) for item requests and order web requests, with processing actions in one place.
  - For order requests, WPF provides a details modal with full order payload before approval.
- Admin: no direct table reset/edit from WPF; only backup and temporary admin unlock for row deletion in tabs.
- Row deletion in tabs (orders/items/locations/partners) is blocked by default and allowed only after admin unlock.
- Admin includes a dedicated "clear operations" action for test cleanup (docs/doc_lines/ledger/orders/order_lines/import events/errors). Dictionaries stay intact.
- Admin includes a dedicated section for global web block access:
  - PC blocks: `Остатки`, `Каталог`, `Заказы`.
  - TSD main blocks: `Операции`, `Остатки`, `Каталог`, `Заказы`.
  - TSD operation blocks: `Приемка`, `Выпуск продукции`, `Отгрузка`, `Перемещение`, `Списание`, `Инвентаризация`.
- KM in WPF is temporarily frozen: the KM tab, KM actions in document windows, and KM edit controls in item cards are hidden from the client. During the freeze, document validation/closing does not require KM assignment and does not auto-ship KM codes.

## Documents (extra)
- HU в документе хранится на уровне строки (`doc_lines.from_hu` / `doc_lines.to_hu`). Значение HU в шапке используется как значение по умолчанию и как быстрый выбор для операций со строками, но один документ может содержать строки с разными HU.
- `doc_lines.replaces_line_id` используется для append-only draft lifecycle semantics (`UpdateDocLine` / `DeleteDocLine`). Историческая строка остаётся в БД; удаление строки создаёт tombstone-row с `qty = 0` и `replaces_line_id = deleted_line_id`. В read-model документа и в расчетах заказов/проведения учитываются только активные строки с `qty > 0`, на которые не ссылается более новая запись.
- Выпуск продукции: приемка готовой продукции на склад (плюс в ledger), HU обязателен на момент проведения по каждой строке, партия производства хранится в `production_batch_no`, документ может быть связан с заказом (order_id/order_ref).
  - При выборе заказа автоматически подставляются остатки по строкам заказа (order_line_id) с учетом уже закрытых выпусков.
  - Для `orders.order_type = INTERNAL` этот документ является основным способом закрытия заказа по факту выпуска.
  - В WPF для выпуска используется отдельное действие `Распределить по HU`: система сама берёт свободные HU из реестра по порядку кода, при необходимости повторно вводит в оборот свободные `CLOSED` HU и, если их не хватает, создаёт новые HU.
  - В TSD выпуск продукции доступен как отдельная операция в группе `Операции`; по базовому сценарию он работает как online-документ приемки готовой продукции в выбранную локацию/HU.
  - Если в карточке товара задан `items.max_qty_per_hu`, `Распределить по HU` автоматически делит строки выпуска так, чтобы каждая строка не превышала лимит на один HU.
  - Для строки выпуска можно включить флаг общего HU (`doc_lines.pack_single_hu`): такая строка не дробится при автораспределении и может быть уложена вместе с другими отмеченными строками на один общий HU, если суммарная загрузка паллеты по формуле `sum(qty / max_qty_per_hu)` не превышает `1`.
  - При проведении приоритет имеет HU строки; HU из шапки используется только как fallback для старых/неполных строк, а не как переопределение уже назначенных HU строки.
  - При проведении проверяется, что количество в каждой строке выпуска не превышает `max_qty_per_hu` (если лимит задан), а для строк на одном HU суммарная загрузка паллеты также не превышает `1`.
  - Для совместимости со старыми черновиками при проверке/проведении выполняется авто-ремап устаревшего `order_line_id` по `item_id` (только при однозначном совпадении строки заказа).
- Отгрузка по заказу: OUTBOUND может быть связан с заказом (order_id/order_ref).
  - При выборе заказа автоматически подставляются остатки по строкам заказа (order_line_id) с учетом уже закрытых отгрузок.
  - В OUTBOUND доступны только клиентские заказы (`order_type = CUSTOMER`); внутренние заказы не участвуют в клиентской отгрузке.
  - TSD показывает pick list HU/локаций по выбранной строке и позволяет привязать HU/локацию к строке отгрузки.
  - Для маркируемых SKU pick list строится по `km_code` (status=OnHand) с фильтром по заказу; для немаркируемых — по ledger.
  - В ручной OUTBOUND выбор товара фильтруется по остаткам источника (выбранные локация/HU), чтобы показывать только отгружаемые позиции.

## Marking (KM) MVP
- KM domain logic and data storage remain in backend/DB, but the client workflow is currently frozen: WPF UI is disabled, and document validation/closing does not enforce KM assignment.
- Импорт CSV/TSV кодов в `km_code_batch` + `km_code` (защита по hash файла и UNIQUE(code_raw)).
- During KM import, code duplicate checks are enforced per file and against DB (case-insensitive), with normalization of wrapped quotes.
- При сопоставлении GTIN в КМ поддерживаются 13 и 14 цифр: 13-значный GTIN нормализуется до 14-значного добавлением ведущего `0`.
- При импорте КМ и при старте приложения выполняется авто-пометка `items.is_marked=1` для SKU, обнаруженных в `km_code` (включая сопоставление по GTIN), чтобы КМ-товары не выпадали из сценария выпуска/отгрузки.
- Привязка пакета к заказу через `order_id`.
- Коды в пуле имеют статус `InPool`; стандартный перевод в `OnHand` выполняется через документ "Выпуск продукции".
- Для маркируемых SKU (items.is_marked = true) требуется привязать ровно Qty кодов к строке документа (receipt_line_id, hu_id, location_id).
- Распределение кодов в выпуске выполняется по строке (HU+SKU+Qty) из пула `InPool` по заказу или выбранному пакету.
- Отгрузка переводит коды в статус `Shipped` и связывает с документом отгрузки.
- Для отгрузки ручной ввод КМ не требуется: при проведении OUTBOUND коды `OnHand` подбираются и списываются автоматически по SKU, заказу (если указан) и источнику строки (локация/HU, если заданы).
- В окне OUTBOUND для маркируемых строк доступны действия `Коды КМ...` и `Распределить`: можно открыть привязку кодов вручную (скан/ввод) или выполнить авто-подбор из остатков.
- Если для OUTBOUND не хватает кодов `OnHand`, система добирает недостающее из пула `InPool` (по SKU/GTIN и заказу, если он указан), чтобы завершить распределение в отгрузке.
