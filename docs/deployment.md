# Деплой FlowStock

## Обзор
- Production deploy выполняется через `deploy/docker-compose.yml`.
- Имя compose-проекта зафиксировано как `flowstock`, чтобы ручные команды `docker compose` не создавали параллельный стек `deploy-*`.
- PostgreSQL init-скрипты в `deploy/postgres/init/` используются только для самого первого bootstrap пустого каталога данных.
- Все последующие изменения схемы применяются через версионируемые SQL-миграции из `deploy/postgres/migrations/`.
- На сервере должен быть обычный git clone репозитория.
- Обновления из GitHub выполняются вручную по запросу; автоматического deploy-loop нет.
- Рекомендуемый путь обновления:
  1. backup
  2. schema migration
  3. recreate контейнеров приложения
  4. проверка health

## URL-схема
- Production использует единый HTTPS origin на `7154`.
- `https://SERVER_IP:7154/` открывает PC web client.
- `https://SERVER_IP:7154/tsd/` открывает TSD web client.
- В `deploy/.env.example` по умолчанию используется только внешний HTTPS-порт `7154`.

## Обязательные файлы и каталоги
- `deploy/.env`
  - create it from `deploy/.env.example`
- `deploy/runtime/`
  - created automatically by scripts for manual backups and operator artifacts
- `FLOWSTOCK_CA_DIR`
  - external directory for local CA materials
  - do not store the CA private key in git

## Базовые образы
- По умолчанию серверный образ собирается из:
  - `mcr.microsoft.com/dotnet/sdk:8.0`
  - `mcr.microsoft.com/dotnet/aspnet:8.0`
- Если у сервера плохая доступность `mcr.microsoft.com`, переопределите образы в `deploy/.env`:
```bash
FLOWSTOCK_DOTNET_SDK_IMAGE=registry.example.com/mirror/dotnet/sdk:8.0
FLOWSTOCK_DOTNET_ASPNET_IMAGE=registry.example.com/mirror/dotnet/aspnet:8.0
```
- Это сохраняет текущий deploy workflow без изменений: `deploy_from_git.sh` и `deploy_update.sh` подхватят override автоматически.
- В проекте намеренно не зашит неофициальный fallback registry. Используйте собственный mirror, registry cache или заранее прогретый внутренний registry, если `mcr.microsoft.com` медленно доступен с production-сервера.

## Сервисы
- `postgres`
  - primary database
  - keeps bootstrap-only init scripts mounted at `/docker-entrypoint-initdb.d`
- `migrator`
  - one-shot service
  - waits for healthy Postgres
  - applies pending SQL files in lexical order
  - writes applied entries into `schema_migrations`
- `flowstock`
  - starts only after successful `migrator`
  - exposes `/health/live` and `/health/ready`
- `nginx`
  - starts after healthy `flowstock`
  - uses `deploy/nginx/certs/flowstock.crt` and `deploy/nginx/certs/flowstock.key`
- `pgbackup`
  - continuous scheduled `pg_dump -Fc` backups inside the compose stack

## Режимы TLS
- `FLOWSTOCK_TLS_MODE=local_ca`
  - recommended for internal LAN deployments
  - deploy scripts expect a local root CA in `FLOWSTOCK_CA_DIR`
  - server certificates are issued automatically into `deploy/nginx/certs/`
  - root CA private key stays outside the repository
- `FLOWSTOCK_TLS_MODE=manual`
  - deploy expects `deploy/nginx/certs/flowstock.crt` and `deploy/nginx/certs/flowstock.key` to already exist
  - use this only if certificates are managed outside the FlowStock deploy scripts

## Первичный bootstrap локального CA
Для внутреннего HTTPS один раз выполните bootstrap CA на сервере:
```bash
cd /opt/FlowStock
mkdir -p /opt/flowstock-secrets/ca
bash deploy/scripts/bootstrap_local_ca.sh
```

Будут созданы:
- `FLOWSTOCK_CA_DIR/flowstock-root-ca.crt`
- `FLOWSTOCK_CA_DIR/flowstock-root-ca.key`

Важно:
- keep the CA private key backed up securely and out of git
- do not leave the CA private key on operators' laptops unless they are responsible for certificate issuance
- client devices must trust `flowstock-root-ca.crt` once

## Доверие на клиентах
После bootstrap CA установите `flowstock-root-ca.crt` в доверенные корневые сертификаты на:
- Windows PCs that open the PC web client
- Android TSD devices that open the PWA

Это делается один раз на устройство, пока используется тот же root CA.

