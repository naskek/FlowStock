# RFC: TSD-confirmed ledger flow

**Статус:** принят к реализации  
**Базовая точка:** `6afaac6`  
**Ветка:** `new-ledger-logic`  
**Дата:** 2026-05-22

## 1. Current architecture map

### 1.1 Production / PRD

| Шаг | Компонент | Файлы / API |
|-----|-----------|-------------|
| План паллет | `ProductionPalletService.PlanOrder` | `POST /api/orders/{orderId}/production-pallets/plan` |
| Печать | `MarkPrinted` | `POST .../mark-printed` → `PLANNED` → `PRINTED` |
| TSD fill | `Fill` → `MarkProductionPalletFilled` | `POST /api/tsd/production/fill-pallet` → `FILLED`, **без ledger** |
| Ledger + | `DocumentService.TryCloseDoc` | `POST /api/docs/{docUid}/close` → `AddProductionPalletLedgerEntries` |
| WPF gate | `WpfCloseDocumentService` | обязательный close в normal path |

Один draft PRD на весь план паллет; close атомарен по всем `FILLED` паллетам.

### 1.2 Customer reservation

- Таблица `order_receipt_plan_lines`: план INTERNAL + резерв CUSTOMER.
- Кандидаты: `LEDGER_STOCK` + `INTERNAL_FILLED` (FILLED паллета, draft PRD, нет ledger).
- Apply не пишет в `ledger`; уменьшает `receipt_remaining` / влияет на `qty_customer_ready`.

### 1.3 OUT / TSD

- Expected: резерв ∩ ledger `qty > 0`.
- Scan → draft `OUTBOUND` (`TSD OUTBOUND PICKING`).
- `Complete` → comment `READY`, **без close и ledger**.
- Ledger − только через WPF/server close OUT.

### 1.4 Order status

- CUSTOMER «Готов» (`ACCEPTED`): coverage через receipt_remaining (вкл. резерв/FILLED).
- CUSTOMER «Выполнен» (`SHIPPED`): closed OUT.
- INTERNAL «Выполнен»: только **closed PRD** по строкам заказа.

---

## 2. Problems with current model

1. Два «склада»: `FILLED` / plan lines vs `ledger`.
2. Разрыв fill → WPF close → ledger; repair/backfill endpoints.
3. `INTERNAL_FILLED` резерв = обещание будущего товара.
4. Mega-PRD: нельзя провести одну паллету без WPF.
5. OUT picked ≠ shipped.
6. Смешение смыслов `order_receipt_plan_lines`.

---

## 3. Target model

**Принцип:** ledger — единственный физический stock; TSD scan = immutable doc fact + ledger в одной транзакции.

### Production

- **PRD per pallet fill:** TSD confirm → create/close PRD → ledger + → `FILLED`.
- Много closed PRD на один INTERNAL order.
- WPF close не нужен в happy path.

### Reservation

- Только физический HU (`ledger balance > 0`).
- Логическое имя: `customer_hu_allocations` (таблица `order_receipt_plan_lines` для CUSTOMER).

### OUT

- TSD scan reserved physical HU → auto close OUT при последнем HU → ledger −.

### WPF

- Мониторинг, исключения, сторно, ручные операции с audit reason.

---

## 4. Core invariants

- `PLANNED` / `PRINTED` — не товар.
- TSD production confirm = closed PRD + ledger +.
- Reservation не создаёт qty.
- OUT close = ledger −; draft picked без close не влияет на `qty_shipped`.
- CUSTOMER не резервирует future HU.
- INTERNAL progress не уменьшается отгрузкой CUSTOMER.
- Closed documents immutable; исправления — correction/storno docs.
- Один HU — одна локация; один HU — один open CUSTOMER allocation.

---

## 5. Proposed data model changes

**Остаются:** `orders`, `order_lines`, `docs`, `doc_lines`, `ledger`, `production_pallets`, `production_pallet_lines`.

**Сужаются:**

| Сущность | Целевой смысл |
|----------|---------------|
| `production_pallets.FILLED` | после ledger (или derived) |
| `order_receipt_plan_lines` | CUSTOMER: physical allocation only; INTERNAL: plan HU (не stock) |
| Draft mega-PRD | legacy |
| TSD draft OUT | internal; auto-close |

