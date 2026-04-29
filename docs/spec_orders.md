# MVP: Заказы

## Модель данных

Таблица `orders`:
- `id` INTEGER PRIMARY KEY
- `order_ref` TEXT NOT NULL
- `order_type` TEXT NOT NULL DEFAULT `CUSTOMER`  // `CUSTOMER` | `INTERNAL`
- `partner_id` INTEGER NULL (FK -> `partners.id`)
- `due_date` TEXT NULL (ISO date)
- `status` TEXT NOT NULL DEFAULT `IN_PROGRESS`  // `DRAFT` | `IN_PROGRESS` | `ACCEPTED` | `SHIPPED`
- `comment` TEXT NULL
- `created_at` TEXT NOT NULL
- `bind_reserved_stock` BOOLEAN NOT NULL DEFAULT `FALSE` // резервировать свободные HU под клиентский заказ

Таблица `order_lines`:
- `id` INTEGER PRIMARY KEY
- `order_id` INTEGER NOT NULL (FK -> `orders.id`)
- `item_id` INTEGER NOT NULL (FK -> `items.id`)
- `qty_ordered` REAL NOT NULL

Таблица `order_receipt_plan_lines`:
- `id` INTEGER PRIMARY KEY
- `order_id` INTEGER NOT NULL (FK -> `orders.id`)
- `order_line_id` INTEGER NOT NULL (FK -> `order_lines.id`)
- `item_id` INTEGER NOT NULL (FK -> `items.id`)
- `qty_planned` REAL NOT NULL
- `to_location_id` INTEGER NULL (FK -> `locations.id`)
- `to_hu` TEXT NULL
- `sort_order` INTEGER NOT NULL

Связь с отгрузками:
- В `docs` добавлено поле `order_id` (NULL, FK -> `orders.id`)
- OUTBOUND, созданные из заказа, получают `order_id` и `order_ref`.
- В `doc_lines` используется `order_line_id` для связи строки отгрузки с позицией заказа.
- При редактировании заказа существующие строки сохраняют свой `order_lines.id` (для тех же `item_id`), чтобы не ломать связи уже созданных документов.

Связь с выпуском продукции:
- PRD (`docs.type = PRODUCTION_RECEIPT`) также может быть связан с заказом через `order_id` / `order_ref`.
- В `doc_lines.order_line_id` хранится связь строки выпуска с позицией заказа.
- Для `INTERNAL`-заказа PRD является документом исполнения заказа.
- Для HU фиксируются два независимых смысла связи с заказами:
  - `origin/internal order`: происхождение HU по закрытому `PRODUCTION_RECEIPT` внутреннего заказа (`docs.order_id` + `docs.type=PRODUCTION_RECEIPT` + `doc_lines.to_hu`).
  - `reserved/customer order`: текущий резерв HU под клиентский заказ в `order_receipt_plan_lines`.
- `origin/internal order` является справочной историей происхождения HU и не является обязательным условием для клиентского резерва. Если закрытый `PRODUCTION_RECEIPT` имеет `docs.order_id = NULL`, HU все равно может быть зарезервирован как свободный складской HU; в read-model `origin_internal_order_ref` остается пустым.
- Резерв в `order_receipt_plan_lines` не изменяет `ledger` и не меняет физический остаток, а только read-model привязки.
- Для типов номенклатуры с флагом `item_types.min_stock_uses_order_binding = true` этот резерв дополнительно участвует в контроле минимального остатка как `reserved_customer_order_qty` (только по активным клиентским заказам, `status <> SHIPPED`).
- Отдельный отчет `Потребность производства` считается независимо от контроля минимума и не заменяет его.
  - `active_customer_order_open_qty` = сумма по активным `CUSTOMER`-заказам `qty_ordered - shipped_qty` по строкам.
  - Частичный резерв в `order_receipt_plan_lines` не уменьшает `active_customer_order_open_qty`, потому что резерв не является отгрузкой.
  - `reserved_customer_order_qty` в отчете показывается отдельно и может использоваться только как справочная колонка по текущему резерву.
  - `production_need_qty = max(0, active_customer_order_open_qty + min_stock_qty - physical_stock_qty)`.
