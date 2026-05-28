# ЧЗ-индикатор в PC web заказах

## Что найдено

PC web список заказов реализован в `apps/android/tsd/pc/app.js` и получает данные из `/api/orders?include_internal=1&include_pending_requests=1`.

Root cause текущего UX-расхождения: PC web восстанавливал код статуса заказа из display-строки `status`. Значение `Отменён`/`CANCELLED` не распознавалось, поэтому отмененный заказ попадал в fallback `В работе`.

## Измененные файлы

- `apps/android/tsd/pc/app.js`
- `apps/android/tsd/pc/app.presentation.test.js`
- `apps/android/tsd/pc/styles.css`
- `apps/windows/FlowStock.Server/OrderApiMapper.cs`
- `apps/windows/FlowStock.Server.Tests/Marking/OrderApiMapperTests.cs`
- `docs/spec_orders.md`
- `docs/marking_web_order_indicator.md`

## Отображение

- Серый индикатор: `Маркировка не требуется`.
- Оранжевый индикатор: `Требуется ЧЗ`.
- Зеленый индикатор: `ЧЗ готов к нанесению`.

Индикатор компактный, находится в отдельной колонке `ЧЗ`, использует общий стиль status-бейджей заказа: иконка, текст, высота, скругление и типографика совпадают с колонкой `Статус`.

Состояние ЧЗ берется только из `marking_effective_status` API заказов. Текст/tooltip берется из `marking_status_display`, при этом `REQUIRED` в компактной колонке сокращается до `Требуется ЧЗ`, а legacy `EXCEL_GENERATED` отображается как `PRINTED` / `ЧЗ готов к нанесению`. Frontend не использует строки заказа, `marking_required` или raw `marking_status`.

Статус заказа в PC web использует canonical `order_status` из `/api/orders` для выбора тона бейджа. Пользовательский текст остается из `status`/`order_status_display`; `CANCELLED` отображается как `Отменён`, а не как fallback `В работе`.

## Проверки

- `dotnet build apps/windows/FlowStock.Server/FlowStock.Server.csproj /nodeReuse:false` - успешно.
- `dotnet test apps/windows/FlowStock.Server.Tests/FlowStock.Server.Tests.csproj /nodeReuse:false` - успешно: 244 passed, 8 skipped.
- `dotnet build apps/windows/FlowStock.App/FlowStock.App.csproj /nodeReuse:false` - успешно.
- `node --check apps/android/tsd/pc/app.js` - успешно.
- `node apps/android/tsd/pc/app.presentation.test.js` - успешно.
- PC web не имеет отдельной npm/vite/build-команды.
- `git grep -n "маркировка проведена\|наклейки нанесены\|отпечатана\|отпечатано\|EXCEL_GENERATED\|ExcelGenerated" -- apps docs` - запрещенные пользовательские формулировки не найдены; `EXCEL_GENERATED` остался только в legacy compatibility/test/docs местах.
- Локально запущенный PC web отдал страницу `/` со статусом 200 и подключенными `app.js`/`styles.css`.
- В локальной БД через `/api/orders?include_internal=1` проверен отмененный заказ: `order_status = CANCELLED`, `status = Отменён`.
