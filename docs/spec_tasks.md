# Warehouse Task Board (пакеты действий и задания TSD)

> **Статус: экспериментально / отключено в пользовательском UI.**
>
> Модуль Warehouse Task Board сохранён как технический задел (backend, миграции, тесты, API).
> Точки входа Web / WPF / TSD **по умолчанию скрыты**. Возврат к полноценной реализации UI планируется позже.
>
> - Backend и API (`PlannerEndpoints`, `WarehouseTaskEndpoints`, сервисы) **сохранены** и покрыты тестами.
> - Web PC: вкладка «Задания склада» только при `localStorage.flowstock_experimental_warehouse_tasks = "1"`.
> - WPF: вкладка «Задания» только при `ExperimentalFeatureFlags.WarehouseTasksEnabled = true` в коде.
> - TSD: client block `tsd_warehouse_tasks` по умолчанию **выключен** (миграция `V0022`, fallback в PWA).
> - Инварианты `ledger`, TSD scan ≠ проводка, `confirm-execution` в WPF — **без изменений** в документе.
> - Работа над операторским UI **приостановлена**.

Документ описывает слой управления складом: планирование намерений → подтверждение в WPF → физическое исполнение на TSD → финальное подтверждение и проводка документов.

Связанные документы:
- [`spec.md`](spec.md) — общие инварианты `ledger`, документы, TSD online.
- [`spec_orders.md`](spec_orders.md) — заказы, план паллет, `ADOPT_PALLET_PLAN`, статус `MERGED`.

## Цель и принцип

```text
Web / WPF planner  →  WarehouseActionBundle (намерение)
WPF approve        →  draft docs + TSD tasks (без ledger)
TSD scan           →  факты исполнения (warehouse_task_events)
WPF confirm exec   →  Close документов → ledger
```

**Web и drag-and-drop ничего не проводят напрямую.** Web (в будущих фазах) только формирует пакет действий. Фактическое движение товара появляется только после `confirm-execution` в WPF и `Close` документа на сервере.

### Инварианты (обязательны)

- Остатки рассчитываются **только** из `ledger` (см. `spec.md`).
- Документ влияет на остатки **только** при `Close`.
- Закрытые документы неизменяемы.
- `TSD scan ≠ ledger movement` — скан фиксирует факт физического исполнения задания.
- `WPF confirm execution` — разрешение серверу провести документ.
- `POST /api/tsd/tasks/{id}/complete` **не** вызывает `Close` и **не** пишет в `ledger`.

Legacy-потоки (`/api/ops` one-shot MOVE, прямое создание/закрытие документов на TSD) **сохраняются**; task-board — отдельный контролируемый pipeline для плановых операций.

## Модель данных

### `warehouse_action_bundles`

Пакет планируемых действий.

| Поле | Описание |
|------|----------|
| `id` | PK |
| `bundle_ref` | Уникальный номер, напр. `BND-2026-000125` |
| `source` | `WEB_PLANNER` \| `WPF` \| `API` |
| `status` | см. §Статусы пакета |
| `created_at`, `created_by` | |
| `approved_at`, `approved_by` | |
| `executed_at` | TSD завершил физическое исполнение |
| `completed_at` | WPF подтвердил проводку |
| `rejected_at`, `rejected_by` | |
| `comment` | |
| `error_code`, `error_message` | при `FAILED` |

### `warehouse_action_lines`

Одна строка действия внутри пакета.

| Поле | Описание |
|------|----------|
| `id`, `bundle_id`, `line_no` | |
| `action_type` | см. §Типы действий MVP |
| `status` | `PENDING` \| `DONE` \| `FAILED` \| `CANCELLED` |
| `source_order_id`, `target_order_id` | опционально |
| `source_doc_id`, `target_doc_id` | опционально, связь с draft MOVE и т.д. |
| `item_id`, `hu_code` | |
| `from_location_id`, `to_location_id` | |
| `qty` | |
| `payload_json` | параметры действия (jsonb) |
| `result_json` | результат server-only действий (jsonb) |
| `error_code`, `error_message` | |
| `created_at`, `updated_at` | |

### `warehouse_tasks`

