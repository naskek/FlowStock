# Документация FlowStock

Краткая карта: что читать первым, что устарело и где лежит архив.

## Читать в первую очередь

| Документ | Назначение |
|----------|------------|
| [`spec.md`](spec.md) | Сервер, TSD, WPF, `ledger`, документы, производство, маркировка, диагностика |
| [`spec_orders.md`](spec_orders.md) | Заказы, резерв HU, план паллет, отгрузка, ЧЗ из заказа, UI-правила WPF; canonical схема таблиц order-домена |
| [`deployment.md`](deployment.md) | Deploy, backup, миграции, health, rollback |
| [`../deploy/docs/operations/`](../deploy/docs/operations/) | Разовые операционные процедуры: cutover ЧЗ, backfill статусов ЧЗ, полная проверка UDP discovery |
| [`../AGENTS.md`](../AGENTS.md) | Правила для агентов (Codex/Cursor) |

Компонентный README (если нужен контекст клиента): [`../apps/android/tsd/README.md`](../apps/android/tsd/README.md).

## Источники истины

Актуальный контракт продукта — только:

- `spec.md`
- `spec_orders.md`
- `deployment.md` + `deploy/docs/operations/` (для эксплуатации)
- `AGENTS.md` (для процесса разработки)

Иерархия внутри источников:

- схема таблиц БД: canonical описание полей order-домена — в `spec_orders.md`; `spec.md` даёт краткий перечень и ссылается на него. Фактическая структура БД определяется миграциями `deploy/postgres/migrations/`; при расхождении спеки с миграциями актуальны миграции, а спека подлежит исправлению;
- maintenance-команды (backfill, transition, repair): полные правила — в разделах Maintenance `spec_orders.md` и в `spec.md`; процедуры запуска на production-сервере — в `deploy/docs/operations/`.

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

## Правила ведения спек

- Спеки описывают **целевое состояние** системы, без привязки к номерам PR, веткам и фазам. Разовые переходы и процедуры живут в `deploy/docs/operations/` или в release notes.
- Каждый факт схемы/контракта описывается в одном документе; остальные ссылаются, а не копируют.

## Дополнительные ADR (не архив)

Дополнение к спекам, не замена:

- [`architecture/incoming-requests-order-api-convergence.md`](architecture/incoming-requests-order-api-convergence.md)
- [`architecture/server-operation-logging.md`](architecture/server-operation-logging.md)