## Первый deploy на пустой production
1. Подготовьте env:
```bash
cd /opt/FlowStock
cp deploy/.env.example deploy/.env
```
2. Отредактируйте `deploy/.env`, указав реальный пароль PostgreSQL и нужные порты.
3. Один раз выполните bootstrap локального CA:
```bash
mkdir -p /opt/flowstock-secrets/ca
bash deploy/scripts/bootstrap_local_ca.sh
```
4. Установите сгенерированный root CA cert на клиентские устройства.
5. Выполните первый deploy:
```bash
cd /opt/FlowStock
bash deploy/scripts/deploy_update.sh
```
6. Проверьте health:
```bash
docker compose --project-name flowstock --env-file deploy/.env -f deploy/docker-compose.yml ps
curl -fsS http://127.0.0.1:${FLOWSTOCK_PORT:-8080}/health/ready
```

7. Проверьте зафиксированное состояние релиза:
```bash
bash deploy/scripts/release_status.sh
```

### Необязательная разовая проверка чистого bootstrap
Используйте только на заведомо пустом сервере или во временном тестовом проекте:
```bash
docker compose --project-name flowstock --env-file deploy/.env -f deploy/docker-compose.yml down -v
bash deploy/scripts/deploy_update.sh
```

## Обычное обновление production
Рекомендуемый путь обновления из GitHub:
```bash
cd /opt/FlowStock
bash deploy/scripts/deploy_from_git.sh
```

По умолчанию `deploy_from_git.sh`:
- делает `fetch` из `origin`
- резолвит `origin/main`
- отказывается продолжать, если в отслеживаемом worktree есть локальные изменения
- записывает метаданные попытки deploy
- выполняет fast-forward локальной ветки `main`
- запускает стандартный deployment flow
- записывает метаданные успешного релиза

Чтобы задеплоить конкретный tag или commit вместо `origin/main`:
```bash
cd /opt/FlowStock
bash deploy/scripts/deploy_from_git.sh v2026.04.10-1
```

Нижележащий `deploy_update.sh` остается доступен для случаев, когда репозиторий уже стоит на нужной ревизии и требуется только rebuild/restart:
```bash
cd /opt/FlowStock
bash deploy/scripts/deploy_update.sh
```

Что делает `deploy_update.sh`:
- ensures TLS assets are present
- in `local_ca` mode reissues the server certificate if it is missing, mismatched, or close to expiry
- validates `docker compose config`
- ensures `postgres` is up and healthy
- creates a manual pre-deploy dump into `deploy/runtime/backups/`
- pulls base images
- rebuilds `flowstock`
- runs the `migrator`
- recreates `flowstock`, `nginx`, and `pgbackup`
- waits for healthy `flowstock`

## Проверка сервера для git-driven deploy
Выполните это один раз после подготовки server clone:
```bash
cd /opt/FlowStock
git checkout main
git pull --ff-only origin main
bash deploy/scripts/release_status.sh
bash deploy/scripts/deploy_from_git.sh
```

Если сервер использует mirror для базовых образов, задайте `FLOWSTOCK_DOTNET_SDK_IMAGE` и `FLOWSTOCK_DOTNET_ASPNET_IMAGE` в `deploy/.env` до первого запуска.

Быстрая post-deploy проверка:
```bash
cd /opt/FlowStock
bash deploy/scripts/release_status.sh
docker compose --project-name flowstock --env-file deploy/.env -f deploy/docker-compose.yml ps
curl -fsS http://127.0.0.1:${FLOWSTOCK_PORT:-8080}/health/ready
```

## Ручной backup
Создать dump вручную:
```bash
cd /opt/FlowStock
bash deploy/scripts/backup_now.sh
```

Создать dump по конкретному пути:
```bash
bash deploy/scripts/backup_now.sh /opt/flowstock-backups/pre_release.dump
```

## Ручной запуск миграций
Применить pending migrations без пересоздания контейнеров приложения:
```bash
cd /opt/FlowStock
bash deploy/scripts/migrate.sh
```

Проверить примененные миграции:
```bash
docker compose --project-name flowstock --env-file deploy/.env -f deploy/docker-compose.yml exec -T postgres \
  sh -lc 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "SELECT version, filename, applied_at FROM schema_migrations ORDER BY version;"'
```

## Production backfill статусов ЧЗ
Для production-БД, где этикетки ЧЗ уже были напечатаны до появления новой модели, используйте Docker Compose wrapper. Host `dotnet` на Debian-сервере не требуется: команда выполняется внутри контейнера `flowstock`.

Перед применением обязательно сделайте свежий backup БД:
```bash
cd /opt/FlowStock
bash deploy/scripts/backup_now.sh
```

Dry-run, без изменения данных:
```bash
cd /opt/FlowStock
bash deploy/scripts/backfill_marking_status.sh --created-before 2026-04-30 --dry-run
```

Apply только с явным подтверждением:
```bash
cd /opt/FlowStock
bash deploy/scripts/backfill_marking_status.sh --created-before 2026-04-30 --apply --confirm APPLY
```

