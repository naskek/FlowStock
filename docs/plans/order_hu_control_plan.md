# Контроль готовых заказов: план реализации

## 1. Краткое резюме

Функция "Контроль готовых заказов" нужна для физической проверки всех HU, подготовленных к отгрузке по одному или нескольким клиентским заказам, до начала TSD-отгрузки.

Рекомендуемый MVP:

- создать отдельную предметную модель `order_control_*`, не активируя архивный Warehouse Task Board;
- строить ожидаемый список HU через общий outbound-ready read model на базе `CustomerOutboundBoundHuService.GetUnshippedOutboundHuLines(...)` и SQL-эквивалента `PostgresDataStore.GetTsdOutboundOrderRows()`;
- фиксировать неизменяемый snapshot HU при создании задания;
- считать mixed-HU одной физической HU, а состав хранить и показывать отдельно;
- завершать контроль только после проверки всех ожидаемых HU;
- блокировать outbound по заказу, пока по нему есть активный контроль;
- не создавать документы, не писать `ledger`, не менять `orders.status`.

Этот документ описывает план. Production-код, миграции и существующие runtime-файлы в рамках текущей задачи не меняются.

## 2. Подтвержденное текущее состояние репозитория

### Источники истины

- `docs/spec.md` задает инварианты: остатки считаются только из `ledger`, документы влияют на остатки только через `Close`, TSD/WPF не являются источником истины для production quantities, CUSTOMER HU reservation и TSD outbound.
- `docs/spec_orders.md` задает правила `CUSTOMER`-заказов, готовности к отгрузке, mixed pallet, production pallet plan и explicit HU binding.
- `docs/archive/tasks/spec_tasks.md` помечен как deprecated / removal candidate. Его можно использовать только как технический контекст по уже существующим таблицам `warehouse_tasks`, но не как актуальный контракт normal TSD flow.

### Актуальная outbound-логика

- `CustomerOutboundBoundHuService.GetUnshippedOutboundHuLines(IDataStore store, long orderId)` объединяет:
  - warehouse-bound HU из `order_receipt_plan_lines`;
  - `FILLED production_pallets` активного `CUSTOMER`-заказа;
  - только HU с положительным физическим остатком по `ledger`;
  - только неотгруженный остаток по `order_line_id` и HU.
- `OutboundPickingService.BuildExpectedHus(Order order)` группирует эти строки по `HuCode`, поэтому mixed-HU отображается как одна HU с несколькими component lines.
- `OutboundPickingService.Scan(...)` уже проверяет:
  - пустой scan (`HU_REQUIRED`);
  - чужую HU (`HU_NOT_EXPECTED`);
  - bound HU без физического stock (`HU_BOUND_WITHOUT_STOCK`);
  - HU без physical ledger stock (`HU_NO_PHYSICAL_STOCK`);
  - HU в другом draft OUTBOUND (`HU_PICKED_IN_OTHER_OUTBOUND`);
  - повторный scan в текущем draft как идемпотентный успех;
  - уже закрытую отгрузку как `HU_ALREADY_SHIPPED`;
  - shipment remaining по строкам заказа.
- `TsdOutboundPickingEndpoints` публикует текущий TSD outbound API:
  - `GET /api/tsd/outbound/orders`;
  - `GET /api/tsd/outbound/orders/{orderId}`;
  - `POST /api/tsd/outbound/orders/{orderId}/scan`;
  - `POST /api/tsd/outbound/orders/{orderId}/complete`.
- `PostgresDataStore.GetTsdOutboundOrderRows()` реализует optimized list read model для `/api/tsd/outbound/orders`: один SQL command, `COUNT(DISTINCT hu_code)`, shipped/remaining, picked HU, fingerprint. Для новой функции нельзя возвращаться к N+1 по `GetDetails()`/`BuildExpectedHus()` на list path.

### Warehouse Task Board

- Миграция `V0021__warehouse_tasks.sql` создает `warehouse_action_bundles`, `warehouse_action_lines`, `warehouse_tasks`, `warehouse_task_lines`, `warehouse_task_events`.
- `V0022__disable_warehouse_tasks_ui.sql` выключает TSD client block `tsd_warehouse_tasks`.
- WPF вкладка `WarehouseTasksTab` скрыта через `ExperimentalFeatureFlags.WarehouseTasksEnabled = false`.
- `WarehouseTaskExecutionService` ориентирован на одно активное scan-line задание MOVE_HU: сначала HU, затем location. Он не подходит напрямую для много-HU контроля заказов без существенного расширения семантики.
- `WarehouseActionBundleService` связан с action pipeline, draft documents, `MOVE_HU` и `ADOPT_PALLET_PLAN`. Контроль готовых заказов не является складским движением и не должен проводить документы.

### WPF и TSD паттерны

- WPF экран "Заказы" живет в `MainWindow.xaml` / `MainWindow.xaml.cs`, использует `OrdersGrid`, toolbar-кнопки и `DataGrid` с badge-like статусами.
- Сейчас `OrdersGrid` использует одиночный выбор. Для контроля нужен `SelectionMode=Extended`.
- TSD outbound UI уже имеет подходящие паттерны в `apps/android/tsd/app.js` и `storage.js`: список карточек, detail screen, hidden scan input, `focusScan()`, live refresh, structured error mapper, красно-зеленые HU rows.
- Client blocks сейчас описаны в `ClientBlockCatalog`; для новой функции нужен отдельный ключ `tsd_order_control`, не `tsd_warehouse_tasks`.

## 3. Бизнес-сценарий

### Создание задания в WPF

1. Пользователь открывает вкладку "Заказы".
2. Выбирает один или несколько `CUSTOMER`-заказов.
3. Нажимает "Создать контроль".
4. WPF вызывает preview endpoint и показывает диалог:
   - список заказов;
   - количество уникальных HU;
   - товары и количества;
   - mixed-HU и их состав;
   - предупреждения;
   - причины блокировки.
5. Пользователь подтверждает создание.
6. Сервер в одной транзакции повторно валидирует заказы, формирует snapshot HU и создает контрольное задание.
7. WPF перечитывает список заказов и список заданий.

### Выполнение на TSD

