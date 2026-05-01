# Унификация статуса ЧЗ в заказах

## Root Cause

Основной список заказов и окно `Маркировка` считали effective-статус ЧЗ разными путями. `/api/orders` шел через `Order.EffectiveMarkingStatus`, а очередь ЧЗ нормализовала статус локально в `MarkingExcelService`. При этом `marking_required` мог маскировать сохраненный lifecycle-статус или превращать отмененный заказ в активное `Требуется`.

## Новое Правило

Единый resolver статуса находится в Core:

- `orders.marking_status = PRINTED` всегда имеет приоритет и отображается как `ЧЗ готов к нанесению`;
- legacy `EXCEL_GENERATED` парсится как `PRINTED`, новые записи его не генерируют;
- `marking_required` влияет только на `REQUIRED`, если сохраненный статус не `PRINTED`;
- `CANCELLED` с маркируемыми строками не отображается как активное `Требуется`, если статус еще не `PRINTED`.

`/api/orders`, `/api/orders/{id}`, `/api/marking/orders`, WPF список заказов и WPF окно маркировки используют этот resolver или effective-статус, полученный из API.

## Проверки

- `dotnet build apps/windows/FlowStock.Server/FlowStock.Server.csproj /nodeReuse:false` - успешно.
- `dotnet test apps/windows/FlowStock.Server.Tests/FlowStock.Server.Tests.csproj /nodeReuse:false` - успешно: 241 passed, 8 skipped.
- `dotnet build apps/windows/FlowStock.App/FlowStock.App.csproj /nodeReuse:false` - успешно.

Первый запуск server build был заблокирован локально запущенным `FlowStock.Server` из `bin\Debug`; процесс был остановлен, после чего проверка прошла.
