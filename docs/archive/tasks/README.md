# Архив: Warehouse Task Board

**Статус:** deprecated / removal candidate.

[`spec_tasks.md`](spec_tasks.md) описывает экспериментальный поток «пакеты действий → TSD tasks → confirm-execution в WPF». Это **не** нормальный TSD flow производства и отгрузки.

## Нормальные TSD-потоки (источник истины)

См. [`docs/spec.md`](../../spec.md):

- **Наполнение** — production pallets, `ProductionAutoCloseOnFill`, запись `ledger` через серверное закрытие PRD
- **Отгрузка** — outbound по зарезервированным HU, `OutboundAutoCloseOnComplete`

## Источник истины

- [`docs/spec.md`](../../spec.md)
- [`docs/spec_orders.md`](../../spec_orders.md)

При конфликте побеждают активные спеки, а не Warehouse Task Board.
