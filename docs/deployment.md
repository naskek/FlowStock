# Деплой FlowStock

> **Compatibility warning для HU correction.** После первого committed `CORRECT_FILLED` в данных появляется `production_pallets.status = CORRECTED`. Старый runtime с условиями вида `status <> 'CANCELLED'` может ошибочно считать такую историческую ревизию активной. До применения миграции нужен свежий PostgreSQL backup. Если функция ещё не использовалась, additive schema допускает обычный code rollback. После появления `CORRECTED` сначала выключите `pc_hu_correction`; запуск старого runtime допускается только после forward-fix либо восстановления согласованного pre-deploy backup. Production deploy и backup выполняет пользователь вручную по действующему каноническому FlowStock PowerShell-процессу; Compose запускается из `/opt/FlowStock` с явными `-p flowstock --env-file deploy/.env -f deploy/docker-compose.yml`.

> **Compatibility warning для ранней частичной отгрузки.** Миграция `V0031` добавляет `orders.allow_partial_outbound` и CHECK `terminal status => allow_partial_outbound = false`; новый runtime атомарно сбрасывает флаг при `SHIPPED`, `CANCELLED`, `MERGED`. Пока у всех активных заказов permission равен `false`, additive schema совместима с обычным code rollback. Если существует активный заказ с `allow_partial_outbound = true`, старый runtime нельзя запускать как штатный rollback: он не выполняет terminal-reset, и его попытка терминального перехода такого заказа будет fail-closed отклонена CHECK constraint. Поддерживаемые варианты в этом состоянии — forward-fix либо восстановление согласованного pre-deploy PostgreSQL backup. Ручной `UPDATE` production-БД не является rollback-процедурой. Production backup и deploy выполняет пользователь вручную по каноническому FlowStock-процессу.

Этот документ — постоянный runbook деплоя. Разовые операционные процедуры (cutover ЧЗ, backfill статусов маркировки) вынесены в `deploy/docs/operations/`.

Переход маркировки в `ENFORCED` не имеет отдельного короткого deploy-пути. Он выполняется только по обычному FlowStock deployment process и `docs/marking-cutover.md`: dev rehearsal, свежий проверенный PostgreSQL backup, остановленные writers, final preflight/hash, trusted admin-only maintenance-команда `POST /api/admin/marking/cutover/enforce` с повторной проверкой exact hash внутри одной транзакции, health/diagnostics/smoke и только затем возврат writers. После запуска runtime с V0036 состояние `SHADOW` является maintenance/fail-closed: новые marking export/import, наполнение маркируемых production pallets и проведение соответствующего выпуска запрещены с `MARKING_CUTOVER_MAINTENANCE_REQUIRED`; preflight и enforce доступны только авторизованному WPF/PC admin. Ручные production SQL edits запрещены. Если preflight/enforce не завершён, writers не возвращаются: recovery — восстановление свежего pre-deploy backup и предыдущего runtime. После commit применяется canonical restore либо fix-forward при остановленных writers.

## Обзор

- Production deploy выполняется через `deploy/docker-compose.yml`.
- Имя compose-проекта зафиксировано как `flowstock`, чтобы ручные команды `docker compose` не создавали параллельный стек `deploy-*`.
- PostgreSQL init-скрипты в `deploy/postgres/init/` используются только для самого первого bootstrap пустого каталога данных.
- Все последующие изменения схемы применяются через версионируемые SQL-миграции из `deploy/postgres/migrations/`.
- На сервере должен быть обычный git clone репозитория.
- Обновления из GitHub выполняются вручную по запросу; автоматического deploy-loop нет.
- Production deploy и update выполняются пользователем вручную через канонический FlowStock PowerShell-процесс:
  1. определить локальный expected commit и создать свежий PostgreSQL backup;
  2. обновить `/opt/FlowStock` и подтвердить server `HEAD`;
  3. выполнить Compose config/resolved gate и build/deploy одной invocation `docker compose -p flowstock --env-file deploy/.env -f deploy/docker-compose.yml ...`;
  4. проверить containers, live/ready, TSD version, disk space и путь backup.

