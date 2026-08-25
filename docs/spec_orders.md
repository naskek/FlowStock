# FlowStock: Заказы

## Безопасная корректировка производственного HU

Корректировка наполнения не меняет строки заказа и не перестраивает CUSTOMER reservation:

- любая актуальная `order_receipt_plan_lines` с корректируемым HU блокирует `CORRECT_FILLED` и `RESET_PARTIAL`;
- принадлежность production pallet собственному CUSTOMER-заказу без reservation-row блокером не является;
- replacement в `PLANNED`/`PRINTED` не создаёт reservation, поскольку физический stock существует только в `ledger`;
- persisted status затронутого заказа пересчитывается через `OrderService.RefreshPersistedStatus` внутри correction transaction; этот путь не вызывает `RefreshCustomerReceiptPlansCore`;
- source и replacement образуют revision edge в `production_pallet_filling_adjustments`; predecessor находится строго по `previous.replacement_pallet_id = current.source_pallet_id`, а не по совпадению HU. Повторно наполненный replacement может быть source следующей коррекции, независимые исторические цепочки с тем же HU не объединяются;
- один COR и одна replacement pallet могут принадлежать только одному committed/provisional adjustment; один target DRAFT PRD может содержать несколько replacement pallets и поэтому не является уникальным;
- `RESET_PARTIAL` replacement после предыдущей COR допустим, если текущий DRAFT PRD не имеет ledger, а исторические source/COR movements дают нулевой физический balance по component item/location;
- `CORRECTED` означает историческую, более не operational ревизию паллеты. Она подтверждает существование palletized history, но не считается открытым планом, новым выпуском или shipment-ready stock.

## Модель данных

Таблица `orders`:
- `id` INTEGER PRIMARY KEY
- `order_ref` TEXT NOT NULL
- `order_type` TEXT NOT NULL DEFAULT `CUSTOMER`  // `CUSTOMER` | `INTERNAL`
- `partner_id` INTEGER NULL (FK -> `partners.id`)
- `due_date` TEXT NULL (ISO date)
- `status` TEXT NOT NULL DEFAULT `IN_PROGRESS`  // `DRAFT` | `IN_PROGRESS` | `ACCEPTED` | `SHIPPED` | `CANCELLED` | `MERGED`
  // `CANCELLED` выставляется только WPF-отменой заказа; `MERGED` — terminal статус INTERNAL-заказа, потребность которого перенесена в CUSTOMER (см. «Правила статусов»)
- `comment` TEXT NULL
- `created_at` TEXT NOT NULL
- `bind_reserved_stock` BOOLEAN NOT NULL DEFAULT `FALSE` // legacy/compatibility flag; не запускает фоновую привязку HU
- `marking_status` TEXT NOT NULL DEFAULT `NOT_REQUIRED` // canonical API: `NOT_REQUIRED` | `NOT_APPLIED` | `APPLIED`; legacy `REQUIRED`/`PRINTED` принимаются только при чтении старых данных
- `marking_excel_generated_at` TEXT NULL
- `marking_printed_at` TEXT NULL
- `marking_responsibility` TEXT NOT NULL DEFAULT `FLOWSTOCK` // `FLOWSTOCK` | `CUSTOMER`

`marking_responsibility` задает, кто отвечает за ЧЗ-коды по заказу. `CUSTOMER` допустим только для `CUSTOMER`-заказа и валидируется сервером; DB-level check ограничивает только словарь значений. Существующие заказы мигрируют в `FLOWSTOCK`. Последующие изменения responsibility выполняются через серверный workflow и пишутся в `marking_responsibility_audit` с server-derived actor/device context.

Таблица `order_lines`:
- `id` INTEGER PRIMARY KEY
- `order_id` INTEGER NOT NULL (FK -> `orders.id`)
- `item_id` INTEGER NOT NULL (FK -> `items.id`)
- `qty_ordered` REAL NOT NULL
- `unit_price_gross` NUMERIC(19,4) NULL // цена продажи с НДС за базовую единицу, snapshot
- `vat_rate` NUMERIC(7,4) NULL // числовой snapshot исходящей ставки НДС, без FK на справочник
- `production_pallet_group` TEXT NULL // группа микс-паллеты; строки с одинаковой группой планируются на общий HU
- `cancelled_at` TEXT NULL
- `cancelled_by_actor` TEXT NULL
- `cancelled_by_device_id` TEXT NULL
- `cancel_reason` TEXT NULL
- `revision` BIGINT NOT NULL DEFAULT `0`

Строка заказа может быть отменена логически, если у нее уже есть исторические или runtime-зависимости. Физическое удаление допустимо только когда таких зависимостей нет. Behavior PR, который включает отмену/уменьшение количества, должен исключать логически отмененные строки из marking requirement/export/import, order coverage/status, production need, pallet plan, TSD filling, outbound и отчетов.

## Неактивный товар в заказе

`items.is_active = false` запрещает только новое дополнительное количество заказа. Дополнительным количеством считаются INSERT новой `order_lines` и увеличение `qty_ordered` относительно конкретной существующей canonical-строки. Правило одинаково для `CUSTOMER` и `INTERNAL`; отказ возвращается как HTTP 400 с кодом `ITEM_INACTIVE_FOR_ORDER` и сообщением, что товар выведен из оборота и недоступен для нового количества заказа.

Историческая строка неактивного товара остаётся читаемой. Сохранение того же количества, уменьшение, удаление, допустимое изменение цены/metadata, привязка готовой HU и перенос уже существующего производственного покрытия не считаются новым количеством и остаются разрешены при прохождении остальных workflow guards. Реактивация товара снова разрешает INSERT и увеличение.

При наличии `order_line_id` именно эта строка является canonical existing line для проверки количества и всей mutation-фазы. Сначала сервер проверяет принадлежность ID текущему заказу и совпадение `item_id`/`production_purpose`; подмена товара возвращает `ORDER_LINE_NOT_FOUND` до activity validation. Fallback по `(item_id, production_purpose)` применяется только для legacy payload без `order_line_id`. При legacy duplicates explicit-selected строка сохраняет свой ID и получает изменения qty/цены/pallet-group, а остальные duplicates удаляются по обычным cleanup-правилам; любой blocker откатывает update целиком.

Сервер является окончательным источником решения. До первой мутации он нормализует и сортирует затронутые item IDs, в PostgreSQL удерживает для них transactional row lock и затем отдельно перечитывает товар для проверки существования и `IsActive`. Semantic validation обязательна для любого `IDataStore` и не зависит от наличия PostgreSQL locking. Activity validation, stale-selection validation, коммерческие проверки и запись header/lines выполняются в одной транзакции; отдельный commit между preview/validation и canonical create запрещён.

## Коммерческие условия CUSTOMER-строк

Для `CUSTOMER` партнёр обязателен до добавления строки. При наличии активных строк смена партнёра запрещена (`ORDER_PARTNER_CHANGE_REQUIRES_EMPTY_ORDER`), существующие строки автоматически не переоцениваются. Смена типа `CUSTOMER`/`INTERNAL` также разрешена только для заказа без активных строк (`ORDER_TYPE_CHANGE_REQUIRES_EMPTY_ORDER`).

При настоящем INSERT новой `CUSTOMER`-строки сервер в транзакции:

1. проверяет партнёра роли `Client`/`Both` и товар;
2. требует `items.default_sale_vat_rate_id`;
3. проверяет существование и активность ставки;
4. копирует `vat_rates.rate` в `order_lines.vat_rate`;
5. если write-контракт явно передал intent ручного override — валидирует и использует ручную цену; иначе автоматически выбирает активную `partner_item_sale_prices`, а при её отсутствии — `items.default_sale_price_gross`;
6. сохраняет `order_lines.unit_price_gross`.

Стабильные ошибки: `ITEM_SALE_VAT_RATE_REQUIRED`, `VAT_RATE_INACTIVE_FOR_NEW_ORDER_LINE`, `ITEM_SALE_VAT_RATE_REFERENCE_INVALID`, `ITEM_SALE_PRICE_REQUIRED`. Fallback VAT, выбор первой активной ставки и перенос отображаемого preview в write-команду запрещены. Любая ошибка любой строки откатывает заказ, строки, существующие изменения, резервы, планы и производные записи.

Write-контракт строки принимает `order_line_id`, `item_id`, `qty_ordered` и опциональные `change_unit_price_gross=true` + `unit_price_gross`. Числовой VAT и `vat_rate_id` write-контракт не принимает. Для `INTERNAL` коммерческие intent-поля запрещены, а snapshots равны `NULL`.

Manual override является неотрицательным `decimal` с максимум четырьмя дробными знаками; значение `0` допустимо. Override не изменяет базовую или индивидуальную цену. При отсутствии intent переданное числовое значение отклоняется `UNIT_PRICE_OVERRIDE_INTENT_REQUIRED`.

Это не общая цепочка `manual -> partner -> default`: manual price участвует только при явном intent. PC Web не выбирает источник цены и не переносит `CommercialTermsResolver` в JavaScript; browser отправляет только допустимый write intent, а окончательное решение остаётся серверным. Правила и реализация `CommercialTermsResolver` этим каталогом не изменяются.

Update сначала сопоставляет строку по `order_line_id`; legacy fallback `(item_id, production_purpose)` предназначен только для старых клиентов. Matched-строка сохраняет цену и VAT, quantity-only не читает текущие master-данные. Только новый INSERT запускает resolver.

Matched legacy `CUSTOMER`-строка с `unit_price_gross = NULL` может быть сохранена при quantity-only update: WPF не требует автоматическую цену и без отдельного checkbox не отправляет price intent, а сервер сохраняет `NULL`-цену и текущий `vat_rate`. Для настоящего INSERT новой строки цена по-прежнему обязательна через server resolver или явный manual override.

VAT после создания строки не редактируется. Цена существующей строки изменяется только при явном intent. Update заказа сначала внутри транзакции блокирует строку `orders`, затем повторно читает заказ и `order_lines` и только после этого проверяет partner/type/shipment. Закрытие `OUTBOUND` вычисляет отсортированное объединение `docs.order_id` и order IDs всех существующих `doc_lines.order_line_id`, блокирует все эти `orders`, затем повторно читает документ и строки. Появление новой незаблокированной связи отклоняется `OUTBOUND_ORDER_LINKS_CHANGED_DURING_CLOSE`. Line-linked `OUTBOUND` без header `order_id` отклоняется `OUTBOUND_ORDER_HEADER_REQUIRED_FOR_LINE_LINK`; при заданном header все line-links после допустимого legacy remap должны относиться к нему, иначе возвращается `OUTBOUND_ORDER_LINE_ORDER_MISMATCH`. Только прошедший эти guards документ может быть проведён и создать commercial shipment. После первой активной положительной строки закрытого `OUTBOUND` изменение цены запрещено `ORDER_LINE_PRICE_LOCKED_BY_SHIPMENT`; повторная передача текущего decimal-значения — no-op. Если update цены зафиксирован первым, последующее закрытие использует новый snapshot. Канонический predicate `HasCommercialShipment`/Sales учитывает `OUTBOUND + CLOSED + qty > tolerance + order_line_id` и исключает superseded `doc_lines` через `NOT EXISTS newer.replaces_line_id`.

Read-контракт строки возвращает `unit_price_gross`, `vat_rate`, `commercial_terms_locked`. WPF показывает VAT read-only, разрешает ручную цену только явным checkbox и после сохранения перечитывает canonical строку с сервера. Preview передаёт `issue_code`: разрешены только отсутствие кода и `ITEM_SALE_PRICE_REQUIRED` с manual override; любой другой непустой код fail-closed блокирует добавление строки до локальной коллекции. Серверный resolver повторяет проверку при сохранении.

Существующие исторические `NULL` не backfill-ятся текущими данными товара или справочников. Денежные Orders/Sales используют исключительно snapshots строки заказа; `doc_lines` финансовых колонок не имеет.

Таблица `order_receipt_plan_lines`:
- `id` INTEGER PRIMARY KEY
- `order_id` INTEGER NOT NULL (FK -> `orders.id`)
- `order_line_id` INTEGER NOT NULL (FK -> `order_lines.id`)
- `item_id` INTEGER NOT NULL (FK -> `items.id`)
- `qty_planned` REAL NOT NULL
- `to_location_id` INTEGER NULL (FK -> `locations.id`)
- `to_hu` TEXT NULL
- `sort_order` INTEGER NOT NULL

Таблицы контроля готовых заказов:
- `order_control_tasks(id, task_ref, status, created_at, created_by, started_at, completed_at, cancelled_at, expected_hu_count, checked_hu_count, discrepancy_hu_count, snapshot_hash, ...)`
- `order_control_task_orders(task_id, order_id, order_ref, partner_name, is_active)`; один `order_id` может быть только в одном активном контроле.
- `order_control_task_hus(task_id, hu_code, normalized_hu, status, qty, item_summary, snapshot_hash, checked_at, checked_by_device_id, error_code, error_message)`; прогресс считается по `DISTINCT normalized_hu`.
- `order_control_task_hu_lines(task_hu_id, task_id, order_id, order_line_id, item_id, qty, location_id, source_type)`; mixed-HU хранит один физический HU и несколько строк состава.
- `order_control_events(task_id, task_hu_id, event_type, event_at, device_id, operator_id, hu_code, request_id, payload_json, error_code, message)`; `request_id` обеспечивает идемпотентность scan.

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
- `cancel_reason` TEXT NULL
- `cancelled_at` TEXT NULL
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

Связь с выпуском продукции:
- PRD (`docs.type = PRODUCTION_RECEIPT`) также может быть связан с заказом через `order_id` / `order_ref`.
- В `doc_lines.order_line_id` хранится связь строки выпуска с позицией заказа.
- Для `INTERNAL`-заказа PRD является документом исполнения заказа.
- В TSD filling model один PRD может содержать несколько плановых паллет: `doc_lines.to_hu` хранит технический код паллеты/HU, `doc_lines.qty` - плановое количество, а факт наполнения хранится в `production_pallets`.
- В normal operator workflow production HU либо ещё не наполнена, либо наполнена целиком. `POST /api/tsd/production/fill-pallet` атомарно фиксирует весь single/mixed composition, переводит pallet в `FILLED`, закрывает dedicated PRD и пишет `ledger`; при любой ошибке откатываются component fill, pallet status, документ и ledger. Partial component progress может существовать только как историческая/fixture/maintenance anomaly в `production_pallet_lines.filled_qty`/`filled_at`, не является operator state и классифицируется `INCONSISTENT`.
- Существующие заказы и legacy PRD без `production_pallets` остаются legacy: они не появляются в TSD `Наполнение` и не конвертируются автоматически. План паллет создается только явным действием `POST /api/orders/{orderId}/production-pallets/plan`.
- При создании плана по заказу сервер дробит обычные строки по `items.max_qty_per_hu`, а строки с одинаковым `production_pallet_group` (≥2 строк в группе) планирует как mixed pallet: один `production_pallets.hu_code` и несколько `production_pallet_lines`. Для ручной mixed-группы `max_qty_per_hu` не проверяется: оператор сам задает состав HU. Для обычных строк без группы, если capacity не задана, план не создается и возвращается ошибка `Не задано количество на паллете для номенклатуры`. Последняя одиночная паллета может быть неполной.
- Автоматическая синхронизация плана после сохранения WPF/API является trim-only: она может безопасно отменить surplus open planned pallets при уменьшении количества или при замене части плана готовыми warehouse HU, но не создает PRD, не добавляет недостающие паллеты и не восстанавливает план после отвязки HU.
- Для `INTERNAL`-заказа явное повторное планирование через `POST /api/orders/{orderId}/production-pallets/plan` идемпотентно и добавляет только нехватку по строке: `max(0, qty_ordered - active_production_pallet_qty)`. `active_production_pallet_qty` считается по всем PRD заказа, включая `FILLED` паллеты, перенесенные в dedicated PRD через TSD auto-close; статусы `PLANNED`, `PRINTED`, `FILLED` считаются покрытием, `CANCELLED` не считается. Если у паллеты есть `production_pallet_lines`, покрытие строки считается только по `production_pallet_lines.order_line_id/planned_qty`, без fallback на `production_pallets.order_line_id/planned_qty`.
- При увеличении количества строки уже palletized `INTERNAL`-заказа обычное сохранение заказа сохраняет существующие production HU, но не добавляет недостающую дельту автоматически; оператор должен выполнить явное планирование. Свободные складские HU/ledger stock не используются для будущего производства.
- Для `CUSTOMER`-заказа явное планирование паллет на выпуск создает паллеты только на нехватку по строке: `max(0, qty_ordered - qty_shipped - customer_bound_hu_qty - active_production_pallet_qty)`.
  - `customer_bound_hu_qty` берется из текущего резерва складских HU в `order_receipt_plan_lines` / `CustomerOutboundBoundHuService` и уменьшает только объем к планированию производства.
  - `active_production_pallet_qty` включает `PLANNED`, `PRINTED` и `FILLED` паллеты по `order_line_id`; `CANCELLED` паллеты не считаются покрытием. Для mixed/component pallet покрытие строки считается по `production_pallet_lines.order_line_id/planned_qty`.
  - Уже привязанные складские HU остаются outbound candidates клиентского заказа, не перезаписываются, не отменяются и не превращаются в производственные паллеты повторным планированием.
