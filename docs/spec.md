# Спецификация MVP для FlowStock Server

## Область охвата
- Workflow центрирован на сервере. Сервер является единым источником истины.
- Desktop WPF-клиент подключается напрямую к PostgreSQL как операторский UI.
- TSD PWA работает онлайн через server API (без offline JSONL).
- PostgreSQL является единственным поддерживаемым хранилищем.
- Изменения production schema применяются через версионируемые SQL-миграции; `postgres init`-скрипты используются только для bootstrap пустого каталога данных.
- Остатки рассчитываются только из `ledger`.

## Компоненты
- `FlowStock.Server`: ASP.NET Core Minimal API, доступ к БД, диагностика, раздача TSD/PC web clients.
  - Экспортирует `/health/live` и `/health/ready` для container liveness/readiness.
- `FlowStock.App`: WPF desktop operator UI с прямым подключением к PostgreSQL (`FLOWSTOCK_PG_*` env или `settings.json postgres`).
  - Если PostgreSQL не настроен или недоступен на старте приложения, WPF сначала открывает окно подключения к БД и не открывает основной UI, пока подключение не будет настроено успешно.
  - В окне подключения доступна кнопка `Инициализировать / миграции`: она применяет недостающие SQL-миграции `deploy/postgres/migrations` в выбранную БД, не переисполняя уже отмеченные версии в `schema_migrations`.
  - В том же окне есть кнопка `Проверить схему`: она только проверяет текущие примененные версии и соответствие доступным миграциям, без изменения БД.
  - Настройка подключения к БД по умолчанию использует autodiscovery кандидатов `host:port` PostgreSQL на локальном ПК и в локальной сети; ручной ввод `host/port` остается как fallback.
  - Раздел FlowStock Server в окне подключения к БД используется только для настройки API endpoint-ов и диагностики. Для non-KM runtime-сценариев WPF legacy per-operation toggles удалены, всегда используется server path.
  - Non-KM runtime read/write-потоки WPF используют `FlowStock.Server` для списков, деталей, request inbox, справочников, контрагентов, HU, packaging и import tooling. Прямой доступ к PostgreSQL в WPF остается только для startup connection/bootstrap и замороженных KM-specific code path.
- `TSD PWA`: online data capture через API (без прямого доступа к БД).
- `PC web client`: остатки доступны только на чтение; создание заказа и смена статуса заказа отправляются как requests и применяются только после подтверждения в WPF.
  - Отправка requests разрешена только активным аккаунтам с PC-access (`tsd_devices.platform=PC` или `BOTH`).
- На порту `7154` доступны три операторских экрана: `Состояние склада`, `Каталог`, `Заказы`.
  - Каталог продукции показывает только товары, у которых тип номенклатуры помечен `is_visible_in_product_catalog = true`.
- WPF admin хранит глобальные web block switches в PostgreSQL. Если блок отключен, он скрывается для всех пользователей соответствующего web client, а прямой переход на него блокируется после перезагрузки страницы.
- Web clients не опрашивают block settings непрерывно. Конфигурация блоков перечитывается на старте и на `focus` / `visibilitychange`; server API requests из отключенного block context отклоняются с `403 BLOCK_DISABLED`, после чего клиент перечитывает конфигурацию блоков и перерисовывает UI.

## URL-схема web
- Production использует единый HTTPS origin на `7154`.
- `https://SERVER_IP:7154/` открывает PC web client.
- `https://SERVER_IP:7154/tsd/` открывает TSD web client.