1. Пользователь открывает отдельный пункт меню "Контроль заказов".
2. Видит список активных заданий.
3. Открывает задание и нажимает/автоматически выполняет `start`.
4. Сканирует HU в произвольном порядке.
5. Сервер отмечает HU как `CHECKED` или возвращает структурированную ошибку.
6. TSD показывает общий прогресс и список HU:
   - `PENDING` - красный;
   - `CHECKED` - зеленый;
   - `DISCREPANCY` - красный warning/error;
   - mixed-HU раскрывает component lines.
7. Кнопка "Завершить" доступна только при `checked_hu_count == expected_hu_count` и отсутствии blocking discrepancies.
8. `complete` переводит task в `COMPLETED`; документов и ledger-движений не создается.

### Отмена

- WPF может отменить `NEW` или `IN_EXECUTION` задание.
- Отмена не меняет заказы, документы, HU reservation и `ledger`.
- Если TSD находится в отмененном задании, live refresh показывает "Задание отменено" и запрещает scan/complete.

## 4. Допустимость заказов

Заказ допустим для контроля, если все условия истинны:

- `order_type = CUSTOMER`;
- статус не `DRAFT`, не `CANCELLED`, не `MERGED`, не `SHIPPED`;
- для MVP основной happy path - `ACCEPTED`; `IN_PROGRESS` можно разрешить только если текущая outbound-ready логика уже считает заказ доступным к подбору, как `OutboundPickingService.IsCustomerOrderReadyForPicking`;
- есть хотя бы одна ожидаемая HU из общего outbound-ready read model;
- заказ не находится в другом активном контроле (`NEW`, `IN_EXECUTION`);
- по заказу нет draft/TSD OUTBOUND с уже подобранными HU;
- заказ не полностью отгружен по `shipment_remaining`;
- expected HU имеют положительный физический остаток по `ledger`.

Недопустимые состояния возвращаются в preview как `blocking_errors[]`; create endpoint обязан повторить проверки в транзакции.

## 5. Архитектурные варианты

### Вариант 1. Расширить Warehouse Task Board целиком

Использовать `warehouse_action_bundles`, `warehouse_action_lines`, `warehouse_tasks`, `warehouse_task_lines`, `warehouse_task_events`.

Преимущества:

- таблицы и часть store/API уже существуют;
- есть базовые lifecycle-тесты;
- есть похожая идея "TSD scan != ledger movement".

Недостатки:

- модуль deprecated / removal candidate;
- UI скрыт и не должен задавать normal TSD flow;
- `WarehouseActionBundleService` завязан на action pipeline и MOVE/ADOPT;
- `WarehouseTaskExecutionService` рассчитан на узкий сценарий одной HU/location line, а контроль заказов требует много HU, multi-order, snapshot, расхождения и progress;
- велик риск случайно активировать архивную WPF/TSD вкладку;
- придется вводить `ORDER_CONTROL` в старый action vocabulary, хотя это не складское движение.

Миграции:

- потребуются nullable `bundle_id`/`action_line_id` или искусственные action lines;
- потребуется payload для snapshot состава HU;
- потребуется расширение статусов и индексов.

Оценка: не рекомендовано для MVP.

### Вариант 2. Использовать только нижний слой warehouse tasks

Переиспользовать `warehouse_tasks`, `warehouse_task_lines`, `warehouse_task_events`, не используя bundle/action pipeline.

Преимущества:

- меньше таблиц, чем в варианте 3;
- похожая структура task/lines/events;
- можно сохранить идею TSD task lifecycle.

Недостатки:

- текущая схема требует `bundle_id` и `action_line_id NOT NULL`;
- `warehouse_task_lines` не хранит `order_line_id`, item snapshot detail, source type, expected location code, mixed composition, fingerprint и discrepancy reasons в достаточном виде;
- нужно разделять task execution MOVE_HU и ORDER_CONTROL внутри одного сервиса;
- старые endpoints `/api/tsd/tasks` останутся рядом с новым flow и могут спутать client blocks;
- миграции будут не меньше, чем отдельная модель, но с худшей предметной ясностью.

Миграции:

- изменить nullable constraints;
- добавить task type;
- добавить snapshot payload и индексы активных order/HU;
- расширить execution service.

Оценка: возможный компромисс, но не рекомендован для MVP из-за смешивания deprecated слоя с новым production flow.

### Вариант 3. Отдельная модель order control

Создать `order_control_tasks`, `order_control_task_orders`, `order_control_task_hus`, `order_control_task_hu_lines`, `order_control_events`.

Преимущества:

- чистая предметная модель: контроль готовых заказов не притворяется складским движением;
- нет риска активации архивного Warehouse Task Board;
- проще хранить immutable HU snapshot, mixed composition, progress и discrepancies;
- проще блокировать outbound по active order control;
- API names соответствуют бизнес-функции;
- тесты можно писать напрямую под бизнес-инварианты.

Недостатки:

- новые таблицы и store methods;
- нужно создать новый сервис и endpoints;
- часть generic task/event логики будет похожа на warehouse tasks.

Миграции:

- новые таблицы, индексы, FK, active partial indexes;
- новый client block `tsd_order_control`.

Оценка: рекомендованный вариант для MVP.

## 6. Рекомендованная архитектура

### Компоненты

- `OrderControlExpectedHuReadModel`:
  - строит expected HU для одного или нескольких заказов;
  - использует общую outbound-ready логику;
  - в production store должен иметь batch/SQL path без N+1.
- `OrderControlService`:
  - preview;
  - create;
  - list/detail/progress;
  - start/scan/complete/cancel;
  - outbound lock checks.
- `OrderControlEndpoints`:
  - WPF endpoints `/api/order-control/*`;
  - TSD endpoints `/api/tsd/order-control/*`.
- `IOrderControlStore` / методы в `IDataStore`:
  - CRUD task/order/HU/event;
  - active conflict checks by order and HU;
  - idempotency by request/event id;
  - row-level locking for scan/complete.
- WPF:
  - `WpfOrderControlApiService`;
  - button and extended selection in Orders tab;
  - preview dialog;
  - list/detail window or tab.
- TSD:
  - storage API functions;
  - routes `/order-control` and `/order-control/{taskId}`;
  - render/wire methods modeled after outbound picking;
  - error mapper preserving visible red error after refresh.