- `POST /api/orders/{orderId}/production-pallets/plan` для `CUSTOMER` поддерживает режимы:
  - пустое тело, `{}`, `null`, отсутствующий `mode` или `mode = "full"` сохраняют обычное полное планирование на текущую серверную нехватку;
  - `mode = "skip_internal_supply"` сохраняет поведение планирования только позиций без предупреждения о ожидаемом `INTERNAL`-выпуске;
  - `mode = "adopt_internal_then_plan"` переносит eligible planned production HU из открытых `INTERNAL`-заказов в CUSTOMER plan, уменьшает expected output исходных `INTERNAL`-строк в той же транзакции и затем планирует новые паллеты только на остаток CUSTOMER-нехватки;
  - `mode = "apply_selected_coverage_then_plan"` применяет выбранное оператором покрытие из unified preview: складские `FILLED`/ledger-backed HU и selected INTERNAL planned HU, затем планирует только остаток. Request принимает `selected_warehouse_hus[]` (`hu_code`, `item_id`, `target_order_line_id`), `selected_internal_production_pallet_ids[]` и `plan_remainder=true`; клиентские qty/status/source не являются authoritative.
- `GET /api/orders/{orderId}/production-pallets/pre-plan-coverage-preview` является advisory preview: он показывает предупреждение о ожидаемом `INTERNAL`-выпуске, per-HU складских кандидатов (`warehouse_hu_candidates`) и per-HU INTERNAL planned candidates (`internal_planned_hu_candidates`), а также legacy projected adoption поля (`adoptable_internal_planned_hus`, `adoption_skipped_candidates`, projected counts/qty), но не изменяет данные. Для unified preview сервер рекомендует `selected_by_default` сначала по свободным warehouse HU в пределах нехватки, затем по INTERNAL planned HU на оставшийся shortage.
- Unified preview показывает warehouse/Internal candidates только для текущей production shortage. Если CUSTOMER shortage уже полностью закрыт существующими active `PLANNED`/`PRINTED` pallets, preview не предлагает replacement складскими HU; такой replacement не является обещанным UI-сценарием v1. Writer всё равно сохраняет strict safety: если selected warehouse HU создает surplus, он может отменить только safe whole `PLANNED` pallets через текущие helpers, иначе откатывает всю команду.
- Writer-команда `/plan` всегда повторно пересчитывает кандидатов в одной server-side transaction. Для selected coverage она locks target CUSTOMER и snapshot открытых `INTERNAL` source order ids в порядке `order_id ASC`, затем финальную INTERNAL projection ограничивает только этим locked source set. Если после snapshot появился новый source `INTERNAL`, он не переносится в этом запуске и попадёт в следующий preview/run.
- Для `apply_selected_coverage_then_plan` rollback semantics строгие: если любая selected warehouse HU или selected INTERNAL planned HU стала stale/ineligible, не помещается в recomputed shortage или требует unsafe cancellation, команда возвращает structured stale/validation error для refresh preview и откатывает всё: `order_receipt_plan_lines` не меняются, INTERNAL HU не переносится, source `qty_ordered` не уменьшается, depleted-line cleanup не выполняется, новые CUSTOMER pallets не создаются.
- Складская часть `apply_selected_coverage_then_plan` использует ту же ready-HU binding семантику, но как lower-level helper внутри общей transaction, а не отдельный вызов `apply-final`: HU должна быть физической `LEDGER_STOCK` по `ledger`, не зарезервированной другим активным CUSTOMER order, совместимой с item/order line, и итоговый bound set должен пройти qty-limit. Если selected warehouse HU создает surplus будущего CUSTOMER production plan, отменяются только existing safe whole `PLANNED` pallets через текущие helpers; `FILLED`, ledger-backed stock, closed PRD/docs, partial progress и unsafe `PRINTED` не изменяются.
- Идентификаторы выбора: для INTERNAL planned HU используется `production_pallet_id`; для warehouse HU используется normalized `hu_code` в контексте `item_id + target_order_line_id`, как в ReadyHuBinding. В обоих случаях WPF передает только operator preference, сервер пересчитывает факты и итоговые количества.
- UI-путь покрытия для `CUSTOMER` перед планированием — окно `PrePlanCoverageDialog` из карточки заказа (кнопка «План паллет (производство)») + режим `apply_selected_coverage_then_plan`. Это сценарий выбора покрытия перед production planning, а не замена ручных detach/replace. Основная WPF-точка редких ручных операций bind/detach/move/replace warehouse HU reservations — `Заказы → Управление HU`; она сохраняет через `manage/apply-final`. Inline picker из карточки заказа удалён. Серверные ready-HU helpers (`OrderHuBindingApplyFinalService`, `HuBindingApplyShared`, read-model `/api/orders/hu-bindings/ready`) и compatibility endpoints сохраняются.
- В `adopt_internal_then_plan` eligible source HU:
  - source order имеет тип `INTERNAL` и статус `DRAFT` или `IN_PROGRESS`;
  - source PRD открыт (`DRAFT`), не закрыт и не имеет `ledger` rows;
  - production pallet имеет статус `PLANNED` или пустой `PRINTED`, без `filled_at`, без component `filled_qty`/partial progress;
  - количество HU целиком помещается в текущий CUSTOMER shortage; HU не дробится;
  - mixed/common HU переносится атомарно только если все component-lines совместимы с CUSTOMER mixed group, иначе skipped.
- `PRINTED` в `adopt_internal_then_plan` означает уже напечатанный ярлык для пустой planned HU. При переносе такой HU сервер сохраняет `PRINTED` и `printed_at`, не возвращает его в `reprint_required_hus`: это перенос существующей planned production HU в CUSTOMER plan, а не создание новой HU. `PLANNED` при переносе остаётся `PLANNED`.
- Перенос planned HU является reassignment производственного плана, а не складским движением: `ledger` не пишется, физическая warehouse HU binding в `order_receipt_plan_lines` не создается, скрытого auto-bind после release нет. Мутация ограничена переносом `production_pallets`, `production_pallet_lines` и связанных `doc_lines` на CUSTOMER PRD/order lines.
- После successful adoption исходный `INTERNAL` expected output уменьшается в той же транзакции: для обычной HU уменьшается `order_lines.qty_ordered` source line на transferred qty, для mixed HU уменьшается каждая source component line на transferred component qty. Перед уменьшением сервер использует те же remaining/coverage helpers, что `OrderReceiptRemainingCalculator` и `INTERNAL PlanOrder`: new `qty_ordered` не может стать меньше уже произведенного/confirmed qty плюс активного неперенесенного `INTERNAL` production coverage. Если source `INTERNAL` строка после уменьшения стала `qty_ordered = 0` и по ней нет produced/confirmed qty, `ledger`, активных production pallets/components, оставшихся `doc_lines` или других обнаруживаемых ссылок, сервер очищает stale `INTERNAL order_receipt_plan_lines` этой depleted строки и удаляет строку в той же транзакции до `TryMarkAsMerged`; при любом реальном blocker/history строка остаётся с нулевым количеством. Поэтому повторный `INTERNAL PlanOrder` не должен воссоздать перенесенную HU; предупреждение CUSTOMER preview и production need видят только оставшийся `INTERNAL` expected output.

## CUSTOMER: смешанное покрытие строки (warehouse + production)

`CUSTOMER`-заказ может иметь **смешанное** покрытие по одной и той же строке:

- часть количества закрыта **складскими** HU (`order_receipt_plan_lines`, warehouse-bound);
- часть — **planned production pallets** на оставшуюся нехватку.

Правила:

- warehouse-bound HU **не превращаются** в `production_pallets` при планировании;
- `POST /api/orders/{orderId}/production-pallets/plan` создаёт паллеты **только на нехватку** после учёта отгрузки, резерва и уже активных production pallets;
- повторное планирование **не снимает** уже привязанные складские HU;
- одна `order_line` может одновременно иметь складской reserve и production pallet plan.

См. также normal TSD flows в [`spec.md`](spec.md).

## CUSTOMER: контроль готовых заказов

- Контроль создается только для `CUSTOMER`-заказов в `ACCEPTED` / «Готов». `DRAFT`, `IN_PROGRESS`, `SHIPPED`, `CANCELLED`, `MERGED` и `INTERNAL` недопустимы.
- Создание атомарное: preview показывает причины, но create повторяет все проверки в транзакции и не исключает отдельные заказы молча.
- Ожидаемые HU берутся из того же outbound-ready read-model, что TSD outbound: `CustomerOutboundBoundHuService.GetUnshippedOutboundHuLines(...)` / SQL-эквивалент списка outbound. Обязательна семантика одной физической HU: `DISTINCT normalized_hu`.
- Snapshot HU фиксируется при создании задания. Изменение набора HU, состава mixed-HU, ledger-остатка, привязки или outbound-состояния после создания дает blocking discrepancy; продолжение возможно только через отмену задания и создание нового.
- Scan с `request_id` идемпотентен и при retry возвращает исходный результат; тот же `request_id` с другой HU возвращает `IDEMPOTENCY_CONFLICT`. Complete перед `COMPLETED` повторно строит текущий snapshot, игнорируя только собственный active-control task.
- Операции одного задания (`start`, `scan`, `complete`, `cancel`) сериализуются row lock на `order_control_tasks`; для scan порядок блокировок фиксирован: task, затем `order_control_task_hus`, затем progress/events.
- Cross-workflow invariant с outbound защищается row lock заказов в порядке `order_id ASC`: create control, создание order-bound `OUTBOUND`, TSD outbound scan и Close order-bound `OUTBOUND` после lock повторно проверяют конфликтующее состояние.
- Генерация `CTRL-YYYY-NNNNNN` выполняется под transaction advisory lock на год, чтобы параллельные create не получали один `task_ref`.
- Terminal semantics: повторный `complete` для `COMPLETED` возвращает текущее завершенное состояние, повторный `cancel` для `CANCELLED` не создает новое событие, `COMPLETED` нельзя отменить, `CANCELLED` нельзя сканировать или завершать.
- Контроль не изменяет `orders.status`, `ledger`, `docs`, `doc_lines`, reserve/read-model и production pallet plan.
- Активный контроль блокирует outbound по всему `order_id`: TSD scan, TSD complete, создание order-bound draft `OUTBOUND`, закрытие order-bound `OUTBOUND` через общий Close.
- Начатая draft/TSD-отгрузка блокирует создание контроля.

- В TSD режиме `Наполнение` оператор выбирает заказ с уже подготовленными паллетами, а не PRD. После выбора сервер возвращает существующий контекст наполнения (`order_id` + `prd_doc_id`) и не создает HU/план паллет на стороне TSD.
- Filling context аддитивно содержит `order_hu_presentation`, построенный одним вызовом order-wide canonical HU read-model: раздельные `production_tasks`/`operational_hus`, уникальные normalized HU, canonical state/label, composition, contextual progress, diagnostics, `ready_hu_count`, `total_hu_count` и server-owned `filling_eligible`. Клиент только группирует и оформляет готовые данные; raw pallet status не является operator-facing fallback и eligibility на клиенте не вычисляется.
- Contextual `filling_eligible` не является lifecycle state. Он требует текущий order/PRD/active target, открытый `PRODUCTION_RECEIPT`, включённый `ProductionAutoCloseOnFill`, допустимый persisted pallet status, `ProductionPallet.CanFill`, полный однозначный composition с актуальными order/doc lines и прохождение ownership/location/quantity guards. Значение `true` выдаётся только для HU, которая одновременно присутствует в canonical production tasks без противоречий; fill после locks/reload повторно выполняет те же guards и остаётся окончательным решением.
- Экран заказа показывает каждую physical HU ровно один раз и `Готово N / M паллет`. `N` — уникальные `ON_STOCK`, `RESERVED`, `AWAITING_SHIPMENT`, `SHIPPED`; `M` дополнительно включает `AWAITING_FILL` и `INCONSISTENT`. Mixed HU всегда считается одной паллетой. Ready/shipped HU не вызывают fill scan API и дают inline canonical message; `INCONSISTENT` имеет отдельный янтарный warning style/иконку, блокирует mutation и сохраняет diagnostics. HU вне текущего snapshot отправляется в server scan, чтобы клиент не классифицировал stale/foreign HU самостоятельно.
- Picker `/filling` содержит заказ только пока у него есть хотя бы одна server-owned actionable HU. Ready-only и partial/inconsistent-only заказы не показываются; terminal UI после последнего fill не создаёт отдельного механизма возврата заказа в picker.
- При обязательном `ProductionAutoCloseOnFill=true` TSD `POST /api/tsd/production/fill-pallet` изолирует single/mixed pallet в отдельный PRD, заполняет весь composition, переводит pallet в `FILLED`, закрывает PRD и пишет `ledger` (+qty) одной серверной транзакцией. Для mixed pallet ledger пишется по всем компонентам `production_pallet_lines`/`doc_lines`. Ошибка Close/ledger откатывает всю whole-HU mutation; повторный полностью проведённый fill идемпотентен (`already_filled`). При выключенном auto-close команда возвращает `PRODUCTION_AUTO_CLOSE_REQUIRED` до записи.
- Compatibility `POST /api/tsd/production/fill-mixed-pallet-components` принимает только точный полный набор всех component line ids без прежнего partial progress и делегирует whole-pallet command. Subset отклоняется как `PARTIAL_COMPONENT_FILL_NOT_ALLOWED`, найденный partial progress — как `PALLET_PARTIAL_FILL_INCONSISTENT`, без persisted изменений.
- Persisted partial mixed HU — anomaly, не участвующая в OUT, ready-HU binding, produced stock, CUSTOMER reserve или release-produced-stock. Она имеет canonical presentation `INCONSISTENT` и исправляется только controlled correction/maintenance; normal scan/fill не продолжает её component-by-component.
- После server-confirmed последнего действительно actionable whole-HU fill существующий filling completion UI становится terminal screen с текстом `Заказ полностью собран`: scanner и preferred target выключены, popup/modal/alert, timeout и автоматическая навигация отсутствуют, единственная большая кнопка `OK` ведёт на `/filling`.
- Если у PRD есть активные `production_pallets`, закрытие разрешено только когда все они `FILLED`; иначе сервер возвращает ошибку `Нельзя закрыть выпуск: есть ненаполненные паллеты`.
- Для HU фиксируются два независимых смысла связи с заказами:
  - `origin/internal order`: происхождение HU по закрытому `PRODUCTION_RECEIPT` внутреннего заказа (`docs.order_id` + `docs.type=PRODUCTION_RECEIPT` + `doc_lines.to_hu`).
  - `reserved/customer order`: текущий owner HU под клиентский заказ. Источники owner в порядке приоритета:
    - `FILLED production_pallets` активного `CUSTOMER`-заказа;
    - reserve/read-model в `order_receipt_plan_lines`;
    - fallback для legacy/compatibility: закрытый `PRODUCTION_RECEIPT` клиентского заказа.
