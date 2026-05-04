# Унификация статуса ЧЗ в заказах

## Root Cause

Основной список заказов и окно `Маркировка` считали effective-статус ЧЗ разными путями. `/api/orders` шел через `OrderService.GetOrders()`, где `ApplyAutoStatus()` пересоздавал `Order` и не копировал поля `MarkingStatus`, `MarkingExcelGeneratedAt`, `MarkingPrintedAt` и `MarkingRequired`. Поэтому сохраненный `PRINTED` после чтения из `PostgresDataStore` превращался в default `NOT_REQUIRED`.

`/api/marking/orders` шел через `GetMarkingOrderQueue()` и читал `orders.marking_status` напрямую, поэтому тот же заказ отображался как `PRINTED`.

Дополнительно очередь ЧЗ нормализовала статус локально в `MarkingExcelService`. При этом `marking_required` мог маскировать сохраненный lifecycle-статус или превращать отмененный заказ в активное `Требуется`.

## Новое Правило

Единый resolver статуса находится в Core:

- `orders.marking_status = PRINTED` всегда имеет приоритет и отображается как `ЧЗ готов к нанесению`;
- legacy `EXCEL_GENERATED` парсится как `PRINTED`, новые записи его не генерируют;
- `marking_required` влияет только на `REQUIRED`, если сохраненный статус не `PRINTED`;
- `CANCELLED` с маркируемыми строками не отображается как активное `Требуется`, если статус еще не `PRINTED`.

`/api/orders`, `/api/orders/{id}`, `/api/marking/orders`, WPF список заказов и WPF окно маркировки используют этот resolver или effective-статус, полученный из API.
`OrderService.ApplyAutoStatus()` теперь сохраняет persisted marking-поля при пересчете статуса заказа.

## Проверки

- `dotnet build apps/windows/FlowStock.Server/FlowStock.Server.csproj /nodeReuse:false` - успешно.
- `dotnet test apps/windows/FlowStock.Server.Tests/FlowStock.Server.Tests.csproj /nodeReuse:false` - успешно: 243 passed, 8 skipped.
- `dotnet build apps/windows/FlowStock.App/FlowStock.App.csproj /nodeReuse:false` - успешно.

Первый запуск server build был заблокирован локально запущенным `FlowStock.Server` из `bin\Debug`; процесс был остановлен, после чего проверка прошла.

## Ручная Проверка API

Локально запущен `FlowStock.Server` на `http://127.0.0.1:7154`.

Для `order_ref = 002`:

- `/api/orders?include_internal=1`: `marking_status = PRINTED`, `marking_effective_status = PRINTED`, `marking_status_display = "ЧЗ готов к нанесению"`;
- `/api/marking/orders?include_completed=1`: `marking_status = PRINTED`, `marking_status_display = "ЧЗ готов к нанесению"`, `marking_line_count = 1`, `marking_code_count = 500`.

Локальный server после проверки остановлен.