### Сокращение для ручных команд

Все ручные команды `docker compose` в этом документе используют переменную:

```bash
DC='docker compose -p flowstock --env-file deploy/.env -f deploy/docker-compose.yml'
```

Задайте её один раз в сессии (`export DC=...` не нужен, достаточно `DC=...` и вызова `$DC ...` в том же shell) или используйте полную форму.

## URL-схема

- Production использует единый HTTPS origin на порту `7154`.
- `https://SERVER_IP:7154/` — PC web client.
- `https://SERVER_IP:7154/tsd/` — TSD web client.
- В `deploy/.env.example` по умолчанию: внешний HTTPS-порт `7154`, public UDP discovery listener `7155/udp`, loopback backend port `17155/udp`.

## Обновление PC web client

- Версия PC frontend не привязана к версии server assembly: `/api/version` сохраняет поле `version` и отдельно возвращает детерминированный `pc_web_version` текущего JS/CSS bundle.
- PC index всегда отдается с `Cache-Control: no-store, max-age=0`; `/api/version` — с `no-store`. Runtime JS/CSS и общий `/compat.js` с актуальным `?v=<pc_web_version>` immutable, а без `v` или с неактуальным `v` требуют revalidation через `no-cache`. Logo, favicon и другие декоративные PC assets также используют `no-cache` и не меняют `pc_web_version`.
- Уже открытая авторизованная вкладка не перезагружается автоматически: при обнаружении нового `pc_web_version` она показывает немодальный баннер `Доступна новая версия FlowStock`, а reload выполняется только по кнопке `Обновить`.

Одноразовое ограничение первого rollout этой политики: ранее сохранённый браузером PC index без cache headers может быть полностью взят из локального кэша без обращения к серверу. Для такой вкладки может потребоваться одно ручное обновление с обходом кэша (`Ctrl+F5`). Не добавляйте для обхода `Clear-Site-Data`, cookies, service worker, origin-wide очистку кэша или forced reload. После первого получения нового `no-store` index последующие deploy не требуют `Ctrl+F5`.

## Детерминированный source commit production server

Production server source commit является authority для WPF update. Канонический deploy до Compose разрешает exact commit подготовленного server checkout:

```powershell
$expectedCommit = (git rev-parse --verify 'HEAD^{commit}').Trim().ToLowerInvariant()
if ($expectedCommit -notmatch '^[0-9a-f]{40}$') { throw 'Invalid production source commit' }
$env:FLOWSTOCK_SOURCE_COMMIT = $expectedCommit
docker compose -p flowstock --env-file deploy/.env -f deploy/docker-compose.yml config -q
docker compose -p flowstock --env-file deploy/.env -f deploy/docker-compose.yml build
```

Если Compose запускается по SSH, переменная задаётся в том же удалённом execution context, который непосредственно вызывает Compose. `FLOWSTOCK_SOURCE_COMMIT` не записывается в `deploy/.env`: это исключает случайное повторное использование SHA предыдущего deploy. Shell environment имеет приоритет при Compose interpolation.

`deploy/docker-compose.yml` передаёт SHA как Docker build arg и runtime environment. Default `INVALID_SOURCE_COMMIT` нужен только для разрешения `config`; Docker build с ним, uppercase, short или non-hex значением завершается ошибкой. `deploy/Dockerfile` не читает `.git`: publish server получает `/p:SourceRevisionId=<full-sha>`, поэтому изменение SHA инвалидирует publish layer.

После `/health/live` и `/health/ready`, но до записи successful release metadata, канонический deploy запрашивает `/api/version` и fail closed проверяет:

- `server_build.source_commit == $expectedCommit`;
- `desktop_update.target_commit == $expectedCommit`;
- оба SHA полные и lowercase;
- `desktop_update.policy == server_source_commit`;
- repository URL равен `https://github.com/naskek/FlowStock.git`, branch равна `main`.