### Source of expected HU

Нельзя реализовывать второй независимый алгоритм. Общий read model должен возвращать:

- `order_id`, `order_ref`;
- `order_line_id`;
- `item_id`, `item_name`;
- `normalized_hu`;
- `qty`;
- `from_location_id`, `from_location_code`;
- `source_type`: `WAREHOUSE_BOUND`, `FILLED_PRODUCTION_PALLET`, `LEGACY_CUSTOMER_PRD`;
- `source_ref`: optional PRD/doc/pallet id;
- `is_mixed_hu`;
- component lines.

Правила включения:

- warehouse-bound HU входят из `order_receipt_plan_lines`, если есть положительный ledger balance по `item_id + hu`;
- FILLED production pallets входят только если `status = FILLED` и есть положительный receipt ledger/stock по HU;
- PLANNED/PRINTED/partial mixed HU не входят;
- уже отгруженные HU исключаются по closed OUTBOUND;
- частично отгруженный заказ получает только оставшиеся HU/quantities;
- mixed-HU входит один раз по normalized HU, даже если в ней несколько товаров.

## 7. Модель данных

### `order_control_tasks`

| Поле | Тип | Null | Описание |
|---|---|---|---|
| `id` | BIGSERIAL PK | no | Идентификатор |
| `task_ref` | TEXT | no | `CTRL-YYYY-NNNNNN` |
| `status` | TEXT | no | `NEW`, `IN_EXECUTION`, `COMPLETED`, `CANCELLED` |
| `expected_hu_count` | INTEGER | no | Количество уникальных HU |
| `checked_hu_count` | INTEGER | no | Денормализованный progress |
| `discrepancy_count` | INTEGER | no | Количество расхождений |
| `expected_fingerprint` | TEXT | no | SHA-256 snapshot expected HU/source |
| `created_at` | TEXT | no | UTC ISO |
| `created_by` | TEXT | yes | WPF user |
| `started_at` | TEXT | yes | Первый start |
| `started_by_device_id` | TEXT | yes | TSD |
| `completed_at` | TEXT | yes | Complete |
| `completed_by_device_id` | TEXT | yes | TSD |
| `cancelled_at` | TEXT | yes | Cancel |
| `cancelled_by` | TEXT | yes | WPF user |
| `cancel_reason` | TEXT | yes | Причина отмены |
| `comment` | TEXT | yes | Комментарий |
| `created_snapshot_json` | JSONB | no | Краткий immutable snapshot preview |

Индексы:

- `ux_order_control_tasks_ref` по `UPPER(BTRIM(task_ref))`;
- `ix_order_control_tasks_status_created` по `(status, created_at DESC)`;
- partial `ix_order_control_tasks_active` where status in `NEW`, `IN_EXECUTION`.

### `order_control_task_orders`

| Поле | Тип | Null | Описание |
|---|---|---|---|
| `id` | BIGSERIAL PK | no | |
| `task_id` | BIGINT FK | no | `order_control_tasks(id)` |
| `order_id` | BIGINT FK | no | `orders(id)` |
| `order_ref_snapshot` | TEXT | no | Номер на момент создания |
| `partner_name_snapshot` | TEXT | yes | Клиент |
| `status_snapshot` | TEXT | no | Статус заказа |
| `shipment_remaining_snapshot` | DOUBLE PRECISION | no | Остаток к отгрузке |

Индексы:

- `ux_order_control_task_orders_task_order` по `(task_id, order_id)`;
- partial unique active order check через SQL/partial index: один `order_id` не может быть в активных task.

PostgreSQL partial unique через join невозможен напрямую, поэтому для production лучше:

- либо хранить `active_until`/`is_active` в `order_control_task_orders`;
- либо проверять в транзакции `SELECT ... FOR UPDATE` по active tasks.

Для MVP рекомендуется `is_active BOOLEAN NOT NULL DEFAULT TRUE`, который сбрасывается при `COMPLETED`/`CANCELLED`, и unique index:

```sql
CREATE UNIQUE INDEX ux_order_control_active_order
ON order_control_task_orders(order_id)
WHERE is_active;
```

### `order_control_task_hus`

| Поле | Тип | Null | Описание |
|---|---|---|---|
| `id` | BIGSERIAL PK | no | |
| `task_id` | BIGINT FK | no | |
| `line_no` | INTEGER | no | Порядок |
| `hu_code` | TEXT | no | Original display code |
| `normalized_hu_code` | TEXT | no | `UPPER(BTRIM(...))` |
| `primary_order_id` | BIGINT FK | yes | Для сортировки/группировки |
| `status` | TEXT | no | `PENDING`, `CHECKED`, `DISCREPANCY`, `CANCELLED` |
| `expected_qty_total` | DOUBLE PRECISION | no | Сумма component qty |
| `expected_location_id` | BIGINT FK | yes | Если однозначно |
| `expected_location_code` | TEXT | yes | Snapshot |
| `is_mixed_hu` | BOOLEAN | no | `true` если component count > 1 |
| `source_types` | TEXT | no | CSV/JSON кратко |
| `snapshot_hash` | TEXT | no | Hash component snapshot |
| `checked_at` | TEXT | yes | |
| `checked_by_device_id` | TEXT | yes | |
| `checked_by_operator_id` | TEXT | yes | |
| `last_error_code` | TEXT | yes | |
| `last_error_message` | TEXT | yes | |

Индексы:

- `ux_order_control_task_hus_task_hu` по `(task_id, normalized_hu_code)`;
- `ix_order_control_task_hus_hu_active` по `normalized_hu_code` plus `is_active`;
- `ix_order_control_task_hus_status` по `(task_id, status)`.

### `order_control_task_hu_lines`

| Поле | Тип | Null | Описание |
|---|---|---|---|
| `id` | BIGSERIAL PK | no | |
| `task_hu_id` | BIGINT FK | no | |
| `task_id` | BIGINT FK | no | Денормализация для query |
| `order_id` | BIGINT FK | no | |
| `order_ref_snapshot` | TEXT | no | |
| `order_line_id` | BIGINT FK | no | |
| `item_id` | BIGINT FK | no | |
| `item_name_snapshot` | TEXT | no | |
| `expected_qty` | DOUBLE PRECISION | no | |
| `expected_location_id` | BIGINT FK | yes | |
| `expected_location_code` | TEXT | yes | |
| `source_type` | TEXT | no | `WAREHOUSE_BOUND`, `FILLED_PRODUCTION_PALLET`, `LEGACY_CUSTOMER_PRD` |
| `source_id` | BIGINT | yes | pallet/doc/plan id if available |
| `source_ref` | TEXT | yes | Display |

