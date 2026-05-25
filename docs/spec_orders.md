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
- `marking_status` TEXT NOT NULL DEFAULT `NOT_REQUIRED` // `NOT_REQUIRED` | `REQUIRED` | `PRINTED`
- `marking_excel_generated_at` TEXT NULL
- `marking_printed_at` TEXT NULL
- `commercial_offer_id` BIGINT NULL (FK -> `commercial_offers.id`, миграция `V0026`) — обратная связь с КП, из которого создан заказ; **не** переносит цены в `order_lines`

Таблица `order_lines`:
- `id` INTEGER PRIMARY KEY
- `order_id` INTEGER NOT NULL (FK -> `orders.id`)
- `item_id` INTEGER NOT NULL (FK -> `items.id`)
- `qty_ordered` REAL NOT NULL
- `production_pallet_group` TEXT NULL // группа микс-паллеты; строки с одинаковой группой планируются на общий HU

Таблица `order_receipt_plan_lines`:
- `id` INTEGER PRIMARY KEY
- `order_id` INTEGER NOT NULL (FK -> `orders.id`)
- `order_line_id` INTEGER NOT NULL (FK -> `order_lines.id`)
- `item_id` INTEGER NOT NULL (FK -> `items.id`)
- `qty_planned` REAL NOT NULL
- `to_location_id` INTEGER NULL (FK -> `locations.id`)
- `to_hu` TEXT NULL
- `sort_order` INTEGER NOT NULL

Таблица `production_pallets`:
- `id` INTEGER PRIMARY KEY
- `prd_doc_id` INTEGER NOT NULL (FK -> `docs.id`)
- `doc_line_id` INTEGER NOT NULL (FK -> `doc_lines.id`)
- `order_id` INTEGER NULL (FK -> `orders.id`)
- `order_line_id` INTEGER NULL (FK -> `order_lines.id`)
- `item_id` INTEGER NOT NULL (FK -> `items.id`)
- `hu_code` TEXT NOT NULL
- `planned_qty` REAL NOT NULL
- `to_location_id` INTEGER NULL (FK -> `locations.id`)
- `status` TEXT NOT NULL // `PLANNED` | `PRINTED` | `FILLED` | `CANCELLED`
- `pallet_no` INTEGER NOT NULL DEFAULT 0
- `pallet_count` INTEGER NOT NULL DEFAULT 0
- `printed_at` TEXT NULL
- `filled_at` TEXT NULL
- `filled_by_device_id` TEXT NULL
- `created_at` TEXT NOT NULL

Таблица `production_pallet_lines`:
- `id` INTEGER PRIMARY KEY
- `production_pallet_id` INTEGER NOT NULL (FK -> `production_pallets.id`)
- `doc_line_id` INTEGER NOT NULL (FK -> `doc_lines.id`)
- `order_line_id` INTEGER NULL (FK -> `order_lines.id`)
- `item_id` INTEGER NOT NULL (FK -> `items.id`)
- `planned_qty` REAL NOT NULL
- `filled_qty` REAL NOT NULL DEFAULT `0`
- `created_at` TEXT NOT NULL

Связь с отгрузками:
- В `docs` добавлено поле `order_id` (NULL, FK -> `orders.id`)
- OUTBOUND, созданные из заказа, получают `order_id` и `order_ref`.
- В `doc_lines` используется `order_line_id` для связи строки отгрузки с позицией заказа.
- При редактировании заказа существующие строки сохраняют свой `order_lines.id` (для тех же `item_id`), чтобы не ломать связи уже созданных документов.

Создание заказа из коммерческого предложения (Commercial module):
- `POST /api/commercial/offers/{id}/create-order` доступен только для КП в статусе `WON`.
- Создаётся `CUSTOMER`-заказ с `order_lines.qty_ordered` по строкам КП; **цены из КП не копируются** в заказ.
- В `orders.commercial_offer_id` сохраняется ссылка на исходное КП; в `commercial_offers.converted_order_id` — обратная ссылка.
- Операция **не** пишет в `ledger` и не создаёт OUT/PRD. Подробнее: [`docs/architecture/commercial-offers-and-pricing-plan.md`](architecture/commercial-offers-and-pricing-plan.md).

Связь с выпуском продукции:
- PRD (`docs.type = PRODUCTION_RECEIPT`) также может быть связан с заказом через `order_id` / `order_ref`.
- В `doc_lines.order_line_id` хранится связь строки выпуска с позицией заказа.
- Для `INTERNAL`-заказа PRD является документом исполнения заказа.
- В TSD filling model один PRD может содержать несколько плановых паллет: `doc_lines.to_hu` хранит технический код паллеты/HU, `doc_lines.qty` - плановое количество, а факт наполнения хранится в `production_pallets`.
- Существующие заказы и legacy PRD без `production_pallets` остаются legacy: они не появляются в TSD `Наполнение` и не конвертируются автоматически. План паллет создается только явным действием `POST /api/orders/{orderId}/production-pallets/plan`.
- При создании плана по заказу сервер дробит обычные строки по `items.max_qty_per_hu`, а строки с одинаковым `production_pallet_group` (≥2 строк в группе) планирует как mixed pallet: один `production_pallets.hu_code` и несколько `production_pallet_lines`. Для ручной mixed-группы `max_qty_per_hu` не проверяется: оператор сам задает состав HU. Для обычных строк без группы, если capacity не задана, план не создается и возвращается ошибка `Не задано количество на паллете для номенклатуры`. Последняя одиночная паллета может быть неполной.
- В TSD режиме `Наполнение` оператор выбирает заказ с уже подготовленными паллетами, а не PRD. После выбора сервер возвращает существующий контекст наполнения (`order_id` + `prd_doc_id`) и не создает HU/план паллет на стороне TSD.
- При `ProductionAutoCloseOnFill` (default в `new-ledger-logic`) TSD fill изолирует паллету в отдельный PRD, сразу закрывает его и пишет `ledger` (+qty); паллета становится `FILLED`. Legacy path без auto-close: fill только меняет статус паллеты, `ledger` — при WPF close всего PRD. Повторный fill идемпотентен (`already_filled`), без дубля `ledger`.
- Если у PRD есть активные `production_pallets`, закрытие разрешено только когда все они `FILLED`; иначе сервер возвращает ошибку `Нельзя закрыть выпуск: есть ненаполненные паллеты`.
- Для HU фиксируются два независимых смысла связи с заказами:
  - `origin/internal order`: происхождение HU по закрытому `PRODUCTION_RECEIPT` внутреннего заказа (`docs.order_id` + `docs.type=PRODUCTION_RECEIPT` + `doc_lines.to_hu`).
  - `reserved/customer order`: текущий резерв HU под клиентский заказ в `order_receipt_plan_lines`.
