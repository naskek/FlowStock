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

## GitHub workflow и контроль качества

- Обязательный lifecycle: **`Issue → проверка main/specs/истории и baseline → отдельная branch/worktree → implementation + regression tests → commit/push → PR → CI → review актуального diff/head → применимый локальный smoke → явное подтверждение пользователя → merge → проверка main → безопасная cleanup → отдельный deploy gate`**.
- Не начинать изменение кода без GitHub Issue с ожидаемым поведением, scope и acceptance criteria. Один логический fix — один Issue и один PR; не добавлять в PR посторонний рефакторинг.
- Перед implementation сверять актуальный `main`, релевантную историю/тесты/specs и production/stabilization baseline, если он отличается от `main`. При конфликте кода и документации установить фактический контракт и зафиксировать расхождение.
- После commit/push открыть PR в `main`, дождаться обязательных GitHub Actions checks. Ошибки CI исправлять в той же ветке, не обходить checks. Green CI **не заменяет** code review.
- Review выполнять в текущем ChatGPT-чате через GitHub **для актуального head**: соответствие Issue, минимальный scope, regression coverage, safety/инварианты, риск соседних сценариев. После исправлений в PR снова проверить новый head, CI и применимые проверки.
- **WPF UI/UX:** перед merge запускать локальный визуальный smoke из **точной ветки/commit PR** в отдельном worktree через `FLOWSTOCK.cmd devui` или `tools/windows/start-flowstock-ui-preview.ps1`. Пользователь смотрит интерфейс и явно подтверждает результат. `UI Preview / DEV` не доказывает работоспособность production API/DB/updater: если Issue затрагивает runtime-сценарии, добавлять отдельную подходящую проверку.
- Для задач без UI выбирать smoke по характеру изменения; если production-поведение нельзя проверить в CI, отдельно указывать локальную integration-проверку, production-copy smoke и будущий production/operator smoke. **Ручной smoke на изолированной копии production PostgreSQL + attestation exact Git tree** остаётся обязательным gate для применимых production-impacting изменений по правилам ниже.
- Любое изменение после подтверждённого smoke, влияющее на его предмет, требует повторного smoke на новом PR head. Для изменений deploy-tree повторять также требуемую exact-tree attestation. Не переносить подтверждение со старого commit на новый без проверки.
- **Merge в `main` разрешён только после отдельного явного подтверждения пользователя.** Tags, Releases, production/update channel, secrets и production deploy также требуют отдельного явного разрешения. Merge **не означает** deploy.
- **Cleanup после успешного merge:** сначала убедиться, что PR merged, затем проверить `git status` и отсутствие незакоммиченных/неотслеживаемых нужных файлов в конкретном worktree; удалить только принадлежащие задаче временный worktree и feature-ветку. Удалённую ветку удалять только после merge, если GitHub ещё не удалил её автоматически. Не использовать `git reset --hard`, `git clean`, принудительное удаление worktree/веток или очистку чужих локальных изменений. Без прямого доступа к Windows-машине давать PowerShell-команды, но не сообщать, что локальная очистка выполнена.
- Завершать задачу отчётом: Issue/PR, exact head/merge SHA, CI, review, smoke и отдельно состояние remote/local cleanup и deploy.

## Production

- `main` — единственная stable/production-ветка; недотестированные runtime/schema/business изменения в неё не мержить.
- Обязательный путь production-impacting изменения: `Issue → branch → implementation → tests → PR → CI → review → ручной smoke на изолированной копии production PostgreSQL (exact-tree attestation) → явное подтверждение merge → merge → отдельное разрешение deploy`.
- Любой production deploy требует локальную attestation-запись успешного production-copy smoke для **точного Git tree**, который разворачивается. CI не заменяет этот gate.
- Если после smoke изменился Git tree (rebase, merge conflict, новые файлы/коммиты), smoke и attestation нужно повторить.
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
