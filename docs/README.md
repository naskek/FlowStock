# Документация FlowStock

Краткая карта: что читать первым, что устарело и где лежит архив.

## Читать в первую очередь

| Документ | Назначение |
|----------|------------|
| [`spec.md`](spec.md) | Сервер, TSD, WPF, `ledger`, документы, производство, маркировка, диагностика |
| [`spec_orders.md`](spec_orders.md) | Заказы, резерв HU, план паллет, отгрузка, ЧЗ из заказа, UI-правила WPF |
| [`deployment.md`](deployment.md) | Deploy, backup, миграции, maintenance-скрипты |
| [`../AGENTS.md`](../AGENTS.md) | Правила для агентов (Codex/Cursor) |

Компонентный README (если нужен контекст клиента): [`../apps/android/tsd/README.md`](../apps/android/tsd/README.md).

## Источники истины

Актуальный контракт продукта — только:

- `spec.md`
- `spec_orders.md`
- `deployment.md` (для эксплуатации)
- `AGENTS.md` (для процесса разработки)

## Deprecated / removal candidate

- **Warehouse Task Board** — [`archive/tasks/spec_tasks.md`](archive/tasks/spec_tasks.md)
- Не использовать для проектирования normal TSD `Наполнение` / `Отгрузка`.

## Архив

| Путь | Содержимое |
|------|------------|
| [`archive/architecture/`](archive/architecture/) | Исторические current-state, contracts, test matrix, RFC/delta |
| [`archive/marking/`](archive/marking/) | Legacy/исследовательские заметки по ЧЗ |
| [`archive/tasks/`](archive/tasks/) | Warehouse Task Board |

См. также [`archive/README.md`](archive/README.md).

## Правило конфликта

Если архивный документ, RFC или implementation note противоречит `spec.md` / `spec_orders.md`, **актуальны активные спеки**.

## Дополнительные ADR (не архив)

Дополнение к спекам, не замена:

- [`architecture/incoming-requests-order-api-convergence.md`](architecture/incoming-requests-order-api-convergence.md)
- [`architecture/server-operation-logging.md`](architecture/server-operation-logging.md)