## Модель данных (server DB)
- `items(id, name, barcode, gtin, base_uom, default_packaging_id, brand, volume, shelf_life_months, max_qty_per_hu, tara_id, is_marked, item_type_id, min_stock_qty)`
- `item_types(id, name, code, sort_order, is_active, is_visible_in_product_catalog, enable_min_stock_control)`
- `taras(id, name)`
- `item_requests(id, barcode, comment, device_id, login, status, created_at, resolved_at)`
- `order_requests(id, request_type, payload_json, status, created_at, created_by_login, created_by_device_id, resolved_at, resolved_by, resolution_note, applied_order_id)`
- `locations(id, code, name)`
- `partners(id, name, inn, ... )`
- `orders(id, order_ref, order_type, partner_id NULL, due_date, status, comment, created_at)`
- `order_lines(id, order_id, item_id, qty_ordered)`
- `docs(id, doc_ref, type, status, created_at, closed_at, partner_id, order_id, order_ref, shipping_ref, reason_code, comment, production_batch_no)`
- `doc_lines(id, doc_id, replaces_line_id NULL, order_line_id, item_id, qty, qty_input, uom_code, from_location_id, to_location_id, from_hu, to_hu)`
- `ledger(id, ts, doc_id, item_id, location_id, qty_delta, hu_code)`
- `km_code_batch(id, order_id, file_name, file_hash, imported_at, imported_by, total_codes, error_count)`
- `km_code(id, batch_id, code_raw, gtin14, sku_id, product_name, status, receipt_doc_id, receipt_line_id, hu_id, location_id, ship_doc_id, ship_line_id, order_id)`
- `client_blocks(block_key, is_enabled, updated_at)`

## Инварианты
- Остатки рассчитываются только из `ledger`.
- Контроль минимального остатка рассчитывается только из `ledger` (суммарно по всем локациям и HU для товара).
- Закрытые документы неизменяемы.
- Исправления оформляются отдельными документами.
- Один HU-code может иметь остаток только в одной локации одновременно.
- Документы списания требуют `reason_code` до проведения.
- Черновые правки строк документа на сервере append-only: замена draft line создает новую строку `doc_lines`, связанную через `replaces_line_id`; активные draft/read projections игнорируют superseded rows.

## Онлайн-поток TSD
- Черновики создаются через API; сервер назначает `doc_ref`.
- Строки добавляются через API; закрытие документа пишет изменения в `ledger`.
- Canonical online submit-flow TSD: `create draft -> add lines -> close`.
- Формат `doc_ref`: `TYPE-YYYY-000001` (последовательность дополняется нулями до 6 знаков и уникальна в рамках года для всех типов документов).
- Черновики с `doc_uid` могут быть восстановлены на TSD (server draft реконструируется как local draft для редактирования).
- TSD scanner по умолчанию использует Keyboard wedge; Intent mode работает только через JS bridge (см. `TSD README`). Для `Chrome/PWA` нужно оставлять Keyboard mode и выключать Intent output.
- Главное меню TSD сгруппировано по разделам `Операции`, `Состояние склада`, `Каталог`, `Заказы`; `История операций` больше не является отдельным top-level block.
- В разделе `Операции` на TSD доступны: `Приемка`, `Выпуск продукции`, `Отгрузка`, `Перемещение`, `Списание`, `Инвентаризация`.

## Неизвестные товары
- TSD не может создавать товары напрямую.
- Если scanned barcode отсутствует в каталоге, TSD отправляет item request (barcode + comment + device/login).
- Requests сохраняются в `item_requests` и показываются в WPF-уведомлениях для разбора оператором.

## Экраны UI (MVP)
- Status / Состояние склада: список остатков + поиск.
  - Добавлены фильтры по типу номенклатуры, месту хранения и HU.
  - Добавлен блок позиций ниже минимума над основным списком.
  - Ручной поиск по отдельному полю убран, вместо него используются встроенные фильтры.
- Documents: список + детали + проведение.
- Items: список с ID + modal create/edit (`name`, `barcode/SKU`, `gtin`, `brand`, `volume`, `shelf life months`, `max qty per HU`, `tara`, `uom`, `item_type`, `min_stock_qty`, `is_marked`) + Excel import с preview и column mapping.
- Item types: редактор справочника типов номенклатуры (создание, редактирование, удаление/деактивация при использовании, настройка флагов `is_visible_in_product_catalog` и `enable_min_stock_control`).
  - Для новых типов `is_visible_in_product_catalog` по умолчанию включен; после миграции V0005 тип `Без типа` (`GENERAL`) автоматически помечается видимым в PC каталоге.
- Item packagings: редактор упаковок в карточке товара и общий packaging manager используют server API для list/create/update/deactivate/set-default.
- Tara: редактор справочника в разделе `Справочники`.
- Locations: список с ID + modal create/edit (`code`, `name`).
- Простой CRUD по `items`, `locations`, `uoms` и `taras` использует server API.
- Partners: список с ID + modal create/edit/delete.
  - CRUD по контрагентам и их роли/статусу (`Supplier` / `Client` / `Both`) работают через server API.
