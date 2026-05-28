# Архив документации FlowStock

Документы в `docs/archive/**` **не являются источником истины**. Они сохранены для истории реализации, RFC, migration notes, test matrix и исследовательских заметок.

## Правило конфликта

При расхождении между архивным документом и активными спецификациями приоритет имеют:

- [`spec.md`](../spec.md)
- [`spec_orders.md`](../spec_orders.md)
- [`deployment.md`](../deployment.md)

Актуальная карта документации: [`README.md`](../README.md).

## Каталоги

| Каталог | Содержимое |
|---------|------------|
| [`architecture/`](architecture/) | current-state, implementation notes, server-contract, wpf-migration, test-spec/matrix, delta/RFC по операциям |
| [`marking/`](marking/) | legacy и исследовательские заметки по ЧЗ/маркировке до order-based модели |
| [`tasks/`](tasks/) | Warehouse Task Board (`spec_tasks.md`) — deprecated / removal candidate |

Документы могут быть устаревшими и не отражать текущее поведение продукта.