Задание на физическое исполнение (TSD). Не каждое действие требует TSD.

| Поле | Описание |
|------|----------|
| `id`, `task_ref` | |
| `bundle_id`, `action_line_id` | |
| `task_type` | обычно совпадает с `action_type` для MOVE |
| `status` | см. §Статусы задания |
| `assigned_to_device_id`, `assigned_to_user` | |
| `created_at`, `started_at`, `executed_at`, `confirmed_at`, `cancelled_at` | |
| `comment` | |

### `warehouse_task_lines`

Строка задания для сканирования.

| Поле | Описание |
|------|----------|
| `id`, `task_id`, `line_no` | |
| `expected_hu_code`, `expected_item_id`, `expected_qty` | |
| `from_location_id`, `to_location_id` | |
| `order_id`, `doc_id` | |
| `status` | `PENDING` \| `SCANNED` \| `DONE` \| `CANCELLED` \| `FAILED` |
| `scanned_hu_code`, `scanned_location_id`, `scanned_at` | |
| `device_id`, `operator_id` | |
| `error_code`, `error_message` | |

### `warehouse_task_events`

Журнал событий исполнения (для WPF: галочки и история).

| Поле | Описание |
|------|----------|
| `id`, `task_id`, `task_line_id` | |
| `event_type` | `SCAN_HU` \| `SCAN_LOCATION` \| `CONFIRM_LINE` \| `COMPLETE_TASK` \| `ERROR` |
| `event_at`, `device_id`, `operator_id` | |
| `hu_code`, `location_id` | |
| `payload_json`, `message` | |

### Блокировка HU (MVP)

Отдельная таблица `warehouse_task_locks` **не используется**. Блокировка вычисляется по активным пакетам и строкам заданий:

- Пакет в статусе `SUBMITTED`, `APPROVED`, `IN_EXECUTION` или `EXECUTED`.
- HU указан в `warehouse_action_lines.hu_code` или `warehouse_task_lines.expected_hu_code` / `scanned_hu_code` связанного задания.

Серверный helper: `IsHuLockedByActiveWarehouseTask(huCode)` — второй пакет не может использовать тот же HU.

## Статусы пакета

| Статус | Смысл |
|--------|--------|
| `DRAFT` | Создан, не отправлен на подтверждение |
| `SUBMITTED` | Отправлен на подтверждение WPF |
| `APPROVED` | WPF подтвердил план; server-only действия выполнены; TSD-задачи ещё не начаты или не требуются |
| `IN_EXECUTION` | Есть активные TSD-задания |
| `EXECUTED` | TSD отсканировал всё; документы **ещё не** проведены |
| `COMPLETED` | WPF подтвердил исполнение; документы `Closed`; есть `ledger` |
| `REJECTED` | WPF отклонил до исполнения |
| `CANCELLED` | Отменён |
| `FAILED` | Ошибка валидации или исполнения |

**Ключевое различие:** `EXECUTED` = физически выполнено, сканы есть; `COMPLETED` = проведено документами, `ledger` создан.

### Допустимые переходы (MVP)

| Из | В | Действие |
|----|---|----------|
| `DRAFT` | `SUBMITTED` | `POST .../submit` |
| `DRAFT` | `CANCELLED` | cancel |
| `SUBMITTED` | `APPROVED` / `IN_EXECUTION` | `POST .../approve` |
| `SUBMITTED` | `REJECTED` | `POST .../reject` |
| `IN_EXECUTION` | `EXECUTED` | TSD `complete` (все задачи) |
| `EXECUTED` | `COMPLETED` | `POST .../confirm-execution` |
| `EXECUTED` | `IN_EXECUTION` | return to work (post-MVP UI) |
| `APPROVED` | `COMPLETED` | только server-only пакет без TSD |

Повторный `confirm-execution` при `COMPLETED` — идемпотентный успех **без** второго `Close` и без дублирования `ledger`.

## Статусы задания TSD

| Статус | Смысл |
|--------|--------|
| `NEW` | Создано при approve |
| `ASSIGNED` | Назначено устройству (опционально) |
| `IN_EXECUTION` | Оператор начал (`start`) |
| `EXECUTED` | TSD `complete` — физически готово |
| `CONFIRMED` | После WPF `confirm-execution` |
| `CANCELLED` | Отменено |
| `FAILED` | Ошибка |