Индексы:

- `ix_order_control_task_hu_lines_task_order` по `(task_id, order_id)`;
- `ix_order_control_task_hu_lines_order_line` по `(order_line_id)`.

### `order_control_events`

| Поле | Тип | Null | Описание |
|---|---|---|---|
| `id` | BIGSERIAL PK | no | |
| `task_id` | BIGINT FK | no | |
| `task_hu_id` | BIGINT FK | yes | |
| `event_type` | TEXT | no | `CREATE`, `START`, `SCAN_OK`, `SCAN_DUPLICATE`, `SCAN_REJECTED`, `DISCREPANCY`, `COMPLETE`, `CANCEL` |
| `event_at` | TEXT | no | UTC ISO |
| `device_id` | TEXT | yes | |
| `operator_id` | TEXT | yes | |
| `request_id` | TEXT | yes | Idempotency key |
| `hu_code` | TEXT | yes | |
| `error_code` | TEXT | yes | |
| `message` | TEXT | yes | |
| `payload_json` | JSONB | no | Context |

Индексы:

- `ix_order_control_events_task_at` по `(task_id, event_at DESC)`;
- unique `ux_order_control_events_request` по `(task_id, request_id)` where `request_id IS NOT NULL`.

## 8. API-контракты

Все ответы ошибок используют форму:

```json
{
  "ok": false,
  "error": "ERROR_CODE",
  "message": "Человекочитаемое сообщение",
  "details": {}
}
```

### `POST /api/order-control/preview`

Request:

```json
{
  "order_ids": [80, 81]
}
```

Success:

```json
{
  "ok": true,
  "orders": [
    {
      "order_id": 80,
      "order_ref": "080",
      "status": "ACCEPTED",
      "status_display": "Готов",
      "eligible": true,
      "expected_hu_count": 12,
      "shipment_remaining_qty": 1890
    }
  ],
  "summary": {
    "order_count": 2,
    "expected_hu_count": 18,
    "item_count": 6,
    "fingerprint": "sha256..."
  },
  "hus": [
    {
      "hu_code": "HU-0000506",
      "order_refs": ["080"],
      "qty_total": 1890,
      "location_code": "FG-01",
      "is_mixed_hu": true,
      "source_types": ["FILLED_PRODUCTION_PALLET"],
      "lines": [
        {
          "order_id": 80,
          "order_line_id": 208,
          "item_id": 30,
          "item_name": "Горчица",
          "qty": 1200
        }
      ]
    }
  ],
  "warnings": [],
  "blocking_errors": []
}
```

Idempotency: read-only, no key required.

Transaction: no write transaction; query must use a consistent read snapshot where practical.

### `POST /api/order-control/tasks`

Request:

```json
{
  "order_ids": [80, 81],
  "created_by": "semion",
  "comment": "Перед отгрузкой"
}
```

Success:

```json
{
  "ok": true,
  "task": {
    "id": 123,
    "task_ref": "CTRL-2026-000123",
    "status": "NEW",
    "expected_hu_count": 18,
    "checked_hu_count": 0,
    "discrepancy_count": 0,
    "created_at": "2026-06-24T10:15:00Z"
  }
}
```

Errors:

- `ORDER_NOT_ELIGIBLE`;
- `NO_EXPECTED_HU`;
- `ACTIVE_CONTROL_EXISTS`;
- `OUTBOUND_IN_PROGRESS`;
- `EXPECTED_SET_CHANGED`.

Idempotency: optional `request_id` in future. MVP can require client-side disable and server conflict checks.

Transaction:

- lock selected orders or active-control rows;
- rebuild expected HU;
- check active control and outbound conflicts;
- insert task/orders/HU/lines/events;
- commit.

### `GET /api/order-control/tasks`

Query:

- `status=NEW|IN_EXECUTION|COMPLETED|CANCELLED`;
- `active=1`;
- `order_id=80`.

Success:

```json
{
  "ok": true,
  "tasks": [
    {
      "id": 123,
      "task_ref": "CTRL-2026-000123",
      "status": "IN_EXECUTION",
      "order_refs": ["080", "081"],
      "expected_hu_count": 18,
      "checked_hu_count": 12,
      "discrepancy_count": 0,
      "created_by": "semion",
      "started_by_device_id": "TSD-01",
      "created_at": "2026-06-24T10:15:00Z",
      "completed_at": null
    }
  ]
}
```

Idempotency: read-only.

### `GET /api/order-control/tasks/{id}`

Success:

```json
{
  "ok": true,
  "task": {
    "id": 123,
    "task_ref": "CTRL-2026-000123",
    "status": "IN_EXECUTION",
    "expected_hu_count": 18,
    "checked_hu_count": 12,
    "discrepancy_count": 0,
    "created_at": "2026-06-24T10:15:00Z"
  },
  "orders": [],
  "hus": [],
  "events": []
}
```

### `GET /api/order-control/tasks/{id}/progress`

Возвращает короткий progress для WPF/TSD live refresh:

```json
{
  "ok": true,
  "task_id": 123,
  "status": "IN_EXECUTION",
  "expected_hu_count": 18,
  "checked_hu_count": 12,
  "discrepancy_count": 0,
  "operation_fingerprint": "sha256..."
}
```

### `POST /api/order-control/tasks/{id}/cancel`

Request:

```json
{
  "cancelled_by": "semion",
  "reason": "Отгрузка перенесена"
}
```

Success:

```json
{
  "ok": true,
  "task_id": 123,
  "status": "CANCELLED"
}
```

Transaction:

- lock task;
- reject if already `COMPLETED`;
- set task and active order rows inactive;
- append event.

### `GET /api/tsd/order-control/tasks`

Success:

```json
{
  "ok": true,
  "tasks": [
    {
      "id": 123,
      "task_ref": "CTRL-2026-000123",
      "status": "IN_EXECUTION",
      "order_refs": ["080", "081"],
      "expected_hu_count": 18,
      "checked_hu_count": 12,
      "created_at": "2026-06-24T10:15:00Z"
    }
  ]
}
```

### `GET /api/tsd/order-control/tasks/{id}`

Success:

```json
{
  "ok": true,
  "task": {
    "id": 123,
    "task_ref": "CTRL-2026-000123",
    "status": "IN_EXECUTION",
    "expected_hu_count": 18,
    "checked_hu_count": 12,
    "can_complete": false
  },
  "hus": [
    {
      "hu_id": 1001,
      "hu_code": "HU-0000506",
      "status": "CHECKED",
      "qty_total": 1890,
      "location_code": "FG-01",
      "is_mixed_hu": true,
      "checked_at": "2026-06-24T10:20:00Z",
      "checked_by_device_id": "TSD-01",
      "lines": []
    }
  ],
  "last_event": {}
}
```

### `POST /api/tsd/order-control/tasks/{id}/start`

Request:

```json
{
  "device_id": "TSD-01",
  "operator_id": "user"
}
```

Success: returns updated detail.

Idempotency:

- `NEW -> IN_EXECUTION`;
- repeated start on same or another device returns success and current detail.

### `POST /api/tsd/order-control/tasks/{id}/scan`

Request:

```json
{
  "hu_code": "HU-0000506",
  "device_id": "TSD-01",
  "operator_id": "user",
  "request_id": "uuid-or-scan-guid"
}
```

Success:

```json
{
  "ok": true,
  "message": "HU проверена.",
  "already_checked": false,
  "task": {},
  "hu": {}
}
```

Errors:

- `TASK_CANCELLED`;
- `TASK_ALREADY_COMPLETED`;
- `HU_NOT_IN_TASK`;
- `HU_ALREADY_CHECKED` (or success with `already_checked=true`; MVP recommendation: success for idempotent repeat);
- `HU_NO_PHYSICAL_STOCK`;
- `HU_ALREADY_SHIPPED`;
- `OUTBOUND_IN_PROGRESS`;
- `EXPECTED_SET_CHANGED`;
- `IDEMPOTENCY_CONFLICT`.

Transaction:

- lock task row `FOR UPDATE`;
- lock HU row by `(task_id, normalized_hu)` `FOR UPDATE`;
- check idempotency `request_id`;
- compare snapshot with current outbound-ready/ledger/outbound state;
- update HU status and task counters;
- insert event;
- commit.

### `POST /api/tsd/order-control/tasks/{id}/complete`

Request:

```json
{
  "device_id": "TSD-01",
  "operator_id": "user"
}
```

Success:

```json
{
  "ok": true,
  "task_id": 123,
  "status": "COMPLETED",
  "message": "Контроль завершен."
}
```

Errors:

- `TASK_INCOMPLETE`;
- `TASK_CANCELLED`;
- `TASK_HAS_DISCREPANCIES`.

Transaction:

- lock task;
- verify all HU `CHECKED`;
- set task `COMPLETED`;
- set task order rows inactive;
- append event.

## 9. Статусы и переходы

### Задание

| Из | В | Инициатор | Endpoint | Условия | Повтор |
|---|---|---|---|---|---|
| - | `NEW` | WPF | `POST /api/order-control/tasks` | Valid orders and HU snapshot | conflict if active duplicate |
| `NEW` | `IN_EXECUTION` | TSD | `POST /start` | Not cancelled | success |
| `IN_EXECUTION` | `COMPLETED` | TSD | `POST /complete` | All HU checked, no blocking discrepancy | success if already completed |
| `NEW`/`IN_EXECUTION` | `CANCELLED` | WPF | `POST /cancel` | Not completed | success if already cancelled |

### HU строка

| Из | В | Инициатор | Условия |
|---|---|---|---|
| `PENDING` | `CHECKED` | TSD scan | HU in snapshot and current state valid |
| `CHECKED` | `CHECKED` | repeated TSD scan | idempotent success |
| `PENDING` | `DISCREPANCY` | TSD scan/current check | HU lost stock, already shipped, order changed |
| `PENDING`/`CHECKED` | `CANCELLED` | WPF cancel | task cancelled |

Для MVP `DISCREPANCY` блокирует successful complete. Вариант v2 может добавить `COMPLETED_WITH_DISCREPANCIES` и обязательный comment.

## 10. Конкуренция с outbound

### Рассмотренные варианты

1. Запрещать создавать контроль, если уже есть draft OUTBOUND с подобранными HU.
   - Просто и безопасно.
   - Обязательно для MVP.
2. Запрещать начинать TSD outbound, пока заказ в активном контроле.
   - Минимизирует гонки.
   - Обязательно для MVP.
3. Разрешать просмотр outbound, но запрещать scan.
   - Хороший UX: оператор видит причину, почему нельзя отгружать.
   - Рекомендуется для TSD list/detail.
4. Блокировать только конкретные HU.
   - Более гибко, но сложнее при mixed-HU, partial shipment и multi-order task.
   - Не рекомендуется для MVP.
5. Проверять блокировку при создании и на каждом scan.
   - Нужно обязательно, потому что между preview и scan состояние меняется.

### Рекомендация MVP

- Блокировать весь `order_id`.
- Preview/create контроля проверяет наличие draft/TSD OUTBOUND по каждому заказу.
- TSD outbound list исключает или помечает заказ, если есть active control.
- `OutboundPickingService.Scan(...)` перед созданием/использованием draft OUTBOUND проверяет active order control и возвращает `ORDER_CONTROL_ACTIVE`.
- `OutboundPickingService.Complete(...)` также проверяет active order control, чтобы нельзя было закрыть ранее начатый draft после старта контроля.
- WPF manual OUTBOUND creation/close для order-bound outbound должен получить такую же guard-проверку в document service или перед close, если flow может пересекаться.

Конкретные места:

- `OutboundPickingService.GetOrders()` / optimized `GetTsdOutboundOrderRows()` - исключить active-control orders из list или вернуть block metadata;
- `OutboundPickingService.EnsureCustomerOrderReadyForPicking(...)` - reject active control;
- `OutboundPickingService.Scan(...)` - check immediately before draft creation inside transaction;
- `OutboundPickingService.Complete(...)` / `TryAutoCloseOutbound(...)` - reject if control active;
- order-bound WPF OUTBOUND close path in `DocumentService.TryCloseDoc(...)` - final guard before ledger write.