Mismatch делает deploy неуспешным и не разрешает считать server источником WPF update. Автоматический DB rollback не запускается: используется действующая production recovery/forward-fix процедура. `deploy_update.sh` всегда экспортирует SHA текущего exact checkout и выполняет этот строгий modern gate.

`rollback_release.sh` также экспортирует exact rollback SHA, но capability определяет заранее только по target Git tree: наличие `apps/windows/FlowStock.DesktopUpdate/FlowStock.DesktopUpdate.csproj` в exact target commit означает modern source identity contract. Для такого target тот же строгий gate обязателен и отсутствие `server_build`/`desktop_update` является ошибкой. Target без marker считается legacy: после readiness helper требует доступный валидный `/api/version` с непустыми строковыми `version` и `pc_web_version`, затем предупреждает, что exact running source commit через legacy API подтвердить невозможно. Отсутствие modern полей в HTTP response само по себе никогда не выбирает legacy mode.

## Первый rollout WPF updater

Старый WPF не может установить subsystem, которого в нём ещё нет. Один раз вручную доставьте в repository root `D:\FlowStock` bootstrap commit с `FlowStock.DesktopUpdate`, `FlowStock.Updater`, обновлённым `FLOWSTOCK.cmd` и launcher. Запустите его обычной MAIN-командой. Production server к этому моменту должен быть развёрнут с deterministic identity contract; до этого AdminWindow показывает, что desktop update не поддерживается.

На WPF-машине имя `flowstock.local` должно резолвиться в production server, а HTTPS certificate на `https://flowstock.local:7154` должен быть доверен Windows и содержать соответствующее имя. Operational подключение WPF может использовать другой `server.base_url` и `server.allow_invalid_tls`, но эти параметры не ослабляют desktop update TLS. Для отдельного доверенного стенда допускается process-level `FLOWSTOCK_UPDATE_SERVER_BASE_URL`; production/non-loopback override обязан быть HTTPS root URL.

Пока `%LOCALAPPDATA%\FlowStock\Desktop\state\active-runtime.json` отсутствует, launcher запускает `dotnet run` из repository root. Первая WPF версия с updater subsystem собирает recovery updater из clean detached worktree своего embedded commit, затем устанавливает первый side-by-side target и создаёт active manifest. Installed identity в этом режиме берётся из assembly, а не из repository `HEAD`.

Если embedded commit bootstrap WPF уже равен server target, Git fetch для подтверждения равенства не выполняется и WPF остаётся в source-run mode до появления следующего разрешённого production target.

При аварии/reboot launcher проверяет pending transaction раньше active runtime: transaction recovery bundle → LKG updater → source-run fallback. Успешный recovery завершается с code `0` и оставляет `success-ready` либо `fallback-ready`; launcher запускает выбранный runtime с session handoff и только затем удаляет pending. Ненулевой recovery code сохраняет pending и блокирует дальнейший запуск в этой попытке. Candidate не используется как единственная recovery-копия. Если recovery недоступен, unresolved candidate не запускается; диагностический лог находится в `%APPDATA%\FlowStock\Logs\Updates`.

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

Compose подхватит override из `deploy/.env`. Существующие `deploy_from_git.sh` и `deploy_update.sh` также читают этот файл, но являются helper/legacy scripts, а не каноническим production deploy-процессом. Неофициальный fallback registry в проект намеренно не зашит: используйте собственный mirror, registry cache или заранее прогретый внутренний registry.

## Сервисы