## Типы действий

### MVP

| Тип | TSD | Проводка ledger |
|-----|-----|-----------------|
| `MOVE_HU` | Да | При `confirm-execution` → `Close` MOVE |
| `ADOPT_PALLET_PLAN` | Нет | Нет; правила в `spec_orders.md` §«Перенести план паллет» |

### Payload (примеры)

`MOVE_HU`:

```json
{
  "hu_code": "HU-0000462",
  "item_id": 1,
  "qty": 10,
  "from_location_id": 2,
  "to_location_id": 5
}
```

`ADOPT_PALLET_PLAN`:

```json
{
  "source_internal_order_id": 66,
  "target_customer_order_id": 67
}
```

### После MVP (не реализовано)

`SHIP_HU`, `DELETE_PALLET_PLAN`, `MERGE_INTERNAL_TO_CUSTOMER`, `CREATE_OUTBOUND_DRAFT`, `CREATE_PRODUCTION_PALLET_PLAN`, `UNRESERVE_HU`, `RESERVE_HU_TO_ORDER`, `SPLIT_HU`, `REPACK_HU`, `CHANGE_LOCATION`, `RETURN_FROM_SHIPMENT_ZONE`.

`MERGE_INTERNAL_TO_CUSTOMER` может использовать логику `InternalOrderMergeService` (см. код).

## Жизненный цикл (MVP)

### 1. Создание пакета

- Источник: WPF (тестовые кнопки), API, позже web planner.
- Статус `DRAFT`, добавление `warehouse_action_lines`.
- `POST /api/planner/bundles/preview` — валидация без сохранения.

### 2. Submit

- `DRAFT` → `SUBMITTED`.
- Проверка HU lock и валидаторов.

### 3. WPF approve

- Повторная валидация всего пакета.
- **`ADOPT_PALLET_PLAN`:** немедленно `ProductionPalletService.AdoptPlanFromInternal`; line → `DONE`; `ledger` не создаётся.
- **`MOVE_HU`:** создать draft `MOVE`, `warehouse_tasks` + lines; `target_doc_id` на line; bundle → `IN_EXECUTION` (если есть TSD-задачи).

### 4. TSD execution

- `GET /api/tsd/tasks`, `start`, `scan`, `complete`.
- События в `warehouse_task_events`; обновление `warehouse_task_lines`.
- При завершении всех задач пакета: bundle → `EXECUTED`.

### 5. WPF confirm execution

- Только из `EXECUTED` (или server-only путь в `APPROVED` без TSD).
- Сверка expected vs scanned (MVP: расхождение блокирует confirm; `EXECUTED_WITH_WARNINGS` — post-MVP).
- Обновить `doc_lines` фактическими данными сканов; `DocumentService.TryCloseDoc` для MOVE.
- Bundle → `COMPLETED`; tasks → `CONFIRMED`.

## Документная модель MVP

### `MOVE_HU`

| Этап | Действие |
|------|----------|
| approve | draft `MOVE` + TSD task |
| TSD | scan HU, scan location, complete |
| confirm | заполнить lines, `Close` MOVE → `ledger` (−from, +to) |

### `ADOPT_PALLET_PLAN`

| Этап | Действие |
|------|----------|
| approve | `AdoptPlanFromInternal`; без `ledger` |
| TSD | не требуется |
| confirm | не требуется для чисто adopt-пакета; bundle может стать `COMPLETED` сразу после approve, если нет MOVE |

Правила adopt: `spec_orders.md` (INTERNAL → CUSTOMER, open PRD, нет FILLED/ledger, target без активного плана).

## API

### Planner (`/api/planner/bundles`)

| Метод | Путь |
|-------|------|
| POST | `/preview` |
| POST | `/` — создать bundle |
| GET | `/` — список (фильтр по status) |
| GET | `/{id}` |
| POST | `/{id}/lines` — добавить line (DRAFT) |
| POST | `/{id}/submit` |
| POST | `/{id}/approve` |
| POST | `/{id}/reject` |
| POST | `/{id}/confirm-execution` |
| POST | `/{id}/cancel` |

