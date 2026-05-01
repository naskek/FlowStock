# Ленивая загрузка заказов в PC web

## Зачем

PC web список заказов раньше запрашивал весь `/api/orders?include_internal=1&include_pending_requests=1` при каждом открытии и поиске. При росте истории это перегружает сеть и браузер.

## Что изменено

- `/api/orders` получил optional query params `limit` и `offset`.
- Вызов без `limit` сохраняет прежнее поведение и прежнюю форму ответа.
- Paged-вызов использует отдельный путь чтения `GetOrdersPage(...)` и SQL `LIMIT/OFFSET`.
- PC web загружает 21 строку, показывает 20, а 21-ю использует как признак следующей страницы.
- Кнопка `Загрузить еще` догружает следующую пачку и блокируется на время запроса.
- При поиске список сбрасывается и снова загружает первую страницу.

## Сортировка

В paged-режиме сервер отдает заказы по операторскому приоритету:

- `IN_PROGRESS` и `DRAFT`;
- `ACCEPTED`;
- `SHIPPED`;
- `CANCELLED`.

Внутри группы новые заказы идут выше.

## Совместимость

- WPF и старые клиенты без `limit`/`offset` продолжают получать массив заказов как раньше.
- PC web сохраняет текущие бейджи: статус заказа использует `order_status`, ЧЗ использует `marking_effective_status` и `marking_status_display`.

## Проверки

- `dotnet build apps/windows/FlowStock.Server/FlowStock.Server.csproj /nodeReuse:false` - успешно.
- `dotnet test apps/windows/FlowStock.Server.Tests/FlowStock.Server.Tests.csproj /nodeReuse:false` - успешно: 246 passed, 8 skipped.
- `dotnet build apps/windows/FlowStock.App/FlowStock.App.csproj /nodeReuse:false` - успешно.
- `node --check apps/android/tsd/pc/app.js` - успешно.
- `node apps/android/tsd/pc/app.presentation.test.js` - успешно.
- Локально проверено `/api/orders?include_internal=1&limit=2&offset=0`, `/api/orders?include_internal=1&limit=2&offset=2` и поиск `q` вместе с pagination.
