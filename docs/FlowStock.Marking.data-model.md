# FlowStock.Marking: модель данных

## Назначение
Документ фиксирует legacy-модель данных для старого `FlowStock.Marking`.

> Важно: эта модель заморожена и не используется новой упрощенной ЧЗ-очередью заказов. Новая логика определяется через `item_types.enable_marking + items.gtin` и описана в `spec.md` / `spec_orders.md`.

## Общая идея
- домен маркировки должен жить внутри основного репозитория FlowStock
- основное состояние должно храниться в БД, а не во внешнем print-helper
- модель должна поддерживать импорт кодов, связь с заказами, печатные batch-и и аудит изменений

## Ключевые сущности
- `marking_order`
- `marking_code_import`
- `marking_code`
- `marking_print_batch`
- `marking_print_batch_code`