**Фаза 5 (опционально):** таблица событий TSD для offline idempotency (пока не в схеме БД).

---

## 6. API / command flow

| Команда | Endpoint / trigger |
|---------|-------------------|
| `FillProductionHuCommand` | `POST /api/tsd/production/scan-pallet` (preview) |
| `CloseProductionReceiptOnScan` | `POST /api/tsd/production/fill-pallet` (+ `ProductionAutoCloseOnFill`) |
| `ReservePhysicalHuForCustomer` | `POST .../hu-reservations/apply` |
| `ScanOutboundHuCommand` | `POST /api/tsd/outbound/orders/{id}/scan` |
| `CompleteOutboundOnLastHu` | scan last HU / `complete` (+ `OutboundAutoCloseOnComplete`) |

**Feature flags (server config / env):**

- `FlowStock:ProductionAutoCloseOnFill` (default `true` on `new-ledger-logic`)
- `FlowStock:OutboundAutoCloseOnComplete` (default `true`)

**Идемпотентность:**

- Production: `production_pallet_id` / `AlreadyFilled` + closed PRD guard
- Outbound: `(draft_id, from_hu)` + `AlreadyPicked`
- Ledger: doc status `CLOSED` blocks duplicate close

---

## 7. TSD UX flow

### Production

1. Наполнение → выбор заказа → scan HU → confirm.
2. Ответ: «Проведено PRD-{ref}», без «ждёт WPF».
3. Повторный scan: «Уже проведено».

### Outbound

1. Отгрузка → заказ «Готов» → список reserved physical HU.
2. Scan каждой паллеты; при последней — auto close, «Отгрузка проведена OUT-{ref}».

---

## 8. WPF UX flow

- Карточка заказа: «План паллет» отдельно от «Факты выпуска (PRD)».
- Close PRD/OUT в операции — «Ручное проведение (исключение)», не primary CTA.
- Резерв HU: только `LEDGER_STOCK` / «Готово к отгрузке».
- Мониторинг статусов и диагностика без обязательного gate.

---

## 9. Migration phases

| Фаза | Содержание | Rollback |
|------|------------|----------|
| 0 | RFC + spec delta | — |
| 1 | Guards: no INTERNAL_FILLED, physical OUT | revert SQL/service |
| 2 | Auto close PRD on fill | flag off |
| 3 | Auto close OUT on complete | flag off |
| 4 | WPF/TSD UI texts | revert UI |
| 5 | Legacy cleanup, spec finalize | maintenance restore |

**Data:** существующие `INTERNAL_FILLED` plan lines — `maintenance backfill-reservations` trim; FILLED без ledger — `backfill-filled-stock` dry-run.

---

## 10. Test plan

1. TSD fill → closed PRD + ledger receipt  
2. Repeated fill idempotent  
3. INTERNAL progress = sum closed PRD  
4. Reserve only physical HU  
5. INTERNAL_FILLED impossible  
6. One HU — one open CUSTOMER reserve  
7. Outbound expected = reserved + ledger  
8. Scan creates/updates OUT draft  
9. Last scan closes OUT + ledger issue  
10. Repeated outbound scan idempotent  
11. WPF PRD close not required (flag on)  
12. WPF OUT close not required (flag on)  
13. Storno via correction draft  
14. offline retry idempotency (phase 5, schema TBD)

Файлы: `ProductionPalletAutoCloseTests.cs`, `OutboundPickingAutoCloseTests.cs`, расширение reservation tests.

---

## 11. Risks / open questions

1. Mega-PRD с частичным FILLED — legacy close в WPF или cancel plan.  
2. Marking export: только ledger-backed reserve после фазы 5.  
3. N PRD per INTERNAL — UI pagination.  
4. Concurrent fill — transaction + unique constraints.

---

## 12. Minimal first implementation slice

1. Фаза 1: drop `INTERNAL_FILLED` + guards + tests 4–6.  
2. Фаза 2: `ProductionAutoCloseOnFill` per-pallet close on fill.  
3. Фаза 3: `OutboundAutoCloseOnComplete`.  
4. Фазы 4–5: UX + spec finalize.

См. также: [tsd-confirmed-ledger-flow-spec-delta.md](tsd-confirmed-ledger-flow-spec-delta.md).