- Если один и тот же HU одновременно встречается и в `FILLED production_pallets`, и в `order_receipt_plan_lines` другого активного клиентского заказа, owner определяется по `production_pallets`; такой конфликт считается диагностикой и HU не может использоваться как свободный складской остаток.
- `origin/internal order` является справочной историей происхождения HU и не является обязательным условием для клиентского резерва. Если закрытый `PRODUCTION_RECEIPT` имеет `docs.order_id = NULL`, HU все равно может быть зарезервирован как свободный складской HU; в read-model `origin_internal_order_ref` остается пустым.
- Резерв в `order_receipt_plan_lines` не изменяет `ledger` и не меняет физический остаток, а только read-model привязки.
- Для типов номенклатуры с флагом `item_types.min_stock_uses_order_binding = true` этот резерв дополнительно участвует в контроле минимального остатка как `reserved_customer_order_qty` (только по активным клиентским заказам, `status NOT IN (SHIPPED, CANCELLED)`).
- Если клиент отказался от уже выпущенных под его строку FILLED HU, обычное редактирование/PUT продолжает блокировать удаление строки (`ORDER_LINE_HAS_FILLED_PALLETS`). Отдельный explicit endpoint `POST /api/orders/{orderId}/lines/{orderLineId}/release-produced-stock` освобождает такие HU без изменения `ledger`: все target pallets должны быть `FILLED`, по строке не должно быть closed outbound shipped qty, mixed/shared pallet с другим `order_line_id` возвращает `MIXED_PALLET_RELEASE_NOT_SUPPORTED`, а удаление последней строки активного CUSTOMER-заказа возвращает `ORDER_RELEASE_LAST_LINE_FORBIDDEN`.
- `release-produced-stock` имеет отдельную command policy: recovery для полного однозначного `FILLED` без operational ledger остаётся допустимым и не требует fake stock. При существующем положительном ledger его полный item/qty/location composition должен совпадать с production composition; remainder после CLOSED OUTBOUND, multiple locations, foreign reservation/DRAFT и ambiguous correction history отклоняются (`HU_RELEASE_UNSAFE`) до mutations.
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
  - Неактивный товар остаётся диагностической строкой отчёта, но исключается из preview создания `INTERNAL`-черновика. Явно подтверждённая stale selection такого товара отклоняется `ITEM_INACTIVE_FOR_ORDER`, а не маскируется общей ошибкой пустого набора.
  - После подтверждения/редактирования клиент вызывает `POST /api/production-needs/create-orders` и передает подтвержденные строки `item_id + qty`.
  - Сервер при `create-orders` в одной транзакции заново пересчитывает актуальную потребность, валидирует stale selection, блокирует и повторно проверяет активность товара, валидирует `qty > 0`, не создает пустой заказ и не позволяет создать количество больше текущего `qty_to_create` по строке; отдельного commit между этими проверками и записью заказа нет.
  - Endpoint создает один черновик `INTERNAL` (`status = DRAFT`, `partner_id = null`) только по складской части потребности; часть `to_close_orders_qty` остается информативной и не попадает в auto-create.
  - WPF и Web не являются источником истины для production quantities и после команды перечитывают серверные отчеты, поэтому одинаково видят результат.
  - `POST /api/production-needs/create-orders` не создает `marking_order`, не создает synthetic `marking_code`, не трогает Excel ЧЗ и не выполняет side effects маркировки.
  - Если `to_min_stock_qty = 0`, `INTERNAL`-черновик не создается даже при наличии `to_close_orders_qty > 0`.
- Основной workflow ЧЗ запускается из **карточки заказа**:
  - `GET /api/orders/{orderId}/marking/preview`
  - затем `POST /api/orders/{orderId}/marking/export`
- Кнопка `Сформировать Excel ЧЗ` в WPF **не** должна вызывать legacy/global `POST /api/marking/export` из окна `Маркировка`.
- Источник расчёта — серверные `orders` и `order_lines`, а не очередь производственной потребности и не клиентский пересчёт qty.
- Складской HU покрывает marking quantity только при положительном `ledger`, текущей привязке и valid `marking_ready_hu_fact`; один `ledger` без aggregate fact маркировку не доказывает.
- Excel для `CUSTOMER` создаётся только на полностью спланированный `remaining_to_produce`; outstanding immutable scope не дублируется.
  - `GET /api/orders/{orderId}/marking/preview` использует ту же логику расчета, что и export, но не создает `marking_order`, `marking_code`, XLSX и не привязывает коды. В preview попадают строки, где `export_qty + existing_code_qty > 0`; поле `qty` строки = `export_qty + existing_code_qty` (включая переиспользование уже созданных кодов при `export_qty = 0`). Ответ: `order_id`, `order_ref`, `line_count`, `total_qty`, `lines[]` (`order_line_id`, `item_id`, `item_name`, `gtin`, `qty`, `hu_count`, `hu_codes` из активных `production_pallets` заказа).
  - Preview различает full applicability, aggregate coverage, `remaining_to_produce`, planned/unplanned, scoped/requested/imported quantities и reserve; ready-HU fact не отменяет applicability.
  - Для `CUSTOMER` и `INTERNAL` export разрешён только когда весь текущий markable `remaining_to_produce` представлен stable subjects production pallet plan.
  - Endpoint создаёт/переиспользует request-only `marking_order` и immutable scopes; Excel export не создаёт `marking_code`, import, operational coverage или status transition.
  - Повторный export идемпотентен: outstanding scopes покрывают уже запрошенное количество, а quantity increase создаёт request только на uncovered delta.
  - Для заказа в финальном статусе `SHIPPED` (`Выполнен`) Excel ЧЗ не формируется: WPF не дает нажать кнопку, а backend `POST /api/orders/{orderId}/marking/export` возвращает отказ без создания `marking_order` и `marking_code`.
  - Отдельное окно `MarkingWindow`, его обработчик, ручной `POST /api/marking/create-from-production-needs` и legacy global/item mutation endpoints удалены после caller/dependency audit.
  - Web-список заказов показывает бинарный статус ЧЗ из server-side DTO как icon-only индикатор с подсказкой `Маркировка не проведена` / `Маркировка проведена`.
- На этапе создания/обновления `CUSTOMER`-заказа сервер больше не создает новые HU-резервы из свободного stock. Refresh/rebuild может читать кандидатов, валидировать отображение и сохранять уже существующие ручные reservations, но не должен заново привязывать ранее отвязанный HU и не должен удалять ручные reservations без явной причины.
- В WPF refresh кандидатов HU для `CUSTOMER` не делает свободные HU выбранными автоматически: `AutoSelected` из candidate API является только подсказкой/кандидатом для picker. В выбранные HU (`SelectedHuCodes`) попадают только уже сохраненные `order_receipt_plan_lines` или явное действие оператора в HU picker / ready-HU binding.
- Для `CUSTOMER`-заказа read-model кандидатов использует только физические HU с положительным остатком по `ledger` (`LEDGER_STOCK`); `INTERNAL_FILLED` (FILLED без ledger) не допускается.
- Свободные складские HU-кандидаты для `CUSTOMER` резервов сортируются FIFO: приоритет источника, первая положительная `ledger`-приемка по `item_id + HU`, `doc_id` этой приемки, затем `HU` только как финальный tie-breaker. FIFO-порядок может использоваться явной operator command, но не запускается фоново при create/update/refresh.
- После закрытия отдельного `PRD` сервер может пересчитать read-only состояние affected заказов, но не создает новые `order_receipt_plan_lines` из свободных HU. Полный пересчет/backfill резервов остается только для явной maintenance/action-команды.
- Один и тот же HU/объем не может быть одновременно зарезервирован за несколькими клиентскими заказами.
- Для клиентского заказа используется флаг `bind_reserved_stock`:
  - `true`: legacy/compatibility-признак готовности заказа использовать складской HU reserve; сам по себе не запускает фоновую привязку при сохранении;
  - `false`: автоматическая фоновая привязка также не выполняется; explicit HU binding command остается единственным нормальным способом изменить reserve.
- Старый explicit endpoint `POST /api/orders/{orderId}/hu-reservations/apply` сохраняется для legacy/manual сценариев и не меняет контракт. Новый финальный command для backend apply/detach:
  - `POST /api/orders/{orderId}/hu-bindings/apply-final`;
  - request: `{ "mode": "replace_final_selection", "lines": [{ "order_line_id": 123, "expected_bound_hu_codes": ["HU-1"], "final_hu_codes": ["HU-1", "HU-2"] }] }`;
  - `mode` обязателен и имеет единственное значение `replace_final_selection`;
  - `expected_bound_hu_codes` обязателен и сравнивается с текущим состоянием как normalized set; mismatch возвращает `HU_BINDING_STALE`;
  - `final_hu_codes` обязателен, пустой список означает явную отвязку affected строки; порядок списка сохраняется как `sort_order`;
  - строки заказа, не переданные в request, не меняются.
- `apply-final` выполняется транзакционно: внутри одной store transaction заново загружаются заказ, строки, текущий receipt plan, shipment remaining, candidates/read-model и production pallets. Команда повторно проверяет eligibility, stale-state, дубли HU и итоговое условие `final_bound_qty <= qty_ordered - shipped_qty`.
- В `Заказы → Управление HU` unfiltered `GetManageTargets.CurrentBoundHuCodes` и `CurrentBoundQty` образуют **полный reservation snapshot** строки. `HuRows` — только **paged projection** для отображения; фильтр, поиск и пагинация не меняют write intent и отсутствие HU на экране не означает detach.
- Для affected строки `final_hu_codes` и `future_bound_qty` вычисляются совместно как overlay **explicit mutation intent** поверх полного snapshot: явный detach/move-out удаляет код и вычитает известное количество конкретного HU, bind/move-in добавляет код и количество, а незатронутые невидимые HU сохраняют код и долю в агрегированном `CurrentBoundQty`. Move в ту же строку является no-op.
- Future quantity не вычисляется суммированием текущей страницы. Capacity-проверки используют полный future state; отрицательное количество, отсутствие видимого исходного HU в полном snapshot, неизвестная строка/quantity или противоречивый overlay приводят к fail-closed без записи с требованием обновить данные.
- Management apply передаёт только affected строки: `expected_bound_hu_codes` — полный исходный snapshot, `final_hu_codes` — полный snapshot с overlay. Detach возможен только вследствие явного действия оператора. После успешной записи локальный commit сохраняет полные codes/quantity, затем выполняется canonical reload.
- Ready HU binding не пишет `ledger`, не меняет физический остаток и не меняет происхождение HU (`docs.order_id`, `production_pallets.order_id/order_line_id`, source PRD/internal order остаются прежними).
- Global ready HU binding начинается с read-only backend-модели `GET /api/orders/hu-bindings/ready`. Модель возвращает только свободные `LEDGER_STOCK` HU с положительным ledger balance, которые не присутствуют в `order_receipt_plan_lines.to_hu` и не принадлежат другому активному CUSTOMER owner, и подходят хотя бы к одной активной строке CUSTOMER-заказа (`IN_PROGRESS`/`ACCEPTED`) по `item_id` и количеству.
- Входящий тип `READY_HU_BINDING_AVAILABLE` является computed notification, а не persistent request в БД: он не создается в таблице заявок, не поддерживает Confirm/Reject/Dismiss и исчезает только когда computed `hu_count = 0`. `/api/requests/summary` включает его как один pending-сигнал при `hu_count > 0`.
- Global ready HU binding не создает отдельный механизм записи: сохранение выбранных HU должно вызывать только `POST /api/orders/{orderId}/hu-bindings/apply-final` по затронутым заказам.
- Если ready HU заменяет production need клиентской строки, `apply-final` может отменить только целые безопасные customer planned pallets той же строки: `status=PLANNED` (сравнение без учёта регистра), без `printed_at`, без `filled_at`, без заполненных component lines. Order-level `marking_status`, `marking_excel_generated_at` и `marking_printed_at` для apply-final не блокируют замену. Unsafe pallet-level `PRINTED`/`FILLED`/mixed/partial состояние возвращает `HU_BINDING_PLAN_CONFLICT` с конкретной причиной в `problems` (`status=...`, `printed_at=...`, `filled_at=...`, `component_filled_qty=...`).
- Если количество added ready HU нельзя покрыть целыми planned pallets без частичной отмены, команда возвращает `HU_BINDING_PLAN_CONFLICT`.
- При отмене безопасной planned pallet ставятся `production_pallets.status=CANCELLED`, `cancel_reason='replaced_by_ready_hu'`, `cancelled_at=<server time>`. Связанные draft doc lines очищаются только существующим безопасным механизмом, без удаления истории cancelled pallet.
- При detach команда не re-open старые `CANCELLED` pallets. Потребность CUSTOMER строки восстанавливается через существующий sync/recompute planned need для affected customer order lines; internal production orders и stock replenishment при этом не создаются.
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

### Канонический контракт V0040

- Legacy cohort является exemption от требования real KM для immutable frozen line scope, а не marking coverage. `LegacySynthetic`/`TEMP-CHZ-*`, старые allowlists, grandfather allowances и V0039 audit сохраняются только как история и не участвуют в cohort/hash/status/runtime gates.
- Для active line: `legacy_exempt_qty = min(current applicable qty, frozen_quantity)`, `real_required_qty = max(0, current applicable qty - legacy_exempt_qty)`. Новая line получает exemption `0`; увеличение старой line выше frozen cap требует real KM только на delta.
- Order status агрегируется только по real-required quantity: сумма `0` → `NOT_REQUIRED`; вся real-required quantity каждой line покрыта valid real coverage → `APPLIED`; иначе → `NOT_APPLIED`. `has_marking_applicable_quantity` публикуется отдельно и может быть true для полностью frozen `NOT_REQUIRED` order. Legacy line никогда не скрывает uncovered real-required line mixed order.
- `marking_operational_coverage` и transferable `marking_ready_hu_fact` после cutover имеют только `REAL_IMPORT`. Legacy exemption не создаёт request scope, import, code, coverage или ready fact. Production close может пропустить frozen component по subject exemption, но создаёт ready fact только для whole HU, полностью обеспеченной real coverage.
- Frozen CUSTOMER scope сохраняет `shipped_quantity_at_cutover` и `frozen_unshipped_legacy_quantity = frozen_quantity - shipped_quantity_at_cutover`. Post-cutover расход считается только по immutable CLOSED OUTBOUND attribution с basis `LEGACY_EXEMPT`; `REAL_READY`, DRAFT/rollback и binding/reservation quota не расходуют.
- Exact formula: `remaining_legacy_fulfillment = max(0, frozen_unshipped_legacy_quantity - SUM(LEGACY_EXEMPT attribution.quantity))`.
- Binding — reservation, а не consumption. Full-real-ready whole HU не расходует legacy quota. Сумма bound non-real-ready whole HU ограничена current remaining legacy fulfillment. Новый order без frozen scope не получает legacy HU ни через candidates, ни через direct write.
- Authoritative OUTBOUND close под locks выбирает exact basis каждой whole HU: полный active `REAL_IMPORT` ready fact + positive exact ledger composition → `REAL_READY`; иначе допускается только `LEGACY_EXEMPT` в remaining frozen quantity. Partial/mixed/ambiguous HU fail-closed. Document status, ledger и immutable attribution commit/rollback выполняются одной транзакцией.
- Порядок отгрузок не влияет на итог: для frozen `100`, current `150` оба порядка `REAL50→LEGACY100` и `LEGACY100→REAL50` требуют ровно `50` real и расходуют ровно `100` legacy exemption.
- Safe decrease/cancel/replan использует existing production/physical guards. Request/import/code provenance не переписывается; excess active REAL_IMPORT coverage retire/cap-ится и не восстанавливается. Subject exemption trim/release выполняется в той же transaction и может восстановиться только внутри frozen cap/current canonical need. Committed filling/ledger/CLOSED production требует existing controlled correction/release.
- `marking_legacy_cutover_subject_exemption` — bounded subject gate, не второй production balance. Производственная потребность и plan остаются existing authoritative server calculations. Active exemption still-valid stable subject сохраняется в пределах immutable grant/current subject quantity/frozen line cap; функция не обнуляет и не перераспределяет allocations заново. Новый independent safe-replan subject получает immutable grant только из реально свободного остатка той же frozen line scope и не отнимает exemption у существующего subject. Controlled correction использует только canonical `predecessor_subject_id`/root lineage. UUID, item/GTIN/HU similarity и новая/adopted CUSTOMER line не определяют наследование или allocation priority.
- Preflight/hash не зависят от количества/статуса synthetic codes. Coherent DRAFT PRD и `PLANNED/PRINTED` pallet с единственным exact `ACTIVE` subject входят во frozen snapshot без real KM. Полный `FILLED` output допустим с единственным exact `COMPLETED` subject: exact означает совпадение component/pallet/document lineage, current order line, item, GTIN и planned quantity; отсутствие legacy fill timestamp при terminal `FILLED` и полном `filled_qty` само по себе не является partial progress. Missing/ambiguous/mismatched subject или lifecycle, partial/inconsistent filling, orphan structure, invalid quantity, missing GTIN и unsafe real/unknown provenance остаются fail-closed.
- Cutover enforce выполняется после остановки writers в `SERIALIZABLE` transaction: повторный exact snapshot/hash → immutable cohort/line scopes/subject exemptions → `ENFORCED`. Serialization conflict возвращает `409`, без automatic retry.