- `postgres` — основная БД; bootstrap-only init-скрипты смонтированы в `/docker-entrypoint-initdb.d`.
- `migrator` — one-shot сервис: ждёт healthy Postgres, применяет pending SQL-файлы в лексикографическом порядке, записывает применённые файлы в `schema_migrations`.
- `flowstock` — стартует только после успешного `migrator`; отдаёт `/health/live` и `/health/ready`; публикует UDP responder только на loopback backend `127.0.0.1:${FLOWSTOCK_DISCOVERY_BACKEND_PORT:-17155}:7155/udp`.
- `discovery-relay` — host-network UDP sidecar для native Android discovery: всегда слушает host `0.0.0.0:7155/udp`, пересылает datagram в loopback backend `flowstock` и отвечает исходному клиенту с source port `7155`.
- `nginx` — стартует после healthy `flowstock` и healthy `discovery-relay`; использует `deploy/nginx/certs/flowstock.crt` и `deploy/nginx/certs/flowstock.key`.
- `pgbackup` — регулярные scheduled backup'ы `pg_dump -Fc` внутри compose-стека.

## PostgreSQL bind и обязательная проверка Compose

Основной bind PostgreSQL задаётся через `FLOWSTOCK_PG_BIND_HOST`. Без явной настройки используется безопасный loopback `127.0.0.1`.

Необязательный второй bind задаётся через `FLOWSTOCK_PG_SECOND_BIND_HOST`. Пустое или отсутствующее значение означает single-bind: вторая декларация `ports` интерполируется в тот же адрес, что и основная. Эта схема поддерживается только если фактическая версия Docker Compose нормализует resolved-конфигурацию до одного mapping. Для production dual-bind задайте конкретные адреса интерфейсов:

```bash
FLOWSTOCK_PG_BIND_HOST=192.168.1.3
FLOWSTOCK_PG_SECOND_BIND_HOST=100.66.142.112
```

Не используйте для этих переменных пустой host в resolved mapping, `0.0.0.0`, `::` или `[::]`: PostgreSQL должен публиковаться только на явно выбранных интерфейсах.

Перед первым `up` после изменения bind-контракта и перед каждым production deploy выполните одной и той же Compose invocation:

```bash
$DC config -q
$DC config --format json
```

Для single-bind resolved `services.postgres.ports` должен содержать ровно один mapping с `host_ip: 127.0.0.1`. Для production dual-bind должны присутствовать ровно два различных mapping с `host_ip: 192.168.1.3` и `host_ip: 100.66.142.112`; пустые и wildcard HostIp запрещены. Если фактическая production-версия Compose возвращает другой результат, deploy блокируется до отдельного архитектурного решения.

Конфигурация, прошедшая gate, применяется без изменения invocation:

```bash
$DC up -d --build --remove-orphans
$DC ps
POSTGRES_CONTAINER_ID="$($DC ps -q postgres)"
docker inspect "$POSTGRES_CONTAINER_ID" --format '{{json .HostConfig.PortBindings}}'
```

Таким образом `config`, `up`, `ps` и получение ID контейнера используют один `-p flowstock`, один `deploy/.env` и один `deploy/docker-compose.yml`.

### Разовый переход с ручного production drift

После появления canonical commit, но до запуска канонического PowerShell deploy-процесса:

1. Сохраните `deploy/.env`, `git diff -- deploy/docker-compose.yml`, `deploy/.env.backup-*` и `deploy/docker-compose.yml.before-*` в закрытом каталоге вне `/opt/FlowStock`; для каталога используйте права `700`, для файлов — `600`.
2. Переместите `deploy/.env.backup-*` и `deploy/docker-compose.yml.before-*` из clone в этот каталог. Не добавляйте их в Git и не используйте `git clean`.
3. Добавьте в существующий `/opt/FlowStock/deploy/.env` оба production-адреса, не удаляя основной bind:

   ```bash
   FLOWSTOCK_PG_BIND_HOST=192.168.1.3
   FLOWSTOCK_PG_SECOND_BIND_HOST=100.66.142.112
   ```

