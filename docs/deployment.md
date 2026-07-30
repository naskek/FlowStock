# Деплой FlowStock

> **Compatibility warning для HU correction.** После первого committed `CORRECT_FILLED` в данных появляется `production_pallets.status = CORRECTED`. Старый runtime с условиями вида `status <> 'CANCELLED'` может ошибочно считать такую историческую ревизию активной. До применения миграции нужен свежий PostgreSQL backup. Если функция ещё не использовалась, additive schema допускает обычный code rollback. После появления `CORRECTED` сначала выключите `pc_hu_correction`; запуск старого runtime допускается только после forward-fix либо восстановления согласованного pre-deploy backup. Production deploy и backup выполняет пользователь вручную по действующему каноническому FlowStock PowerShell-процессу; Compose запускается из `/opt/FlowStock` с явными `-p flowstock -f deploy/docker-compose.yml`.

Этот документ — постоянный runbook деплоя. Разовые операционные процедуры (cutover ЧЗ, backfill статусов маркировки) вынесены в `deploy/docs/operations/`.

## Обзор

- Production deploy выполняется через `deploy/docker-compose.yml`.
- Имя compose-проекта зафиксировано как `flowstock`, чтобы ручные команды `docker compose` не создавали параллельный стек `deploy-*`.
- PostgreSQL init-скрипты в `deploy/postgres/init/` используются только для самого первого bootstrap пустого каталога данных.
- Все последующие изменения схемы применяются через версионируемые SQL-миграции из `deploy/postgres/migrations/`.
- На сервере должен быть обычный git clone репозитория.
- Обновления из GitHub выполняются вручную по запросу; автоматического deploy-loop нет.
- Рекомендуемый путь обновления:
  1. backup (создаётся автоматически внутри `deploy_update.sh`);
  2. schema migration;
  3. recreate контейнеров приложения;
  4. проверка health.

### Сокращение для ручных команд

Все ручные команды `docker compose` в этом документе используют переменную:

```bash
DC='docker compose --project-name flowstock --env-file deploy/.env -f deploy/docker-compose.yml'
```

Задайте её один раз в сессии (`export DC=...` не нужен, достаточно `DC=...` и вызова `$DC ...` в том же shell) или используйте полную форму.

## URL-схема

- Production использует единый HTTPS origin на порту `7154`.
- `https://SERVER_IP:7154/` — PC web client.
- `https://SERVER_IP:7154/tsd/` — TSD web client.
- В `deploy/.env.example` по умолчанию: внешний HTTPS-порт `7154`, public UDP discovery listener `7155/udp`, loopback backend port `17155/udp`.

## Обязательные файлы и каталоги

- `deploy/.env` — создаётся из `deploy/.env.example`.
- `deploy/runtime/` — создаётся автоматически скриптами; хранит ручные backup'ы и артефакты оператора.
- `FLOWSTOCK_CA_DIR` — внешний каталог для материалов локального CA. Приватный ключ CA никогда не хранится в git.

## Базовые образы

По умолчанию серверный образ собирается из:

- `mcr.microsoft.com/dotnet/sdk:8.0`
- `mcr.microsoft.com/dotnet/aspnet:8.0`

Если у сервера плохая доступность `mcr.microsoft.com`, переопределите образы в `deploy/.env`:

```bash
FLOWSTOCK_DOTNET_SDK_IMAGE=registry.example.com/mirror/dotnet/sdk:8.0
FLOWSTOCK_DOTNET_ASPNET_IMAGE=registry.example.com/mirror/dotnet/aspnet:8.0
```

`deploy_from_git.sh` и `deploy_update.sh` подхватят override автоматически. Неофициальный fallback registry в проект намеренно не зашит: используйте собственный mirror, registry cache или заранее прогретый внутренний registry.

## Сервисы