## 11. Edge cases

| Ситуация | MVP поведение |
|---|---|
| Повторный scan той же HU | Идемпотентный успех, `already_checked=true`, без второго event если тот же `request_id` |
| HU не из задания | `HU_NOT_IN_TASK`, красный блок на TSD |
| HU другого заказа | `HU_NOT_IN_TASK`; details may include "принадлежит другому заказу" только если безопасно |
| HU из другого активного контроля | `HU_IN_ACTIVE_CONTROL` при create/scan |
| HU потеряла ledger stock после snapshot | `HU_NO_PHYSICAL_STOCK`, HU -> `DISCREPANCY`, complete blocked |
| HU уже отгружена после snapshot | `HU_ALREADY_SHIPPED`, HU -> `DISCREPANCY`, complete blocked |
| Изменилась привязка HU | `EXPECTED_SET_CHANGED`, discrepancy |
| Изменился состав mixed-HU | `EXPECTED_SET_CHANGED`, discrepancy |
| Заказ отменен/merged/shipped после snapshot | task получает discrepancy, complete blocked |
| Появился новый HU после snapshot | task не меняется автоматически; WPF detail warning "snapshot устарел" |
| Потеря сети после успешного scan | repeated request with same `request_id` returns previous success |
| Два TSD сканируют одну HU | row lock; один success, второй idempotent already_checked |
| Задание завершено другим TSD | live refresh показывает completed, scan blocked |
| Нет связи с сервером | TSD сохраняет текущий экран, красный message, refocus scan |

## 12. WPF-мокапы

### 12.1 Измененный экран "Заказы"

Стиль остается текущим: toolbar сверху, `DataGrid`, badge-like статус.

```text
Заказы

[Новый] [Изменить] [Отменить] [Управление HU] [Создать контроль]
[ ] Показать отменённые/перенесённые

┌──────┬────────────┬──────────────┬──────────────┬──────────────┬────────┬────────────┐
│  №   │ Тип        │ Контрагент   │ План паллет  │ Статус       │ Контроль │ Создан   │
├──────┼────────────┼──────────────┼──────────────┼──────────────┼────────┼────────────┤
│ 080  │ Клиентский │ ООО Клиент   │ Наполнено 3/3│ Готов        │ нет    │ 24/06     │
│ 081  │ Клиентский │ ООО Клиент   │ Наполнено 2/2│ Готов        │ CTRL-12│ 24/06     │
│ 082  │ Клиентский │ ...          │ План не сформ│ В работе     │ нет    │ 24/06     │
└──────┴────────────┴──────────────┴──────────────┴──────────────┴────────┴────────────┘

Tooltip disabled:
"Заказ 082 не готов к контролю: нет ожидаемых HU к отгрузке."
```

Правила UI:

- `OrdersGrid.SelectionMode = Extended`;
- кнопка enabled только если выбран хотя бы один потенциально допустимый `CUSTOMER` order;
- окончательные причины недоступности берутся с preview endpoint;
- колонка `Контроль` показывает active task ref или `нет`;
- double-click по строке по-прежнему открывает детали заказа.

### 12.2 Диалог preview

```text
Создать контроль готовых заказов

Заказы: 080, 081
Клиент: ООО Клиент
HU к проверке: 18
Товарных строк: 6

Предупреждения
! HU-0000511 находится в FG-02, проверьте маршрут отгрузки.

┌────────────┬──────┬──────────────┬──────────┬──────────────┬────────────┐
│ HU         │ Заказ│ Состав       │ Кол-во   │ Место        │ Источник   │
├────────────┼──────┼──────────────┼──────────┼──────────────┼────────────┤
│ HU-0000506 │ 080  │ Микс-паллета │ 1890 шт  │ FG-01        │ FILLED PRD │
│            │      │ - Горчица    │ 1200 шт  │              │            │
│            │      │ - Соус       │ 690 шт   │              │            │
│ HU-0000507 │ 081  │ Горчица      │ 630 шт   │ FG-01        │ RESERVE    │
└────────────┴──────┴──────────────┴──────────┴──────────────┴────────────┘

[Создать] [Отмена]
```

### 12.3 Список контрольных заданий WPF

```text
Контроль заказов

[Все v] [Обновить] [Открыть] [Отменить]

┌───────────────┬────────────┬──────────┬──────────┬────────────┬────────────┬────────────┐
│ Задание       │ Статус     │ Заказы   │ Прогресс │ Создал     │ TSD        │ Создан     │
├───────────────┼────────────┼──────────┼──────────┼────────────┼────────────┼────────────┤
│ CTRL-2026-123 │ В работе   │ 080,081  │ 12 / 18  │ semion     │ TSD-01     │ 24.06 10:15│
│ CTRL-2026-122 │ Завершено  │ 079      │ 8 / 8    │ semion     │ TSD-02     │ 24.06 09:40│
└───────────────┴────────────┴──────────┴──────────┴────────────┴────────────┴────────────┘
```

### 12.4 Детали задания WPF

```text
CTRL-2026-000123 - Контроль готовых заказов

Статус: В работе       Прогресс: 12 / 18 HU       Расхождения: 0
Заказы: 080, 081       Создал: semion             TSD: TSD-01

HU
┌────────────┬────────────┬──────┬──────────────┬────────┬──────────────┬────────────┐
│ Состояние  │ HU         │ Заказ│ Состав       │ Место  │ Сканировано  │ Устройство │
├────────────┼────────────┼──────┼──────────────┼────────┼──────────────┼────────────┤
│ Проверено  │ HU-0000506 │ 080  │ Микс-паллета │ FG-01  │ 10:20        │ TSD-01     │
│ Ожидается  │ HU-0000507 │ 081  │ Горчица      │ FG-01  │ -            │ -          │
│ Ошибка     │ HU-0000510 │ 081  │ Соус         │ FG-02  │ -            │ -          │
└────────────┴────────────┴──────┴──────────────┴────────┴──────────────┴────────────┘

Состав HU-0000506
┌──────────────┬──────────┬──────────────┐
│ Товар        │ Кол-во   │ Строка заказа│
├──────────────┼──────────┼──────────────┤
│ Горчица      │ 1200 шт  │ 208          │
│ Соус         │ 690 шт   │ 209          │
└──────────────┴──────────┴──────────────┘

Журнал
10:15 Создано задание semion
10:17 TSD-01 начал выполнение
10:20 HU-0000506 проверена
```