- `origin/internal order` является справочной историей происхождения HU и не является обязательным условием для клиентского резерва. Если закрытый `PRODUCTION_RECEIPT` имеет `docs.order_id = NULL`, HU все равно может быть зарезервирован как свободный складской HU; в read-model `origin_internal_order_ref` остается пустым.
- Резерв в `order_receipt_plan_lines` не изменяет `ledger` и не меняет физический остаток, а только read-model привязки.
- Для типов номенклатуры с флагом `item_types.min_stock_uses_order_binding = true` этот резерв дополнительно участвует в контроле минимального остатка как `reserved_customer_order_qty` (только по активным клиентским заказам, `status NOT IN (SHIPPED, CANCELLED)`).
- Отдельный отчет `Потребность производства` считается независимо от контроля минимума и не заменяет его.
  - Отчет показывает текущую суммарную потребность по товару, без календарного фильтра в UI.
  - Клиентские заказы уже являются источником спроса и не создаются повторно из этого отчета.
  - `free_stock_qty = physical_stock_qty - reserved_customer_order_qty`.
  - `raw_to_close_orders_qty` считается как оставшаяся потребность к производству по активным `CUSTOMER`-заказам, но каждая строка дополнительно ограничивается фактическим остатком к отгрузке по закрытым `OUTBOUND`.
  - Для каждой строки `CUSTOMER`-заказа:
    - `shipment_remaining_qty = max(0, qty_ordered - shipped_by_closed_outbound_qty)`.
    - `customer_production_need_qty = min(max(0, qty_ordered - produced_or_reserved_coverage_qty), shipment_remaining_qty)`.
    - `produced_or_reserved_coverage_qty` включает:
      - закрытые `PRODUCTION_RECEIPT` по `order_line_id`;
      - `FILLED production_pallet_lines` по `order_line_id`, если palletized PRD еще не закрыт;
      - уже зарезервированный готовый складской товар из `order_receipt_plan_lines`.
    - Полностью отгруженная или over-shipped строка дает `customer_production_need_qty = 0` даже если persisted `orders.status` еще `IN_PROGRESS`/`ACCEPTED`; отрицательный остаток к отгрузке всегда зажимается в `0`.
    - После закрытия palletized PRD double count не допускается: legacy receipt lines такого PRD не суммируются поверх `production_pallet_lines`.
  - `raw_to_min_stock_qty = max(0, min_stock_qty - free_stock_qty)`.
  - Открытые клиентские заказы в статусах `IN_PROGRESS` (`В работе`) и `ACCEPTED` (`Готов`) являются входными данными для `До закрытия заказов` только в объеме положительного `shipment_remaining_qty`; `SHIPPED`, `CANCELLED` и `MERGED` исключаются.
  - `planned_internal_stock_qty = сумма qty_remaining по открытым/незакрытым производственным заказам и черновикам INTERNAL`.
  - `planned_internal_stock_qty` уменьшает только складскую часть потребности.
  - `to_close_orders_qty = raw_to_close_orders_qty`.
  - `to_min_stock_qty = max(0, raw_to_min_stock_qty - planned_internal_stock_qty)`.
  - `total_to_make_qty = to_close_orders_qty + to_min_stock_qty`.
  - Для UI/report дополнительно возвращаются `open_internal_order_qty`, `open_internal_order_refs`, `planned_pallet_qty`, `filled_pallet_qty`, `planned_pallet_count`, `filled_pallet_count`, `remaining_pallet_qty`, `qty_to_create`, `can_create_order`, `reason`.
  - Строка отчета не скрывается, если `qty_to_create = 0`, но по товару есть открытый `INTERNAL` order или открытая palletized production work.
  - Колонка `Всего произвести` показывает информационную сумму `total_to_make_qty`.
  - Кнопка `Сформировать заказ` сначала вызывает `POST /api/reports/production-need/create-orders/preview`; preview не создает заказ и возвращает только строки складской части потребности (`qty_to_create > 0`), которые реально можно создать сейчас.
  - После подтверждения/редактирования клиент вызывает `POST /api/production-needs/create-orders` и передает подтвержденные строки `item_id + qty`.
  - Сервер при `create-orders` заново пересчитывает актуальную потребность, валидирует `qty > 0`, не создает пустой заказ и не позволяет создать количество больше текущего `qty_to_create` по строке.
  - Endpoint создает один черновик `INTERNAL` (`status = DRAFT`, `partner_id = null`) только по складской части потребности; часть `to_close_orders_qty` остается информативной и не попадает в auto-create.
  - WPF и Web не являются источником истины для production quantities и после команды перечитывают серверные отчеты, поэтому одинаково видят результат.
  - `POST /api/production-needs/create-orders` не создает `marking_order`, не создает synthetic `marking_code`, не трогает Excel ЧЗ и не выполняет side effects маркировки.
  - Если `to_min_stock_qty = 0`, `INTERNAL`-черновик не создается даже при наличии `to_close_orders_qty > 0`.
- Основной workflow ЧЗ запускается из карточки заказа через `POST /api/orders/{orderId}/marking/export`; источником расчета являются серверные `orders` и `order_lines`, а не очередь производственной потребности.
  - Для `CUSTOMER` Excel ЧЗ создается только на нехватку маркируемых строк после учета уже покрытого объема: `export_qty = max(0, qty_ordered - shipped_qty - reserved_filled_hu_qty - existing_order_code_qty)`.
  - `reserved_filled_hu_qty` учитывает только строки `order_receipt_plan_lines` с `to_hu`, для которых HU имеет `production_pallets.status = FILLED`; planned/unfilled резерв в маркировку не входит, а наличие ledger balance для этой проверки не обязательно.
  - Для `INTERNAL` Excel ЧЗ создается на объем выпуска внутреннего заказа: `export_qty = max(0, qty_ordered - existing_codes_for_this_internal_order)`.
  - Новый endpoint создает/переиспользует `marking_order` с `source_type = PRODUCTION_ORDER`, `source_order_id = order.id` и временные synthetic `marking_code` через существующий Excel workflow.
  - Повторный export идемпотентен: если коды по этому заказу уже покрывают расчетный объем, новые задачи и коды не создаются.
  - Для заказа в финальном статусе `SHIPPED` (`Выполнен`) Excel ЧЗ не формируется: WPF не дает нажать кнопку, а backend `POST /api/orders/{orderId}/marking/export` возвращает отказ без создания `marking_order` и `marking_code`.
  - Раздел `Маркировка` остается журналом/legacy-view задач. Ручной `POST /api/marking/create-from-production-needs` сохраняется для совместимости, но не является основным способом формирования ЧЗ.
  - Web-список заказов показывает бинарный статус ЧЗ из server-side DTO как icon-only индикатор с подсказкой `Маркировка не проведена` / `Маркировка проведена`.
- На этапе создания/обновления заказа сервер формирует план выпуска `order_receipt_plan_lines` (HU и локации хранения).
- Для `CUSTOMER`-заказа на этапе создания/обновления сервер пытается зарезервировать в `order_receipt_plan_lines` только физические HU с положительным остатком по `ledger` (`LEDGER_STOCK`); `INTERNAL_FILLED` (FILLED без ledger) не допускается.
- После закрытия отдельного `PRD` сервер автоматически пересчитывает эти резервы только для клиентских заказов, попавших в affected scope закрываемого документа (`docs.order_id` или `doc_lines.order_line_id`). Полный пересчет всех клиентских резервов (FIFO по дате создания заказа) остается для явных операций сохранения/maintenance, где он действительно нужен.
- Один и тот же HU/объем не может быть одновременно зарезервирован за несколькими клиентскими заказами.
- Для клиентского заказа используется флаг `bind_reserved_stock`:
  - `true`: сервер на этапе сохранения заказа резервирует свободные HU/локации из текущего HU stock в `order_receipt_plan_lines` только для товаров, тип которых имеет `item_types.enable_order_reservation = true`;
  - `false`: привязка складского остатка к заказу не выполняется.