4. Повторно проверьте `git diff -- deploy/docker-compose.yml`. Единственным tracked изменением должна быть известная ручная строка `- "100.66.142.112:5432:5432"`; при любом другом diff остановитесь.
5. Уберите только подтверждённый drift командой `git restore --source=HEAD -- deploy/docker-compose.yml` и подтвердите чистый `git status --short`. Ignored `deploy/.env` остаётся на месте.

На подготовительном этапе не выполняйте `git fetch`, `git pull`, merge, fast-forward или Compose-команды. Восстановление файла в working tree не меняет уже запущенный контейнер PostgreSQL: действующий dual-bind сохраняется до controlled recreate.

Дальнейшее обновление выполняется только существующим каноническим FlowStock PowerShell deploy-процессом в порядке: локальный expected commit, свежий PostgreSQL backup, update `/opt/FlowStock`, проверка server `HEAD`, `config -q` и resolved HostIp gate, `up`, containers, `/health/live`, `/health/ready`, TSD version, disk space и путь backup. Для этого rollout не используйте `deploy_from_git.sh`, `deploy_update.sh`, короткую SSH deploy-команду или отдельный server-side deploy. После recreate проверьте фактические Docker `PortBindings` и доступ к PostgreSQL отдельно через LAN и Tailscale.

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
- Helper scripts не меняют firewall автоматически. На сервере/firewall inbound UDP `7155` должен быть разрешён только из operator LAN; legacy/helper `deploy_update.sh` выполняет read-only `ss -lun` и best-effort firewall inspection, но не заменяет канонический PowerShell deploy-процесс.
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

2. Отредактируйте `deploy/.env`: реальный пароль PostgreSQL, `FLOWSTOCK_PUBLIC_BASE_URL`, `FLOWSTOCK_INSTANCE_NAME`, нужные порты и случайный `FLOWSTOCK_WPF_ADMIN_API_KEY` длиной не менее 32 символов.
   Тот же secret задайте каждому доверенному WPF-клиенту через environment `FLOWSTOCK_WPF_ADMIN_API_KEY` либо локальное поле `server.wpf_admin_api_key` в `settings.json`. Secret не добавляется в Git и не идентифицирует конкретного Windows-пользователя.
   Для прямого доступа WPF к PostgreSQL задайте bind-переменные:
   - безопасный default: `127.0.0.1` (доступ только с хоста сервера);
   - пример для production LAN: `FLOWSTOCK_PG_BIND_HOST=192.168.1.3`;
   - optional Tailscale bind: `FLOWSTOCK_PG_SECOND_BIND_HOST=100.66.142.112`;
   - пустой `FLOWSTOCK_PG_SECOND_BIND_HOST` не добавляет второй resolved mapping после обязательной проверки Compose;
   - `docker-compose.override.yml` не требуется.
3. Один раз выполните bootstrap локального CA (см. раздел выше).
4. Установите сгенерированный root CA cert на клиентские устройства, включая Android TSD user trust store для release APK.
5. Выполните первый deploy существующим каноническим FlowStock PowerShell-процессом: expected commit, свежий PostgreSQL backup, update `/opt/FlowStock`, проверка `HEAD`, Compose config/resolved gate, build/deploy и post-deploy проверки.

6. Проверьте health:

```bash
$DC ps
curl -fsS http://127.0.0.1:${FLOWSTOCK_PORT:-8080}/health/ready
```

7. Проверьте зафиксированное состояние релиза:

```bash
bash deploy/scripts/release_status.sh
```

## Rollout PC Web RBAC (`V0032`)

Перед применением `V0032__pc_web_rbac_sessions.sql` канонический deploy-процесс обязан создать свежий PostgreSQL backup. До обновления runtime выполните read-only инвентаризацию:

```sql
SELECT upper(request_type) AS request_type, status, count(*)
FROM order_requests
GROUP BY upper(request_type), status
ORDER BY upper(request_type), status;
```

Текущий dispatcher поддерживает `CREATE_ORDER` и `SET_ORDER_STATUS`. Иные значения не удаляются и не мигрируют автоматически: confirm для них fail-closed возвращает `422`, заявка остаётся `PENDING`.