Полная maintenance-процедура и rehearsal: [`marking-cutover.md`](marking-cutover.md).

### Исторический контракт V0027–V0039 (не является runtime authority после V0040)

- Applicability считается по каждой неотменённой строке с `qty_ordered > 0` и `item_types.enable_marking=true`; `remaining_to_produce`, plan и ready stock applicability не отменяют. Пустой GTIN даёт `NOT_APPLIED` и configuration error.
- Статусы: `NOT_REQUIRED` — маркируемого активного количества нет; `NOT_APPLIED` — оно есть, но полного aggregate coverage нет; `APPLIED` — всё текущее relevant quantity покрыто real operational coverage, bounded grandfather coverage и/или ledger-backed `marking_ready_hu_fact`. Пользовательского `PARTIAL` нет.
- Preview возвращает applicability, coverage, `remaining_to_produce`, planned/unplanned, scoped/requested/imported quantity и reserve. Export разрешён только при полном pallet plan текущего markable `remaining_to_produce`; ready-HU с valid fact остаётся applicable, но нового Excel не требует.
- Export — request-only атомарная операция. Он создаёт immutable `marking_order`/`marking_request_scope` и XLSX, но ноль `marking_code`, imports, coverage и status transitions. Default reserve равен DB-настройке `5`; повтор с тем же snapshot/hash идемпотентен.
- Для `marking_responsibility=CUSTOMER` order export отклоняется и XLSX не создаётся. Первый явный upload/preview из related order card идемпотентно создаёт acquisition envelope и immutable scopes с reserve `0`; уже scoped shared quantity повторно не запрашивается, а до Confirm codes и operational coverage отсутствуют.
- Scope фиксирует stable `marking_production_subject`, component/pallet/doc-line identity, item, GTIN, quantity и original ownership. Quantity increase запрашивает только uncovered delta; decrease/cancel не переписывает provenance и не переиспользует excess. Item/GTIN mutation после request отклоняется; adoption меняет current ownership subject, сохраняя IDs и immutable request/import provenance.
- Immutable scope и aggregate coverage имеют отдельную монотонную active/consumable quantity. При уменьшении subject излишек `REAL_IMPORT` и `GRANDFATHER_ALLOWANCE` необратимо retire/cap-ится; последующее увеличение того же subject не восстанавливает его и требует нового real request на delta. Cancellation обнуляет consumable quantity без возможности повторного увеличения. Controlled correction атомарно переносит только оставшуюся active quantity в successor lineage; для одного grandfather allowance допустим ровно один active consumer и никогда не больше `approved_quantity`.
- Real import создаёт immutable `marking_code(origin=RealImport,status=Imported)` только внутри request scope. Нормальный ответ Kontur содержит `requested_quantity` и подтверждается целиком. `valid < required` доступен лишь как recovery/anomaly Confirm: codes сохраняются для supplement, но allocations отсутствуют, `NOT_APPLIED`, filling закрыт. Все scopes активируются вместе при достижении required; `required <= valid < requested` — `APPLIED` с reserve-short warning.
- Confirm shared request атомарен/идемпотентен для всех current allocations; request виден в каждой карточке связанных после adoption заказов, upload разрешён из любой, duplicate Excel на inherited outstanding quantity запрещён.
- Карточка заказа WPF выполняет multi-select upload через order-scoped `import/preview`, показывает распределение `imported_after / requested_qty`, reserve-short и отдельное предупреждение recovery; `import/confirm` отправляет тот же набор файлов, `batch_id`, `idempotency_key` и `snapshot_hash`. Имя файла не выбирает request. Server mapping выполняется по GTIN из AI(01) только среди currently related outstanding scopes; ambiguity блокирует Confirm.
- `marking_import_batch_request` делает один multi-file/multi-GTIN Confirm общим parent для всех requests. Вся проверка snapshot и запись `marking_import_file`, `marking_code_import`, immutable RealImport codes, batch/request lineage и aggregate coverage выполняются атомарно. При `valid < required` WPF требует явного recovery-подтверждения; это не создаёт pallet allocation и не открывает filling.
- Production close не выбирает КМ. После блокировки документа/subjects/coverage он проверяет полное aggregate coverage, создаёт `marking_ready_hu_fact` и lineage и в той же транзакции пишет ledger/закрывает документ. Любая ошибка откатывает fact и ledger.
- CUSTOMER `1200` + bound `1200` ledger-backed ready HU с valid fact => `APPLIED`, хотя `remaining_to_produce=0`; plan/Excel отсутствуют. Bind/unbind/rebind fact не меняют.
- `SHADOW` после deploy V0036 — maintenance/fail-closed, а не рабочий смешанный режим: новые marking export/import, filling маркируемых production pallets и соответствующий production close запрещены до атомарного `ENFORCED`; synthetic fallback не существует.
- Одноразовая V0037 меняет только `marking_code.origin: HistoricalUnknown → LegacySynthetic` при одновременном совпадении всех доказательных признаков: `TEMP-CHZ-%`, существующий linked import, exact `temporary-chz-export`, exact `<temporary-chz-export>` и status не `Quarantined`. Любое неполное совпадение и `Quarantined` остаются fail-closed `HistoricalUnknown`; реклассификация не создаёт operational coverage/ready-HU fact и не изменяет ledger, документы, HU или production. Возможный механизм пропуска V0027 воспроизводится regression-тестом, но неизвестная историческая production-причина не объявляется доказанной.
- Cutover preflight не классифицирует `FILLED` pallet как active plan/filling progress, только если связанный current subject имеет `lifecycle=COMPLETED`, а production document уже `CLOSED`. При достаточном текущем ledger такой history остаётся кандидатом на grandfathered ready-HU fact; при недостаточном ledger fact не создаётся. `FILLED` с незакрытым PRD и partial filling у `PLANNED/PRINTED` по-прежнему fail-closed блокируют cutover.
- `MARKING_ACTIVE_PALLET_PLAN` для `PLANNED/PRINTED` является non-blocking warning только при `ACTIVE` subject со строгой current-связью component→subject→pallet/component/doc, незакрытом PRD и нулевом filling progress. Отсутствующая/stale lineage, `CLOSED` PRD или любой `filled_qty`/fill timestamp сохраняют fail-closed error; bounded approval выполняется существующими exact-hash `MARKING_SUBJECT_SNAPSHOT`/`MARKING_OPEN_PRD`, а не исключением всего issue code в enforce.
- V0038 вводит canonical item exemption: `applicable = item_types.enable_marking AND NOT items.chz_marking_exempt`; GTIN не меняет applicability. API сохраняет alias `cz_marking_required`, равный derived `chz_marking_applicable`, и отдельно возвращает `GTIN_REQUIRED`. Validation transition-based: GTIN обязателен при создании/включении applicable-состояния и очистке ранее заполненного GTIN, но metadata-only update уже legacy-invalid applicable товара допустим. Exemption, смена item type и отключение marking у типа проходят одинаковый lineage guard; persisted `orders.marking_status` затронутых активных заказов пересчитывается атомарно в lock order `item_types→items→orders`.
- V0039 вводит `controlled legacy task retirement` для узкого cutover-maintenance случая: явно выбранный избыточный historical acquisition task, содержащий только `Reserved LegacySynthetic` без operational lineage, атомарно переводится из `Printed` в `Cancelled`. Canonical evidence определяется только `marking_code.origin='LegacySynthetic'`; текст/prefix кода не участвует. Операция не удаляет и не изменяет `marking_code`/`marking_code_import`, ledger, документы, строки заказа, production pallets/subjects, coverage или allowlist. Она разрешена только в `SHADOW`, если после исключения task каждый remaining active task имеет `Applied LegacySynthetic`, их суммарный `Applied` точно равен current target, а любой `Reserved`/`Voided` не входит в evidence/cap. Partial legacy, real/unknown/quarantined provenance, excess/shortage и любая operational lineage остаются fail-closed.
- Успешный retirement имеет immutable audit и не добавляет информационную запись в canonical preflight: hash меняется естественно из-за terminal status и новой line classification. Normal runtime не может переоткрыть audited task, активировать/перераспределить его `Reserved` codes или создать для него новую import/request/print/coverage lineage. Исторические task/code/import остаются доступны для audit; V0039 намеренно не вводит новый запрет `DELETE` для будущей отдельно спроектированной controlled maintenance. Line approval, subject approval, retirement apply и enforce выполняются локально в `SERIALIZABLE` transaction поверх общего state-row lock; serialization conflict возвращается как `409 MARKING_CUTOVER_SERIALIZATION_CONFLICT`, автоматического retry нет, оператор повторяет preflight/dry-run с новым snapshot/hash.
- Cutover line approval принимает line, optional quantity intent и current hash; actor всегда server-derived. Только aggregate `Applied LegacySynthetic` может образовать cap, `Reserved` не evidence, excess `Applied > target` и unsafe/unmapped evidence являются hard error. На line существует один immutable parent: `H1→parent→H2`, retry исходного intent идемпотентен, повторное approval по `H2` запрещено. Subject approval требует exact `H2` и exact current snapshot; child hash не меняет, поэтому `parent→H2→child→H2→enforce(H2)`. DB uniqueness не допускает второй parent той же line, второй child subject или двойную active coverage одного allowance.
- Grandfather capacity сохраняется при adoption того же subject и controlled correction только по recursive canonical `predecessor_subject_id`. Successor получает не больше остаточной bounded capacity; уменьшенная/retired capacity не восстанавливается. Cancellation завершает capacity, unrelated replan без predecessor lineage её не получает. Для explicit exempt line `FILLED/CLOSED` history и `PRINTED/DRAFT` plan не являются marking blocker и не создают marking subject/coverage requirement; связанные `MARKING_OPEN_PRD` также исключаются из marking preflight как отдельные approvable errors, поскольку open-PRD scope ограничен applicable components. Pallet/doc/ledger history при этом не меняется.

## WPF: паллеты по строке заказа

- В гриде строк карточки заказа одна read-only колонка **`Паллеты`** отображает `OperatorHuDisplayRows` из canonical server presentation. В ней остаются существующие warehouse reservations, production tasks и operational HU.
- Отдельные колонки `Доступно HU`, `Осталось HU` и inline-колонка `HU` с кнопками выбора удалены. Обычные открытие, refresh и сохранение карточки не формируют HU apply intent и не изменяют reservations.
- Редкие ручные bind/detach/move/replace выполняются в `Заказы → Управление HU`; `CustomerOrderHuBindingCoordinator` продолжает загружать persisted reservations и обслуживать явное безопасное уменьшение количества строки, но не является вторым inline picker.
- Pure `HuFactConsistencyAnalyzer` анализирует normalized raw HU facts и возвращает issue codes без labels/dominant status. Единственный `HuOperatorClassifier` интерпретирует эти facts/issues для presentation, а `HuOperatorReadModelService` проецирует результат; projectors не повторяют precedence.
- Публичные wire types раздельны: `ProductionTaskPresentation` и `OperationalHuPresentation`; общий публичный `layer` enum не вводится. Production branch содержит только `AWAITING_FILL` / «Ожидает наполнения»; operational codes/labels: `ON_STOCK` / «На складе», `RESERVED` / «Зарезервирован», `AWAITING_SHIPMENT` / «Ожидает отгрузки», `SHIPPED` / «Отгружен», `INCONSISTENT` / «Несогласованное состояние». `PLANNED` и `PRINTED` до fill оба отображаются как `AWAITING_FILL`; печать передаётся отдельно как additive action fact `is_label_printed` и не меняет lifecycle state. Нормальный источник факта — persisted `printed_at`; для исторической совместимости `status = PRINTED` также означает, что этикетка печаталась, но `FILLED` сам по себе этого не доказывает.
- `AWAITING_SHIPMENT` допустим для полностью завершённой CUSTOMER production HU active owner-заказа только при положительном ledger по всем компонентам и отсутствии reservation/shipment conflict. `EffectiveStatus`, raw pallet status и `fate_*` не являются operator state.
- Mixed HU классифицируется атомарно целиком. Normal fill подтверждает весь composition одной physical HU; component subset до записи отклоняется как `PARTIAL_COMPONENT_FILL_NOT_ALLOWED`. Persisted partial progress, промежуточное `filled_qty`, `FILLED` с незавершённым component и `FILLED` без physical ledger дают `INCONSISTENT`. Разные targets/locations/reservations, неполная component shipment и uncertain correction lineage также fail-closed.
- CLOSED OUTBOUND с положительным ledger remainder той же HU классифицируется `INCONSISTENT`, а не `SHIPPED`/`ON_STOCK`. `SHIPPED` требует совпадения effective OUTBOUND lines с ledger movements, полного balance HU непосредственно перед проведением, нулевого текущего balance и одного shipment target. Более поздний CLOSED `INVENTORY_CORRECTION` с положительными rows при нулевом post-shipment balance открывает новую lifecycle epoch; старый OUTBOUND остаётся history и не участвует в current composition/target/line quantity. После restoration допустимы обычные MOVE/reservation facts и новый whole-HU OUTBOUND; повторный `SHIPPED` относится только к последнему доказанному OUTBOUND. Недоказанный lineage даёт `CORRECTION_LINEAGE_UNCERTAIN`. DRAFT OUTBOUND dominant state и shipped quantity не меняет.
- Order-scoped facts загружаются одним batch command для всех candidate HU заказа, после чего production/reservation/outbound/ledger facts этих HU не фильтруются текущим `order_id`. HU-scoped canonical query использует тот же loader и те же semantics, включая отрицательные balances и ledger history. Legacy global TSD details читаются отдельно только для compatibility; `HuOperatorClassifier` от них не зависит. Precedence не кодируется в SQL.
- Operational presentation для положительного stock строит current content/qty/location из `ledger`, для `SHIPPED` — из доказанной effective shipment. Production plan не заменяет физический balance. Different-item/UOM mixed content не суммируется в один scalar; `INCONSISTENT` order row может иметь nullable `qty`. Public component DTO не содержит raw `planned_qty`, `filled_qty`, `order_line_id` или classifier lineage.
- Canonical server contract аддитивен: detailed order endpoint возвращает `hu_presentation`, global HU endpoints — `operator_presentation`, а релевантные production endpoints — `production_presentation`. WPF, PC Web и TSD используют эти поля как main operator status. `HuDisplayRows`, `warehouse_hu_rows`, `production_hu_rows`, `shipped_hu_rows`, `production_hu_rows.fate_*`, legacy `TsdHuState` и raw production fields временно сохраняются только для compatibility/technical details и удаляются отдельным follow-up после проверки runtime consumers.
- В текущем compatibility fate-read-model CUSTOMER-заказа-получателя резерв и проведённая отгрузка показывают достоверно найденный source-заказ (`← выпуск заказ N`). В detailed order read-model target reservation передаётся существующей коллекцией `hu_presentation.operational_hus`: `state.code = RESERVED`, `reservation_target` и optional `source_production_order`; для проведённой отгрузки compatibility-атрибуция передаётся optional полями `shipped_hu_rows.source_order_id/source_order_ref`. Направление fate production HU определяется структурным `production_hu_rows.fate_order_id`. Если source установить нельзя, source-поле опускается и стрелка не выводится; legacy `CLOSED PRODUCTION_RECEIPT` без нормализованного HU и ledger по тому же `doc + item + HU` не создаёт fate-строку.
- В текущем WPF compatibility merge source mixed production pallet определяется по `production_pallet_lines`; если source и target совпадают, лишняя стрелка подавляется. Legacy merge заменяет bare `наполнено` fate-строкой, чтобы для одного HU не показывать одновременно `Наполнено` и `Ожидает отгрузки`.
- PC, WPF и TSD используют одинаковую server-owned semantics и terminology, не реализуя labels/precedence локально; platform layout может различаться. XAML/JS выбирают tone/icon только по canonical code.
- Additive read model не используется как permission. `HuMutationEligibilityPolicy` независимо интерпретирует raw facts и issues анализатора для конкретной команды и не принимает решение по `StateCode == INCONSISTENT`; второго status engine нет. PostgreSQL mutation перечитывает HU facts через scoped store той же connection/transaction после канонического порядка order → normalized HU → document locks. `ReleaseProducedStock` recovery при `FILLED` без ledger разрешён только для единственного issue `FILLED_WITHOUT_LEDGER_STOCK`, нулевых current balances, одной relevant active `FILLED` pallet, однозначного ownership, полного совпадающего plan/request composition и отсутствия foreign reservation, другого active DRAFT OUTBOUND, effective outbound, correction/lineage ambiguity и любых иных issues. Все остальные inconsistent cases fail-closed.