- `postgres` — основная БД; bootstrap-only init-скрипты смонтированы в `/docker-entrypoint-initdb.d`.
- `migrator` — one-shot сервис: ждёт healthy Postgres, применяет pending SQL-файлы в лексикографическом порядке, записывает применённые файлы в `schema_migrations`.
- `flowstock` — стартует только после успешного `migrator`; отдаёт `/health/live` и `/health/ready`; публикует UDP responder только на loopback backend `127.0.0.1:${FLOWSTOCK_DISCOVERY_BACKEND_PORT:-17155}:7155/udp`.
- `discovery-relay` — host-network UDP sidecar для native Android discovery: всегда слушает host `0.0.0.0:7155/udp`, пересылает datagram в loopback backend `flowstock` и отвечает исходному клиенту с source port `7155`.
- `nginx` — стартует после healthy `flowstock` и healthy `discovery-relay`; использует `deploy/nginx/certs/flowstock.crt` и `deploy/nginx/certs/flowstock.key`.
- `pgbackup` — регулярные scheduled backup'ы `pg_dump -Fc` внутри compose-стека.

## Canonical HTTPS endpoint и discovery

Для native Android TSD задайте в `deploy/.env`:

```bash
FLOWSTOCK_PUBLIC_BASE_URL=https://flowstock.local:7154
FLOWSTOCK_INSTANCE_NAME=FlowStock
```

- `FLOWSTOCK_PUBLIC_BASE_URL` — абсолютный HTTPS root URL без path/query/fragment. Он не вычисляется из HTTP `Host`, IP клиента или входящего запроса.
- `/api/discovery` и UDP responder используют одну и ту же конфигурацию.
- UDP discovery public listener фиксирован на `7155/udp`: в production Docker Compose его реализует `discovery-relay` в `network_mode: host`, слушая `0.0.0.0:7155/udp`. Сервис `flowstock` не публикует public `7155/udp`; его UDP responder доступен только через loopback backend `127.0.0.1:${FLOWSTOCK_DISCOVERY_BACKEND_PORT:-17155}:7155/udp`.
- Relay пересылает допустимый request в backend и отправляет response исходному клиенту через public socket, поэтому source port UDP-ответа остаётся `7155`.
- UDP-ответ является только подсказкой: Android-приложение сохраняет сервер только после strict HTTPS validation `/api/discovery`, `/api/ping` и `/tsd/`.
- Операторские настройки discovery relay в `deploy/.env`:

```bash
FLOWSTOCK_DISCOVERY_BACKEND_PORT=17155
FLOWSTOCK_DISCOVERY_RELAY_TIMEOUT_MS=2000
FLOWSTOCK_DISCOVERY_RELAY_MAX_IN_FLIGHT=64
```

- `FLOWSTOCK_DISCOVERY_BEHIND_RELAY=1` задаётся Compose для `flowstock`: backend per-source limiter отключён, остаётся absolute backend global ceiling `320/10s`. Основные relay limits: `20/source/10s`, `120/global/10s`, local healthcheck `20/10s`.
- Docker healthcheck `discovery-relay` использует protocol-v1 request через public listener и имеет timeout `12s`.
- Deploy scripts не меняют firewall автоматически. На сервере/firewall inbound UDP `7155` должен быть разрешён только из operator LAN; `deploy_update.sh` выполняет read-only `ss -lun` и best-effort firewall inspection.
- `docker compose config -q` проверяет только корректность compose-файла и не доказывает проходимость directed broadcast через firewall/host listener.

Быстрая проверка после deploy:

1. DNS `flowstock.local` указывает на сервер, `FLOWSTOCK_PUBLIC_BASE_URL` совпадает с SAN сертификата;
2. `curl -fsS https://flowstock.local:7154/api/discovery` отрабатывает без ошибок.

Полная проверка UDP discovery (directed broadcast, nonce, relay path, source port ответа) описана в `deploy/docs/operations/discovery-smoke.md`.

## Режимы TLS

- `FLOWSTOCK_TLS_MODE=local_ca` — рекомендуется для внутреннего LAN:
  - deploy-скрипты ожидают локальный root CA в `FLOWSTOCK_CA_DIR`;
  - серверные сертификаты выпускаются автоматически в `deploy/nginx/certs/`;
  - приватный ключ root CA остаётся вне репозитория;
  - deploy validation останавливается, если `FLOWSTOCK_PUBLIC_BASE_URL` не является HTTPS root URL, содержит userinfo/path/query/fragment, или его host не совпадает с `FLOWSTOCK_TLS_SERVER_NAME`/`FLOWSTOCK_TLS_SANS` и фактическим SAN сертификата.