Скрипт:
- проверяет `docker compose config -q`;
- поднимает `postgres`;
- запускает SQL-миграции через `migrator`;
- собирает/использует maintenance image `flowstock`;
- обновляет только поля статуса маркировки в `orders`: `marking_status`, `marking_excel_generated_at`, `marking_printed_at`;
- не изменяет `ledger`, `docs`, `doc_lines` и не удаляет volumes.

## Восстановление из dump
Восстановить `.dump`-файл:
```bash
cd /opt/FlowStock
bash deploy/scripts/restore_dump.sh /opt/flowstock-backups/pre_release.dump
```

Что делает restore-скрипт:
- verifies compose config
- ensures Postgres is healthy
- creates a pre-restore safety backup
- stops `flowstock`, `nginx`, and `pgbackup`
- recreates the target database
- restores the supplied dump with `pg_restore`
- starts `pgbackup` again
- intentionally leaves `flowstock`/`nginx` stopped for explicit validation

## Статус релиза
Показать текущий git checkout и записанные метаданные релиза:
```bash
cd /opt/FlowStock
bash deploy/scripts/release_status.sh
```

Скрипт выводит:
- current git branch / detached state
- current checked out commit
- latest successful release metadata
- previous successful release metadata
- last attempted deployment metadata
- current `docker compose ps` output when Docker is available

## Путь rollback
Если обновление сломалось после backup или после пересоздания приложения:
1. Посмотрите статус и логи:
```bash
docker compose --project-name flowstock --env-file deploy/.env -f deploy/docker-compose.yml ps
docker compose --project-name flowstock --env-file deploy/.env -f deploy/docker-compose.yml logs --tail=100 migrator flowstock nginx postgres
```
2. Если проблема в несовместимости схемы и приложения, вернитесь на предыдущую рабочую ревизию:
```bash
git checkout <previous-good-commit-or-tag>
```
3. Восстановите pre-deploy dump, созданный `deploy_update.sh`:
```bash
bash deploy/scripts/restore_dump.sh /opt/FlowStock/deploy/runtime/backups/FlowStock_<timestamp>.dump
```
4. Поднимите предыдущую рабочую ревизию приложения:
```bash
docker compose --project-name flowstock --env-file deploy/.env -f deploy/docker-compose.yml up -d --build flowstock nginx pgbackup
```

Для типового сценария есть отдельный rollback helper:
```bash
cd /opt/FlowStock
bash deploy/scripts/rollback_release.sh
```

По умолчанию `rollback_release.sh`:
- checks out the previously recorded successful release revision
- restores the last recorded pre-deploy dump of the current release
- starts `flowstock`, `nginx`, and `pgbackup`
- records the rollback as the new latest successful release

Откатить только код приложения без восстановления БД:
```bash
bash deploy/scripts/rollback_release.sh --no-restore
```

Откатиться на конкретную ревизию и конкретный dump:
```bash
bash deploy/scripts/rollback_release.sh <git-ref> /opt/flowstock-backups/pre_release.dump
```

## Health checks
- Liveness:
```bash
curl -fsS http://127.0.0.1:${FLOWSTOCK_PORT:-8080}/health/live
```
- Readiness:
```bash
curl -fsS http://127.0.0.1:${FLOWSTOCK_PORT:-8080}/health/ready
```

Readiness возвращает успех только если приложение может открыть подключение к PostgreSQL и история миграций присутствует в `schema_migrations`.

## Ручные операции с сертификатами
Принудительно перевыпустить серверный сертификат от локального CA:
```bash
cd /opt/FlowStock
bash deploy/scripts/renew_server_cert.sh --force
docker compose --project-name flowstock --env-file deploy/.env -f deploy/docker-compose.yml restart nginx
```

Немедленно выпустить текущий серверный сертификат:
```bash
cd /opt/FlowStock
bash deploy/scripts/issue_server_cert.sh
docker compose --project-name flowstock --env-file deploy/.env -f deploy/docker-compose.yml restart nginx
```

## Примечания
- Migration-файлы применяются в лексикографическом порядке, поэтому сохраняйте схему именования `V0001__name.sql`.
- Migration-файлы выполняются раннером внутри транзакции. При любой SQL-ошибке процесс миграции завершается с non-zero кодом, а файл не записывается в `schema_migrations`.
- `deploy/postgres/init/001_init.sql` намеренно минимален. Он только bootstrap'ит `schema_migrations` для нового Postgres volume.
- WPF-клиент больше не отвечает за создание production schema. Если в БД нет миграций, приложение явно сообщает об этом состоянии вместо скрытого создания/обновления схемы.
- Держите server-side clone репозитория чистым. `deploy_from_git.sh` останавливается, если на сервере вручную изменяли tracked files.
- Метаданные релизов хранятся в `deploy/runtime/releases/`.
- `deploy/nginx/gen_cert.sh` остается только как быстрый self-signed fallback; рекомендуемый режим для внутреннего production — `FLOWSTOCK_TLS_MODE=local_ca`.