## Подготовка паллетных этикеток

- Существующие активные заказы без `production_pallets` остаются legacy и не появляются в TSD `Наполнение`, пока оператор явно не выполнит подготовку паллет.
- В WPF карточке заказа есть команды: `Сформировать план паллет`, `Удалить план паллет`, `Печать паллетных этикеток`. Ручная операторская команда `Перенести план паллет` не показывается.
- `Сформировать план паллет` вызывает `POST /api/orders/{orderId}/production-pallets/plan`, создает/переиспользует технический open `PRODUCTION_RECEIPT`, дробит строки по `items.max_qty_per_hu`, назначает HU серверной sequence-генерацией и не пишет `ledger`.
- **Ожидаемый внутренний выпуск** — остаток к выпуску `max(0, qty_ordered - produced_qty)` по строкам открытых `INTERNAL`-заказов (`status NOT IN (SHIPPED, CANCELLED, MERGED)`; `DRAFT` и `IN_PROGRESS` считаются открытыми). Это не ledger-остаток, не coverage и не резерв: он не входит в формулу полного планирования, не уменьшает потребность и не блокирует операции.
- **Pre-plan coverage preview**: перед явным планированием паллет `CUSTOMER`-заказа UI использует read-only `GET /api/orders/{orderId}/production-pallets/pre-plan-coverage-preview` (endpoint не меняет БД); старый `GET .../production-pallets/internal-supply-warning` сохраняется как compatibility alias с тем же payload. Ответ: `has_warning`, человекочитаемое `message`, строки `customer_order_line_id`, `item_id`, `item_name`, `would_plan_qty` (нехватка строки по той же формуле, что использует `POST /plan`), `internal_order_id`, `internal_order_ref`, `internal_status`, `expected_qty`, а также `would_plan_line_count`, `safe_line_count`, `warning_line_count`, `has_free_warehouse_hu` и `free_warehouse_hu[]` (свободные `LEDGER_STOCK` HU-кандидаты по планируемым строкам из candidates read-model). Warning срабатывает при любом пересечении: `would_plan_qty > 0` и положительный ожидаемый внутренний выпуск по тому же `item_id`. Для `INTERNAL`-заказа и terminal-статусов endpoint возвращает пустой preview.
- WPF показывает `PrePlanCoverageDialog` при наличии вариантов покрытия или предупреждений preview. Оператор выбирает складские и INTERNAL production HU для `apply_selected_coverage_then_plan` либо продолжает планирование без выбранного покрытия. Диалог не является экраном общего управления reservations и не предоставляет произвольный detach/replace: эти операции выполняются в `Заказы → Управление HU`. При отмене `POST /plan` не вызывается; если preview недоступен, WPF показывает нейтральное подтверждение продолжения без скрытого изменения reservations.
- **Safe-only планирование**: `POST /api/orders/{orderId}/production-pallets/plan` принимает опциональное тело `{ "mode": "skip_internal_supply" }`. Сервер в одной транзакции заново пересчитывает пересечение с ожидаемым внутренним выпуском, исключает affected строки и **целые mixed-группы атомарно** (частичное планирование общего HU не допускается) и планирует только остальные строки; qty/`order_line_id` от клиента не принимаются. Ответ дополняется `mode`, `planned_order_line_ids` и `skipped_lines[]` (`customer_order_line_id`, `item_id`, `item_name`, `production_pallet_group`, `skipped_reason` = `expected_internal_supply` | `mixed_group_contains_expected_internal_supply`, `triggered_by_order_line_id`, `internal_refs[]`); перечисляются **все** пропущенные строки, включая строки группы без прямого пересечения. Если безопасных строк нет, план не создаётся (no-op). Повторный вызов идемпотентен (append-only).
- Совместимость `POST /plan`: пустое тело, отсутствующий Content-Type, `null`/`{}` или отсутствующий `mode` означают полный режим с прежним поведением (существующие клиенты не обязаны слать тело); `mode = "full"` эквивалентен отсутствию тела; неизвестный `mode` возвращает `400 INVALID_PLAN_MODE` без записи; некорректный JSON — `400 INVALID_JSON`. Новые поля ответа аддитивны. Warning/preview не резервирует товар, не меняет `ledger` и формулу полного планирования; окончательное создание паллет остаётся за серверным пересчётом в `POST /plan`.
- `Удалить план паллет` сначала читает `GET /api/orders/{orderId}/production-pallets/cancel-plan-options` и открывает выбор паллет по строкам заказа/номенклатуре. `PLANNED` и `PRINTED` паллеты доступны к выбору и выбраны по умолчанию; `FILLED` паллеты видны, но disabled с причиной `Нельзя удалить: паллета уже наполнена/выпущена`. Подтверждение вызывает `POST /api/orders/{orderId}/production-pallets/cancel-plan` с непустым списком выбранных `pallet_ids`.
- Selected-delete строгий: сервер удаляет только перечисленные `pallet_ids`; пустой, отсутствующий или `null` список не трактуется как full-plan delete и возвращает validation/no-op. Ответ содержит `requested_pallet_ids`, `removed_pallet_ids`, `skipped_pallet_ids`, `removed_line_count`.
- Выборочное удаление плана меняет только выбранные `PLANNED`/`PRINTED` паллеты и связанные draft `PRODUCTION_RECEIPT` строки; `FILLED` паллеты, закрытые PRD и любые строки `ledger` не изменяются. Если по `PRINTED` паллете уже могла быть сформирована ЧЗ/маркировка, WPF показывает предупреждение и требует дополнительное подтверждение.
- Ручное удаление строк `PRODUCTION_RECEIPT` запрещено для любых PRD, включая legacy PRD без паллет. WPF скрывает действие и блокирует старые handlers/hotkeys; ручной API удаления строки возвращает `PRD_LINE_DELETE_FORBIDDEN` с сообщением `Строки выпуска PRD нельзя удалять вручную. Измените паллетный план или заказ.` Связанные draft PRD-строки изменяются только серверными workflow паллетного плана или заказа.
- Внутренний сервис/endpoint переноса плана `POST /api/orders/{targetCustomerOrderId}/production-pallets/adopt-from-internal/{sourceInternalOrderId}` не является пользовательским действием в WPF. Он может использоваться только системными, тестовыми или maintenance-сценариями, где явно проверены ограничения переноса.
- Перенос разрешен только из INTERNAL-заказа в CUSTOMER-заказ, если оба заказа не `CANCELLED`/`SHIPPED`, source имеет open draft `PRODUCTION_RECEIPT` с активными `PLANNED`/`PRINTED` паллетами, по source PRD нет `ledger`, нет `FILLED` паллет, PRD не закрыт, а target еще не имеет активного плана паллет. Если у target уже есть план, сервер возвращает `TARGET_ALREADY_HAS_PALLET_PLAN` с требованием сначала удалить текущий план паллет у клиентского заказа.
- При переносе сервер в одной транзакции переносит `doc_lines`, `production_pallets` и `production_pallet_lines` на target PRD, ремапит `order_line_id` по совпадающему `item_id`, сохраняет HU/status/qty/location/timestamps и не меняет `ledger`, закрытые документы, OUTBOUND и `qty_ordered` source/target. Если в target нет строки заказа для любого `item_id` из source плана, сервер возвращает `TARGET_LINE_NOT_FOUND`.
- Для legacy INTERNAL-заказа, у которого вся потребность была перенесена в CUSTOMER и выпуск больше не требуется, сохраняется статус `MERGED` / display `Объединён`. Статус terminal: не участвует в TSD filling, production need и формировании плана паллет. Новый CUSTOMER HU binding не переводит INTERNAL в `MERGED` и не уменьшает `qty_ordered`.
- После adopt pallet plan с INTERNAL на CUSTOMER, merge/redistribute source INTERNAL, `Удалить план паллет` и других операций, которые опустошают source draft `PRODUCTION_RECEIPT`, сервер безопасно удаляет пустой draft PRD, если по документу нет `doc_lines`, активных `production_pallets`, `production_pallet_lines`, `ledger` и документ не `CLOSED`. Закрытые/проведённые PRD и PRD с движениями склада не удаляются.
- `Печать паллетных этикеток` сначала читает `GET /api/orders/{orderId}/production-pallets/print-rows`; если плана нет, пользователь получает `Сначала сформируйте план паллет`.
- После появления production pallet plan печать **не должна терять** warehouse-bound HU, уже привязанные к заказу.
- Перед печатью WPF открывает модальное окно выбора с **категориями**:
  - **складские HU**, уже привязанные к заказу (`order_receipt_plan_lines`);
  - **planned production HU** (`production_pallets` в `PLANNED`/`PRINTED` и т.д.).
- **Выбор по умолчанию:** planned production HU **включены**; warehouse-bound HU **выключены**, но оператор может включить их вручную.
- Цель: не допустить ситуации, когда до планирования печатались складские HU, а после `Сформировать план паллет` они **исчезают** из диалога печати.
- По умолчанию в блоке production pallets выбраны `PLANNED`; `FILLED`/`PRINTED` показываются, но не выбираются автоматически. Печать и `POST /api/orders/{orderId}/production-pallets/mark-printed` выполняются только для выбранных позиций.
- Production print rows строятся только из operational `production_pallets`/`production_pallet_lines`, привязанных к актуальным строкам текущего заказа, по всем его DRAFT и CLOSED PRD. В список входят `PLANNED`, `PRINTED` и `FILLED`, исключаются `CANCELLED`; наличие открытого DRAFT PRD не скрывает `FILLED` паллеты закрытых PRD. Display-only `HuDisplayRows`, fate-строки и стрелки резерва/отгрузки не добавляют и не удаляют строки печати.
- Печать использует только уже существующие `production_pallets.hu_code`. Она не вызывает ручной генератор HU, не создает новые паллеты, не меняет склад и не пишет `ledger`.
- Для складских `reserved_hu` строк `StoragePlace` берётся из фактического текущего места хранения HU по ledger / current HU stock, а не из планового `order_receipt_plan_lines.ToLocationCode` (последнее для места этикетки не используется). Инвариант: один HU может иметь положительный остаток **не более чем в одном** уникальном `location_id`, независимо от количества `item_id` внутри HU. Случаи: 0 положительных мест → `StoragePlace` пустой; 1 место и есть остаток запрошенного `HU+item` → код этого места; 1 место, но у запрошенного `item` остатка нет → пусто; >1 места по HU → нарушение инварианта, печать блокируется ошибкой (`GET .../print-rows` → HTTP `400` с сообщением, содержащим HU и конфликтующие места). Несколько строк/`item_id` одного HU в одном месте конфликтом не являются. Endpoint остаётся read-only.
- BarTender `.btw` шаблон получает NamedSubStrings: `HuCode`, `ItemName`, `Qty`, `OrderRef`, `PrdRef`, `Brand`, `StorageConditions`, `Uom`, `PalletNo`, `PalletCount`, `StoragePlace`, `ProductionDate`, `BatchNumber`, `Comment`, `IsMixedPallet`, `Composition`, `Line1ItemName`, `Line1Qty`, `Line2ItemName`, `Line2Qty`, `Line3ItemName`, `Line3Qty`. Для mixed pallet печатается одна строка/этикетка на общий `HuCode`, `ItemName = Микс-паллета`, а состав передается в `Composition`.
- `StorageConditions` для печати является серверным read-model полем из `GET /api/orders/{orderId}/production-pallets/print-rows`. Для одиночной production pallet используется текущее `items.storage_conditions` товара паллеты или пустая строка, если значение не задано. Для складской `reserved_hu` строки используется текущее `items.storage_conditions` товара привязанного HU или пустая строка. Для mixed pallet `StorageConditions` всегда пустой, даже если все компоненты имеют одинаковые условия хранения.
- `ProductionDate` и `BatchNumber` — необязательные параметры **одного запуска** WPF-печати: оператор задаёт их в окне `Печать паллетных этикеток` (поля `Дата изготовления` и `Номер партии`), и значения применяются ко **всем** выбранным этикеткам — и производственным, и складским HU. Поля при каждом открытии окна пустые и не сохраняются в `settings.json`. Дата передаётся в формате `dd.MM.yyyy`; номер партии обрезается по краям. Пустое значение передаётся пустой строкой и скрывается условной печатью шаблона. Если оператор ввёл непустое значение, а соответствующего NamedSubString в шаблоне нет, печать не запускается и показывается `В шаблоне BarTender отсутствует поле ProductionDate.`/`… BatchNumber.` (обязательные `HuCode`, `ItemName`, `Qty` проверяются как раньше).
- Если среди выбранных строк непустой `StorageConditions`, а соответствующего NamedSubString в `.btw` нет, печать не запускается и показывается `В шаблоне BarTender отсутствует поле StorageConditions.`. Пустой `StorageConditions` остаётся необязательным и не блокирует старые шаблоны.
- Сервер **по-прежнему** возвращает `ProductionDate` в `print-rows` из даты создания PRD (`doc.CreatedAt.Date`), но WPF перед печатью **всегда переопределяет** это значение значением из диалога — включая `ProductionDate = null` при пустом `DatePicker`. Поэтому дата создания PRD больше не попадает на этикетку как дата изготовления. API, серверная БД и модель производственного документа для этого не менялись.
- После успешной печати допускается перевод `PLANNED -> PRINTED`; повторная печать разрешена и использует тот же HU. `FILLED` паллеты не переводятся обратно в `PRINTED`.
- TSD `Наполнение` работает только с подготовленными известными HU: unknown HU отклоняется, HU другого заказа отклоняется, mixed pallet показывает состав `lines`, а `FILLED` паллета не пишет `ledger`.
- TSD filling-context, scan и fill используют только production pallets/components, привязанные к актуальным `order_lines` того же заказа; active orphan pallets с `order_line_id = NULL` или component-lines без валидной строки заказа не отображаются в read-model и отклоняются при scan/fill.
- Повторное TSD-наполнение `FILLED` паллеты идемпотентно по `order_id + hu_code`: если паллета уже перенесена в изолированный закрытый PRD, сервер возвращает текущий закрытый PRD и не требует, чтобы входной `prd_doc_id` совпадал со старым planning PRD. Такой fallback разрешен только для `FILLED` паллеты того же заказа; неизвестные HU, HU другого заказа и еще не наполненные HU с несовпадающим PRD отклоняются.
- TSD filling-context остается привязанным к активному open PRD для продолжения сканирования, но `document.summary`, `document.lines` и список `document.pallets` отражают order-level прогресс по всем активным `production_pallets` заказа, включая `FILLED` паллеты, перенесенные в изолированные закрытые PRD.
- Закрытие palletized PRD не требует legacy-действия `Распределить по HU`; сервер проверяет активные `production_pallets`, блокирует ненаполненные паллеты и только при успешном close пишет складской `ledger`. Draft PRD с уже начатым `Наполнением` подсвечивается в списке документов.
- WPF для `PRODUCTION_RECEIPT` с активным `production_pallets` не предлагает legacy `Выбрать HU`, автораспределение HU и ручное назначение HU по строкам: HU задаются планом паллет, наполнение — в TSD, WPF показывает статус паллет/печать. Проведение palletized PRD при уже `FILLED` паллетах — finalize-only (без повторного распределения HU и без дубля `ledger`).
- Если по заказу все паллеты `FILLED`, а open PRD закрыт, TSD `GET /api/tsd/production/filling-context/{orderId}` возвращает ошибку `Выпуск по заказу уже завершён. Нет паллет к наполнению.` (не `план паллет не сформирован`).
- На TSD отдельная legacy-кнопка `Выпуск продукции` не показывается. Производственный поток подготовленных паллет выполняется через `Наполнение`; legacy PRD остаются только для совместимости существующих документов/API.

