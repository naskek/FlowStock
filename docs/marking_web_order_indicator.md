# ЧЗ-индикатор в PC web заказах

## Что найдено

PC web список заказов реализован в `apps/android/tsd/pc/app.js` и получает данные из `/api/orders?include_internal=1&include_pending_requests=1`. API уже отдает поля ЧЗ, поэтому backend не менялся.

## Измененные файлы

- `apps/android/tsd/pc/app.js`
- `apps/android/tsd/pc/styles.css`
- `docs/spec_orders.md`
- `docs/marking_web_order_indicator.md`

## Отображение

- Серый индикатор: `ЧЗ не требуется`.
- Оранжевый индикатор: `Требуется ЧЗ`.
- Зеленый индикатор: `ЧЗ готов к нанесению`.

Индикатор компактный, находится в отдельной колонке `ЧЗ`, использует `title` и `aria-label`.

## Проверки

- `dotnet build apps/windows/FlowStock.Server/FlowStock.Server.csproj /nodeReuse:false` - успешно.
- `dotnet test apps/windows/FlowStock.Server.Tests/FlowStock.Server.Tests.csproj /nodeReuse:false` - успешно: 237 passed, 8 skipped.
- `dotnet build apps/windows/FlowStock.App/FlowStock.App.csproj /nodeReuse:false` - успешно.
- `node --check apps/android/tsd/pc/app.js` - успешно.
- PC web не имеет отдельной npm/vite/build-команды.
- `git grep -n "маркировка проведена\|наклейки нанесены\|отпечатана\|отпечатано\|EXCEL_GENERATED\|ExcelGenerated" -- apps docs` - запрещенные пользовательские формулировки не найдены; `EXCEL_GENERATED` остался только в legacy compatibility/test/docs местах.