Server и WPF разворачиваются согласованно с заранее заданным одинаковым machine key. Старый WPF после rollout не может использовать удалённый `/api/orders/requests/{id}/resolve` или прямой `/api/orders` без credential. Все существующие `tsd_devices` получают роль `OPERATOR`; нужные `ADMIN` назначаются после rollout через защищённое WPF-окно аккаунтов. Browser-пользователи проходят новый login, поскольку прежний `localStorage.flowstock_account` больше не является authenticated state.

## Rollout ADMIN-каталога (`V0033` / `V0034`)

Перед rollout создайте свежий PostgreSQL backup каноническим PowerShell-процессом. `V0033__partner_roles.sql` является additive: добавляет nullable `partners.partner_role` и CHECK допустимых значений `NULL|SUPPLIER|CLIENT|BOTH`. В ней намеренно нет `NOT NULL` и нет `CHECK (partner_role IS NOT NULL) NOT VALID`: применение migration не зависит от заранее выполненного maintenance.

### Read-only identifier preflight

До production migration выполните read-only проверку case-insensitive дублей SKU/штрихкода и GTIN:

```bash
cd /opt/FlowStock
bash deploy/scripts/preflight_catalog_identifiers.sh
```

Проверка группирует непустые значения по `LOWER(BTRIM(value))` и выводит нормализованное значение с item IDs. Любой дубль останавливает rollout. Скрипт и `V0034` не выполняют merge, rename, deactivate или `UPDATE`: исправление делается только штатным WPF/API после свежего backup.

`V0034__item_identifier_case_insensitive_uniqueness.sql` повторяет fail-fast проверку внутри migration transaction, затем создаёт partial functional unique indexes для `LOWER(BTRIM(barcode))` и `LOWER(BTRIM(gtin))`. При конфликте transaction откатывается: `V0034` не появляется в `schema_migrations`, частично созданных индексов не остаётся.

### Staged partner-role cutover

Production cutover выполняется без пользовательских Partner writes:

1. примените additive `V0033` и остановитесь до открытия нового runtime;
2. оставьте текущую версию либо остановите writers на maintenance window;
3. из нового server image выполните dry-run:

   ```bash
   $DC run --rm --no-deps flowstock maintenance partner-role-backfill --dry-run
   ```

4. проверьте explicit JSON roles, default `Both`, уже заполненные строки, orphan IDs, malformed JSON и итоговый `NULL` count;
5. при наличии свежего backup выполните:

   ```bash
   $DC run --rm --no-deps flowstock maintenance partner-role-backfill --apply --confirm APPLY
   ```

6. подтвердите `NULL = 0`, примените `V0034`, затем открывайте трафик согласованной Server/WPF/PC-версии.

Apply отказывается работать с malformed JSON, не перезаписывает заполненную PostgreSQL-role, импортирует explicit JSON только в `NULL`, оставшиеся `NULL` заполняет `BOTH` и откатывает transaction, если `NULL` остались. Legacy JSON не удаляется.

Rollback до APPLY совместим: старая версия продолжает читать/писать неизменённый JSON, nullable колонка ей не мешает, а новая версия ещё не принимала Partner writes. После APPLY и начала DB-only Partner writes запуск старой JSON-writing версии запрещён: используйте forward-fix либо восстановление согласованного pre-cutover backup. Окончательный `SET NOT NULL`/validated mandatory CHECK выполняется отдельным последующим release только после подтверждённого production backfill и не входит в V0033/V0034.

### UOM remediation и blocking migration `V0035`

Legacy token `LOWER(BTRIM(items.base_uom)) = 'шт'` остаётся допустим без строки в `uoms`. Он не является alias master-значения `Шт.`: точка сохраняется normalization, поэтому эти значения не объединяются автоматически. Создавать либо переименовывать master UOM в зарезервированное `шт` запрещено. Исторические `items.uom`, document/audit/import JSON и другие snapshots не переписываются.