### Редактирование строк заказа с паллетным планом

- При изменении строк `INTERNAL`/`CUSTOMER` сервер синхронизирует будущий production plan в той же транзакции сохранения заказа. `FILLED`, ledger и CLOSED PRD не изменяются.
- Активный будущий план состоит только из `PLANNED`/`PRINTED` pallets DRAFT PRD. При уменьшении, удалении, замене товара или изменении mixed-группы лишние pallets переводятся в `CANCELLED`, а связанные активные `doc_lines` supersede-ятся tombstone-строками `qty=0`; `CANCELLED`/superseded строки не участвуют в расчётах.
- Для `INTERNAL`: `future_need = max(0, qty_ordered - confirmed_production_qty)`. Для `CUSTOMER`: `protected_coverage_qty = min(qty_ordered, deduplicated_coverage_qty)` и `future_need = max(0, qty_ordered - protected_coverage_qty)`.
- CUSTOMER `deduplicated_coverage_qty` объединяет shipped, ledger-подтверждённый выпуск и существующие неотгруженные ready-HU bindings по `order_line_id + item_id + normalized HU`. Shipped всегда защищён; confirmed/bound часть того же уже отгруженного HU повторно не считается; совпадающие confirmed и bound покрытия одного HU объединяются через максимум. Legacy confirmed production без HU консервативно уменьшается на отгрузку, ещё не сопоставленную с HU-выпуском.
- Проверка уменьшения использует необрезанный `deduplicated_coverage_qty`; cap по `qty_ordered` применяется только к расчёту `future_need`. Удаление/замена строки с shipped, confirmed/FILLED/ledger production или существующим неотгруженным ready-HU binding блокируется. `PRINTED` сам по себе является будущим планом и может быть автоматически отменён.
- При уменьшении `CUSTOMER` обычное сохранение без явного `selectedHuCodes` не снимает текущие ready-HU bindings: они входят в protected coverage и могут заблокировать уменьшение. Явный final selection применяется атомарно до validation, после чего защищёнными считаются только оставшиеся неотгруженные bindings. HU неделимы; выбранный набор не может превышать новое количество. Запрошенное `qty <= 0` для строки `CUSTOMER` отклоняется сообщением `Количество строки не может быть 0. Удалите строку заказа.`.
- WPF перед immediate-save такого уменьшения показывает диалог со списком текущих reserved HU строки (`HU`, `qty`, source/status). По умолчанию выбран тот же максимальный набор `<= запрошенному qty`; после подтверждения WPF не вызывает отдельный `POST /api/orders/{orderId}/hu-reservations/apply`, а передает выбранные HU в серверное сохранение заказа. Сервер в одной транзакции сохраняет нормализованное `qty_ordered`, заменяет `order_receipt_plan_lines` выбранными HU и синхронизирует production pallets. Отмена диалога не меняет резерв и перечитывает серверное состояние; при ошибке не допускается частичное снятие HU без изменения qty.
- При увеличении `CUSTOMER` сервер перед созданием новых `production_pallets` проверяет свободные складские HU (`LEDGER_STOCK`) той же номенклатуры: если есть детерминированный subset с суммой ровно на shortage после текущих резервов/отгрузки/активных production pallets, эти HU добавляются в `order_receipt_plan_lines`; planned production pallets создаются только на остаток без точного складского HU-покрытия.
- При уменьшении `CUSTOMER` после сохранения строки сервер оставляет отгруженные и `FILLED` HU без изменений, снятие `reserved_hu` удаляет только строки резерва `order_receipt_plan_lines` и не отменяет складские HU, ledger или source `production_pallets`; лишние unfilled planned production pallets строки текущего заказа отменяются в рамках текущего `order_id + order_line_id`. Если после отмены нужен меньший остаток к производству, сервер добавляет только недостающий planned-объем по актуальному `qty_ordered`.
- При уменьшении сервер оставляет `FILLED` без изменений и отменяет (`CANCELLED`) лишние будущие `PLANNED`/`PRINTED` pallets; при увеличении append-only добавляет только недостающий будущий объём. Для mixed pallet изменение любого компонента отменяет и пересобирает весь будущий общий HU по актуальным component lines.
- После редактирования строки WPF обновляет подсветку HU-покрытия строки; частичная привязка и scoped-сброс `PLANNED`-части являются нормальным состоянием и не показываются как аварийная ошибка.

## Mixed pallet / общий HU

- Смысл `Общий HU` перенесен из PRD в заказ/план паллет. В WPF карточке заказа у каждой строки есть галочка `Общий HU` и номер группы; строки с одинаковым `production_pallet_group` (≥2 строк) планируются на один HU. Для ручной mixed-группы `items.max_qty_per_hu` не проверяется.
- В строках заказа WPF показывает назначенные `HU паллет`, чтобы оператор видел результат планирования. Повторное `Сформировать план паллет` до печати пересобирает нераспечатанный план по текущим галочкам строк и назначает новые HU без создания дублей.
- Mixed pallet = одна запись `production_pallets` с одним `hu_code` и несколько строк `production_pallet_lines`.
- Planning не создает несколько `production_pallets` с одинаковым `hu_code`; повторный planning переиспользует уже подготовленный PRD/план.
- Printing печатает одну этикетку на `HuCode`, а не одну этикетку на товар внутри mixed pallet.
- TSD сканирует один HU, показывает состав и после подтверждения пишет `ledger` по всем component-lines.
- После наполнения (`FILLED`) переназначение/удаление HU запрещено. Наличие `PRINTED` или существующего `PLANNED` плана не блокирует append-only добавление недостающих `PLANNED` HU при увеличении `qty_ordered`; флаг `printed` сам по себе не запрещает дополнение плана. Legacy PRD-флаг `Общий HU` не используется в palletized flow. В WPF `Общий HU` в заказе disabled только у строк, по которым есть активные `PLANNED`/`PRINTED`/`FILLED` production pallets; удаление единственной активной паллеты строки снова разблокирует чекбокс этой строки. Для PRD с активными `production_pallets` старые действия `Распределить по HU` и `Назначить HU` скрываются или disabled; legacy PRD без `production_pallets` сохраняет старое поведение.

## Разрешение ранней частичной отгрузки CUSTOMER

- `orders.allow_partial_outbound BOOLEAN NOT NULL DEFAULT FALSE` — persisted server-owned намерение оператора. Effective permission истинно только для CUSTOMER в `IN_PROGRESS`/`ACCEPTED`; для INTERNAL, `DRAFT` и terminal legacy-значение fail-closed отображается как `false`. Permission не меняет readiness или status.
- Единственный пользовательский write-контракт — идемпотентный `PUT /api/orders/{orderId}/partial-outbound-permission` с `allow_partial_outbound` и nullable диагностическим `device_id`. Общий request `PUT /api/orders/{id}` и `WpfUpdateOrderService` permission не принимают, а generic `UPDATE orders` не присваивает клиентское значение, поэтому обычное или конкурентное сохранение карточки не может восстановить stale value. При фактической смене типа `CUSTOMER → INTERNAL` `OrderService.UpdateOrder` отдельным server-side действием в той же транзакции сбрасывает persisted permission в `false`; rollback откатывает оба изменения, а `INTERNAL → CUSTOMER` прежнее значение не восстанавливает.
- Команда разрешена только для сохранённого CUSTOMER в `IN_PROGRESS`/`ACCEPTED`, блокирует строку заказа, перечитывает canonical state и возвращает canonical `order_id`, `order_ref`, `status`, permission и `changed`. WPF применяет только эти permission-related поля; unsaved header/line edits не перечитываются.
- Включение показывает ровно один confirmation; отключение — без диалога. INTERNAL скрывает checkbox; `DRAFT` и terminal отключают его. Любой terminal wire-value отображается как `false`, но клиент не исправляет БД самостоятельно.
- Structured server operation `ORDER_PARTIAL_OUTBOUND_PERMISSION_CHANGE` пишет `ATTEMPT` и `RESULT` через существующий logger, без audit-таблицы. Canonical поля добавляются только после read; malformed JSON не обещает requested value, not-found не обещает `order_ref`. `device_id` недоверенный диагностический идентификатор, `actor_id` отсутствует/null, timestamp серверный. Ошибка diagnostic logger не влияет на бизнес-транзакцию.
- Eligibility: active CUSTOMER, нет active order control, есть хотя бы одна целиком доступная expected HU и истинно хотя бы одно из `is_fully_ready`, permission, open same-order TSD OUTBOUND, partial shipment. SQL optimized и C# fallback реализуют одну truth table раздельно.
- HU в открытом OUTBOUND другого заказа, а также HU в draft OUTBOUND с `order_id = NULL`, исключается до list/details; same-order draft сохраняет continuation, закрытый исторический OUTBOUND не считается foreign conflict. Для mixed `FILLED` HU отсутствие достаточного ledger stock хотя бы одного обязательного компонента исключает HU целиком; persisted pallet state, filling, ledger и close semantics не меняются.
- Canonical `UpdateOrderStatus` одним SQL statement устанавливает `SHIPPED`/`CANCELLED`/`MERGED` и сбрасывает permission в `false`. CHECK `status NOT IN ('SHIPPED','CANCELLED','MERGED') OR allow_partial_outbound = FALSE` закрепляет invariant в БД. Status guard TSD независимо исключает terminal orders.
- После partial close permission автоматически не снимается. Отключение скрывает заказ без draft, но открытый TSD draft продолжает details/scan/complete. Неполный `complete` по-прежнему требует action-level `allow_partial=true`.

## Типы заказов

- `CUSTOMER`
  - контрагент обязателен
  - после полного выпуска PRD переходит в `ACCEPTED` / UI `Готов`
  - заказ закрывается отгрузками OUTBOUND и после полной отгрузки переходит в `SHIPPED` / UI `Выполнен`
  - частичная отгрузка является обычным active-состоянием без отдельного persisted enum: при `shipped_qty > 0` и положительном `shipment_remaining` API/WPF/TSD показывают derived-статус `Частично отгружено`
  - участвует в клиентской отгрузке и в `/api/orders`
  - в picker отгрузки попадает, если `shipment_remaining > 0`; отсутствие предыдущего `OUTBOUND` не мешает выбору
  - в TSD picker отгрузки попадают активные клиентские заказы с хотя бы одной целиком outbound-ready HU, если заказ полностью готов, имеет `allow_partial_outbound = true`, продолжает открытый TSD draft либо уже частично отгружен. Permission не меняет persisted status/readiness и не заменяет подтверждение неполного `complete`
  - TSD при выборе заказа загружает ожидаемые HU через `/api/tsd/outbound/orders/{orderId}`; для mixed pallet ответ содержит `hus[].lines[]` с полным составом HU (`item_id`, `order_line_id`, `item_name`, `qty`), а TSD показывает компоненты под HU-кодом. Scan HU вызывает `/api/tsd/outbound/orders/{orderId}/scan`, создаёт/использует draft `OUTBOUND` и добавляет полный текущий ledger composition HU без записи в `ledger`; усечение через `min(stock, shipment remaining)` не допускается. Повторный scan HU в текущем draft идемпотентен; HU из другого active DRAFT отклоняется, а повторный `complete` закрытого TSD OUT не дублирует `ledger`
  - При `FlowStock:OutboundAutoCloseOnComplete = true` (default) happy-path последний полный scan закрывает `OUTBOUND`; неполный подбор закрывается через `complete` с явным подтверждением `allow_partial=true`, пишет `ledger` только по отсканированным HU и оставляет заказ активным
  - При `OutboundAutoCloseOnComplete = false` TSD `complete` только помечает подбор готовым; проведение — WPF `Close`
  - проведение `OUTBOUND` по заказу допускает только HU, выпущенные/зарезервированные под этот заказ, и отклоняет HU другого заказа
  - следующий WPF/TSD OUT строится только по серверному `remaining_to_ship`; уже закрытые OUT/HU не предлагаются повторно, а draft OUT не уменьшает shipped qty
  - partial OUT разрешает выбрать не все HU заказа, но каждая выбранная physical HU отгружается только целиком: single-item partial возвращает `HU_SHIP_AS_WHOLE_REQUIRED`, mixed incomplete — `MIXED_HU_SHIP_AS_WHOLE_REQUIRED`, до записи в `ledger`. Generic DRAFT может быть временно неполным для recovery/editing, но authoritative Close через единственную canonical policy сравнивает все item/qty/location с текущим положительным ledger composition; production-plan/history не является вторым источником whole-HU состава. Несколько positive locations одной HU дают `HU_MULTIPLE_LOCATIONS`, даже если request не указал location. Текущий WPF editor ещё не мигрирован и может визуально предлагать уменьшение qty explicit HU; это compatibility UI, а не допустимая финальная операция.
  - OUTBOUND без effective normalized HU расходует только HU-less ledger source. `NULL`, blank и whitespace эквивалентны HU-less; requested qty агрегируется по item и explicit location либо по существующему auto-distribution scope до ledger writes. HU-backed остаток не является fallback и при недостаточном HU-less balance требует explicit HU (`HU_EXPLICIT_REQUIRED_FOR_HU_STOCK`). Семантика `WRITE_OFF` не меняется.
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
- `produced_qty` / `Выпущено` и автостатус выполнения считаются по gross positive `ledger` движениям `PRODUCTION_RECEIPT` этого заказа/строки/товара, а не по текущему HU balance после отгрузок. `INTERNAL` заказ считается выполненным, когда gross receipt покрывает `qty_ordered`, без требования `OUTBOUND`.
- Для normal `production_hu_codes` не показываются historical `FILLED` паллеты без положительного текущего ledger balance; для CUSTOMER reserve/read-model stale HU из `order_receipt_plan_lines` с текущим ledger balance `<= 0` не считается произведенным и не показывается как обычный `production_hu_codes`.
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
- Если у типа выключен `enable_hu_distribution`, WPF скрывает поле `max_qty_per_hu`, не валидирует его и при сохранении существующего товара передает прежнее значение без очистки; для нового товара значение может быть `NULL`.
- Для `INTERNAL` legacy `order_receipt_plan_lines` могут сокращаться при уменьшении `qty_ordered`, но обычное сохранение заказа не добавляет новые HU/паллеты при увеличении количества или добавлении строки. Для `CUSTOMER` складской HU-резерв меняется только явным apply-flow, а обычное сохранение заказа не запускает FIFO и не пересобирает `order_receipt_plan_lines`.
- Для `INTERNAL` legacy trim считается только по остатку к выпуску: `max(0, qty_ordered - produced_qty)`; уже выпущенный объем не должен оставаться в `order_receipt_plan_lines` после уменьшения `qty_ordered`.
- TSD не генерирует HU локально: используются только серверные номера HU.

## HU binding CUSTOMER