- В WPF подтверждение привязки складского остатка запрашивается при сохранении клиентского заказа и только если по его позициям есть остаток на складе.
  - Prompt привязки HU идемпотентен: если подходящие HU уже зарезервированы под этот заказ и покрывают его строки, повторный Save/Close и повторное открытие заказа не должны спрашивать снова.
  - После успешной привязки WPF перечитывает заказ и строит следующий prompt только по непокрытой части сверх уже привязанных HU.
- Первичный перенос заказа в WPF `PRODUCTION_RECEIPT` берет только простой незакрытый остаток `receipt_remaining`: одна строка заказа переносится одной строкой выпуска, `HU` остается пустым до явного назначения.
- `receipt-remaining?detailed=1` и HU-ориентированный план используются только как вспомогательный view для подсказок и реального HU-distribution; они не должны создавать в PRD псевдо-строки с пустым `HU`.
- Для `CUSTOMER`-заказа резерв в `order_receipt_plan_lines` считается уже покрытым складским объемом: он уменьшает остаток к PRD, но не превращается в строки выпуска. Если заказ полностью закрыт резервом, PRD по нему не предлагается и не проводится.
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
- активными для резерва считаются только `CUSTOMER`-заказы с `bind_reserved_stock = true`, которые фактически еще не отгружены полностью и не отменены;
- `SHIPPED`/выполненные и `CANCELLED`/отмененные клиентские заказы не получают активный reserve, их исторические customer reservation lines очищаются;
- количество к резерву считается как `qty_ordered - shipped_qty`, чтобы уже отгруженный объем не вычитался повторно из свободного остатка;
- кандидаты для резерва берутся из текущего положительного HU stock; наличие `docs.order_id` у origin PRD не требуется;
- если один HU уже заявлен несколькими активными customer orders, такой HU исключается из нового плана и выводится в отчете как конфликт для ручного разбора.

## Maintenance: backfill статусов ЧЗ

Для production-БД, где уже есть старые выполненные и активные заказы с фактически напечатанными этикетками, статус ЧЗ заполняется только явной maintenance-командой после резервной копии БД. Команда не запускается автоматически при старте приложения.

Команда:
- dry-run по умолчанию: `dotnet FlowStock.Server.dll maintenance backfill-marking-status --created-before YYYY-MM-DD --dry-run`
- явное применение: `dotnet FlowStock.Server.dll maintenance backfill-marking-status --created-before YYYY-MM-DD --apply --confirm APPLY`

Production Docker Compose wrapper:
- dry-run: `bash deploy/scripts/backfill_marking_status.sh --created-before YYYY-MM-DD --dry-run`
- apply: `bash deploy/scripts/backfill_marking_status.sh --created-before YYYY-MM-DD --apply --confirm APPLY`

Правила:
- без `--apply --confirm APPLY` данные не изменяются;
- cutoff `--created-before` обязателен, команда обрабатывает только заказы, созданные раньше этой даты;
- `ledger`, `docs` и `doc_lines` не изменяются;
- `CANCELLED` и pending/ожидающие подтверждения не переводятся в `PRINTED`;
- для старых `IN_PROGRESS`, `ACCEPTED`, `SHIPPED` заказов с маркируемыми строками выставляется `PRINTED`, а пустые `marking_excel_generated_at` и `marking_printed_at` заполняются текущим временем;
- для старых `IN_PROGRESS`, `ACCEPTED`, `SHIPPED` заказов с историческим lifecycle-признаком ЧЗ (legacy `marking_status = EXCEL_GENERATED`, `marking_excel_generated_at` или `marking_printed_at` заполнены) выставляется `PRINTED`, даже если текущий `qty_for_marking` уже стал `0`;
- для таких же заказов без маркируемых строк выставляется `NOT_REQUIRED`;
- существующие `PRINTED` заказы не понижаются.

Отчет команды содержит: всего просмотрено, переведено в `PRINTED`, переведено в `NOT_REQUIRED`, пропущено отмененных, пропущено pending/ожидающих подтверждения, уже `PRINTED`, количество строк `ledger` до/после. При `--apply` команда проверяет, что количество строк `ledger` не изменилось.

## Маркировка ЧЗ из заказа

Новая ЧЗ-модель не использует старый ручной флаг `items.is_marked`. Позиция заказа попадает в расчет только если тип номенклатуры имеет `item_types.enable_marking = true`, а у товара заполнен непустой `items.gtin`.

Основной workflow маркировки теперь order-based: оператор открывает заказ и запускает `Сформировать Excel ЧЗ`, WPF вызывает `POST /api/orders/{orderId}/marking/export`, а сервер сам читает заказ и строки.

Для `CUSTOMER`-заказа расчет идет по маркируемым строкам заказа. `required_qty = qty_ordered`; покрытым считается уже отгруженный объем, резерв готовых HU (`reserved_filled_hu_qty`: FILLED паллета, зарезервированная под CUSTOMER) и уже созданные коды, явно связанные с этим заказом. Excel создается только на нехватку: если заказано `7200`, а `3600` уже покрыто таким резервом, в Excel попадает `3600`. Плановый резерв без FILLED shortage не уменьшает.

Для `INTERNAL`-заказа расчет идет на весь объем выпуска строк внутреннего заказа. Уже созданные коды для этого production order не дублируются, а новые order-based задачи создаются с `source_type = PRODUCTION_ORDER`, `source_order_id = order.id` и `order_id = order.id`.

Раздел `/api/marking/orders` и окно WPF `Маркировка` являются журналом/legacy-view задач маркировки, а не основным местом генерации ЧЗ. Старый `POST /api/marking/create-from-production-needs` сохраняется для совместимости: он может создавать production-based `marking_order`, но основной операторский workflow должен идти из карточки заказа.

Полноценный импорт КМ/DM из Честного Знака пока не реализован. Временный workflow сохраняется: после успешного формирования Excel ЧЗ сервер создает technical/synthetic `marking_code` со значениями `TEMP-CHZ-{marking_order_id}-{NNNNNN}` и статусом, доступным для автопривязки к выпуску, пока количество кодов по задаче не достигнет `requested_quantity`. Повторное формирование Excel не создает дубли, а при частичном наличии кодов создает только недостающее количество.

`status = Printed` у `marking_order` означает сформированный Excel/печать, но не отключает close validation. Для контроля выпуска используются счетчики кодов в `marking_code`: `codes_total`, `codes_free`, `codes_bound`.

Пока `codes_total < requested_quantity`, заказ и задача маркировки должны считаться `Маркировка не проведена` даже если `marking_order` уже существует.

Задача маркировки считается выполненной/обеспеченной, когда `codes_total >= requested_quantity`. В активном списке `Маркировка` такие задачи скрываются, если не включен режим `Показать выполненные`; в режиме выполненных они отображаются как обеспеченные.