- `FLOWSTOCK_TLS_MODE=manual` — deploy ожидает уже существующие `deploy/nginx/certs/flowstock.crt` и `flowstock.key`. Используйте только если сертификаты управляются вне deploy-скриптов FlowStock.

## Первичный bootstrap локального CA

Выполняется один раз на сервере:

```bash
cd /opt/FlowStock
mkdir -p /opt/flowstock-secrets/ca
bash deploy/scripts/bootstrap_local_ca.sh
```

Будут созданы:

- `FLOWSTOCK_CA_DIR/flowstock-root-ca.crt`
- `FLOWSTOCK_CA_DIR/flowstock-root-ca.key`

Важно:

- храните приватный ключ CA в защищённом backup и вне git;
- не оставляйте приватный ключ CA на ноутбуках операторов, если они не отвечают за выпуск сертификатов;
- клиентские устройства должны один раз доверять `flowstock-root-ca.crt`;
- для release APK native Android TSD root CA устанавливается в Android user trust store вручную или через MDM; debug embedded CA не является production trust model.

## Доверие на клиентах

После bootstrap CA установите `flowstock-root-ca.crt` в доверенные корневые сертификаты на:

- Windows PC, открывающих PC web client;
- Android TSD, открывающих PWA.

Делается один раз на устройство, пока используется тот же root CA.

Ротация CA: сначала установить новый CA на устройства с периодом overlap, затем выпустить серверный сертификат от нового CA, после проверки удалить старый CA. APK не требуется пересобирать при смене адреса сервера или ротации CA, если новый CA установлен на устройствах.

## Первый deploy на пустой production

1. Подготовьте env:

```bash
cd /opt/FlowStock
cp deploy/.env.example deploy/.env
```

2. Отредактируйте `deploy/.env`: реальный пароль PostgreSQL, `FLOWSTOCK_PUBLIC_BASE_URL`, `FLOWSTOCK_INSTANCE_NAME`, нужные порты.
   Для прямого доступа WPF к PostgreSQL из LAN задайте `FLOWSTOCK_PG_BIND_HOST`:
   - безопасный default: `127.0.0.1` (доступ только с хоста сервера);
   - пример для production LAN: `FLOWSTOCK_PG_BIND_HOST=192.168.1.3`;
   - `docker-compose.override.yml` не требуется.
3. Один раз выполните bootstrap локального CA (см. раздел выше).
4. Установите сгенерированный root CA cert на клиентские устройства, включая Android TSD user trust store для release APK.
5. Выполните первый deploy:

```bash
cd /opt/FlowStock
bash deploy/scripts/deploy_update.sh
```

6. Проверьте health:

```bash
$DC ps
curl -fsS http://127.0.0.1:${FLOWSTOCK_PORT:-8080}/health/ready
```

7. Проверьте зафиксированное состояние релиза:

```bash
bash deploy/scripts/release_status.sh
```

### Необязательная разовая проверка чистого bootstrap

Только на заведомо пустом сервере или во временном тестовом проекте (команда `down -v` удаляет данные):

```bash
$DC down -v
bash deploy/scripts/deploy_update.sh
```

## Обычное обновление production

Рекомендуемый путь обновления из GitHub:

```bash
cd /opt/FlowStock
bash deploy/scripts/deploy_from_git.sh
```

По умолчанию `deploy_from_git.sh`:

- делает `fetch` из `origin` и резолвит `origin/main`;
- отказывается продолжать, если в отслеживаемом worktree есть локальные изменения;
- записывает метаданные попытки deploy;
- выполняет fast-forward локальной ветки `main`;
- запускает стандартный deployment flow;
- записывает метаданные успешного релиза.

Деплой конкретного tag или commit вместо `origin/main`:

```bash
cd /opt/FlowStock
bash deploy/scripts/deploy_from_git.sh v2026.04.10-1
```