- На этапе создания/обновления заказа сервер формирует план выпуска `order_receipt_plan_lines` (HU и локации хранения).
- Для `CUSTOMER`-заказа на этапе создания/обновления сервер пытается зарезервировать в `order_receipt_plan_lines` свободные HU/локации из текущего HU stock (по совпадающим SKU), независимо от наличия `origin/internal order`.
- После закрытия внутреннего выпуска (`PRD` по `INTERNAL`) сервер автоматически пересчитывает эти резервы по всем клиентским заказам (FIFO по дате создания заказа), чтобы новый складской объем сразу становился доступным к отгрузке/добору в выпуске.
- Один и тот же HU/объем не может быть одновременно зарезервирован за несколькими клиентскими заказами.
- Для клиентского заказа используется флаг `bind_reserved_stock`:
  - `true`: сервер на этапе сохранения заказа резервирует свободные HU/локации из текущего HU stock в `order_receipt_plan_lines` только для товаров, тип которых имеет `item_types.enable_order_reservation = true`;
  - `false`: привязка складского остатка к заказу не выполняется.
- В WPF подтверждение привязки складского остатка запрашивается при сохранении клиентского заказа и только если по его позициям есть остаток на складе.
- WPF/TSD автозаполнение PRD из заказа берет только незакрытый остаток `receipt_remaining` (режим `?detailed=1`), включая заранее назначенные `to_location` и `to_hu` из уже сохраненного серверного плана заказа.
- Повторный выпуск по одному и тому же заказу запрещен: если `receipt_remaining = 0`, строки заказа в PRD не подставляются и операция блокируется.

## Maintenance: backfill HU reservation-layer

После обновления production-БД, где уже есть исторические заказы, внутренние выпуски, HU и `ledger`, reservation/read-model слой можно пересобрать отдельной maintenance-командой. Команда не запускается автоматически при старте приложения.

Команда:
- dry-run по умолчанию: `dotnet FlowStock.Server.dll maintenance backfill-reservations`
- явное применение: `dotnet FlowStock.Server.dll maintenance backfill-reservations --apply`
- docker/deploy wrapper: `bash deploy/scripts/backfill_order_reservations.sh` или `bash deploy/scripts/backfill_order_reservations.sh --apply`

Server API/WPF:
- `POST /api/admin/maintenance/backfill-reservations/dry-run` выполняет dry-run на сервере и возвращает структурированный отчет без изменения данных;
- `POST /api/admin/maintenance/backfill-reservations/apply` применяет backfill только при `confirm = "APPLY"`;
- WPF запускает backfill только через server API из окна `Администрирование / Обслуживание`;
- в рамках процесса сервера параллельный запуск backfill блокируется.

Правила backfill:
- перед `--apply` обязателен свежий backup БД;
- изменяется только `order_receipt_plan_lines`;
- `ledger`, `docs` и `doc_lines` не изменяются;
- активными для резерва считаются только `CUSTOMER`-заказы с `bind_reserved_stock = true`, которые фактически еще не отгружены полностью;
- `SHIPPED`/выполненные клиентские заказы не получают активный reserve, их исторические customer reservation lines очищаются;
- количество к резерву считается как `qty_ordered - shipped_qty`, чтобы уже отгруженный объем не вычитался повторно из свободного остатка;
- кандидаты для резерва берутся из текущего положительного HU stock; наличие `docs.order_id` у origin PRD не требуется;
- если один HU уже заявлен несколькими активными customer orders, такой HU исключается из нового плана и выводится в отчете как конфликт для ручного разбора.

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
- В WPF разрешена смена типа сохраненного заказа в обе стороны (`CUSTOMER <-> INTERNAL`):
  - `CUSTOMER -> INTERNAL` разрешена только если по заказу еще нет отгрузок/связанных OUTBOUND-документов.
  - `INTERNAL -> CUSTOMER` разрешена только если по заказу еще нет выпусков продукции/связанных PRD-документов.

## Вычисляемые поля по строке заказа

