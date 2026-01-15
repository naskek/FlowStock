# MVP: Заказы

## Модель данных

Таблица `orders`:
- `id` INTEGER PRIMARY KEY
- `order_ref` TEXT NOT NULL
- `partner_id` INTEGER NOT NULL (FK -> `partners.id`)
- `due_date` TEXT NULL (ISO date)
- `status` TEXT NOT NULL DEFAULT `ACCEPTED`  // `ACCEPTED` | `IN_PROGRESS` | `SHIPPED`
- `comment` TEXT NULL
- `created_at` TEXT NOT NULL

Таблица `order_lines`:
- `id` INTEGER PRIMARY KEY
- `order_id` INTEGER NOT NULL (FK -> `orders.id`)
- `item_id` INTEGER NOT NULL (FK -> `items.id`)
- `qty_ordered` REAL NOT NULL

Связь с отгрузками:
- В `docs` добавлено поле `order_id` (NULL, FK -> `orders.id`)
- OUTBOUND, созданные из заказа, получают `order_id` и `order_ref`.

Индексы:
- `orders(order_ref)`
- `orders(partner_id)`
- `order_lines(order_id)`
- `docs(order_id)`
- `ledger(item_id, location_id)` (уже было)

## Вычисляемые поля по строке заказа

Для каждой позиции:
- `available_qty` = сумма `ledger.qty_delta` по `item_id` (по всем местам хранения)
- `shipped_qty` = сумма `doc_lines.qty` по закрытым OUTBOUND с `order_id` и соответствующим `item_id`
- `remaining_qty` = max(0, `qty_ordered` - `shipped_qty`)
- `can_ship_now` = min(`remaining_qty`, max(0, `available_qty`))
- `shortage` = max(0, `remaining_qty` - max(0, `available_qty`))

## Создание отгрузки из наличия

Команда "Создать отгрузку из наличия":
1. Создается OUTBOUND-документ со статусом DRAFT:
   - `partner_id` = из заказа
   - `order_ref` = из заказа
   - `order_id` = id заказа
   - `doc_ref` = `OUT-{order_ref}-{yyyyMMdd-HHmm}` (с уникальным суффиксом при коллизии)
2. Для каждой позиции с `can_ship_now > 0` создается строка:
   - `qty` = `can_ship_now`
   - `from_location`:
     - если есть локация с кодом `01` -> она
     - иначе первая доступная локация
3. Документ открывается в окне деталей операции.

## Правила статусов

- `ACCEPTED` и `IN_PROGRESS` можно выставлять вручную.
- `SHIPPED` выставляется автоматически, если по всем позициям `remaining_qty == 0`.
- Если для заказа создана хотя бы одна OUTBOUND, статус может автоматически перейти в `IN_PROGRESS`.
- `shipped_at` вычисляется как MAX(`closed_at`) по закрытым OUTBOUND с `order_id` и показывается только если заказ полностью отгружен.
