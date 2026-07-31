# ADR: управляемое разрешение ранней частичной отгрузки

Статус: принято.

## Контекст

Иногда CUSTOMER-заказ ещё не готов целиком, но часть принадлежащих ему HU уже физически доступна по `ledger`. Автоматически показывать такие заказы в TSD нельзя: это смешало бы фактическую готовность HU с явным операционным решением разрешить ранний подбор.

## Решение

- Хранить permission на сервере в `orders.allow_partial_outbound`, default `false`; readiness остаётся отдельным вычисляемым фактом.
- Изменять permission отдельной пользовательской командой, а не клиентским полем общего сохранения заказа. Это исключает lost update от stale WPF-модели; единственное server-side следствие generic update — атомарный сброс при смене типа `CUSTOMER → INTERNAL`.
- Использовать существующий structured server operation log без отдельной audit-таблицы. Verified actor и WPF-аутентификация в решение не входят; `device_id` не является identity.
- При терминальном переходе `SHIPPED`, `CANCELLED`, `MERGED` атомарно сбрасывать permission и закрепить invariant CHECK constraint.
- Сохранить две реализации eligibility — optimized PostgreSQL и C# fallback — с одинаковыми именованными facts, truth table и integration parity tests.

## Последствия

Permission позволяет показать только реально доступные HU и не отменяет отдельное подтверждение partial complete. Generic order update не владеет этим полем. Diagnostic logging может быть недоступен и не участвует в atomic business commit.

CHECK constraint создаёт data-dependent rollback compatibility. Полная операционная семантика, включая различие состояний «все permissions false» и «есть active permission=true», зафиксирована в [`docs/deployment.md`](../deployment.md).