## 13. TSD-мокапы

### 13.1 Главное меню

```text
FlowStock TSD

[Наполнение]
[Отгрузка]
[Контроль заказов]
[Остатки]
[Настройки]
```

Кнопка скрывается или блокируется через `tsd_order_control = false`.

### 13.2 Список заданий

Размер: мобильный экран 360-430 px, вертикальные карточки.

```text
Контроль заказов

┌────────────────────────────┐
│ CTRL-2026-000123           │
│ Заказы: 080, 081           │
│ HU: 12 / 18                │
│ Статус: В работе           │
│ Создано: 24.06 10:15       │
└────────────────────────────┘

┌────────────────────────────┐
│ CTRL-2026-000124           │
│ Заказ: 082                 │
│ HU: 0 / 9                  │
│ Статус: Новое              │
│ Создано: 24.06 10:22       │
└────────────────────────────┘
```

### 13.3 Экран выполнения

```text
Контроль CTRL-2026-000123
Заказы: 080, 081

Прогресс: 12 / 18 HU
[████████░░░░]

[сканер активен]

Последняя: HU-0000506 проверена

Паллеты к проверке

┌────────────────────────────┐
│ ● HU-0000507               │ красный pending
│ Горчица - 630 шт           │
│ Место: FG-01               │
└────────────────────────────┘

┌────────────────────────────┐
│ ● HU-0000506               │ зеленый checked
│ Микс-паллета               │
│ - Горчица 1200 шт          │
│ - Соус 690 шт              │
│ Проверено: 10:20 TSD-01    │
└────────────────────────────┘

[Завершить] disabled до 18 / 18
```

Группировка:

- MVP: группировать по товарной label как outbound picking (`Микс-паллета`, item name), а не по заказам.
- Причина: оператор физически ищет HU; multi-order task может иметь mixed/shared view, а grouping by order увеличит количество повторов.
- В detail row показывать order refs.

### 13.4 Ошибочные состояния

```text
Контроль CTRL-2026-000123

[Ошибка]
HU не входит в это задание.

Прогресс: 12 / 18 HU
[сканер активен]
```

Коды и тексты:

- `HU_NOT_IN_TASK` - "HU не входит в это задание.";
- `HU_ALREADY_CHECKED` - "HU уже проверена.";
- `HU_NO_PHYSICAL_STOCK` - "HU больше не имеет физического остатка на складе.";
- `HU_ALREADY_SHIPPED` - "HU уже отгружена после создания контроля.";
- `OUTBOUND_IN_PROGRESS` - "По заказу уже начата отгрузка. Контроль остановлен.";
- `TASK_ALREADY_COMPLETED` - "Задание завершено другим TSD.";
- network failure - "Нет связи с сервером. Скан не сохранен.";

После rejected scan TSD должен:

- перечитать task detail;
- оставить красный error block видимым;
- очистить scan input;
- вернуть focus на scanner input.

## 14. Безопасность, блокировки и транзакции

- MVP разрешает нескольким TSD открыть одно задание, но scan одной HU защищен row lock.
- Устройство назначается при первом `start`, но не является эксклюзивным lock.
- Продолжить задание с другого TSD можно.
- Cancel во время выполнения:
  - WPF переводит task в `CANCELLED`;
  - scan/complete после cancel возвращают `TASK_CANCELLED`;
  - уже checked HU остаются в истории.
- Для scan нужен `SELECT ... FOR UPDATE` по task и HU row.
- Для complete нужен `SELECT ... FOR UPDATE` по task и aggregate check HU statuses.
- Для create нужен transactional conflict check:
  - active order control;
  - draft/TSD OUTBOUND;
  - duplicate selected orders;
  - expected HU snapshot still matches preview if client sent fingerprint.
- Идемпотентность scan:
  - `request_id` unique per task;
  - same `request_id` returns saved result;
  - same HU without `request_id` returns success with `already_checked=true`.

## 15. Тестовая матрица

| # | Сценарий | Уровень |
|---|---|---|
| 1 | Создание контроля для одного готового заказа | server integration |
| 2 | Создание для нескольких заказов | server integration |
| 3 | Один HU с несколькими товарными строками считается одной HU | server |
| 4 | Повторяющаяся HU не создает две строки task_hus | server |
| 5 | Заказ без HU отклоняется | server |
| 6 | Draft/Cancelled/Merged/Shipped отклоняются | server |
| 7 | Уже отгруженные HU не входят в snapshot | server |
| 8 | HU без физического ledger stock не входит в snapshot или дает blocking warning | server |
| 9 | Правильная HU становится `CHECKED` | server |
| 10 | Повторный scan идемпотентен | server |
| 11 | Посторонняя HU отклоняется | server/TSD |
| 12 | Нельзя завершить неполную задачу | server |
| 13 | Полная задача завершается | server |
| 14 | Контроль не создает документы | server invariant |
| 15 | Контроль не создает ledger | server invariant |
| 16 | Контроль не меняет status заказа | server invariant |
| 17 | Нельзя начать outbound во время active control | outbound regression |
| 18 | Нельзя создать контроль после начала outbound | server |
| 19 | Два TSD одновременно сканируют одну HU | concurrency |
| 20 | Повтор HTTP после timeout с тем же `request_id` | idempotency |
| 21 | Отмена задания | server/WPF |
| 22 | Заказ изменился после snapshot | server discrepancy |
| 23 | Частично отгруженный заказ получает только remaining HU | server |
| 24 | Live refresh обновляет второй TSD и WPF | UI |
| 25 | Client block `tsd_order_control` скрывает/показывает раздел | TSD |

Дополнительно:

- static query-shape test для production list read model: не допустить N+1 и тяжелый fallback;
- tests around `OutboundPickingService` guard points;
- TSD Node VM tests по render/wire/error mapper;
- WPF view-model/controller tests там, где UI logic вынесена из code-behind.

