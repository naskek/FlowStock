# Исправление effective-статуса ЧЗ

## Причина

`MarkingStatusMapper.ToEffectiveStatus` сначала учитывал текущую вычисленную потребность `marking_required`. Если текущая потребность становилась `false`, уже сохраненный lifecycle-статус `PRINTED` или `EXCEL_GENERATED` маскировался как `NOT_REQUIRED`.

Дополнительно `/api/orders` использовал `GetMarkingOrderQueue(includeCompleted: true)` как overlay для статуса. Эта очередь фильтруется по текущему `qty_for_marking > 0`, поэтому она не является корректным источником общего статуса списка заказов.

## Исправление

- `PRINTED` и `EXCEL_GENERATED` теперь имеют приоритет в effective-статусе.
- `/api/orders` и `/api/orders/{id}` возвращают статус из `Order` read-model, а не из очереди ЧЗ.
- Backfill восстанавливает `PRINTED` по историческому lifecycle-признаку: `EXCEL_GENERATED`, `marking_excel_generated_at` или `marking_printed_at`.
- Backfill по-прежнему меняет только `orders.marking_status`, `orders.marking_excel_generated_at`, `orders.marking_printed_at`.

## Проверки

Добавлены regression tests для:

- текущей ЧЗ-потребности при raw `NOT_REQUIRED`;
- сохраненных `PRINTED` и `EXCEL_GENERATED` без текущей ЧЗ-потребности;
- восстановления `PRINTED` backfill-ом по историческому lifecycle-признаку;
- обычного немаркируемого заказа без истории ЧЗ.