Для `CUSTOMER`:
- `available_qty` = сумма `ledger.qty_delta` по `item_id` (по всем местам хранения)
- `shipped_qty` = сумма `doc_lines.qty` по закрытым OUTBOUND, где `doc_lines.order_line_id = order_lines.id`
- `remaining_qty` = max(0, `qty_ordered` - `shipped_qty`)
- если `bind_reserved_stock = true` и тип товара имеет `enable_order_reservation = true`, `can_ship_now` = min(`remaining_qty`, max(0, `produced_qty_for_order` - `shipped_qty`))
- иначе `can_ship_now` = min(`remaining_qty`, max(0, `available_qty`))
- `shortage` считается от того же доступного объема, который использован для `can_ship_now`
  - где `produced_qty_for_order` = объем, выпущенный по строке этого же заказа (`PRD`) + объем, заранее присвоенный из свободного HU stock (`order_receipt_plan_lines` для `CUSTOMER`).

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
   - `from_hu` выбирается только из HU, выпущенных под этот заказ (`PRD` с тем же `order_id`); отгрузка из "чужих" HU блокируется.
   - если заказу заранее присвоены HU из свободного HU stock, эти HU также входят в разрешенный набор источников для отгрузки.
3. Документ открывается в окне деталей операции.

Для `INTERNAL` создается/заполняется PRD:
- документ `PRODUCTION_RECEIPT` может быть привязан к заказу
- строки подставляются по `remaining_qty` из еще не выпущенного остатка заказа, с серверными назначениями `to_location/to_hu`
- после полного выпуска повторное создание PRD по этому заказу запрещено

## Планирование HU для выпуска

- В типе номенклатуры используется флаг `enable_hu_distribution`.
- Если у типа включен `enable_hu_distribution`, в карточке товара обязательно должен быть заполнен `max_qty_per_hu > 0`.
- При создании/обновлении заказа сервер дробит строки по `max_qty_per_hu` и резервирует серверные HU в `order_receipt_plan_lines`.
- TSD не генерирует HU локально: используются только серверные номера HU.

## Правила статусов

- Ручная установка статуса заказа отключена (WPF/PC web/TSD).
- Финальный статус в БД остается `SHIPPED`, в UI для всех типов показывается как `Выполнен`.
- Единая UI-схема статусов заказа:
  - `В работе` (оранжевый бейдж)
  - `Готов` (светло-зеленый бейдж)
  - `Выполнен` (темно-зеленый бейдж)
- Для `CUSTOMER`:
  - после создания/сохранения заказа статус автоматически `IN_PROGRESS` (UI: `В работе`)
  - `DRAFT` используется только локально в WPF для нового, еще не сохраненного заказа, и в UI нормализуется как `В работе`
  - после частичного выпуска PRD (с привязкой к заказу) статус остается `IN_PROGRESS` (UI: `В работе`)
  - статус `ACCEPTED` (UI: `Готов`) выставляется только после полного комплекта по заказу: `produced >= qty_ordered` по всем строкам
  - при частичной отгрузке статус возвращается в `IN_PROGRESS` (UI: `В работе`)
  - `SHIPPED` выставляется автоматически, если по всем позициям `remaining_qty == 0` по OUTBOUND (заказ закрывается сразу после проведения полного объема)
  - `shipped_at` = MAX(`closed_at`) по закрытым OUTBOUND
- Для `INTERNAL`:
  - после создания/сохранения заказа статус автоматически `IN_PROGRESS` (UI: `В работе`)
  - выпущено частично -> `IN_PROGRESS` (UI: `В работе`)
  - выпущено полностью или больше -> финальный статус `SHIPPED` / UI `Выполнен`
  - `shipped_at` используется как дата завершения заказа и вычисляется как MAX(`closed_at`) по закрытым PRD

## Веб-заявки по заказам (PC)

- Веб-интерфейс не изменяет `orders`/`order_lines` напрямую.
- Веб-интерфейс использует только заявки `CREATE_ORDER` для создания новых заказов.
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
- Запросы ручной смены статуса (`SET_ORDER_STATUS`) отключены на сервере.