- Orders: список + детали (см. `spec_orders.md`).
- Incoming requests: единое WPF-окно входящих запросов (из колокольчика/меню) для item requests и web order requests, со всеми действиями обработки в одном месте.
  - Список inbox, pending badge и resolve/reject-actions работают через server API.
  - Для order requests в WPF есть отдельное modal-окно деталей с полным payload заказа до подтверждения.
- Обработка import errors в WPF использует server API для чтения и повторного применения `import_errors`; lookup/create товаров при cleanup импорта используют те же server-backed catalog/read path, что и остальная часть WPF.
- WPF data grid-ы используют общие правила adaptive sizing: короткие колонки подстраиваются по header/content, длинные текстовые колонки ограничиваются и режутся, а видимые колонки перераспределяют ширину при ресайзе окна.
- WPF dialog-окна автоматически растут по содержимому в пределах экрана; если содержимое все равно не помещается, окно становится прокручиваемым, чтобы нижние action-кнопки оставались достижимыми.
- Admin: без прямого reset/edit таблиц из WPF; админское окно содержит настройку подключения к БД, управление web-login account-ами, глобальный доступ к web blocks и тестовую cleanup-action.
  - Управление web-login account-ами (`tsd_devices`) и глобальными настройками блоков (`client_blocks`) читается и сохраняется через server API.
- HU registry в WPF (`HU Реестр`) читает/генерирует/закрывает HU через server API.
- WPF HU-selectors и stock-by-HU helper-ы берут доступные HU из тех же server read-model, что используются для stock/HU.
- Удаление строк в табах `orders/items/locations` в текущем WPF UI по-прежнему заблокировано; контрагенты удаляются через server API при наличии выбранной строки.
- Для `locations` поддерживается опциональный лимит вместимости по HU (`max_hu_slots`): это максимум одновременно занятых HU-мест в локации.
  - Если лимит задан, проведение документов (`INBOUND`/`PRODUCTION_RECEIPT`/`MOVE`) не допускает превышение количества занятых HU в целевой локации.
- В Admin есть отдельная action `очистить операции` для тестового cleanup (`docs/doc_lines/ledger/orders/order_lines/import events/errors`). Справочники при этом не затрагиваются.
- В Admin есть отдельный раздел глобального доступа к web blocks:
  - PC blocks: `Состояние склада`, `Каталог`, `Заказы`.
  - TSD main blocks: `Операции`, `Состояние склада`, `Каталог`, `Заказы`.
  - TSD operation blocks: `Приемка`, `Выпуск продукции`, `Отгрузка`, `Перемещение`, `Списание`, `Инвентаризация`.
- KM в WPF временно заморожен: вкладка KM, KM-действия в окнах документов и KM edit-control в карточках товара скрыты из клиента. Во время freeze document validation/close не требует назначения KM и не делает auto-ship KM codes.

## Документы (дополнительно)
- HU в документе хранится на уровне строки (`doc_lines.from_hu` / `doc_lines.to_hu`). Значение HU в шапке используется как значение по умолчанию и как быстрый выбор для операций со строками, но один документ может содержать строки с разными HU.
- `doc_lines.replaces_line_id` используется для append-only semantics черновиков (`UpdateDocLine` / `DeleteDocLine`). Историческая строка остается в БД; удаление строки создает tombstone-row с `qty = 0` и `replaces_line_id = deleted_line_id`. В document read-model и в расчетах заказов/проведения учитываются только активные строки с `qty > 0`, на которые не ссылается более новая запись.
- Выпуск продукции: приемка готовой продукции на склад (плюс в `ledger`), HU обязателен на момент проведения по каждой строке, партия производства хранится в `production_batch_no`, документ может быть связан с заказом (`order_id/order_ref`).
  - При выборе заказа автоматически подставляются остатки по строкам заказа (`order_line_id`) с учетом уже закрытых выпусков.
  - Для `orders.order_type = INTERNAL` этот документ является основным способом закрытия заказа по факту выпуска.
  - В WPF для выпуска используется отдельное действие `Распределить по HU`: система сама берет свободные HU из реестра по порядку кода, при необходимости повторно вводит в оборот свободные `CLOSED` HU и, если их не хватает, создает новые HU.
  - В TSD выпуск продукции доступен как отдельная операция в разделе `Операции`; базовый сценарий работает как online-документ приемки готовой продукции в выбранную локацию/HU.
  - Если в карточке товара задан `items.max_qty_per_hu`, `Распределить по HU` автоматически дробит строки выпуска так, чтобы каждая строка не превышала лимит на один HU.
  - Для строки выпуска можно включить флаг общего HU (`doc_lines.pack_single_hu`): такая строка не дробится при автораспределении и может быть уложена вместе с другими отмеченными строками на один общий HU, если суммарная загрузка паллеты по формуле `sum(qty / max_qty_per_hu)` не превышает `1`.
  - При проведении приоритет имеет HU строки; HU из шапки используется только как fallback для старых/неполных строк, а не как переопределение уже назначенного HU строки.
  - При проведении проверяется, что количество в каждой строке выпуска не превышает `max_qty_per_hu` (если лимит задан), а для строк на одном HU суммарная загрузка паллеты также не превышает `1`.
  - Для совместимости со старыми черновиками при проверке/проведении выполняется auto-remap устаревшего `order_line_id` по `item_id` (только при однозначном совпадении строки заказа).
