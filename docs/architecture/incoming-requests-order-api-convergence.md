# Архитектурная заметка: сведение входящих запросов по заказам к единому API-потоку

**Статус:** accepted (дополнение к спекам; не заменяет `spec.md` / `spec_orders.md`).

## Контекст

Исторически WPF подтверждал `CREATE_ORDER` и `SET_ORDER_STATUS` двумя независимыми вызовами: сначала выполнял canonical mutation, затем вызывал `/resolve`. При сетевой ошибке или гонке canonical результат мог существовать без конечного статуса заявки, а повторное подтверждение могло создать второй результат.

## Решение

- Единственная команда управления заявкой — server-side confirm/reject по `request_id`.
- Authorization выражена capability `ManagePendingRequests` и не зависит от `request_type`, `CUSTOMER` или `INTERNAL`. PC получает capability из актуальной роли `ADMIN`; WPF подтверждает доверенный клиент machine API key.
- OS username WPF передаётся только как trusted audit label. PC actor выводится сервером из authenticated session.
- Dispatcher содержит явные handlers фактически поддерживаемых типов `CREATE_ORDER` и `SET_ORDER_STATUS`; неизвестный тип остаётся `PENDING` и получает `422`.
- Confirm открывает одну транзакцию и на одном transaction-scoped `IDataStore` выполняет `SELECT ... FOR UPDATE`, canonical mutation и условный terminal update заявки. Transaction-aware order create переиспользует `CreateOrderCoreInTransaction`; status transition также не открывает вложенный commit.
- Reject использует ту же блокировку и terminal update без business mutation. Повторный или конкурентный запрос получает `409`, не создавая второго canonical результата.
- Legacy `/resolve` удалён. Прямой `POST /api/orders` остаётся только доверенным WPF flow и защищён тем же machine key.

Основной продуктовый контракт и rollout-ограничения зафиксированы в `docs/spec.md`, `docs/spec_orders.md` и `docs/deployment.md`.