- Клиентский заказ привязывает HU через явное резервирование, а не через перенос строк INTERNAL → CUSTOMER.
- Основная WPF-точка редких ручных bind/detach/move/replace warehouse HU reservations — `Заказы → Управление HU`. Оператор выбирает номенклатуру и работает с paged/search/filter projection HU и полным списком compatible target lines; staged изменения записываются только по явному `Сохранить` через management `apply-final` contract.
- Полный исходный snapshot строки приходит из unfiltered manage targets и не строится из видимых HU rows. При сохранении каждая affected строка несёт полный expected set и полный final set с overlay только явно staged операций; невидимые, отфильтрованные и находящиеся на других страницах reservations сохраняются.
- `PrePlanCoverageDialog` остаётся отдельным сценарием выбора покрытия перед production planning и не заменяет detach/replace. Inline picker в OrderDetails отсутствует. Legacy `POST /api/orders/{orderId}/hu-reservations/apply`, order-scoped `POST /api/orders/{orderId}/hu-bindings/apply-final`, picker helpers и compatibility windows/endpoints могут сохраняться, но не образуют параллельный основной WPF flow.
- При `HU_BINDING_STALE` WPF показывает оператору `Список HU изменился. Обновите заказ и повторите действие.` и не перезатирает staged state автоматическим refresh.
- Обычное сохранение/закрытие карточки CUSTOMER-заказа не применяет, не пересчитывает и не заменяет HU-резервы. Изменение `order_receipt_plan_lines` допускается только явным HU apply-flow или отдельной maintenance/action-командой.
- Сервер при применении заново валидирует после order/HU locks, что HU имеет полный положительный ledger composition, соответствует `item_id`, не зарезервирован другим активным CUSTOMER-заказом, не находится в другом DRAFT OUTBOUND и его source state не изменился.
- HU/паллета в обычном CUSTOMER flow неделимы: auto-proposal не выбирает HU больше остатка строки, а apply отклоняет выбранный HU, если его количество превышает доступный остаток строки. Частичное покрытие строки CUSTOMER несколькими целыми HU является нормальным состоянием: сервер применяет доступный выбранный резерв и не возвращает warning только из-за того, что `reserved_qty < shipment_remaining_qty`.
- Текущие reservation/binding commands item/line-scoped и не умеют атомарно представить multi-line mixed HU, поэтому mixed HU намеренно исключается из candidate/apply/ready-binding/selected-coverage/legacy produced-reservation flows с `HU_MIXED_NOT_SUPPORTED` либо совместимым existing error. Новый multi-line mixed binding command в этот safety slice не вводится.
- Management read-model читает HU страницами и targets полным unfiltered snapshot; применение выполняется существующим management `apply-final`, а server writer остаётся authoritative и повторно проверяет ownership, eligibility, stale expected set и полное итоговое количество. Order-scoped candidate/apply endpoints сохраняют compatibility contract.
- `LEDGER_STOCK` HU имеет положительный ledger balance, может быть зарезервирован и отгружен; HU с нулевым/отрицательным текущим ledger balance не выбирается как кандидат, не привязывается новым резервом и не проходит outbound close.
- После доказанной whole-HU отгрузки соответствующая строка `order_receipt_plan_lines` может сохраняться как historical binding: она не удаляется автоматически, но при полностью отгруженной строке не добавляется WPF wrapper-ом в selected HU/picker candidates и не отправляется повторным apply. Persisted `FILLED` сам по себе не доказывает открытый PRD; WPF не показывает `PRD не закрыт` без отдельного server fact о статусе документа.
- HU, уже присутствующий в `order_receipt_plan_lines` другого активного `CUSTOMER`-заказа, не является свободным: candidate API исключает его до FIFO-сортировки, а apply отклоняет повторный выбор.
- INTERNAL-заказ при HU binding не меняется: `order_lines.qty_ordered`, `production_pallets.order_id/order_line_id`, `doc_lines` и `marking_order` остаются у источника.
- Legacy endpoints `POST /api/orders/redistribute`, `POST /api/orders/{id}/auto-redistribute-from-internal` и `POST /api/orders/{id}/reserve-produced-hu` могут временно существовать для совместимости и тестов, но WPF CUSTOMER flow не должен их вызывать. В WPF не должно быть двух параллельных сценариев: старый «перенести/перераспределить» и новый «привязать HU».

### Что видит оператор в WPF после save/apply HU

Оператор видит summary по новой модели:
- `Зарезервировано` — выбранные HU, успешно закрепленные за CUSTOMER;
- `Не хватило` — остаток строки CUSTOMER без покрытия;
- Compatibility non-ledger history, если она ещё присутствует в старом ответе, показывается нейтрально как недоступная для новой отгрузки и не получает локально вычисленный статус PRD;
- `Пропущено` — HU, снятые оператором или не прошедшие повторную серверную проверку;
- `Ошибки` — конфликт резерва, несовпадение товара, изменение статуса HU/PRD или недостаточный ledger balance.
- В WPF и PC web частичное или отсутствующее HU-покрытие `CUSTOMER`-строки не показывается модальным warning-окном. Строки заказа подсвечиваются только по серверным `coverage.missing_qty`/`shortage`/`can_ship_now`: мягкий зеленый фон при `missing_qty <= tolerance`, предупреждающий фон при частичном покрытии или отсутствии coverage при положительном остатке; tooltip/title использует наименование товара и количества, а не internal `order_line_id`. Прогресс `Наполнение паллет` остается отдельным индикатором и сам по себе не делает `CUSTOMER`-строку зеленой.
- Для `INTERNAL`-строки мягкий зеленый фон означает, что фактически произведенный/наполненный объем по строке покрывает заказанное количество (`produced_or_filled_qty >= ordered_qty` и `ordered_qty > 0`). Плановые HU, `PLANNED` паллеты и сам факт существования паллетного плана не считаются завершенным выпуском и не делают строку зеленой.

## Правила статусов

- Ручная установка статуса заказа отключена, кроме отмены заказа из WPF (`CANCELLED`).
- Финальный статус в БД остается `SHIPPED`, в UI для всех типов показывается как `Выполнен`.
- Отмена заказа не удаляет его из истории: сервер ставит `CANCELLED` (UI: `Отменён`), очищает `order_receipt_plan_lines` этого заказа и пересчитывает резервы остальных активных клиентских заказов. `ledger`, документы и строки документов не изменяются.
- При отмене `INTERNAL`-заказа сервер может удалить только removable future pallet plan из DRAFT PRD: без ledger, без `PRINTED`/`FILLED` и без component progress. Сначала удаляются связанные `production_pallet_lines`, затем `production_pallets`, после чего server workflow удаляет строки и сам пустой DRAFT PRD; `production_pallet_lines.doc_line_id` никогда не обнуляется и остаётся `NOT NULL`.
- Authoritative operator presentation заказа передаётся аддитивным объектом `order_status_presentation { code, label }`. Persisted/raw `order_status` сохраняется. `status`, `order_status_display` и другие legacy display aliases пока сохраняют прежнюю семантику и строки для compatibility; canonical PC/TSD/WPF не используют их для label или precedence.
- `OrderOperatorStatusResolver` — единственный server-owned resolver presentation. Он получает raw status, order type и уже рассчитанный batch `OrderShipmentProgress`; per-order запросы и N+1 для построения статуса запрещены.
- `order_status_presentation` присутствует на актуальных order surfaces: `/api/orders`, production filling contexts, TSD outbound list/details, marking/order projections и `/api/reports/warehouse-production-state`. Все эти surfaces переиспользуют уже рассчитанные shipment batch metrics.
- Для `CUSTOMER` canonical codes/labels:
  - `IN_PROGRESS` / «В работе»;
  - `ACCEPTED` / «Готов к отгрузке»;
  - `PARTIALLY_SHIPPED` / «Частично отгружен», если active raw status равен `IN_PROGRESS` или `ACCEPTED`, `shipped_qty > tolerance` и `remaining_qty > tolerance`;
  - `SHIPPED` / «Выполнен»;
  - `CANCELLED` / «Отменён»;
  - `DRAFT` / «Черновик», если технический draft вообще видим пользователю.
- Terminal `SHIPPED`/`CANCELLED` имеет precedence над partial overlay; partial overlay имеет precedence над базовыми active `IN_PROGRESS`/`ACCEPTED`. `DRAFT` не является обязательным этапом основной операторской цепочки. Compatibility `MERGED` отображается как `MERGED` / «Объединён» без partial overlay; иной неожиданный status — существующим безопасным compatibility code/label либо `UNKNOWN` / «Неизвестно», также без overlay.
- INTERNAL lifecycle/presentation semantics не изменяются и CUSTOMER partial overlay к ним не применяется.
- Для `CUSTOMER`:
  - после создания/сохранения заказа status автоматически `IN_PROGRESS`; technical `DRAFT`, если он возвращён API, имеет canonical presentation `DRAFT` / «Черновик»
  - после частичного protected coverage статус остается `IN_PROGRESS` (UI: `В работе`)
  - статус `ACCEPTED` (presentation «Готов к отгрузке») выставляется только если по всем строкам с `qty_ordered > tolerance` canonical `missing_qty <= tolerance`: учитываются отгрузка, текущие bound ledger HU, подтвержденные filled production HU с положительным ledger и legacy confirmed non-HU выпуск без двойного счета HU.
  - После успешного закрытия `PRODUCTION_RECEIPT`, изменения заказа, HU binding/detach или отмены будущего pallet plan сервер сразу пересчитывает связанный заказ и переводит его в `ACCEPTED` только при полном canonical protected coverage.
  - `PLANNED`/`PRINTED` pallet plan и `FILLED` без ledger не считаются готовностью к отгрузке; полное `filled_pallet_count / planned_pallet_count` не является основанием для `ACCEPTED`.
  - persisted status при частичной отгрузке остаётся active `IN_PROGRESS`/`ACCEPTED`, а presentation resolver возвращает `PARTIALLY_SHIPPED` / «Частично отгружен»
  - `SHIPPED` выставляется автоматически, если по всем позициям `remaining_qty == 0` по OUTBOUND (заказ закрывается сразу после проведения полного объема)
  - `CANCELLED` исключает заказ из производственной потребности, отгрузки и активного резерва
  - `shipped_at` = MAX(`closed_at`) по закрытым OUTBOUND
- Explicit finalize родительского TSD-наполнения хранится отдельно в `production_filling_completions` и не изменяет правила вычисления `ACCEPTED`/`SHIPPED`.
- Диагностика исторически зависших клиентских статусов:
  - `POST /api/diagnostics/order-status/refresh-fully-shipped` по умолчанию выполняет dry-run и возвращает клиентские заказы, у которых все строки полностью покрыты закрытыми `OUTBOUND`, но persisted status еще не `SHIPPED`;
  - тело `{ "apply": true }` применяет пересчет через серверный `OrderService.RefreshPersistedStatus`, без ручного SQL-update, без изменений `ledger`, `docs` и `doc_lines`;
  - `SHIPPED`, `CANCELLED`, `MERGED` и `DRAFT` не являются кандидатами для repair.
  - `POST /api/diagnostics/order-status/refresh-customer-readiness` по умолчанию выполняет dry-run для активных `CUSTOMER` `IN_PROGRESS`/`ACCEPTED` и возвращает `order_id`, `order_ref`, `old_status`, `new_status`, total ordered/shipped/covered/missing по canonical protected coverage; `{ "apply": true }` применяет только новый статус заказа. Production apply выполняется после backup и проверки dry-run.
- Диагностика возможной переотгрузки:
  - `GET /api/diagnostics/over-shipped-orders` read-only и возвращает `ok`, `items[]` с `qty_ordered`, `shipped_by_api/read_model`, `shipped_by_closed_outbound`, `shipped_by_ledger`, деталями `outbound_docs[]`, `ledger_entries[]` и `recommendation`;
  - расчет закрытых `OUTBOUND` учитывает только активные положительные `doc_lines`, то есть исключает строки, замененные более новой строкой через `replaces_line_id`;
  - endpoint не выполняет repair, не меняет закрытые документы и не пишет в `ledger`.
- Диагностика производственного плана:
  - `GET /api/diagnostics/production-plan-consistency` read-only и возвращает `ok`, `items[]` с `order_qty`, `open_prd_doc_qty`, `closed_prd_doc_qty`, `prd_doc_qty`, `open_pallet_planned_qty`, `pallet_planned_qty`, `pallet_filled_qty`, `ledger_closed_prd_qty`, `ledger_open_prd_qty`, `ledger_prd_qty`, `severity`, `problem_code`, `recommendation`, `pallets[]`, `prd_docs[]`;
  - endpoint нужен для dry-run после deploy перед ручными repair-решениями и не меняет `orders`, `order_lines`, `docs`, `doc_lines`, `production_pallets`, `production_pallet_lines` и `ledger`;
  - активные `doc_lines` для расчёта qty: `qty > tolerance` и строка не заменена более новой (`replaces_line_id`);
- `problem_code`: `ORDER_ZERO_BUT_PALLETS_EXIST`, `PALLETS_EXCEED_ORDER_QTY`, `PRD_LINES_EXCEED_ORDER_QTY`, `FILLED_PALLETS_WITH_DRAFT_PRD`, `FILLED_PALLET_MISSING_LEDGER`, `PARTIAL_PALLET_HAS_LEDGER`, `PARTIAL_PALLET_INVALID_STATUS`, `SHIPPED_CUSTOMER_WITH_OPEN_PRD`, `MERGED_ORDER_WITH_PALLET_PLAN`, `CLOSED_PRD_LEDGER_MISMATCH`;
  - partial component progress при persisted `PLANNED`/`PRINTED` является historical anomaly даже при отсутствии ledger: canonical presentation — `INCONSISTENT`, normal fill его не продолжает; persisted `FILLED` без ledger по-прежнему возвращает `FILLED_PALLET_MISSING_LEDGER`;
  - `PALLETS_EXCEED_ORDER_QTY` и `PRD_LINES_EXCEED_ORDER_QTY` считаются только для open PRD/pallet plan и не применяются к `CUSTOMER/SHIPPED`;
  - `CLOSED_PRD_LEDGER_MISMATCH` сравнивает только `closed_prd_doc_qty` и `ledger_closed_prd_qty`, без legacy mix с `pallet_planned_qty`;
  - `SHIPPED_CUSTOMER_WITH_OPEN_PRD`: `WARNING` для legacy stale open PRD без паллет/ledger; `ERROR` только при open pallets/ledger/fill или рассинхроне open PRD vs pallets; не блокирует Close PRD;
  - согласованный `FILLED_PALLETS_WITH_DRAFT_PRD` — `severity=WARNING`, не блокирует Close PRD;
  - закрытие palletized PRD блокируется только при `severity=ERROR` по текущему PRD: `ORDER_ZERO_BUT_PALLETS_EXIST`, `PALLETS_EXCEED_ORDER_QTY`, `PRD_LINES_EXCEED_ORDER_QTY`, `MERGED_ORDER_WITH_PALLET_PLAN`, а также при рассинхроне строк PRD и `production_pallet_lines` на закрываемом документе;
  - `POST /api/diagnostics/production-plan-consistency/repair` — controlled repair (`mode`, `apply=false` по умолчанию); режим `repair-067-072-mustard` для prod-кейса 067/072; при отмене пустых паллет также создаёт tombstone `doc_line` (`qty=0`, `replaces_line_id`) для связанных активных строк PRD, без изменения `ledger`;
  - историческое состояние `production_pallets.status = FILLED` без положительного receipt-ledger по тому же `PRD + item + HU` считается dirty transition state и не является физическим остатком само по себе. Исправление выполняется только явным maintenance dry-run/apply `POST /api/admin/maintenance/new-ledger-transition/filled-ledger-repair/*` с фильтрами и подтверждением `confirm=APPLY`;
  - repair `filled-ledger-repair/apply` может добавить только положительный `PRODUCTION_RECEIPT` ledger для выбранных `SAFE_TO_BACKFILL` паллет; он не меняет `production_pallets.order_id`, не создает отрицательные движения, не создает заказы и не закрывает CUSTOMER workflow принудительно;
  - stale `INTERNAL` PRD в `DRAFT` можно закрыть этим repair только когда gross positive PRD receipt уже покрывает все строки `INTERNAL`-заказа, нет незаполненных неотмененных паллет, а закрытие не пишет ledger повторно;