При закрытии `INTERNAL PRODUCTION_RECEIPT` маркируемой продукции сервер использует свободные ЧЗ/КМ-коды из `marking_code`, включая временные synthetic-коды до реализации настоящего импорта: товар и GTIN должны совпадать, код не должен быть уже привязан к другой строке выпуска, `PRODUCTION_ORDER` сопоставляется по `source_order_id`, а `PRODUCTION_NEED` без `source_order_id` может покрывать выпуск по `item_id`/GTIN. Недостающие коды автопривязываются к `doc_lines.id` в транзакции закрытия до записи `ledger`; при нехватке кодов закрытие блокируется.

Legacy production-based `marking_order` может иметь `order_id = NULL` и `source_type = PRODUCTION_NEED`; ее нельзя складывать с open `INTERNAL` draft как две независимые потребности, если это один и тот же будущий выпуск. Уже использованные/привязанные коды не гасят новую будущую потребность.

Статусы маркировки заказа:
- `NOT_REQUIRED` = индикатор не показывается
- `REQUIRED` = `Маркировка не проведена`
- `PRINTED` = `Маркировка проведена`

В основном списке заказов WPF и в выборе заказа для `PRODUCTION_RECEIPT` показывается effective-статус ЧЗ. Для `CUSTOMER` он считается по нехватке после отгрузки/резерва; для `INTERNAL` - по наличию достаточных кодов для production order. Наличие самой задачи `marking_order` не должно маскировать отсутствие кодов: до Excel/synthetic codes effective-статус остается `REQUIRED`.
Сохраненный lifecycle-статус `PRINTED` имеет приоритет над текущей вычисленной потребностью: текущий `marking_required = false` не должен превращать такой заказ в `NOT_REQUIRED`. Старое значение БД/API `EXCEL_GENERATED` читается как `PRINTED` и не записывается новыми операциями.

Короткая колонка `Маркировка ЧЗ` в заказах бинарная:
- `REQUIRED` = `Маркировка не проведена` красным
- `PRINTED` = `Маркировка проведена` зеленым

Если в заказе нет маркируемых товаров, индикатор можно не показывать. Статусы `требуется`, `в работе`, `частично`, `не требуется` рядом с заказом не выводятся.

Очередь WPF `Маркировка` по умолчанию показывает задачи маркировки как журнал/legacy-view. Production-based `marking_order` с `order_id = NULL` отображается как обычная задача; для старых order-based строк источник остается клиентским заказом. Новую генерацию Excel ЧЗ оператор запускает из окна заказа.

Legacy-расчет задач маркировки из производственной потребности:
- сервер берет строки `Потребность производства`;
- для маркируемых товаров (`item_types.enable_marking = true` и непустой `items.gtin`) считает полный производимый объем;
- открытые `INTERNAL` drafts/orders участвуют в расчете как server-side представление будущего выпуска, но не должны автоматически удваивать тот же объем относительно production need report;
- уже созданные задачи маркировки с production-source вычитаются из нового объема;
- исторические/использованные `marking_order` без `source_type` не вычитаются, даже если GTIN совпадает.

При order-based export или выборе нескольких задач маркировки сервер формирует Excel-файл и агрегирует строки с одинаковыми GTIN и наименованием. Основной лист Excel не содержит строку заголовков: первая строка является первой строкой данных. Формат строго состоит из трех колонок в фиксированном порядке: `Наименование`, `GTIN`, `Кол-во`.

После успешного формирования файла выбранные задачи `marking_order`, по которым реально были строки ЧЗ, переводятся в `PRINTED`. Для order-based задач и legacy order-candidates дополнительно сохраняется прежнее поведение: заказы, по которым реально были строки ЧЗ, получают `marking_status = PRINTED`, `marking_printed_at` и `marking_excel_generated_at`. Заказы без строк ЧЗ не блокируют формирование по другим выбранным задачам и не переводятся в `PRINTED`. Если строк ЧЗ нет по всем выбранным задачам, файл не создается и данные не мутируют.

Повторное формирование доступно в WPF через `Показать выполненные`, где отображаются ранее обработанные `PRINTED` заказы. Legacy `EXCEL_GENERATED` отображается в этом режиме как `PRINTED`.

Формирование Excel ЧЗ не меняет `ledger`, `docs`, `doc_lines`, не редактирует закрытые документы и не запускает автоматический backfill.

Закрытие `PRODUCTION_RECEIPT` с фактическими строками маркируемой продукции разрешено только при достаточном количестве КМ/DM-кодов по строкам выпуска:
- маркируемость определяется товаром: `item_types.enable_marking = true` и непустой `items.gtin`;
- тип заказа `CUSTOMER`/`INTERNAL`, контрагент, `production_purpose` и наличие `order_line_id` не отменяют требование маркировки;
- внутренний выпуск на склад маркируемого товара требует КМ/DM-коды так же, как выпуск под клиентский заказ;
- если `Потребность производства` создала `INTERNAL`-черновик с маркируемым товаром, связанный выпуск тоже блокируется без достаточных КМ/DM-кодов;
- если в строках нет маркируемых товаров, документ закрывается по прежним правилам;
- проверка выполняется на сервере до записи `ledger` и до изменения статуса документа.

В WPF и PC Web рядом с заказом используется только бинарная подсказка ЧЗ: `Маркировка проведена` или `Маркировка не проведена`. В разделе маркировки production-based задачи с `order_id = NULL` не скрываются и получают тот же бинарный effective-статус. Заказ без маркируемых товаров может не показывать индикатор.

Индексы:
- `orders(order_ref)`
- `orders(partner_id)`
- `order_lines(order_id)`
- `docs(order_id)`
- `production_pallets(prd_doc_id, hu_code)` уникальный для активных паллет
- `production_pallet_lines(production_pallet_id, doc_line_id)` уникальный
- `production_pallets(hu_code)` уникальный для активных, не отмененных паллет
- `production_pallets(order_line_id, status)`
- `ledger(item_id, location_id)` (уже было)

## Подготовка паллетных этикеток