## 16. Производительность

- Preview/create для нескольких заказов должен использовать batch read model.
- Нельзя выполнять N+1 по заказам, HU, ledger, order lines.
- Для production PostgreSQL нужен optimized store path, аналогичный `IOptimizedTsdOutboundPickingStore`.
- Реалистичный MVP limit:
  - до 20 заказов в одном задании;
  - до 300 HU в одном задании;
  - TSD detail payload должен оставаться компактным, component lines грузятся вместе с HU, но без лишней истории.
- WPF detail должен использовать `DataGrid` virtualization для HU list.
- TSD список заданий не должен возвращать все HU, только counters.
- Индексы обязательны:
  - active tasks by order;
  - active tasks by HU;
  - task HU by status;
  - events by task/time.

## 17. Поэтапный план разработки

### Этап 1. Документация и контракт

Файлы:

- обновить `docs/spec.md`;
- обновить `docs/spec_orders.md`;
- добавить/обновить этот план при изменении решений.

Тесты: не требуются.

Критерий готовности: контракты контроля и outbound block описаны в источниках истины.

### Этап 2. Миграция и модели

Файлы:

- новая миграция в `deploy/postgres/migrations`;
- новые Core models/enums для order control;
- `ClientBlockCatalog` с `tsd_order_control`.

Тесты:

- migration/schema smoke;
- model status mapping tests.

Риски:

- partial active uniqueness by order requires `is_active` or transactional check.

Критерий готовности: schema создает таблицы и индексы без touching warehouse task board.

### Этап 3. Store и read model

Файлы:

- `IDataStore` / optional optimized interface;
- `PostgresDataStore`;
- test harness store.

Тесты:

- expected HU from warehouse-bound;
- expected HU from FILLED production pallet;
- mixed HU distinct count;
- already shipped exclusion;
- no physical stock exclusion/warning;
- static query-shape guard.

Критерий готовности: expected HU для контроля совпадает с outbound-ready semantics.

### Этап 4. `OrderControlService`

Файлы:

- новый service в Core;
- result/request models.

Тесты:

- preview/create;
- active conflict;
- scan/complete/cancel;
- idempotency;
- no docs/no ledger/no order status change.

Критерий готовности: core lifecycle работает без API/UI.

### Этап 5. API endpoints

Файлы:

- `OrderControlEndpoints`;
- регистрация в `Program.cs`;
- API DTOs.

Тесты:

- HTTP integration tests for WPF endpoints;
- HTTP integration tests for TSD endpoints;
- error response shape.

Критерий готовности: API contracts из раздела 8 реализованы.

### Этап 6. Outbound blocking

Файлы:

- `OutboundPickingService`;
- optimized outbound list SQL;
- `DocumentService.TryCloseDoc(...)` guard for order-bound OUTBOUND;
- tests.

Тесты:

- active control hides/blocks outbound list;
- scan returns `ORDER_CONTROL_ACTIVE`;
- complete/close blocked;
- control create blocked after draft outbound.

Критерий готовности: outbound и control не могут одновременно менять физический workflow одного заказа.

### Этап 7. WPF create flow

Файлы:

- `MainWindow.xaml` / `.cs`;
- `WpfOrderControlApiService`;
- preview dialog.

Тесты:

- controller/view-model tests where practical;
- manual WPF smoke.

Критерий готовности: пользователь может выбрать заказы, увидеть preview и создать task.

### Этап 8. WPF list/detail/cancel

Файлы:

- new window or tab for order control;
- detail window;
- live refresh integration.

Тесты:

- API service parsing;
- UI state tests where possible.

Критерий готовности: WPF показывает progress, HU, mixed composition, events and cancel.

### Этап 9. TSD routes and storage

Файлы:

- `apps/android/tsd/storage.js`;
- `apps/android/tsd/app.js`;
- client block defaults/version as needed.

Тесты:

- Node VM render tests;
- API normalization tests;
- block enabled/disabled tests.

Критерий готовности: меню и список заданий работают.

### Этап 10. TSD scan execution

Файлы:

- TSD detail render/wire;
- scan handler;
- error mapper;
- live refresh.

Тесты:

- scan success;
- repeat scan;
- rejected scan visible after refresh;
- focus restored;
- complete disabled/enabled.

Критерий готовности: TSD оператор может выполнить полный контроль.

### Этап 11. Регрессия и acceptance

Команды:

```bash
dotnet build apps/windows/FlowStock.sln
dotnet test apps/windows/FlowStock.sln
node apps/android/tsd/app.outbound.test.js
git diff --check
```

Добавить точные TSD order-control tests после их создания.

Критерий готовности: все инварианты и UI flows проходят.

## 18. Открытые вопросы

1. Нужно ли в MVP разрешать `IN_PROGRESS` заказы, если outbound-ready logic считает их готовыми, или ограничить создание контроля только `ACCEPTED`?
   - Рекомендация: использовать тот же eligibility, что TSD outbound, чтобы контроль и отгрузка не расходились.
2. Нужен ли один active control на заказ или один active control на HU?
   - Рекомендация: на заказ для MVP.
3. Нужно ли показывать order-control status в `/api/orders` сразу в первой реализации?
   - Рекомендация: да для WPF списка заказов, но компактно: active task ref/status.
4. Нужен ли `COMPLETED_WITH_DISCREPANCIES`?
   - Рекомендация: нет в MVP; рассмотреть v2.
5. Нужна ли exclusive привязка task к одному TSD?
   - Рекомендация: нет; row-level lock и idempotency достаточно.

## 19. Acceptance checklist

- [ ] Ожидаемые HU контроля совпадают с HU, доступными текущей TSD-отгрузке.
- [ ] Mixed-HU считается одной HU в progress.
- [ ] Контроль не пишет `ledger`.
- [ ] Контроль не создает и не закрывает `docs`.
- [ ] Контроль не меняет `orders.status`.
- [ ] Outbound и active control взаимно блокируются.
- [ ] Повторные scans и repeated HTTP requests идемпотентны.
- [ ] TSD rejected scan остается видимым после refresh.
- [ ] Warehouse Task Board не активирован.
- [ ] `tsd_warehouse_tasks` не используется для новой функции.