Rollout однофазный, но выполняется по стадиям в maintenance window. Blocking migration нельзя запускать до появления пути remediation:

1. соберите новый server image, содержащий UOM rename и `maintenance uom-remediation`, но не запускайте migrator;
2. остановите Server, WPF и import writers;
3. создайте свежий PostgreSQL backup каноническим процессом;
4. новым image на pre-`V0035` schema выполните inventory:

   ```bash
   $DC run --rm --no-deps flowstock maintenance uom-remediation --dry-run
   ```

5. если есть blocking findings, подготовьте явный JSON plan и сначала симулируйте его:

   ```bash
   $DC run --rm --no-deps -v /secure/uom-plan.json:/run/uom-plan.json:ro flowstock maintenance uom-remediation --plan /run/uom-plan.json --dry-run
   ```

6. только после проверки dry-run и backup выполните atomic apply:

   ```bash
   $DC run --rm --no-deps -v /secure/uom-plan.json:/run/uom-plan.json:ro flowstock maintenance uom-remediation --plan /run/uom-plan.json --apply --confirm APPLY
   ```

7. повторите command inventory и read-only SQL preflight `bash deploy/scripts/preflight_uom_references.sh`; blocking findings должны отсутствовать;
8. только после этого запустите migrator с `V0035` и откройте трафик согласованных Server/WPF/PC-версий.

Plan содержит только явно выбранные оператором массивы `rename` (`uom_id`, `new_name`), `merge` (`target_uom_id`, `source_uom_ids`, optional `target_name`), `map_orphan` (`source_value`, `target_uom_id`) и `create_and_map_orphan` (`source_value`, `new_uom_name`). Command не выбирает survivor/canonical target самостоятельно, запрещает операции над legacy `шт`, выполняет весь plan в одной transaction и откатывается, если после него остаются blocking findings. Повторный apply уже достигнутого состояния является no-op; противоречивый plan завершается ненулевым кодом.

Remediation для preflight failures выполняется без ручного SQL: blank master — явный rename/merge; отдельная blank item-reference без master — explicit `map_orphan` с `source_value: ""`; normalized duplicate — merge с явно выбранным survivor; orphan — map на существующий master либо explicit create-and-map; legacy `шт` не является failure. Master-row, нормализующаяся в `шт`, выводится как warning overlap и автоматически не изменяется. `V0035` повторяет те же проверки через `LOWER(BTRIM(...))`, не меняет данные, добавляет nonblank CHECK, normalized unique index `uoms.name` и lookup index `items.base_uom`. При ошибке migration transaction откатывается и `V0035` не записывается в `schema_migrations`.

### Обязательный PostgreSQL verification gate

Обычного `dotnet test` без connection string недостаточно. Для release поднимите отдельную непроизводственную PostgreSQL и примените полный migration chain тем же migrator-механизмом. Репозиторий содержит disposable compose-вариант на `127.0.0.1:55432`:

```powershell
docker compose --project-name flowstock-catalog-test -f deploy/docker-compose.test-postgres.yml up -d postgres-test
docker compose --project-name flowstock-catalog-test -f deploy/docker-compose.test-postgres.yml run --rm migrator-test
$env:FLOWSTOCK_POSTGRES_TEST_CONNECTION='Host=127.0.0.1;Port=55432;Database=flowstock_test;Username=flowstock_test;Password=flowstock_test'
dotnet test apps/windows/FlowStock.Server.Tests/FlowStock.Server.Tests.csproj --filter "FullyQualifiedName~CatalogCutoverPostgresTests|FullyQualifiedName~UomCatalogPostgresTests|FullyQualifiedName~CommercialCatalogPostgresConcurrencyTests|FullyQualifiedName~CommercialPriceShipmentConcurrencyPostgresTests"
dotnet test apps/windows/FlowStock.sln
```