- Существующие активные заказы без `production_pallets` остаются legacy и не появляются в TSD `Наполнение`, пока оператор явно не выполнит подготовку паллет.
- В WPF карточке заказа есть команды: `Сформировать план паллет`, `Удалить план паллет`, `Печать паллетных этикеток`. Ручная операторская команда `Перенести план паллет` не показывается.
- `Сформировать план паллет` вызывает `POST /api/orders/{orderId}/production-pallets/plan`, создает/переиспользует технический open `PRODUCTION_RECEIPT`, дробит строки по `items.max_qty_per_hu`, назначает HU серверной sequence-генерацией и не пишет `ledger`.
- `Удалить план паллет` вызывает `POST /api/orders/{orderId}/production-pallets/cancel-plan` для open `PRODUCTION_RECEIPT` с активными `production_pallets`. Разрешено для паллет в статусах `PLANNED` и `PRINTED`; запрещено при `FILLED`, закрытом PRD или наличии `ledger` по документу. После удаления остается пустой draft PRD; оператор может сформировать план заново по актуальному `qty_ordered`.
- Внутренний сервис/endpoint переноса плана `POST /api/orders/{targetCustomerOrderId}/production-pallets/adopt-from-internal/{sourceInternalOrderId}` не является пользовательским действием в WPF. Он может использоваться только системными, тестовыми или maintenance-сценариями, где явно проверены ограничения переноса.
- Перенос разрешен только из INTERNAL-заказа в CUSTOMER-заказ, если оба заказа не `CANCELLED`/`SHIPPED`, source имеет open draft `PRODUCTION_RECEIPT` с активными `PLANNED`/`PRINTED` паллетами, по source PRD нет `ledger`, нет `FILLED` паллет, PRD не закрыт, а target еще не имеет активного плана паллет. Если у target уже есть план, сервер возвращает `TARGET_ALREADY_HAS_PALLET_PLAN` с требованием сначала удалить текущий план паллет у клиентского заказа.
- При переносе сервер в одной транзакции переносит `doc_lines`, `production_pallets` и `production_pallet_lines` на target PRD, ремапит `order_line_id` по совпадающему `item_id`, сохраняет HU/status/qty/location/timestamps и не меняет `ledger`, закрытые документы, OUTBOUND и `qty_ordered` source/target. Если в target нет строки заказа для любого `item_id` из source плана, сервер возвращает `TARGET_LINE_NOT_FOUND`.
- Для legacy INTERNAL-заказа, у которого вся потребность была перенесена в CUSTOMER и выпуск больше не требуется, сохраняется статус `MERGED` / display `Объединён`. Статус terminal: не участвует в TSD filling, production need и формировании плана паллет. Новый CUSTOMER HU binding не переводит INTERNAL в `MERGED` и не уменьшает `qty_ordered`.
- После adopt pallet plan с INTERNAL на CUSTOMER, merge/redistribute source INTERNAL и других операций, которые опустошают source draft `PRODUCTION_RECEIPT`, сервер безопасно удаляет пустой draft PRD source-заказа, если по документу нет `doc_lines`, активных `production_pallets`, `production_pallet_lines`, `ledger` и документ не `CLOSED`. Закрытые/проведённые PRD и PRD с движениями склада не удаляются. После `Удалить план паллет` пустой draft PRD на заказе сохраняется для повторного `PlanOrder` (reuse empty PRD).
- `Печать паллетных этикеток` сначала читает `GET /api/orders/{orderId}/production-pallets/print-rows`; если плана нет, пользователь получает `Сначала сформируйте план паллет`.
- Перед печатью WPF открывает модальное окно выбора HU, сгруппированных по товару. По умолчанию выбраны только `PLANNED` паллеты; `FILLED`/`PRINTED` показываются, но не выбираются автоматически. Печать и `POST /api/orders/{orderId}/production-pallets/mark-printed` выполняются только для выбранных паллет.
- Печать использует только уже существующие `production_pallets.hu_code`. Она не вызывает ручной генератор HU, не создает новые паллеты, не меняет склад и не пишет `ledger`.
- BarTender `.btw` шаблон получает NamedSubStrings: `HuCode`, `ItemName`, `Qty`, `OrderRef`, `PrdRef`, `Brand`, `Uom`, `PalletNo`, `PalletCount`, `StoragePlace`, `ProductionDate`, `Comment`, `IsMixedPallet`, `Composition`, `Line1ItemName`, `Line1Qty`, `Line2ItemName`, `Line2Qty`, `Line3ItemName`, `Line3Qty`. Для mixed pallet печатается одна строка/этикетка на общий `HuCode`, `ItemName = Микс-паллета`, а состав передается в `Composition`.
- После успешной печати допускается перевод `PLANNED -> PRINTED`; повторная печать разрешена и использует тот же HU. `FILLED` паллеты не переводятся обратно в `PRINTED`.
- TSD `Наполнение` работает только с подготовленными известными HU: unknown HU отклоняется, HU другого заказа отклоняется, mixed pallet показывает состав `lines`, а `FILLED` паллета не пишет `ledger`.
- Закрытие palletized PRD не требует legacy-действия `Распределить по HU`; сервер проверяет активные `production_pallets`, блокирует ненаполненные паллеты и только при успешном close пишет складской `ledger`. Draft PRD с уже начатым `Наполнением` подсвечивается в списке документов.
- На TSD отдельная legacy-кнопка `Выпуск продукции` не показывается. Производственный поток подготовленных паллет выполняется через `Наполнение`; legacy PRD остаются только для совместимости существующих документов/API.

### Редактирование строк заказа с паллетным планом

- При изменении количества строки заказа сервер работает только в области `order_id + order_line_id` и синхронизирует паллетный план строки в той же транзакции сохранения заказа; паллеты других строк не трогает.
- `FILLED`, `CLOSED`, `POSTED`, напечатанные или фактически наполненные HU не удаляются, не отвязываются, не получают новый `order_line_id` и не пересоздаются автоматически при редактировании заказа.
- Если меняется товар, GTIN, SKU или production pallet group строки, это считается заменой строки для паллетного плана. `PLANNED`-часть старой строки можно сбросить, но при наличии `FILLED` или другого фактического состояния изменение блокируется понятным сообщением пользователю.
- Удаление строки разрешено только если по ней нет `FILLED`/фактических HU; `PLANNED`-часть этой строки можно сбросить вместе со строкой. Если есть заполненные паллеты/HU, удаление блокируется сообщением вида `Нельзя удалить строку: по ней уже есть заполненные паллеты/HU. Сначала выполните корректировку/сторно.`
- При уменьшении количества для `INTERNAL` защищенный минимум = `shipped_qty + filled_production_pallet_qty`; `PLANNED`/`PRINTED` без `FILLED` не считаются фактическим выпуском и не блокируют уменьшение. Для `CUSTOMER` дополнительно учитывается `order_receipt_plan_lines.qty_planned`. Нарушение блокируется сообщением вида `Нельзя уменьшить количество ниже уже заполненного/выпущенного объема: заполнено {qty}.`
- При уменьшении `INTERNAL` после сохранения строки сервер оставляет `FILLED` без изменений и отменяет (`CANCELLED`) лишние `PLANNED`/`PRINTED` паллеты строки сверх `planned_allowed_qty = max(0, qty_ordered - filled_qty)`; отмененные HU не участвуют в активном плане, печати и TSD filling.
- При увеличении количества существующие `FILLED`-паллеты сохраняются; `PLANNED`/`PRINTED` паллеты строки не сбрасываются автоматически. После сохранения строки сервер append-only добавляет только `missing_qty = qty_ordered - active_pallet_qty` (включая `FILLED`) для заказа в статусе `IN_PROGRESS`/`Draft`; `POST /api/orders/{orderId}/production-pallets/plan` использует ту же формулу и не выдает ошибку переназначения при наличии `FILLED`.
- После редактирования строки WPF обновляет подсветку HU-покрытия строки; частичная привязка и scoped-сброс `PLANNED`-части являются нормальным состоянием и не показываются как аварийная ошибка.

## Mixed pallet / общий HU

