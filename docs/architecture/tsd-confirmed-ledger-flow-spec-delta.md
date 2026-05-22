# Delta к spec.md / spec_orders.md (TSD-confirmed ledger flow)

Черновик изменений для включения в основные спецификации после стабилизации ветки `new-ledger-logic`.

## docs/spec.md

### Онлайн-поток TSD — выпуск (Наполнение)

- После `POST /api/tsd/production/fill-pallet` при включённом `ProductionAutoCloseOnFill` сервер в той же транзакции закрывает PRD по данной паллете и пишет `ledger` (+qty по составу паллеты).
- `FILLED` без записи в `ledger` для новых операций не является складским фактом; legacy FILLED без ledger обрабатывается maintenance `backfill-filled-stock`.
- WPF close PRD для palletized happy path не обязателен; ручной close остаётся для исключений.

### Резерв HU

- Кандидаты и apply для CUSTOMER принимают только `LEDGER_STOCK` (положительный остаток по `ledger`).
- `INTERNAL_FILLED` удалён из API кандидатов.

### Онлайн-поток TSD — отгрузка

- При `OutboundAutoCloseOnComplete` последний scan или `complete` закрывает draft OUT и пишет `ledger` (−qty).
- Сообщение «ожидает проведения в WPF» не используется в normal path.

## docs/spec_orders.md

### order_receipt_plan_lines (CUSTOMER)

- Семантика: **физическое закрепление HU** (`customer_hu_allocations`), не будущий выпуск.
- Резерв возможен только при `ledger` balance > 0 по HU.

### production_pallets / PRD

- Целевой flow: один closed PRD на одну паллету при TSD fill (при auto-close).
- Прогресс INTERNAL: сумма closed PRD по `order_line_id`; отгрузка CUSTOMER не уменьшает выполнение INTERNAL.

### Статусы CUSTOMER

- «Готов» (`ACCEPTED`): coverage физическим stock + резервом ledger-backed HU; FILLED без ledger не учитывается в readiness после миграции guards.

### ЧЗ export

- `reserved_filled_hu_qty` для CUSTOMER: только HU с положительным `ledger` balance (после phase 5 finalize).