Мутирующие POST (`approve`, `confirm-execution`) поддерживают опциональный `eventId` для идемпотентности через `api_events` (как `DOC_CLOSE`).

### TSD (`/api/tsd/tasks`)

| Метод | Путь |
|-------|------|
| GET | `/` |
| GET | `/{id}` |
| POST | `/{id}/start` |
| POST | `/{id}/scan` — body: `barcode`, `scan_type` (`HU` \| `LOCATION`), `device_id` |
| POST | `/{id}/complete` |

Коды ошибок TSD: `HU_NOT_IN_TASK`, `HU_ALREADY_SCANNED`, `WRONG_LOCATION`, `TASK_ALREADY_COMPLETED`, `TASK_NOT_IN_EXECUTION`.

## Клиенты

### WPF

- Вкладка **«Задания»** (experimental, скрыта по умолчанию): списки по фильтрам (На подтверждении / В работе / Исполнено ТСД / Проведено / Отклонено).
- Карточка пакета: действия, документы, события TSD, кнопки Подтвердить / Отклонить / Подтвердить исполнение.
- MVP: кнопки создания тестовых пакетов `MOVE_HU` и `ADOPT_PALLET_PLAN`.
- Runtime read/write — **только** через `FlowStock.Server` API (см. `spec.md`).

### TSD PWA

- Маршруты `#/tasks`, `#/tasks/{id}`.
- Client block: `tsd_warehouse_tasks` (по умолчанию **выключен**, см. `V0022__disable_warehouse_tasks_ui.sql`).
- Экран: список → scan HU → scan место → завершить.

### Web planner (Phase 6 — experimental, UI disabled by default)

- Код PC web (`warehouse-board.js`, guided workflow) **сохранён**, но вкладка скрыта без `localStorage.flowstock_experimental_warehouse_tasks = "1"`.
- **Web** (когда включено) — выбор сценария, wizard MOVE_HU / ADOPT_PALLET_PLAN, пакет действий.
- **WPF** — approve, reject, confirm-execution, контроль и проведение.
- **TSD** — физическое исполнение (scan/complete).
- PC web (`apps/android/tsd/pc/`, вкладка **«Задания склада»**): трёхколоночный экран (заказы · HU/остатки · пакет действий), детали заказа снизу, `GET /api/planner/warehouse-board/state`.
- Создание `MOVE_HU` — клик по HU → выбор места назначения (без ручного `item_id`/`location_id`).
- Создание `ADOPT_PALLET_PLAN` — выбор внутреннего/клиентского заказа кнопками «Источник»/«Получатель» (без ручного `order_id`).
- Web **не** вызывает `approve`, `reject`, `confirm-execution`, не закрывает документы и не пишет в ledger.
- Drag-and-drop карта склада — **future phase**.

## Валидация (MVP)

Общие проверки: сущности существуют; статусы редактируемы; нет дубликата HU в том же bundle; HU не заблокирован другим активным пакетом.

**`MOVE_HU`:** HU существует; остаток > 0; `to_location` существует; HU не в другом активном MOVE/SHIP task.

**`ADOPT_PALLET_PLAN`:** те же правила, что у `POST .../adopt-from-internal` (см. `spec_orders.md`).

## Тестовый минимум

- Bundle/task lifecycle и переходы статусов.
- `MOVE_HU`: approve → task; scan → events, ledger = 0; confirm → один Close, ledger ±qty.
- Повторный confirm не дублирует ledger.
- TSD `complete` без WPF confirm — doc остаётся Draft.
- `ADOPT` на approve — без ledger; PRD remapped.
- HU lock: второй SUBMITTED bundle с тем же HU — ошибка.

## Фазы вне MVP

- Web drag-and-drop карта склада.
- `SHIP_HU`, `DELETE_PALLET_PLAN`, `MERGE_INTERNAL_TO_CUSTOMER`, `CREATE_OUTBOUND_DRAFT`.
- Таблица `warehouse_task_locks`.
- Полный UX `EXECUTED_WITH_WARNINGS`.