- Смысл `Общий HU` перенесен из PRD в заказ/план паллет. В WPF карточке заказа у каждой строки есть галочка `Общий HU` и номер группы; строки с одинаковым `production_pallet_group` (≥2 строк) планируются на один HU. Для ручной mixed-группы `items.max_qty_per_hu` не проверяется.
- В строках заказа WPF показывает назначенные `HU паллет`, чтобы оператор видел результат планирования. Повторное `Сформировать план паллет` до печати пересобирает нераспечатанный план по текущим галочкам строк и назначает новые HU без создания дублей.
- Mixed pallet = одна запись `production_pallets` с одним `hu_code` и несколько строк `production_pallet_lines`.
- Planning не создает несколько `production_pallets` с одинаковым `hu_code`; повторный planning переиспользует уже подготовленный PRD/план.
- Printing печатает одну этикетку на `HuCode`, а не одну этикетку на товар внутри mixed pallet.
- TSD сканирует один HU, показывает состав и после подтверждения пишет `ledger` по всем component-lines.
- После наполнения (`FILLED`) переназначение/удаление HU запрещено. Наличие `PRINTED` или существующего `PLANNED` плана не блокирует append-only добавление недостающих `PLANNED` HU при увеличении `qty_ordered`; флаг `printed` сам по себе не запрещает дополнение плана. Legacy PRD-флаг `Общий HU` не используется в palletized flow. Для PRD с активными `production_pallets` старые действия `Общий HU`, `Распределить по HU`, `Назначить HU` скрываются или disabled; legacy PRD без `production_pallets` сохраняет старое поведение.

## Типы заказов

- `CUSTOMER`
  - контрагент обязателен
  - после полного выпуска PRD переходит в `ACCEPTED` / UI `Готов`
  - заказ закрывается отгрузками OUTBOUND и после полной отгрузки переходит в `SHIPPED` / UI `Выполнен`
  - участвует в клиентской отгрузке и в `/api/orders`
  - в picker отгрузки попадает, если `shipment_remaining > 0`; отсутствие предыдущего `OUTBOUND` не мешает выбору
  - в TSD picker отгрузки попадают только клиентские `ACCEPTED` (`Готов`) заказы, по которым есть ожидаемые HU, зарезервированные за заказом через `order_receipt_plan_lines` и подтвержденные положительным HU stock из `ledger`
  - TSD при выборе заказа загружает ожидаемые HU через `/api/tsd/outbound/orders/{orderId}`; scan HU вызывает `/api/tsd/outbound/orders/{orderId}/scan`, создает/использует draft `OUTBOUND` и добавляет строки по HU без записи в `ledger`
  - TSD `complete` через `/api/tsd/outbound/orders/{orderId}/complete` только помечает подбор готовым; проведение остается за WPF Close, который пишет `ledger` и обновляет статус заказа
  - проведение `OUTBOUND` по заказу допускает только HU, выпущенные/зарезервированные под этот заказ, и отклоняет HU другого заказа
- `INTERNAL`
  - контрагент не обязателен
  - используется как внутренняя потребность на выпуск продукции
  - закрывается выпусками PRD; после полного выпуска переходит сразу в `SHIPPED` / UI `Выполнен`
  - не участвует в клиентской отгрузке и по умолчанию не отдается в `/api/orders` для TSD/PC web
- `production_purpose` остается техническим полем строки заказа для совместимости API/БД и расчетов потребности:
  - для ручного `CUSTOMER`-заказа все строки нормализуются в `CUSTOMER_ORDER`;
  - для ручного `INTERNAL`-заказа все строки нормализуются в `INTERNAL_STOCK`;
  - UI не позволяет вручную смешивать назначения строк в одном заказе;
  - кнопка формирования заказа из `Потребности производства` создает один `INTERNAL`-черновик без клиента только на `На склад до мин.`;
  - строки такого автосформированного черновика могут оставаться `INTERNAL_STOCK`;
  - повторный запуск опирается на planned-вычитание по открытым черновикам и не создает уже запланированный объем повторно.
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
- `produced_qty` = сумма legacy `doc_lines.qty` по закрытым PRD без `production_pallets` + сумма `production_pallet_lines.planned_qty` по паллетам `status = FILLED`; для закрытого palletized PRD эти источники не суммируются повторно
- `remaining_qty` = max(0, `qty_ordered` - `produced_qty`)
- `available_qty` = текущий остаток ГП по `ledger` для `item_id` (информационно)
- `production_purpose` хранит техническое назначение строки: `CUSTOMER_ORDER` уменьшает текущую потребность до закрытия клиентских заказов, `INTERNAL_STOCK` уменьшает потребность пополнения склада до минимального остатка.
- Историческая строка без `production_purpose` считается `CUSTOMER_ORDER`, если у связанной строки выпуска есть `order_line_id`, иначе `INTERNAL_STOCK`.
- Для отчета `Потребность производства` planned-вычитание работает так:
  - открытые/незакрытые `INTERNAL`-заказы и черновики считаются как planned пополнения склада;
  - planned пополнения склада уменьшает только `На склад до мин.`;
  - `До закрытия заказов` остается потребностью клиентского workflow и не дублируется внутренним заказом;
  - закрытые документы в planned-вычитание не входят, потому что уже отражены через `ledger`.

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
- Для `INTERNAL` план строится только по остатку к выпуску: `max(0, qty_ordered - produced_qty)`; уже выпущенный объем не должен оставаться в `order_receipt_plan_lines` после уменьшения `qty_ordered`.
- TSD не генерирует HU локально: используются только серверные номера HU.

## HU binding CUSTOMER

- Клиентский заказ привязывает HU через явное резервирование, а не через перенос строк INTERNAL → CUSTOMER.
- Новый WPF flow должен показывать кандидаты HU в строке CUSTOMER, позволять выбрать HU чекбоксами и отправлять выбранные HU на сервер при save/apply.
- Сервер при применении заново валидирует, что HU свободен, соответствует `item_id`, не зарезервирован другим активным CUSTOMER-заказом и его source state не изменился.
- Частичное HU-покрытие строки CUSTOMER является нормальным состоянием: сервер применяет доступный выбранный резерв и не возвращает warning только из-за того, что `reserved_qty < shipment_remaining_qty`.
- Контракт WPF/server для CUSTOMER HU binding: кандидаты читаются через `POST /api/orders/hu-reservation-candidates`, применение выполняется через `POST /api/orders/{orderId}/hu-reservations/apply`.
- `LEDGER_STOCK` HU имеет положительный ledger balance, может быть зарезервирован и отгружен.
- `INTERNAL_FILLED` HU имеет `production_pallets.status = FILLED` в открытом INTERNAL PRD, может быть предварительно зарезервирован под CUSTOMER, но OUTBOUND close запрещен до закрытия PRD и появления ledger balance.
- INTERNAL-заказ при HU binding не меняется: `order_lines.qty_ordered`, `production_pallets.order_id/order_line_id`, `doc_lines` и `marking_order` остаются у источника.
- Legacy endpoints `POST /api/orders/redistribute`, `POST /api/orders/{id}/auto-redistribute-from-internal` и `POST /api/orders/{id}/reserve-produced-hu` могут временно существовать для совместимости и тестов, но WPF CUSTOMER flow не должен их вызывать. В WPF не должно быть двух параллельных сценариев: старый «перенести/перераспределить» и новый «привязать HU».