Нижележащий `deploy_update.sh` остаётся доступен, когда репозиторий уже стоит на нужной ревизии и требуется только rebuild/restart:

```bash
cd /opt/FlowStock
bash deploy/scripts/deploy_update.sh
```

Что делает `deploy_update.sh`:

- проверяет наличие TLS-ассетов; в `local_ca` режиме перевыпускает серверный сертификат, если он отсутствует, не совпадает с конфигурацией или близок к истечению;
- валидирует `docker compose config`;
- валидирует настройки discovery relay и выполняет read-only UDP/firewall preflight;
- поднимает `postgres` и дожидается healthy;
- создаёт pre-deploy dump в `deploy/runtime/backups/`;
- делает pull базовых образов;
- пересобирает `flowstock` и `discovery-relay`;
- запускает `migrator`;
- останавливает и удаляет старый `discovery-relay` перед изменением backend, чтобы public UDP `7155` не принимал discovery traffic при недоступном backend;
- пересоздаёт `flowstock` с loopback backend publish и дожидается healthy `flowstock`;
- проверяет, что public UDP `7155` свободен, и выполняет direct backend healthcheck на `127.0.0.1:${FLOWSTOCK_DISCOVERY_BACKEND_PORT:-17155}`;
- запускает `discovery-relay` и дожидается healthy `discovery-relay`;
- пересоздаёт `nginx`, `pgbackup` и дожидается running `nginx`.

## Проверка сервера для git-driven deploy

Выполните один раз после подготовки server clone:

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
$DC ps
curl -fsS http://127.0.0.1:${FLOWSTOCK_PORT:-8080}/health/ready
$DC ps discovery-relay
$DC run --rm --no-deps --entrypoint dotnet discovery-relay FlowStock.DiscoveryRelay.dll healthcheck
```

## Ручной backup

```bash
cd /opt/FlowStock
bash deploy/scripts/backup_now.sh
```

Dump по конкретному пути:

```bash
bash deploy/scripts/backup_now.sh /opt/flowstock-backups/pre_release.dump
```

## Ручной запуск миграций

Применить pending migrations без пересоздания контейнеров приложения:

```bash
cd /opt/FlowStock
bash deploy/scripts/migrate.sh
```

Проверить применённые миграции:

```bash
$DC exec -T postgres \
  sh -lc 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "SELECT version, filename, applied_at FROM schema_migrations ORDER BY version;"'
