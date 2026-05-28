# AGENTS.md (FlowStock)

Правила для Codex, Cursor и других агентов, изменяющих этот репозиторий.

## Язык

- Общение с пользователем по репозиторию — **на русском**.
- Все Markdown-файлы (`*.md`) поддерживаются **на русском**.

## Обязательное чтение перед изменениями

Перед правками кода или документации агент **обязан** прочитать:

1. этот файл — `AGENTS.md`;
2. релевантные разделы [`docs/spec.md`](docs/spec.md);
3. релевантные разделы [`docs/spec_orders.md`](docs/spec_orders.md);
4. [`docs/deployment.md`](docs/deployment.md) — если задача касается deploy, backup, миграций, Docker/compose.

Карта документации: [`docs/README.md`](docs/README.md).

## Что не является источником истины

- **`docs/archive/**`** — архив; может быть устаревшим.
- Старые **RFC**, **implementation notes**, **migration notes**, **test matrix**, **current-state**, **server-contract**, **wpf-migration** — не использовать как актуальный контракт.
- **Warehouse Task Board** ([`docs/archive/tasks/spec_tasks.md`](docs/archive/tasks/spec_tasks.md)) — **deprecated / removal candidate**; не задавать правила normal TSD flow.

При конфликте побеждают **`docs/spec.md`** и **`docs/spec_orders.md`**.

## Инварианты продукта (не нарушать)

- Остатки считаются **только** из `ledger`.
- Документы влияют на остатки **только** через `Close` / серверную транзакцию проведения.
- Закрытые документы **неизменяемы**; исправления — отдельными correction/storno-документами.
- Сервер/API — источник истины для: production need, CUSTOMER HU reservation, production pallet plan, marking preview/export, TSD filling/outbound.
- WPF/Web/TSD **не** являются источником истины для production quantities и **не** должны отправлять клиентски рассчитанные количества как финальное решение.

## Изменение поведения

Если меняется поведение системы, в **том же PR** обновляются соответствующие разделы `docs/spec.md` и/или `docs/spec_orders.md`.

## Правила работы

- Сначала короткий план (список шагов).
- **Минимальные диффы**; без побочных рефакторингов.
- После изменений в runtime-коде — build и релевантные тесты (или явное объяснение, почему нет).
- **Не выполнять destructive-команды** (`rm -rf`, `git reset --hard`, `docker system prune`, ручные правки production БД без backup) без **явного** запроса пользователя.

## Production

- Не предлагать ручные правки БД без **свежего backup**.
- Сначала API, логи, диагностические endpoints (`/api/diagnostics/*`, maintenance dry-run).
- Не трогать production без backup.

## Команды проверки

```bash
dotnet build apps/windows/FlowStock.sln
dotnet test apps/windows/FlowStock.sln
docker compose --project-name flowstock --env-file deploy/.env -f deploy/docker-compose.yml config -q
```

- `docker compose ... config -q` — только если менялись `deploy/`, compose или env-шаблоны.
- Для **docs-only** PR build/test можно не запускать; в отчёте явно указать, что runtime-код не менялся.

## Карта репозитория

- `apps/windows/*` — WPF (.NET 8), `FlowStock.Server` — Minimal API + Postgres
- `apps/android/tsd` — TSD PWA (online через API)
- `deploy/` — Dockerfile, compose, миграции