### Что видит оператор в WPF после save/apply HU

Оператор видит summary по новой модели:
- `Зарезервировано` — выбранные HU, успешно закрепленные за CUSTOMER;
- `Не хватило` — остаток строки CUSTOMER без покрытия;
- `Ожидает закрытия PRD` — выбранные `INTERNAL_FILLED` HU, которые пока нельзя отгружать;
- `Пропущено` — HU, снятые оператором или не прошедшие повторную серверную проверку;
- `Ошибки` — конфликт резерва, несовпадение товара, изменение статуса HU/PRD или недостаточный ledger balance.
- В WPF и PC web частичное или отсутствующее HU-покрытие `CUSTOMER`-строки не показывается модальным warning-окном. Строки заказа подсвечиваются: мягкий зеленый фон при полном покрытии остатка к отгрузке HU/reservation, мягкий розовый фон при частичном покрытии или отсутствии HU/reservation при положительном остатке; tooltip/title использует наименование товара и количества, а не internal `order_line_id`.
- Для `INTERNAL`-строки мягкий зеленый фон означает, что фактически произведенный/наполненный объем по строке покрывает заказанное количество (`produced_or_filled_qty >= ordered_qty` и `ordered_qty > 0`). Плановые HU, `PLANNED` паллеты и сам факт существования паллетного плана не считаются завершенным выпуском и не делают строку зеленой.

## Правила статусов

- Ручная установка статуса заказа отключена, кроме отмены заказа из WPF (`CANCELLED`).
- Финальный статус в БД остается `SHIPPED`, в UI для всех типов показывается как `Выполнен`.
- Отмена заказа не удаляет его из истории: сервер ставит `CANCELLED` (UI: `Отменён`), очищает `order_receipt_plan_lines` этого заказа и пересчитывает резервы остальных активных клиентских заказов. `ledger`, документы и строки документов не изменяются.
- Единая UI-схема статусов заказа:
  - `В работе` (оранжевый бейдж)
  - `Готов` (светло-зеленый бейдж)
  - `Выполнен` (темно-зеленый бейдж)
  - `Отменён` (серый бейдж)
- Для `CUSTOMER`:
  - после создания/сохранения заказа статус автоматически `IN_PROGRESS` (UI: `В работе`)
  - `DRAFT` используется только локально в WPF для нового, еще не сохраненного заказа, и в UI нормализуется как `В работе`
  - после частичного выпуска PRD (с привязкой к заказу) статус остается `IN_PROGRESS` (UI: `В работе`)
- статус `ACCEPTED` (UI: `Готов`) выставляется только после полного комплекта по заказу: `produced >= qty_ordered` по всем строкам
  - После успешного закрытия `PRODUCTION_RECEIPT` сервер сразу пересчитывает связанный заказ и переводит его в `ACCEPTED`, если весь объем уже произведен.
  - при частичной отгрузке статус возвращается в `IN_PROGRESS` (UI: `В работе`)
  - `SHIPPED` выставляется автоматически, если по всем позициям `remaining_qty == 0` по OUTBOUND (заказ закрывается сразу после проведения полного объема)
  - `CANCELLED` исключает заказ из производственной потребности, отгрузки и активного резерва
  - `shipped_at` = MAX(`closed_at`) по закрытым OUTBOUND
- Диагностика исторически зависших клиентских статусов:
  - `POST /api/diagnostics/order-status/refresh-fully-shipped` по умолчанию выполняет dry-run и возвращает клиентские заказы, у которых все строки полностью покрыты закрытыми `OUTBOUND`, но persisted status еще не `SHIPPED`;
  - тело `{ "apply": true }` применяет пересчет через серверный `OrderService.RefreshPersistedStatus`, без ручного SQL-update, без изменений `ledger`, `docs` и `doc_lines`;
  - `SHIPPED`, `CANCELLED`, `MERGED` и `DRAFT` не являются кандидатами для repair.
- Диагностика возможной переотгрузки:
  - `GET /api/diagnostics/over-shipped-orders` read-only и возвращает `ok`, `items[]` с `qty_ordered`, `shipped_by_api/read_model`, `shipped_by_closed_outbound`, `shipped_by_ledger`, деталями `outbound_docs[]`, `ledger_entries[]` и `recommendation`;
  - расчет закрытых `OUTBOUND` учитывает только активные положительные `doc_lines`, то есть исключает строки, замененные более новой строкой через `replaces_line_id`;
  - endpoint не выполняет repair, не меняет закрытые документы и не пишет в `ledger`.
- Диагностика производственного плана:
  - `GET /api/diagnostics/production-plan-consistency` read-only и возвращает `ok`, `items[]` с `order_qty`, `open_prd_doc_qty`, `closed_prd_doc_qty`, `prd_doc_qty`, `open_pallet_planned_qty`, `pallet_planned_qty`, `pallet_filled_qty`, `ledger_closed_prd_qty`, `ledger_open_prd_qty`, `ledger_prd_qty`, `severity`, `problem_code`, `recommendation`, `pallets[]`, `prd_docs[]`;
  - endpoint нужен для dry-run после deploy перед ручными repair-решениями и не меняет `orders`, `order_lines`, `docs`, `doc_lines`, `production_pallets`, `production_pallet_lines` и `ledger`;
  - активные `doc_lines` для расчёта qty: `qty > tolerance` и строка не заменена более новой (`replaces_line_id`);
  - `problem_code`: `ORDER_ZERO_BUT_PALLETS_EXIST`, `PALLETS_EXCEED_ORDER_QTY`, `PRD_LINES_EXCEED_ORDER_QTY`, `FILLED_PALLETS_WITH_DRAFT_PRD`, `SHIPPED_CUSTOMER_WITH_OPEN_PRD`, `MERGED_ORDER_WITH_PALLET_PLAN`, `CLOSED_PRD_LEDGER_MISMATCH`;
  - `PALLETS_EXCEED_ORDER_QTY` и `PRD_LINES_EXCEED_ORDER_QTY` считаются только для open PRD/pallet plan и не применяются к `CUSTOMER/SHIPPED`;
  - `CLOSED_PRD_LEDGER_MISMATCH` сравнивает только `closed_prd_doc_qty` и `ledger_closed_prd_qty`, без legacy mix с `pallet_planned_qty`;
  - `SHIPPED_CUSTOMER_WITH_OPEN_PRD`: `WARNING` для legacy stale open PRD без паллет/ledger; `ERROR` только при open pallets/ledger/fill или рассинхроне open PRD vs pallets; не блокирует Close PRD;
  - согласованный `FILLED_PALLETS_WITH_DRAFT_PRD` — `severity=WARNING`, не блокирует Close PRD;
  - закрытие palletized PRD блокируется только при `severity=ERROR` по текущему PRD: `ORDER_ZERO_BUT_PALLETS_EXIST`, `PALLETS_EXCEED_ORDER_QTY`, `PRD_LINES_EXCEED_ORDER_QTY`, `MERGED_ORDER_WITH_PALLET_PLAN`, а также при рассинхроне строк PRD и `production_pallet_lines` на закрываемом документе;
  - `POST /api/diagnostics/production-plan-consistency/repair` — controlled repair (`mode`, `apply=false` по умолчанию); режим `repair-067-072-mustard` для prod-кейса 067/072; при отмене пустых паллет также создаёт tombstone `doc_line` (`qty=0`, `replaces_line_id`) для связанных активных строк PRD, без изменения `ledger`;
  - автоперенос с INTERNAL HARD STOP, если у источника есть DRAFT PRD, PRINTED/FILLED паллеты, ledger по PRD или marking_order; строки заказа не меняются;
  - при merge/redistribution обнуление строки INTERNAL не должно оставлять активный pallet plan на старой строке. Если план уже `FILLED`, PRD закрыт или по PRD есть `ledger`, сервер не правит его молча и требует диагностику/manual repair.