```

## Восстановление из dump

```bash
cd /opt/FlowStock
bash deploy/scripts/restore_dump.sh /opt/flowstock-backups/pre_release.dump
```

Что делает restore-скрипт:

- валидирует compose config;
- дожидается healthy Postgres;
- создаёт pre-restore safety backup;
- останавливает `discovery-relay`, `flowstock`, `nginx`, `pgbackup`, если эти сервисы есть в текущей revision;
- пересоздаёт целевую базу;
- восстанавливает dump через `pg_restore`;
- снова запускает `pgbackup`;
- намеренно оставляет `flowstock`/`nginx` остановленными для явной валидации.

## Статус релиза

```bash
cd /opt/FlowStock
bash deploy/scripts/release_status.sh
```

Скрипт выводит: текущую git-ветку / detached state, текущий commit, метаданные последнего и предыдущего успешных релизов, метаданные последней попытки deploy, текущий `docker compose ps` (если Docker доступен).

## Путь rollback

Если обновление сломалось после backup или после пересоздания приложения:

1. Статус и логи:

```bash
$DC ps
$DC logs --tail=100 migrator flowstock nginx postgres
```

Если текущая revision содержит `discovery-relay`, добавьте его в список логов:

```bash
$DC logs --tail=100 discovery-relay
```

2. При несовместимости схемы и приложения вернитесь на предыдущую рабочую ревизию:

```bash
git checkout <previous-good-commit-or-tag>
```

3. Восстановите pre-deploy dump, созданный `deploy_update.sh`:

```bash
bash deploy/scripts/restore_dump.sh /opt/FlowStock/deploy/runtime/backups/FlowStock_<timestamp>.dump
```

4. Поднимите предыдущую рабочую ревизию приложения. Для старой revision без `discovery-relay` запускайте только существующие сервисы; для новой revision с relay используйте `discovery-relay` между `flowstock` и `nginx`:

```bash
$DC up -d --build flowstock nginx pgbackup
# или, если target revision содержит discovery-relay:
$DC up -d --build flowstock discovery-relay nginx pgbackup
```

Для типового сценария есть rollback helper:

```bash
cd /opt/FlowStock
bash deploy/scripts/rollback_release.sh
```

По умолчанию `rollback_release.sh`:

- перед checkout останавливает и удаляет `discovery-relay`, если он есть в текущем compose project;
- делает checkout предыдущей записанной успешной ревизии релиза;
- проверяет target Compose: старая revision без relay должна публиковать `flowstock` на `7155/udp`, новая revision с relay должна использовать host-network `discovery-relay` и loopback backend publish;
- восстанавливает последний записанный pre-deploy dump текущего релиза;
- запускает сервисы, существующие в target revision: `flowstock`, при наличии `discovery-relay`, затем `nginx` и `pgbackup`;
- записывает rollback как новый последний успешный релиз.

Откатить только код приложения без восстановления БД:

```bash
bash deploy/scripts/rollback_release.sh --no-restore
```

Для rollback только сетевого изменения UDP discovery relay используйте `rollback_release.sh --no-restore`: PostgreSQL restore для этого не требуется.

Откатиться на конкретную ревизию и конкретный dump:

```bash
bash deploy/scripts/rollback_release.sh <git-ref> /opt/flowstock-backups/pre_release.dump
```

## Health checks

Liveness:

```bash
curl -fsS http://127.0.0.1:${FLOWSTOCK_PORT:-8080}/health/live
```

Readiness:

```bash
curl -fsS http://127.0.0.1:${FLOWSTOCK_PORT:-8080}/health/ready
```

Readiness возвращает успех только если приложение может открыть подключение к PostgreSQL и история миграций присутствует в `schema_migrations`.

## Ручные операции с сертификатами

Принудительно перевыпустить серверный сертификат от локального CA:

```bash
cd /opt/FlowStock
bash deploy/scripts/renew_server_cert.sh --force
$DC restart nginx
```

Немедленно выпустить текущий серверный сертификат:

```bash
cd /opt/FlowStock
bash deploy/scripts/issue_server_cert.sh
$DC restart nginx
```

## Разовые операционные процедуры

Процедуры, привязанные к конкретным миграциям или разовым переходам, описаны отдельно:

- Cutover ЧЗ real-code workflow (`V0027`, `marking_cutover_state`, preflight, enforcement) — `deploy/docs/operations/marking-cutover.md`
- Production backfill статусов ЧЗ (`backfill_marking_status.sh`) — `deploy/docs/operations/marking-backfill.md`
- Полная проверка UDP discovery — `deploy/docs/operations/discovery-smoke.md`

Общее правило для любой миграции, помеченной в release notes как чувствительная: свежий backup перед применением обязателен (сверх автоматического pre-deploy dump).

## Примечания

- Migration-файлы применяются в лексикографическом порядке; сохраняйте схему именования `V0001__name.sql`.
- Каждый migration-файл выполняется раннером внутри транзакции. При любой SQL-ошибке процесс завершается с non-zero кодом, а файл не записывается в `schema_migrations`.
- `deploy/postgres/init/001_init.sql` намеренно минимален: он только bootstrap'ит `schema_migrations` для нового Postgres volume.
- WPF-клиент не отвечает за создание production schema. Если в БД нет миграций, приложение явно сообщает об этом состоянии вместо скрытого создания/обновления схемы.
- Держите server-side clone репозитория чистым: `deploy_from_git.sh` останавливается, если на сервере вручную изменяли tracked files.
- Метаданные релизов хранятся в `deploy/runtime/releases/`.
- `deploy/nginx/gen_cert.sh` остаётся только как быстрый self-signed fallback; рекомендуемый режим для внутреннего production — `FLOWSTOCK_TLS_MODE=local_ca`.