Отдельно зафиксируйте `Passed / Failed / Skipped`. Для migration/V0034/V0035, UOM remediation/rename concurrency, ADMIN single-session, promotion, partner-role persistence/fallback/backfill и identifier concurrency любое `Skipped` означает непройденный gate. В тестовой PostgreSQL обязательны: подготовленный duplicate и ожидаемый fail-fast V0034/V0035, legacy `шт` без master, remediation dry-run/apply, успешное повторное применение, отказ normalized indexes принимать duplicate, concurrent UOM rename с catalog/import writers, а также затронутые VAT/customer-price concurrency regressions.

### Необязательная разовая проверка чистого bootstrap

Только на заведомо пустом сервере или во временном тестовом проекте (команда `down -v` удаляет данные):

```bash
$DC down -v
```

После очистки повторите канонический PowerShell deploy-процесс. Не используйте эту процедуру на существующем production volume.

## Обычное обновление production

Production update выполняет пользователь вручную существующим каноническим FlowStock PowerShell-процессом. Процесс определяет expected commit локально, создаёт свежий PostgreSQL backup, обновляет `/opt/FlowStock`, проверяет server `HEAD`, выполняет `config -q` и resolved gate, затем build/deploy и проверки containers, live/ready, TSD version, disk space и пути backup.

Все Compose-команды этого процесса используют одну invocation:

```bash
docker compose -p flowstock --env-file deploy/.env -f deploy/docker-compose.yml ...
```

`deploy_from_git.sh` и `deploy_update.sh` сохраняются как helper/legacy scripts для ограниченных вспомогательных сценариев. Они не являются каноническим production deploy-процессом и не запускаются вместо PowerShell-процесса.

## Проверка server clone

После подготовки clone проверьте чистый tracked worktree, доступность ожидаемого commit и соответствие server `HEAD` в составе канонического PowerShell-процесса. Не выполняйте отдельный server-side deploy вместо него.

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

3. Восстановите свежий pre-deploy dump, путь которого сообщил канонический PowerShell deploy-процесс:

```bash
bash deploy/scripts/restore_dump.sh <backup-path-reported-by-PowerShell-process>
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
- по exact target Git tree определяет source-identity capability: modern target проходит строгую сверку SHA/manifest, legacy target — проверку доступности и legacy payload `/api/version` с явным warning о невозможности подтвердить running SHA;
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

- Cutover ЧЗ real-code workflow (`V0027` + subject aggregate schema, `marking_cutover_state`, preflight, enforcement) — [`docs/marking-cutover.md`](marking-cutover.md)
- Production backfill статусов ЧЗ (`backfill_marking_status.sh`) — `deploy/docs/operations/marking-backfill.md`
- Полная проверка UDP discovery — `deploy/docs/operations/discovery-smoke.md`

Общее правило для любой миграции, помеченной в release notes как чувствительная: свежий backup перед применением обязателен и создаётся каноническим PowerShell deploy-процессом.

## Примечания

- Migration-файлы применяются в лексикографическом порядке; сохраняйте схему именования `V0001__name.sql`.
- Каждый migration-файл выполняется раннером внутри транзакции. При любой SQL-ошибке процесс завершается с non-zero кодом, а файл не записывается в `schema_migrations`.
- `deploy/postgres/init/001_init.sql` намеренно минимален: он только bootstrap'ит `schema_migrations` для нового Postgres volume.
- WPF-клиент не отвечает за создание production schema. Если в БД нет миграций, приложение явно сообщает об этом состоянии вместо скрытого создания/обновления схемы.
- Держите server-side clone репозитория чистым: канонический PowerShell deploy-процесс должен остановиться при неожиданных tracked изменениях.
- Метаданные релизов хранятся в `deploy/runtime/releases/`.
- `deploy/nginx/gen_cert.sh` остаётся только как быстрый self-signed fallback; рекомендуемый режим для внутреннего production — `FLOWSTOCK_TLS_MODE=local_ca`.