- Для `INTERNAL`:
  - ручной созданный/сохраненный заказ обычно стартует как `IN_PROGRESS` (UI: `В работе`)
  - `INTERNAL`-черновик, созданный из `Потребность производства -> Сформировать заказ`, стартует как server-side `DRAFT`
  - в read-model `DRAFT` внутреннего заказа без фактического полного выпуска отображается как `IN_PROGRESS` / UI `В работе`, если это не отдельный редактируемый черновик UI
  - частично наполненные паллеты или частичный закрытый `PRODUCTION_RECEIPT` оставляют заказ в `IN_PROGRESS` (UI: `В работе`)
  - выпущено полностью или больше -> финальный статус `SHIPPED` / UI `Выполнен`
  - полный закрытый `PRODUCTION_RECEIPT` переводит `DRAFT` сразу в `SHIPPED` / UI `Выполнен`
  - статус и `remaining_qty` пересчитываются как по прямой связи `order_line_id`, так и по `docs.order_id`, если draft/строка PRD была сохранена без `order_line_id`
  - `OUTBOUND` для `INTERNAL` не нужен и не участвует в закрытии такого заказа
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
- PC web загружает список заказов лениво: первая загрузка получает 20 строк, затем кнопка `Загрузить еще` догружает следующую пачку. Запросы идут через server-side pagination `/api/orders?include_internal=1&limit=21&offset=N`; 21-я строка используется только как признак следующей страницы. При поиске список сбрасывается и заново грузит первую страницу.
- Для расчета готовности/паллетных индикаторов по загруженной странице PC web использует batch-read строк `GET /api/orders/lines?ids=1,2,3`, а не последовательные `GET /api/orders/{id}/lines` по каждому заказу. Ответ batch endpoint содержит элементы `{ order_id, lines[] }` в порядке запрошенных id; неизвестный `order_id` возвращается с пустым `lines`. Старый endpoint `GET /api/orders/{id}/lines` остается совместимым для карточки одного заказа и использует тот же серверный mapper строк.
- В paged-режиме `/api/orders` сортирует заказы по операторскому приоритету: `DRAFT`, затем `IN_PROGRESS`, затем `ACCEPTED`, затем `SHIPPED`, затем `CANCELLED`; тип `CUSTOMER`/`INTERNAL` не влияет на этот приоритет и не участвует в сортировке. Внутри одного статуса используется `created_at DESC`, затем `order_ref DESC`. Старый вызов без `limit`/`offset` сохраняет прежнюю форму ответа.
- Для статуса заказа PC web использует canonical `order_status` из `/api/orders` для выбора бейджа, а пользовательский текст берет из `status`/`order_status_display`. `CANCELLED` отображается как серый бейдж `Отменён`, без fallback в `В работе`; финальный `SHIPPED` (`Выполнен`) в списке показывается icon-only с подсказкой, чтобы не перегружать таблицу.
- В списке заказов PC web есть колонка `Наполнение паллет`: для заказов с palletized PRD она показывает прогресс `filled_pallet_count / planned_pallet_count` по всем неотмененным паллетам, включая уже закрытые PRD-документы; если DTO списка временно не содержит агрегат, PC web добирает строки заказа и заполняет колонку по тем же pallet-line метрикам, что используются в раскрытии заказа; подсказка индикатора показывает именно количество паллет, а не штуки товара; при полном наполнении индикатор показывается icon-only с подсказкой, при частичном наполнении текстовый прогресс остается видимым; для клиентского заказа с резервом HU/паллет под заказ подсказка показывает готовность к отгрузке в формате `К отгрузке готово X из Y паллет по заказу`; для заказов с потребностью в плане без созданного плана показывает `План не сформирован`, а для заказов без паллетного workflow не выводит индикатор.
- В списке заказов PC web есть компактный icon-only индикатор `ЧЗ`:
  - красная иконка с подсказкой `Маркировка не проведена`, если effective-статус `REQUIRED`;
  - зеленая иконка с подсказкой `Маркировка проведена`, если effective-статус `PRINTED`.
  - Индикатор использует только `marking_effective_status` и `marking_status_display` из `/api/orders`, не пересчитывая ЧЗ по строкам на frontend.
  - Если effective-статус `NOT_REQUIRED`, индикатор не выводится.
- В строках заказа в WPF и PC web отображаются наименование, SKU/штрихкод и GTIN. В раскрытии заказа PC web строковый прогресс паллетного наполнения берется из серверных `filled_pallet_count/planned_pallet_count` и `pallet_filled_qty/pallet_planned_qty`; полностью наполненные строки могут показывать только зеленую иконку с подсказкой, частично наполненные строки сохраняют видимый текстовый прогресс. Зеленая подсветка строки `INTERNAL` включается только когда фактически произведенный/наполненный объем покрывает заказанное количество; частичное наполнение или один только `PLANNED`-план не подсвечиваются зеленым как готовые.
- Для `CUSTOMER` заказа факт отгрузки имеет приоритет над stale pallet plan: если по строке `qty_shipped + tolerance >= qty_ordered`, строка считается закрытой по отгрузке, индикатор `Наполнено X/Y` не показывается как незавершенная потребность (колонка `Отгружено` уже отражает факт), но в списке заказов и в деталях строки отображается зеленая completed-иконка (`pallet_fill_show_completed_icon` / `show_pallet_completed_icon`). Для полностью отгруженного клиентского заказа `pallet_plan_status` и колонка `Наполнение паллет` не должны понижать статус или показывать незавершенное наполнение из-за оставшихся `production_pallets`; незавершенный PRD/pallet plan при этом не удаляется автоматически и может диагностироваться отдельно.
- В строке товара доступен ввод GTIN/SKU/названия с фильтрацией с первого символа и выбором мышью из выпадающего списка подсказок.
- WPF оператор обрабатывает заявки в едином окне входящих запросов (колокольчик/меню):
  - `APPROVED`: выполняется бизнес-логика `OrderService` (создание заказа или смена статуса), заявка помечается обработанной.
  - `REJECTED`: заявка помечается отклоненной без изменения заказов.
- Для заявок заказа в WPF доступно модальное окно подробностей перед подтверждением.
- Запросы ручной смены статуса (`SET_ORDER_STATUS`) отключены на сервере, кроме WPF-отмены заказа в `CANCELLED`.
