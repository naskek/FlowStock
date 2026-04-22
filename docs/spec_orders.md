# MVP: Заказы

## Модель данных

Таблица `orders`:
- `id` INTEGER PRIMARY KEY
- `order_ref` TEXT NOT NULL
- `order_type` TEXT NOT NULL DEFAULT `CUSTOMER`  // `CUSTOMER` | `INTERNAL`
- `partner_id` INTEGER NULL (FK -> `partners.id`)
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
- В `doc_lines` используется `order_line_id` для связи строки отгрузки с позицией заказа.
- При редактировании заказа существующие строки сохраняют свой `order_lines.id` (для тех же `item_id`), чтобы не ломать связи уже созданных документов.

Связь с выпуском продукции:
- PRD (`docs.type = PRODUCTION_RECEIPT`) также может быть связан с заказом через `order_id` / `order_ref`.
- В `doc_lines.order_line_id` хранится связь строки выпуска с позицией заказа.
- Для `INTERNAL`-заказа PRD является документом исполнения заказа.
- В WPF автозаполнение PRD из заказа берет строки заказа с количеством `qty_ordered` (не `receipt_remaining`).
- Проведение PRD не ограничивается остатком уже закрытых выпусков по строке заказа (`receipt_remaining`), чтобы поддержать повторный выпуск под тот же заказ.

Индексы:
- `orders(order_ref)`
- `orders(partner_id)`
- `order_lines(order_id)`
- `docs(order_id)`
- `ledger(item_id, location_id)` (уже было)

## Типы заказов

- `CUSTOMER`
  - контрагент обязателен
  - заказ закрывается отгрузками OUTBOUND
  - участвует в клиентской отгрузке и в `/api/orders`
- `INTERNAL`
  - контрагент не обязателен
  - используется как внутренняя потребность на выпуск продукции
  - закрывается выпусками PRD
  - не участвует в клиентской отгрузке и по умолчанию не отдается в `/api/orders` для TSD/PC web

## Вычисляемые поля по строке заказа

Для `CUSTOMER`:
- `available_qty` = сумма `ledger.qty_delta` по `item_id` (по всем местам хранения)
- `shipped_qty` = сумма `doc_lines.qty` по закрытым OUTBOUND, где `doc_lines.order_line_id = order_lines.id`
- `remaining_qty` = max(0, `qty_ordered` - `shipped_qty`)
- `can_ship_now` = min(`remaining_qty`, max(0, `available_qty`))
- `shortage` = max(0, `remaining_qty` - max(0, `available_qty`))

Для `INTERNAL`:
- `produced_qty` = сумма `doc_lines.qty` по закрытым PRD, где `doc_lines.order_line_id = order_lines.id`
- `remaining_qty` = max(0, `qty_ordered` - `produced_qty`)
- `available_qty` = текущий остаток ГП по `ledger` для `item_id` (информационно)

## Создание отгрузки из наличия

Команда "Создать отгрузку из наличия":
1. Создается OUTBOUND-документ со статусом DRAFT:
   - `partner_id` = из заказа (`CUSTOMER` only)
   - `order_ref` = из заказа
   - `order_id` = id заказа
   - `doc_ref` = `OUT-YYYY-000001` (глобальная последовательность за год, уникальна для всех типов)
2. Для каждой позиции с `can_ship_now > 0` создается строка:
   - `qty` = `can_ship_now`
   - `from_location`:
     - если есть локация с кодом `01` -> она
     - иначе первая доступная локация
   - `from_hu` не заполняется автоматически; при проведении система сама распределяет списание по доступным остаткам выбранной локации, включая несколько HU.
3. Документ открывается в окне деталей операции.

Для `INTERNAL` создается/заполняется PRD:
- документ `PRODUCTION_RECEIPT` может быть привязан к заказу
- строки подставляются по `remaining_qty` из еще не выпущенного остатка заказа
- один `INTERNAL`-заказ может закрываться несколькими PRD (частичный выпуск)

## Правила статусов

- `ACCEPTED` и `IN_PROGRESS` можно выставлять вручную.
- Финальный статус в БД остается `SHIPPED`, но:
  - для `CUSTOMER` в UI показывается как `Отгружен`
  - для `INTERNAL` в UI показывается как `Завершен`
- Для `CUSTOMER`:
  - `SHIPPED` выставляется автоматически, если по всем позициям `remaining_qty == 0` по OUTBOUND
  - если есть факт отгрузки, статус может автоматически перейти в `IN_PROGRESS`
  - `shipped_at` = MAX(`closed_at`) по закрытым OUTBOUND
- Для `INTERNAL`:
  - ничего не выпущено -> `DRAFT` или `ACCEPTED` (если заказ не в черновике)
  - выпущено частично -> `IN_PROGRESS`
  - выпущено полностью или больше -> финальный статус `SHIPPED` / UI `Завершен`
  - `shipped_at` используется как дата завершения заказа и вычисляется как MAX(`closed_at`) по закрытым PRD

## Веб-заявки по заказам (PC)

- Веб-интерфейс не изменяет `orders`/`order_lines` напрямую.
- Доступны 2 типа заявок: `CREATE_ORDER` и `SET_ORDER_STATUS`.
- Заявки сохраняются в `order_requests` со статусом `PENDING`.
- Отправка заявки разрешена только для активного аккаунта с доступом к ПК (`tsd_devices.platform=PC` или `BOTH`).
- При создании заказа номер подставляется автоматически как следующий числовой `order_ref` по текущей БД.
- Pending-заявки `CREATE_ORDER` резервируют свой `order_ref`: `/api/orders/next-ref` учитывает еще не подтвержденные заявки. Виртуальные строки pending-заявок возвращаются в `/api/orders` только при явном `include_pending_requests=1`, чтобы WPF получал чистый список реальных заказов.
- В выборе контрагента для веб-заказа показываются только клиенты (`role=customer`), поставщики исключены; поле поддерживает поиск по имени/коду с выбором найденного значения.
- В WPF-карточке заказа поле контрагента поддерживает ввод для быстрого поиска по списку клиентов.
- PC web создает только клиентские (`CUSTOMER`) заказы; внутренние заказы создаются и ведутся в WPF.
- Страница заказов в PC web используется как просмотр заказов и загружает оба типа (`CUSTOMER` и `INTERNAL`) через `/api/orders?include_internal=1`, но создание нового заказа из PC web остается только для `CUSTOMER`.
- В строках заказа в WPF и PC web отображаются наименование, SKU/штрихкод и GTIN.
- В строке товара доступен ввод GTIN/SKU/названия с фильтрацией с первого символа и выбором мышью из выпадающего списка подсказок.
- WPF оператор обрабатывает заявки в едином окне входящих запросов (колокольчик/меню):
  - `APPROVED`: выполняется бизнес-логика `OrderService` (создание заказа или смена статуса), заявка помечается обработанной.
  - `REJECTED`: заявка помечается отклоненной без изменения заказов.
- Для заявок заказа в WPF доступно модальное окно подробностей перед подтверждением.
- Для `SET_ORDER_STATUS` разрешены только ручные статусы `ACCEPTED` и `IN_PROGRESS`; `SHIPPED` остается автоматическим.
