# Архив: маркировка (ЧЗ)

**Статус каталога:** архивный / legacy.

Документы `FlowStock.Marking.*`, `marking_status_*` и связанные заметки описывают старые или исследовательские модели. Они **не задают** текущий операторский workflow.

## Актуальная модель (см. спеки)

- Маркируемость: `item_types.enable_marking` + непустой `items.gtin`
- Основной workflow: order-based — `GET/POST /api/orders/{orderId}/marking/preview|export` из карточки заказа
- Excel ЧЗ для `CUSTOMER`: только на производственную нехватку / planned production; складской stock считается уже промаркированным
- Источник истины export/preview: сервер по заказу

## Источник истины

- [`docs/spec.md`](../../spec.md) — раздел «Маркировка ЧЗ»
- [`docs/spec_orders.md`](../../spec_orders.md) — «Маркировка ЧЗ из заказа»

При конфликте побеждают активные спеки.