- Отгрузка по заказу: `OUTBOUND` может быть связан с заказом (`order_id/order_ref`).
  - При выборе заказа автоматически подставляются остатки по строкам заказа (`order_line_id`) с учетом уже закрытых отгрузок.
  - В `OUTBOUND` доступны только клиентские заказы (`order_type = CUSTOMER`); внутренние заказы не участвуют в клиентской отгрузке.
  - Если строка `OUTBOUND` содержит `from_location_id`, но не содержит `from_hu`, проведение распределяет списание по доступным остаткам этой локации: сначала без HU, затем по HU в порядке кода.
  - TSD показывает `pick list` HU/локаций по выбранной строке и позволяет привязать HU/локацию к строке отгрузки.
  - Для маркируемых SKU `pick list` строится по `km_code` (`status=OnHand`) с фильтром по заказу; для немаркируемых — по `ledger`.
  - В ручном `OUTBOUND` выбор товара фильтруется по остаткам источника (выбранные `location/HU`), чтобы показывать только реально отгружаемые позиции.

## Marking (KM) MVP
- Domain logic и хранение данных KM пока остаются в backend/DB, но клиентский workflow временно заморожен: WPF UI выключен, а validation/close документов не требует назначения KM.
- Импорт CSV/TSV кодов в `km_code_batch` + `km_code` (защита по hash файла и `UNIQUE(code_raw)`).
- Во время импорта KM проверки duplicate code выполняются внутри файла и против БД (без учета регистра), с нормализацией обернутых кавычек.
- При сопоставлении GTIN в КМ поддерживаются 13 и 14 цифр: 13-значный GTIN нормализуется до 14-значного добавлением ведущего `0`.
- При импорте КМ и при старте приложения выполняется auto-mark `items.is_marked=1` для SKU, найденных в `km_code` (включая сопоставление по GTIN), чтобы KM-товары не выпадали из сценариев выпуска/отгрузки.
- Привязка пакета к заказу через `order_id`.
- Коды в пуле имеют статус `InPool`; стандартный перевод в `OnHand` выполняется через документ `Выпуск продукции`.
- Для маркируемых SKU (`items.is_marked = true`) требуется привязать ровно `Qty` кодов к строке документа (`receipt_line_id`, `hu_id`, `location_id`).
- Распределение кодов в выпуске выполняется по строке (`HU+SKU+Qty`) из пула `InPool` по заказу или выбранному пакету.
- Отгрузка переводит коды в статус `Shipped` и связывает их с документом отгрузки.
- Для отгрузки ручной ввод KM не требуется: при проведении `OUTBOUND` коды `OnHand` подбираются и списываются автоматически по SKU, заказу (если указан) и источнику строки (`location/HU`, если заданы).
- В окне `OUTBOUND` для маркируемых строк доступны действия `Коды КМ...` и `Распределить`: можно открыть привязку кодов вручную (скан/ввод) или выполнить автоподбор из остатков.
- Если для `OUTBOUND` не хватает кодов `OnHand`, система добирает недостающее из пула `InPool` (по SKU/GTIN и заказу, если он указан), чтобы завершить распределение в отгрузке.