- удаление строки `CUSTOMER`-заказа через update/save отменяет связанный будущий `PLANNED`/`PRINTED` pallet plan до удаления `order_lines`; `FILLED`, подтверждённый выпуск, shipped qty и существующие неотгруженные ready-HU bindings блокируют удаление строки;
- исключение из этого правила только явная команда `release-produced-stock`: она разрешает оператору снять CUSTOMER-owner с целых FILLED single-line паллет, удалить строку заказа и перевести HU в свободный складской остаток/read-model без правок `ledger`; WPF предлагает эту команду только после того, как обычный server update вернул `ORDER_LINE_HAS_FILLED_PALLETS`;
- автоперенос с INTERNAL HARD STOP, если у источника есть DRAFT PRD, PRINTED/FILLED паллеты, ledger по PRD или marking_order; строки заказа не меняются;
  - при merge/redistribution обнуление строки INTERNAL не должно оставлять активный pallet plan на старой строке. Если план уже `FILLED`, PRD закрыт или по PRD есть `ledger`, сервер не правит его молча и требует диагностику/manual repair.
- Для `INTERNAL`:
  - ручной созданный/сохраненный заказ обычно стартует как `IN_PROGRESS` (UI: `В работе`)
  - `INTERNAL`-черновик, созданный из `Потребность производства -> Сформировать заказ`, стартует как server-side `DRAFT`
  - в read-model `/api/orders` persisted `DRAFT` внутреннего заказа без активности производства отображается как настоящий черновик `DRAFT` / UI `Черновик`; если есть активные `PLANNED`/`PRINTED`/`FILLED` паллеты, открытый PRD, напечатанная маркировка или частичный проведенный выпуск, read-model возвращает `IN_PROGRESS` / UI `В работе` без backfill persisted `orders.status`
  - частично выполненный заказ из нескольких целиком наполненных паллет либо частичный закрытый `PRODUCTION_RECEIPT` оставляет INTERNAL-заказ в `IN_PROGRESS` (UI: `В работе`); partial component fill одной HU нормальным состоянием не является
  - выпущено полностью или больше -> финальный статус `SHIPPED` / UI `Выполнен`
  - полный закрытый `PRODUCTION_RECEIPT` переводит `DRAFT` сразу в `SHIPPED` / UI `Выполнен`
  - статус и `remaining_qty` пересчитываются как по прямой связи `order_line_id`, так и по `docs.order_id`, если draft/строка PRD была сохранена без `order_line_id`
  - `OUTBOUND` для `INTERNAL` не нужен и не участвует в закрытии такого заказа
  - `shipped_at` используется как дата завершения заказа и вычисляется как MAX(`closed_at`) по закрытым PRD

## Веб-заявки по заказам (PC)

- Веб-интерфейс не изменяет `orders`/`order_lines` напрямую.
- Обычная команда `Новый заказ` в PC Web создаёт заявку `CREATE_ORDER` как для `CUSTOMER`, так и для `INTERNAL`; оба варианта проходят одинаковый pending/request workflow.
- Selector товара нового веб-заказа показывает только позиции с `items.is_active != false`; полный административный `GET /api/items` при этом не меняется.
- При приёме `CREATE_ORDER` server повторно проверяет активность всех товаров до записи `order_requests` и отклоняет inactive item кодом `ITEM_INACTIVE_FOR_ORDER`.
- Заявки сохраняются в `order_requests` со статусом `PENDING`.
- Confirm не доверяет проверке времени создания заявки: server dispatcher повторяет canonical validation. Если товар успел стать inactive, заказ не создаётся, заявка остаётся `PENDING` без `applied_order_id`.
- Отправка заявки требует валидную server-side PC session активного аккаунта с `platform=PC|BOTH`; `created_by_login` и `created_by_device_id` сервер получает из session, а не из request body. `OPERATOR` и `ADMIN` могут создавать оба типа заказа.
- При создании заказа номер подставляется автоматически как следующий числовой `order_ref` по текущей БД.
- Pending-заявки `CREATE_ORDER` резервируют свой `order_ref`: `/api/orders/next-ref` учитывает еще не подтвержденные заявки. Виртуальные строки pending-заявок возвращаются в `/api/orders` только при явном `include_pending_requests=1`, чтобы WPF получал чистый список реальных заказов.
- В выборе контрагента для веб-заказа показываются только клиенты (`role=customer`), поставщики исключены; поле поддерживает поиск по имени/коду с выбором найденного значения.
- В WPF-карточке заказа поле контрагента поддерживает ввод для быстрого поиска по списку клиентов.
- Страница заказов PC Web загружает оба типа (`CUSTOMER` и `INTERNAL`) через `/api/orders?include_internal=1`; selector `Внутренний заказ` в форме `Новый заказ` выбирает тип заявки без изменения authorization policy.
- `Потребность производства → Сформировать заказ` не является этим пользовательским request flow: команда напрямую и server-side создаёт `INTERNAL DRAFT`, не требует pending confirmation и не ограничивается ролью `ADMIN`.
- PC web загружает список заказов лениво: первая загрузка получает 20 строк, затем кнопка `Загрузить еще` догружает следующую пачку. Запросы идут через server-side pagination `/api/orders?include_internal=1&limit=21&offset=N`; 21-я строка используется только как признак следующей страницы. При поиске список сбрасывается и заново грузит первую страницу.
- Для расчета готовности/паллетных индикаторов по загруженной странице PC web использует batch-read строк `GET /api/orders/lines?ids=1,2,3`, а не последовательные `GET /api/orders/{id}/lines` по каждому заказу. Ответ batch endpoint содержит элементы `{ order_id, lines[] }` в порядке запрошенных id; неизвестный `order_id` возвращается с пустым `lines`. Batch endpoint сохраняет быстрый совместимый контракт и не загружает подробную HU-историю. Одиночный `GET /api/orders/{id}/lines` аддитивно возвращает `hu_presentation.production_tasks[]` и `hu_presentation.operational_hus[]`. Legacy `warehouse_hu_rows`, `production_hu_rows`, `shipped_hu_rows`, `fate_*` и nullable `coverage` временно сохраняются для старых consumers, но canonical PC modal их не использует.
- `/api/orders` в no-limit и paged-режимах сортирует реальные заказы одинаково по операторскому приоритету: `DRAFT`, затем `IN_PROGRESS`, затем `ACCEPTED`, затем `SHIPPED`, затем `CANCELLED`; тип `CUSTOMER`/`INTERNAL` не влияет на этот приоритет и не участвует в сортировке. Внутри одного статуса используется `created_at DESC`, затем numeric-aware `order_ref DESC`. При `include_pending_requests=1` наверх поднимаются только синтетические строки pending confirmation (`is_pending_confirmation=true`), а не реальные `INTERNAL`-заказы.
- Для статуса заказа PC web использует только `order_status_presentation`: пользовательский текст — `label`, tone/icon — по `code`. Клиент не вычисляет partial/terminal precedence. Legacy `status`/`order_status_display` сохраняются в wire contract, но не являются источником canonical PC display.
- В списке заказов PC web есть колонка `Наполнение паллет`:
  - для заказов с palletized PRD показывает прогресс `filled_pallet_count / planned_pallet_count` по всем неотмененным паллетам, включая уже закрытые PRD-документы;
  - считаются только production pallets/components, привязанные к актуальным `order_lines` этого же заказа с совпадающим `item_id`; active orphan pallets с `order_line_id = NULL` или удаленной строкой заказа не участвуют в агрегате;
  - если DTO списка временно не содержит агрегат, PC web добирает строки заказа и заполняет колонку по тем же pallet-line метрикам, что используются в раскрытии заказа;
  - подсказка индикатора показывает количество паллет, а не штуки товара; при полном наполнении индикатор icon-only с подсказкой, при частичном наполнении текстовый прогресс остается видимым;
  - для клиентского заказа с резервом HU/паллет под заказ подсказка показывает готовность к отгрузке в формате `К отгрузке готово X из Y паллет по заказу`;
  - для заказов с потребностью в плане без созданного плана показывает `План не сформирован`; для заказов без паллетного workflow индикатор не выводится.
- В списке заказов PC web есть компактный icon-only индикатор `ЧЗ`:
  - красная иконка с подсказкой `Маркировка не проведена`, если effective-статус `NOT_APPLIED`;
  - зеленая иконка с подсказкой `Маркировка проведена`, если effective-статус `APPLIED`.
  - Индикатор использует только `marking_effective_status` и `marking_status_display` из `/api/orders`, не пересчитывая ЧЗ по строкам на frontend.
  - Если effective-статус `NOT_REQUIRED`, индикатор не выводится.
- В строках заказа в WPF и PC web отображаются наименование, SKU/штрихкод и GTIN. Модальное окно деталей заказа PC web:
  - Основные колонки: `Товар`, `SKU / ШК`, `GTIN`, `Заказано`, `Наполнение`; колонки `В наличии`, `Назначение`, `Отгружено`/`Выпущено` в основной строке не выводятся. Верхняя часть окна не дублирует статус заказа, паллет и маркировки отдельным рядом icon-only индикаторов; построчный индикатор `Наполнение` сохраняется в контексте конкретной строки.
  - Единственный блок `Паллеты по товару` визуально объединяет `hu_presentation.operational_hus` и `hu_presentation.production_tasks` в одну таблицу `HU | Кол-во | Состояние | Локация`: сначала в серверном порядке идут operational HU, затем production tasks. Wire-коллекции остаются раздельными; клиент не дедуплицирует их и не вводит собственную HU-классификацию. Shipped HU остаётся в общей таблице со статусом `Отгружен`, а production task получает локацию `—`. Если обе коллекции пусты, внутри блока показывается `Паллеты отсутствуют`.
  - Состояние каждой строки берётся только из canonical `state.label`; для production task это `AWAITING_FILL` / «Ожидает наполнения». Отдельный production block, raw `PRD`, `Plan/Filled`, «Привязка», «Движение HU», legacy fate/status и архитектурные термины в main UI не показываются.
  - Server `coverage.missing_qty`/shortage показывается компактно отдельно от HU state; legacy coverage без HU не создаёт HU presentation.
  - В раскрытии строки операторская HU-секция называется `Паллеты по товару`. Ниже располагается визуально вторичный блок `Итог` с карточками `Заказано`, `Выпущено`, `Не хватает`: он использует более спокойный фон, тонкую рамку и меньший визуальный вес текста, чем таблица паллет, без изменения серверных значений coverage.
  - Раскрытие не содержит команд редактирования/rebind и не вычисляет `covered_qty`/`missing_qty` на frontend; если точный `coverage` отсутствует, показываются HU-детали и существующий серверный `shortage`.
  - Layout: таблица динамически занимает всю доступную ширину модального окна, распределяет ширину между колонками, на узком экране прокручивается внутри окна.
  - Прогресс наполнения строки берется из серверных `filled_pallet_count/planned_pallet_count` и `pallet_filled_qty/pallet_planned_qty`; полностью наполненные строки могут показывать только icon-only индикатор с подсказкой, частично наполненные сохраняют видимый текстовый прогресс.
  - Зеленая подсветка строки `INTERNAL` включается только когда фактически произведенный/наполненный объем покрывает заказанное количество; частичное наполнение или один только `PLANNED`-план не подсвечиваются зеленым. Зеленая подсветка строки `CUSTOMER` включается только по canonical `coverage.missing_qty <= tolerance` или совместимым серверным `shortage/can_ship_now`; pallet fill progress не участвует в shipment readiness.
- Для `CUSTOMER` заказа факт отгрузки имеет приоритет над stale pallet plan: если по строке `qty_shipped + tolerance >= qty_ordered`, строка считается закрытой по отгрузке, индикатор `Наполнено X/Y` не показывается как незавершенная потребность, но в списке заказов и в деталях строки отображается зеленая completed-иконка (`pallet_fill_show_completed_icon` / `show_pallet_completed_icon`). Для полностью отгруженного клиентского заказа `pallet_plan_status` и колонка `Наполнение паллет` не должны понижать статус или показывать незавершенное наполнение из-за оставшихся `production_pallets`; незавершенный PRD/pallet plan при этом не удаляется автоматически и может диагностироваться отдельно.
- В строке товара доступен ввод GTIN/SKU/названия с фильтрацией с первого символа и выбором мышью из выпадающего списка подсказок.
- WPF колокольчик открывает `Центр событий` с отдельными вкладками `Требуют действия` и `Журнал событий`; заявки и persisted business notifications не смешиваются в одном списке.
- Capability `ManagePendingRequests` принадлежит роли PC `ADMIN` и относится ко всем поддерживаемым типам `order_requests`, а не только к `CREATE_ORDER`. `OPERATOR` видит ожидание read-only. Trusted WPF имеет ту же возможность по machine credential.
- В списке заказов PC Web строка с `is_pending_confirmation=true`, `management_supported=true` и валидным `request_id` показывает `ADMIN` inline-команды `Подтвердить` и `Отклонить`; для `OPERATOR`, canonical-заказов и неподдерживаемых/не-pending строк команды отсутствуют. После confirm/reject, а также после `409`, список перечитывается с сервера: подтверждённая synthetic-строка заменяется canonical-заказом с его актуальным статусом, отклонённая исчезает из списка pending-заявок; более старый параллельный ответ загрузки не может перезаписать это состояние.
- Business/application error inline confirm/reject показывается в фирменном warning-dialog с заголовком `Не удалось подтвердить заказ` либо `Не удалось отклонить заявку` и неизменённым человекочитаемым сообщением сервера. Ошибка не запускает refresh или optimistic mutation: pending-строка и доступные действия сохраняются. Success, loading, обычный refresh, `409` с перечитыванием состояния, `401` session invalidation и `403` capability refresh такой dialog не показывают; toolbar не дублирует длинный текст ошибки.
- Server dispatcher явно поддерживает `CREATE_ORDER` и существующие `SET_ORDER_STATUS`; неизвестный/неподдерживаемый тип возвращает `422` и остаётся `PENDING`. Отдельный PC-экран для типа, которого нет в текущей PC read-model, не создаётся.
- Confirm/reject выполняется одним server endpoint по `request_id`. `SELECT ... FOR UPDATE`, canonical business mutation, `APPROVED/REJECTED`, `resolved_*` и `applied_order_id` используют один transaction-scoped `IDataStore`, одно соединение и один commit. Конкурирующий проигравший запрос получает `409` до второго business result.
- Для `CREATE_ORDER` dispatcher использует transaction-aware canonical create с начальным статусом `IN_PROGRESS`; для `SET_ORDER_STATUS` сохраняется текущая manual policy — только переход в `CANCELLED`. Business validation error оставляет заявку `PENDING` и полностью откатывает canonical mutation.
- Reject не выполняет business mutation, сохраняет `applied_order_id = NULL` и server-generated note.
- Legacy `/api/orders/requests/{id}/resolve` отсутствует. Прямой canonical `POST /api/orders` и account administration разрешены только trusted WPF machine key, поэтому PC Web не может обойти pending workflow.
- Computed `READY_HU_BINDING_AVAILABLE` остаётся compatibility/read-only server signal при `hu_count > 0`, но не является отдельной WPF-точкой изменения reservations и не поддерживает Confirm/Reject/Dismiss. Ручные bind/detach/move/replace выполняются через `Заказы → Управление HU`; filter/search/pagination этого окна presentation-only.
- Для глобальной привязки compatible-дефицит строки равен `max(0, qty_ordered - confirmed_produced_qty - open_production_pallet_qty - current_bound_ready_hu_qty)` и не ограничивается shipment remaining. Подтвержденный выпуск включает только ledger-подтвержденные `FILLED production_pallets` и строки закрытого legacy PRD; открытые DRAFT `doc_lines` сами по себе выпуском не являются. Открытое производственное покрытие включает только `PLANNED`/`PRINTED` паллеты незакрытого PRD, включая состав mixed pallet по `order_line_id`; `FILLED`, `CANCELLED` и паллеты закрытого PRD в него не входят. Свободный ready HU совместим со строкой только если его целое количество не превышает этот положительный дефицит с учетом tolerance.
- Для заявок заказа в WPF доступно модальное окно подробностей перед подтверждением.
- Создание новых запросов ручной смены статуса (`SET_ORDER_STATUS`) отключено; уже существующие заявки dispatcher обрабатывает только для canonical перехода в `CANCELLED`.
