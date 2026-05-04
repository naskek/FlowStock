# Исправление effective-статуса ЧЗ

## Причина

`MarkingStatusMapper.ToEffectiveStatus` сначала учитывал текущую вычисленную потребность `marking_required`. Если текущая потребность становилась `false`, уже сохраненный lifecycle-статус `PRINTED` маскировался как `NOT_REQUIRED`.

Дополнительно `/api/orders` использовал `GetMarkingOrderQueue(includeCompleted: true)` как overlay для статуса. Эта очередь фильтруется по текущему `qty_for_marking > 0`, поэтому она не является корректным источником общего статуса списка заказов.

## Исправление

- `PRINTED` теперь имеет приоритет в effective-статусе.
- Старое значение БД/API `EXCEL_GENERATED` читается как `PRINTED` и не записывается новыми операциями.
- `/api/orders` и `/api/orders/{id}` возвращают статус из `Order` read-model, а не из очереди ЧЗ.
- Backfill восстанавливает `PRINTED` по историческому lifecycle-признаку: legacy `EXCEL_GENERATED`, `marking_excel_generated_at` или `marking_printed_at`.
- Backfill по-прежнему меняет только `orders.marking_status`, `orders.marking_excel_generated_at`, `orders.marking_printed_at`.

## Проверки

Добавлены regression tests для:

- текущей ЧЗ-потребности при raw `NOT_REQUIRED`;
- сохраненного `PRINTED` без текущей ЧЗ-потребности;
- legacy `EXCEL_GENERATED`, который отображается как `PRINTED` / `ЧЗ готов к нанесению`;
- восстановления `PRINTED` backfill-ом по историческому lifecycle-признаку;
- обычного немаркируемого заказа без истории ЧЗ.
