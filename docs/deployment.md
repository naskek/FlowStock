# Деплой FlowStock

> **Compatibility warning для HU correction.** После первого committed `CORRECT_FILLED` в данных появляется `production_pallets.status = CORRECTED`. Старый runtime с условиями вида `status <> 'CANCELLED'` может ошибочно считать такую историческую ревизию активной. До применения миграции нужен свежий PostgreSQL backup. Если функция ещё не использовалась, additive schema допускает обычный code rollback. После появления `CORRECTED` сначала выключите `pc_hu_correction`; запуск старого runtime допускается только после forward-fix либо восстановления согласованного pre-deploy backup. Production deploy и backup выполняет пользователь вручную по действующему каноническому FlowStock PowerShell-процессу; Compose запускается из `<REMOTE_REPO_PATH>` с явными `-p flowstock --env-file deploy/.env -f deploy/docker-compose.yml` и optional Telegram overlay только при включённой integration.

> **Compatibility warning для ранней частичной отгрузки.** Миграция `V0031` добавляет `orders.allow_partial_outbound` и CHECK `terminal status => allow_partial_outbound = false`; новый runtime атомарно сбрасывает флаг при `SHIPPED`, `CANCELLED`, `MERGED`. Пока у всех активных заказов permission равен `false`, additive schema совместима с обычным code rollback. Если существует активный заказ с `allow_partial_outbound = true`, старый runtime нельзя запускать как штатный rollback: он не выполняет terminal-reset, и его попытка терминального перехода такого заказа будет fail-closed отклонена CHECK constraint. Поддерживаемые варианты в этом состоянии — forward-fix либо восстановление согласованного pre-deploy PostgreSQL backup. Ручной `UPDATE` production-БД не является rollback-процедурой. Production backup и deploy выполняет пользователь вручную по каноническому FlowStock-процессу.

Этот документ — постоянный runbook деплоя. Разовые операционные процедуры (cutover ЧЗ, backfill статусов маркировки) вынесены в `deploy/docs/operations/`.

## Обзор

- Production deploy всегда использует `deploy/docker-compose.yml`; optional Telegram integration добавляет `deploy/docker-compose.telegram.yml` только при `FLOWSTOCK_TELEGRAM_ENABLED=1`.
- Имя compose-проекта зафиксировано как `flowstock`, чтобы ручные команды `docker compose` не создавали параллельный стек `deploy-*`.
- PostgreSQL init-скрипты в `deploy/postgres/init/` используются только для самого первого bootstrap пустого каталога данных.
- Все последующие изменения схемы применяются через версионируемые SQL-миграции из `deploy/postgres/migrations/`.
- На сервере должен быть обычный git clone репозитория.
- Обновления из GitHub выполняются вручную по запросу; автоматического deploy-loop нет.
- Production deploy и update выполняются пользователем вручную через versioned entrypoint `deploy/scripts/deploy-production.ps1`:
  1. определить локальный expected commit и создать свежий PostgreSQL backup;
  2. обновить `<REMOTE_REPO_PATH>` и подтвердить server `HEAD`;
  3. выполнить Compose config/resolved gate и build/deploy одной invocation с неизменным `-p flowstock --env-file deploy/.env -f deploy/docker-compose.yml`; при включённом Telegram та же invocation дополнительно содержит `-f deploy/docker-compose.telegram.yml`;
  4. проверить containers, live/ready, TSD version, disk space и путь backup.

### Обязательный production-copy gate

CI и code review не являются достаточным основанием для production deploy. Перед merge production-impacting изменения в `main` кандидат должен быть вручную проверен на **изолированной локальной копии свежей production PostgreSQL**, а успешная проверка должна быть привязана к точному Git tree.

Канонический порядок:

1. получить свежий production backup штатным безопасным способом и восстановить его в отдельную локальную/test PostgreSQL; production при smoke не используется и не изменяется;
2. запустить exact candidate tree против этой копии, применить его миграции и выполнить релевантный ручной smoke бизнес-сценариев;
3. только после успешного smoke, из чистого worktree проверенного candidate выполнить:

```powershell
pwsh ./deploy/scripts/record-production-copy-validation.ps1 -ConfirmPassed
```

Команда пишет только безопасную локальную attestation в `.local/production-copy-validation.json`: schema version, тип проверки, result, commit SHA, Git tree SHA и UTC-время. Файл игнорируется Git и не должен содержать connection string, hostname production БД, имя backup, строки данных или другие production details.

После merge в `main` канонический `deploy-production.ps1` вычисляет tree SHA exact `origin/main` commit и **fail closed** требует совпадающую успешную attestation. Commit SHA после merge может отличаться от проверенного candidate, но содержимое обязано иметь тот же Git tree. Любое изменение tree после smoke инвалидирует запись и требует повторной проверки.

Для нестандартного локального расположения записи допустимо передать `-ProductionCopyValidationRecord <path>`; требования к содержимому и совпадению tree не меняются.

### Канонический ручной production deploy

Environment-specific значения не хранятся в Git. На операторской машине задайте их локально, например через PowerShell profile или отдельный неотслеживаемый bootstrap:

```powershell
$env:FLOWSTOCK_PUBLIC_URL='https://<PUBLIC_HOST>:<PORT>'
$env:FLOWSTOCK_REMOTE_REPO_PATH='<REMOTE_REPO_PATH>'
$env:FLOWSTOCK_REMOTE_BACKUP_DIR='<REMOTE_BACKUP_DIR>'
```

Для Telegram-enabled deploy конкретные `FLOWSTOCK_TELEGRAM_EGRESS_NETWORK`, `FLOWSTOCK_TELEGRAM_EGRESS_CONTAINER`, proxy и secret metadata задаются только в локальной/remote environment и не фиксируются в tracked документации.

Запускайте entrypoint **на операторской машине из чистой локальной ветки `main`**, которая точно совпадает с `origin/main`. PowerShell сам выполняет `git fetch origin main`, вычисляет либо проверяет полный expected SHA и останавливается при несовпадении. Production-сервер должен быть доступен по SSH через `<SSH_HOST>` (канонический production SSH host). `<PUBLIC_HOST>` используется отдельно как внешний HTTPS endpoint и должен разрешаться с доверенным TLS-сертификатом на операторской машине, где выполняются внешние HTTPS gates.

```powershell
git switch main
git pull --ff-only origin main
pwsh ./deploy/scripts/deploy-production.ps1 -Server <SSH_HOST> -SshUser <SSH_USER>
```

Для дополнительного операторского подтверждения exact commit допустимо передать полный SHA; он всё равно обязан совпасть с актуальным `origin/main`:

```powershell
pwsh ./deploy/scripts/deploy-production.ps1 `
  -Server <SSH_HOST> `
  -SshUser <SSH_USER> `
  -ExpectedCommit 0123456789abcdef0123456789abcdef01234567
```

Entrypoint последовательно и fail-closed проверяет локальные `main`/worktree/`origin/main`, создаёт и проверяет свежий PostgreSQL custom-format backup в `<REMOTE_BACKUP_DIR>` **до** обновления, fast-forward обновляет `<REMOTE_REPO_PATH>` до exact SHA и проверяет server `HEAD`. Затем он сверяет TSD source versions, экспортирует `FLOWSTOCK_SOURCE_COMMIT`, формирует один массив аргументов Compose, выполняет `config -q` и secret-safe resolved validation через pipe, build/deploy и проверку контейнеров. Server-side gates `live`/`ready`, строгая source identity `/api/version` и deployed TSD version выполняются через loopback backend `http://127.0.0.1:<FLOWSTOCK_PORT>`, где порт берётся из resolved Compose environment (`FLOWSTOCK_PORT`, default `8080`); в текущем production-контуре фактическое значение — resolved `FLOWSTOCK_PORT`. Затем выводятся disk space и путь backup. После успешного SSH/deploy отдельные внешние HTTPS gates выполняются с операторской машины через `<PUBLIC_URL>`.

Entrypoint никогда не печатает `deploy/.env`, connection strings или resolved Compose JSON. При `FLOWSTOCK_TELEGRAM_ENABLED=1` он добавляет `deploy/docker-compose.telegram.yml` в единый массив Compose и fail-closed проверяет absolute secret path, regular/readable/non-empty secret-файл с mode `600`, непустой chat id, непустой `socks5://` proxy, external network и подключение существующего egress-контейнера; после deploy сохраняются Telegram runtime gates. При любом другом значении используется только base `deploy/docker-compose.yml`, а Telegram-файлы, secret, network и runtime не проверяются.

`deploy_from_git.sh` и `deploy_update.sh` остаются helper/legacy и не заменяют этот entrypoint. Скрипт не является unattended CD: production deploy всегда явно запускает оператор. Для проверки контракта без доступа к production используйте:

```bash
python3 deploy/scripts/deploy-production.static.test.py
pwsh -NoProfile -Command '$errors=$null; [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path "deploy/scripts/deploy-production.ps1"), [ref]$null, [ref]$errors) > $null; if ($errors.Count) { exit 1 }'
```

### Сокращение для неканонических ручных диагностических команд

Все отдельные диагностические команды `docker compose` ниже используют переменную. Не собирайте из них альтернативный production deploy:

```bash
DC='docker compose -p flowstock --env-file deploy/.env -f deploy/docker-compose.yml'
```

Для production с `FLOWSTOCK_TELEGRAM_ENABLED=1` задайте вместо неё:

```bash
DC='docker compose -p flowstock --env-file deploy/.env -f deploy/docker-compose.yml -f deploy/docker-compose.telegram.yml'
```

Задайте ровно один вариант один раз в сессии (`export DC=...` не нужен, достаточно `DC=...` и вызова `$DC ...` в том же shell) или используйте полную форму. Не меняйте набор `-f` между `config`, `up`, `ps` и `exec`.

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
$composeArgs = @('-p', 'flowstock', '--env-file', 'deploy/.env', '-f', 'deploy/docker-compose.yml')
if ($env:FLOWSTOCK_TELEGRAM_ENABLED -eq '1') {
    $composeArgs += @('-f', 'deploy/docker-compose.telegram.yml')
}
docker compose @composeArgs config -q
docker compose @composeArgs build
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

Старый WPF не может установить subsystem, которого в нём ещё нет. Один раз вручную доставьте в repository root `D:\Projects\FlowStock` bootstrap commit с `FlowStock.DesktopUpdate`, `FlowStock.Updater`, обновлённым `FLOWSTOCK.cmd` и launcher. Запустите его обычной MAIN-командой. Production server к этому моменту должен быть развёрнут с deterministic identity contract; до этого AdminWindow показывает, что desktop update не поддерживается.

На WPF-машине имя `<PUBLIC_HOST>` должно резолвиться в production server, а HTTPS certificate на `<PUBLIC_URL>` должен быть доверен Windows и содержать соответствующее имя. Operational подключение WPF может использовать другой `server.base_url` и `server.allow_invalid_tls`, но эти параметры не ослабляют desktop update TLS. Для отдельного доверенного стенда допускается process-level `FLOWSTOCK_UPDATE_SERVER_BASE_URL`; production/non-loopback override обязан быть HTTPS root URL.

Пока `%LOCALAPPDATA%\FlowStock\Desktop\state\active-runtime.json` отсутствует, launcher запускает `dotnet run` из repository root. Первая WPF версия с updater subsystem собирает recovery updater из clean detached worktree своего embedded commit, затем устанавливает первый side-by-side target и создаёт active manifest. Installed identity в этом режиме берётся из assembly, а не из repository `HEAD`.

Если для bridge/bootstrap вручную запущена source assembly при уже существующем валидном active runtime, active manifest и каталог предыдущей версии не удаляются. Source assembly явно передаёт source-run mode в bootstrap, recovery updater собирается из её embedded commit, а существующий active runtime остаётся rollback/LKG. При неуспешном обновлении запускается именно предыдущий installed runtime; fallback через `dotnet run` разрешён только когда active runtime отсутствовал до транзакции. Managed updater repository является `--no-checkout` cache: его working-tree `git status` не используется в админке и не означает удаление файлов приложения.

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
FLOWSTOCK_PG_BIND_HOST=<LAN_BIND_IP>
FLOWSTOCK_PG_SECOND_BIND_HOST=<SECOND_BIND_IP>
```

Не используйте для этих переменных пустой host в resolved mapping, `0.0.0.0`, `::` или `[::]`: PostgreSQL должен публиковаться только на явно выбранных интерфейсах.

Перед первым `up` после изменения bind-контракта и перед каждым production deploy выполните одной и той же Compose invocation:

```bash
$DC config -q
set -a
source deploy/.env
set +a
VALIDATOR_ARGS=(--postgres-host "${FLOWSTOCK_PG_BIND_HOST:-127.0.0.1}")
if [ -n "${FLOWSTOCK_PG_SECOND_BIND_HOST:-}" ]; then
  VALIDATOR_ARGS+=(--postgres-host "$FLOWSTOCK_PG_SECOND_BIND_HOST")
fi
if [ "${FLOWSTOCK_TELEGRAM_ENABLED:-0}" = "1" ]; then
  VALIDATOR_ARGS+=(--telegram-enabled)
fi
$DC config --format json |
  python3 deploy/scripts/validate_resolved_compose.py "${VALIDATOR_ARGS[@]}"
```

Полный resolved JSON передаётся validator только через pipe — JSON не печатается и не сохраняется. Validator выводит лишь нормализованный `services.postgres.ports` и итог gate. Для single-bind fragment должен содержать ровно один mapping с `host_ip: 127.0.0.1`. Для production dual-bind должны присутствовать ровно два различных mapping с `host_ip: <LAN_BIND_IP>` и `host_ip: <SECOND_BIND_IP>`; пустые и wildcard HostIp запрещены. При enabled Telegram тот же explicit overlay проверяется флагом `--telegram-enabled`. `python3` уже является fail-closed зависимостью deploy scripts. Если фактическая production-версия Compose возвращает другой результат, deploy блокируется до отдельного архитектурного решения.

Конфигурация, прошедшая gate, применяется без изменения invocation:

```bash
$DC up -d --build --remove-orphans
$DC ps
POSTGRES_CONTAINER_ID="$($DC ps -q postgres)"
docker inspect "$POSTGRES_CONTAINER_ID" --format '{{json .HostConfig.PortBindings}}'
```

Таким образом `config`, `up`, `ps` и получение ID контейнера используют один `-p flowstock`, один `deploy/.env` и один набор Compose-файлов; overlay нельзя добавлять только к части команд.

## Telegram через существующий Tailscale egress

Telegram default-off: только точное `FLOWSTOCK_TELEGRAM_ENABLED=1` включает integration. Production не передаёт bot token через environment и не обращается к Telegram напрямую.

1. Создайте secret file вне clone, например `/opt/flowstock-secrets/telegram/bot-token`; каталог должен иметь mode `700`, файл — `600`. Файл содержит token существующего bot и не добавляется в git.
2. В `deploy/.env` задайте:

   ```bash
   FLOWSTOCK_TELEGRAM_ENABLED=1
   FLOWSTOCK_TELEGRAM_BOT_TOKEN_SECRET_FILE=/opt/flowstock-secrets/telegram/bot-token
   FLOWSTOCK_TELEGRAM_CHAT_ID=<existing-chat-id>
   FLOWSTOCK_TELEGRAM_PROXY_URL=socks5://tailscale-egress:1055
   FLOWSTOCK_TELEGRAM_EGRESS_NETWORK=<TELEGRAM_EGRESS_NETWORK>
   FLOWSTOCK_TELEGRAM_EGRESS_CONTAINER=<TELEGRAM_EGRESS_CONTAINER>
   ```

3. После обязательного свежего PostgreSQL backup, но до controlled recreate, выполните `ensure_telegram_deploy_prerequisites` в shell, где уже был подключён `deploy/scripts/common.sh`. Проверка подтверждает только metadata: source file является обычным читаемым непустым файлом; external network существует; контейнер `<TELEGRAM_EGRESS_CONTAINER>` подключён к ней. Функция не читает и не печатает secret. Затем production deploy использует explicit `compose_with_telegram`/`$DC` с overlay для `config`, validator, `up`, `ps` и `exec`.
4. Overlay монтирует token read-only как `/run/secrets/flowstock_telegram_bot_token`; container environment содержит только `FLOWSTOCK_TELEGRAM_BOT_TOKEN_FILE` с этим путём. Chat id и proxy URL остаются configuration values. Новый bot, egress container или Tailscale instance FlowStock не создаёт.

Общие operational helpers намеренно остаются base-only: `compose()`, `ensure_docker()` и `ensure_compose_config()` сами по себе не подключают Telegram overlay и не проверяют Telegram secret/network. Поэтому `backup_now.sh`, `restore_dump.sh`, `migrate.sh` и backfill не зависят от Telegram даже при `FLOWSTOCK_TELEGRAM_ENABLED=1` в `deploy/.env`. Deploy и rollback после выбора целевой ревизии отдельно переключают application recreate на `compose_with_telegram()`, если Telegram включён и target содержит `deploy/docker-compose.telegram.yml`; rollback к более старой ревизии без overlay остаётся base-only. Перед Telegram-aware recreate выполняется существующий secret-safe `ensure_telegram_deploy_prerequisites`.

При отсутствующем/нечитаемом mounted secret, пустом chat id или неверном proxy server остаётся live/ready, а Telegram отключается либо теряет конкретную best-effort отправку. Не выводите `docker inspect ... .Config.Env`, полный resolved Compose, mounts, token, chat id или proxy URL. Для диагностики разрешены allowlisted health/status, PostgreSQL `PortBindings`, имена networks и безвыводная `test -r /run/secrets/flowstock_telegram_bot_token`.

Ручной smoke ограничен четырьмя сценариями: Disabled — заявка создаётся без Telegram; Enabled — приходит фиксированное сообщение; нерабочий token/proxy — заявка всё равно создаётся без retry; Misconfigured — server и `/health/ready` работают без отправки. Overflow проверяется unit-тестом, а не production smoke.

### Разовый переход с ручного production drift

После появления canonical commit, но до запуска канонического PowerShell deploy-процесса:

1. Сохраните `deploy/.env`, `git diff -- deploy/docker-compose.yml`, `deploy/.env.backup-*` и `deploy/docker-compose.yml.before-*` в закрытом каталоге вне `<REMOTE_REPO_PATH>`; для каталога используйте права `700`, для файлов — `600`.
2. Переместите `deploy/.env.backup-*` и `deploy/docker-compose.yml.before-*` из clone в этот каталог. Не добавляйте их в Git и не используйте `git clean`.
3. Добавьте в существующий `<REMOTE_REPO_PATH>/deploy/.env` оба production-адреса, не удаляя основной bind:

   ```bash
   FLOWSTOCK_PG_BIND_HOST=<LAN_BIND_IP>
   FLOWSTOCK_PG_SECOND_BIND_HOST=<SECOND_BIND_IP>
   ```

4. Повторно проверьте `git diff -- deploy/docker-compose.yml`. Единственным tracked изменением должна быть известная ручная строка `- "<SECOND_BIND_IP>:5432:5432"`; при любом другом diff остановитесь.
5. Уберите только подтверждённый drift командой `git restore --source=HEAD -- deploy/docker-compose.yml` и подтвердите чистый `git status --short`. Ignored `deploy/.env` остаётся на месте.

На подготовительном этапе не выполняйте `git fetch`, `git pull`, merge, fast-forward или Compose-команды. Восстановление файла в working tree не меняет уже запущенный контейнер PostgreSQL: действующий dual-bind сохраняется до controlled recreate.

Дальнейшее обновление выполняется только существующим каноническим FlowStock PowerShell deploy-процессом в порядке: локальный expected commit, свежий PostgreSQL backup, update `<REMOTE_REPO_PATH>`, проверка server `HEAD`, `config -q` и resolved HostIp gate, `up`, containers, `/health/live`, `/health/ready`, TSD version, disk space и путь backup. Для этого rollout не используйте `deploy_from_git.sh`, `deploy_update.sh`, короткую SSH deploy-команду или отдельный server-side deploy. После recreate проверьте фактические Docker `PortBindings` и доступ к PostgreSQL отдельно через LAN и Tailscale.

## Canonical HTTPS endpoint и discovery

Для native Android TSD задайте в `deploy/.env`:

```bash
FLOWSTOCK_PUBLIC_BASE_URL=<PUBLIC_URL>
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

1. DNS `<PUBLIC_HOST>` указывает на сервер, `FLOWSTOCK_PUBLIC_BASE_URL` совпадает с SAN сертификата;
2. `curl -fsS <PUBLIC_URL>/api/discovery` отрабатывает без ошибок.

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
cd <REMOTE_REPO_PATH>
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
cd <REMOTE_REPO_PATH>
cp deploy/.env.example deploy/.env
```

2. Отредактируйте `deploy/.env`: реальный пароль PostgreSQL, `FLOWSTOCK_PUBLIC_BASE_URL`, `FLOWSTOCK_INSTANCE_NAME`, нужные порты и случайный `FLOWSTOCK_WPF_ADMIN_API_KEY` длиной не менее 32 символов.
   Тот же secret задайте каждому доверенному WPF-клиенту через environment `FLOWSTOCK_WPF_ADMIN_API_KEY` либо локальное поле `server.wpf_admin_api_key` в `settings.json`. Secret не добавляется в Git и не идентифицирует конкретного Windows-пользователя.
   Для прямого доступа WPF к PostgreSQL задайте bind-переменные:
   - безопасный default: `127.0.0.1` (доступ только с хоста сервера);
   - пример для production LAN: `FLOWSTOCK_PG_BIND_HOST=<LAN_BIND_IP>`;
   - optional Tailscale bind: `FLOWSTOCK_PG_SECOND_BIND_HOST=<SECOND_BIND_IP>`;
   - пустой `FLOWSTOCK_PG_SECOND_BIND_HOST` не добавляет второй resolved mapping после обязательной проверки Compose;
   - `docker-compose.override.yml` не требуется.
3. Один раз выполните bootstrap локального CA (см. раздел выше).
4. Установите сгенерированный root CA cert на клиентские устройства, включая Android TSD user trust store для release APK.
5. Выполните первый deploy существующим каноническим FlowStock PowerShell-процессом: expected commit, свежий PostgreSQL backup, update `<REMOTE_REPO_PATH>`, проверка `HEAD`, Compose config/resolved gate, build/deploy и post-deploy проверки.

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
cd <REMOTE_REPO_PATH>
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

### GitHub Actions CI gate

Workflow `CI` запускается для каждого pull request в `main`, каждого push в `main` и вручную. Он публикует пять стабильных checks: `windows-build-test`, `postgres-regression`, `web-tests`, `android-native`, `docker-migrations`. Workflow не выполняет production deploy, не читает production secrets и не собирает подписанный Android release.

`postgres-regression` на `windows-2022` поднимает отдельную PostgreSQL 16, применяет полный migration chain через `deploy/scripts/run_migrations.sh`, выполняет четыре focused-класса из обязательного gate выше и затем весь solution с `FLOWSTOCK_POSTGRES_TEST_CONNECTION`. Каждый focused-класс обязан иметь выполненные тесты; любой `Failed`, `Skipped` или `NotExecuted` делает check неуспешным. В полном suite разрешены только явно зафиксированные legacy JSONL skips. Naming guard требует, чтобы DB-зависимые тесты содержали `Postgres` в имени класса и не выпадали между non-DB и PostgreSQL gates.

`docker-migrations` независимо проверяет disposable Compose migrator, точное соответствие `schema_migrations` tracked-файлам и повторное идемпотентное применение, затем выполняет production Compose `config -q`, secret-safe single-bind/dual-bind validator, Telegram overlay config с временным synthetic secret file и build `flowstock`/`discovery-relay` только с dummy CI values. External Telegram network для config-only gate не создаётся. Tracked generated-файлы под `**/artifacts/**` исключаются из Docker context через `.dockerignore`; их удаление остаётся отдельной technical-debt задачей и не блокирует CI.

### Необязательная разовая проверка чистого bootstrap

Только на заведомо пустом сервере или во временном тестовом проекте (команда `down -v` удаляет данные):

```bash
$DC down -v
```

После очистки повторите канонический PowerShell deploy-процесс. Не используйте эту процедуру на существующем production volume.

## Обычное обновление production

Production update выполняет пользователь вручную каноническим versioned entrypoint из чистой локальной `main`:

```powershell
pwsh ./deploy/scripts/deploy-production.ps1 -Server <SSH_HOST> -SshUser <SSH_USER>
```

Процесс определяет expected commit локально, требует его точного равенства `origin/main`, создаёт и проверяет свежий PostgreSQL backup, обновляет `<REMOTE_REPO_PATH>`, проверяет server `HEAD`, выполняет `config -q` и resolved gate, затем build/deploy и проверки containers, live/ready, строгой source identity, TSD version, disk space и пути backup.

Все Compose-команды этого процесса используют одну invocation:

```bash
docker compose -p flowstock --env-file deploy/.env -f deploy/docker-compose.yml ...
```

При `FLOWSTOCK_TELEGRAM_ENABLED=1` к каждой такой команде добавляется `-f deploy/docker-compose.telegram.yml`; base-only invocation остаётся canonical для default-off local/CI.

`deploy_from_git.sh` и `deploy_update.sh` сохраняются как helper/legacy scripts для ограниченных вспомогательных сценариев. Они не являются каноническим production deploy-процессом и не запускаются вместо `deploy-production.ps1`.

## Проверка server clone

После подготовки clone проверьте чистый tracked worktree, доступность ожидаемого commit и соответствие server `HEAD` в составе канонического PowerShell-процесса. Не выполняйте отдельный server-side deploy вместо него.

Если сервер использует mirror для базовых образов, задайте `FLOWSTOCK_DOTNET_SDK_IMAGE` и `FLOWSTOCK_DOTNET_ASPNET_IMAGE` в `deploy/.env` до первого запуска.

Быстрая post-deploy проверка:

```bash
cd <REMOTE_REPO_PATH>
bash deploy/scripts/release_status.sh
$DC ps
curl -fsS http://127.0.0.1:${FLOWSTOCK_PORT:-8080}/health/ready
$DC ps discovery-relay
$DC run --rm --no-deps --entrypoint dotnet discovery-relay FlowStock.DiscoveryRelay.dll healthcheck
```

## Целевой контракт серверного резервного копирования (проектирование)

**Статус:** этот раздел задаёт **целевой контракт будущей реализации**, а не подтверждает наличие перечисленных функций в текущем runtime. Пока не выполнены отдельные реализация, CI, проверка восстановления и операторский cutover, действуют существующие `pgbackup`, `backup_now.sh`, `restore_dump.sh`, клиентский `BackupService` и канонический `deploy-production.ps1`. Новая конфигурация, API, репликация, каталог и протокол заданий **ещё не внедрены**. Никакой автоматической миграции данных или настройки production само появление этого раздела не запускает.

**Согласованное направление:** автономный backup на Debian сохраняется; NAS в той же приватной Tailscale-сети служит отдельным хранилищем с более долгой собственной retention. WPF управляет серверными настройками и заданиями, показывает журналы и сохранённые копии на Debian/NAS через сервер. Это одобрение общей архитектуры, а не разрешение на production provisioning, изменение текущих backups, merge или deploy. Численные значения ниже — целевые параметры для проверки ёмкости и отдельного внедрения.

### Границы ответственности и хранение

- **`pgbackup`** остаётся одним автономным server-side worker, который владеет расписанием, `pg_dump`, валидацией, каталогом, состояниями заданий, очередью отправки и инвентаризацией NAS. Локальный backup работает без WPF, HTTP API и подключения к удалённому хранилищу. Второй backup daemon не создаётся.
- **FlowStock Server** — только защищённый control plane: валидирует actor/permissions, заранее созданный клиентом `request_id` и схему запроса, присваивает внутренний `job_id`, публикует ограниченные команды и читает события/статусы. У него нет Docker socket, права запускать произвольные процессы/команды и доступа к произвольным host paths. WPF не получает PostgreSQL credentials и не запускает локальный `pg_dump` после отдельного cutover.
- **Debian и NAS — два независимых места хранения.** Локальный валидный архив публикуется первым; NAS получает проверенную копию. Debian обеспечивает быстрое восстановление и pre-deploy/pre-restore safety, NAS — более долгую историю и восстановление при потере Debian. Локальный каталог не заменяется сетевым mount NAS; WPF не подключается к NAS напрямую. Приватная сеть не заменяет права, проверку SSH host key, checksum и шифрование хранения.
- **Операторские pre-deploy / pre-restore backup** используют общий низкоуровневый dump/lock primitive, но не зависят от HTTP, remote, конфигурации расписания или живого нового worker. До обновления server checkout должны срабатывать на старой поддерживаемой версии. `deploy-production.ps1` сохраняет порядок: exact-tree attestation → SSH/healthy PostgreSQL → **свежий проверенный dump** → git update/exact HEAD → source/TSD/Compose gates → build/up → live/ready/identity/disk/backup path.
- Persisted worker state, archive catalog, manifests, queue, applied config и maintenance barrier размещаются **в устойчивом server-owned хранилище вне Git checkout и вне самой резервируемой business PostgreSQL**. Сохранённая DB-копия не считается backup этих внешних метаданных, TLS/SSH credentials, PostgreSQL roles или инфраструктуры.
- Базовая совместимость: сохраняется существующий Docker volume `pgbackups` и его legacy `/backups/FlowStock_*.dump`. Новые managed subdirectories: `/backups/managed/archives`, `/backups/managed/staging`, `/backups/managed/catalog`, `/backups/managed/jobs`, `/backups/managed/config`, `/backups/managed/maintenance`. Control-plane inbox/outbox — отдельный подготовленный ограниченный persistent mount, **не** произвольный общий writable mount с архивами. Конкретные volume/UID/GID/ACL в Compose устанавливаются только отдельным implementation PR и operator gate.
- Каталоги доступны только отдельным service identities; каталоги `0700`, архивы/manifest/credentials `0600`, где разрешает FS/владелец. Managed paths проверяются относительно заранее разрешённого root через canonical open/no-follow semantics, directory ownership и запрет traversal, symlink и hardlink alias при публикации/удалении. WPF не принимает абсолютные пути ОС к произвольным файлам. Старые архивы и rollback metadata нельзя молча перемещать, переименовывать, удалять или считать managed.

### Контракт control transport, commands и наблюдения

Запрос клиента `POST` возвращает `202 Accepted` **только после durable enqueue**, но это не успех backup. Server и worker общаются через версионированные JSON-записи ограниченного файлового spool: сервер атомарно публикует `request_id.json` (unique temp → flush/fsync → no-replace rename), worker публикует durable ack/result аналогично. Worker признаёт запрос обработанным только после durable записи статуса; после crash/restart тот же `request_id` возобновляется без второй операции. API возвращает только состояние уже записанного задания, а не оценивает его по HTTP timeout. WPF **до отправки POST** генерирует UUID `request_id`, сохраняет его в локальном pending-action state и использует для последующего lookup. Неизвестный исход POST проверяется чтением этого `request_id`; автоматического повторного POST нет.

Минимальная версия `v1` command envelope (пример схемы, не действующий endpoint):

```json
{
  "schema_version": 1,
  "request_id": "uuid",
  "operation": "CREATE_BACKUP|APPLY_CONFIG|PROBE_REMOTE|RETRY_MIRROR|REFRESH_REMOTE_INVENTORY",
  "actor_id": "server-verified-principal",
  "expected_config_revision": 12,
  "created_at_utc": "RFC3339 UTC",
  "payload": {}
}
```

- `request_id` — клиентский UUID/idempotency key, заранее известный WPF и проверенный сервером (формат, ownership и срок хранения mapping); сервер назначает отдельный внутренний `job_id`. Повтор с тем же actor, operation и canonical payload возвращает существующее задание; повтор с другим содержимым — `409 REQUEST_ID_CONFLICT`. Пользовательский `device_id`, role и произвольные shell args никогда не становятся авторизацией.
- Операции — фиксированный enum; `payload` — закрытая типизированная схема с лимитом размера, обязательными bounds и запретом неизвестных полей. `CREATE_BACKUP` допускает только `origin=manual` и необязательную безопасную метку, а не target OS path или команду оболочки.
- API управления и чтения (контракт для будущей реализации): `GET /api/admin/backups/status`, `GET /api/admin/backups/config`, `PUT /api/admin/backups/config`, `POST /api/admin/backups/jobs`, `GET /api/admin/backups/jobs/{request_id}`, `GET /api/admin/backups/history`, `GET /api/admin/backups/events`, `GET /api/admin/backups/remote/inventory`, `POST /api/admin/backups/remote/inventory/refresh`, `POST /api/admin/backups/remote/probe`, `POST /api/admin/backups/mirror/retry`. Все пути **зарезервированы для будущего API**, а не обещаны текущим сервером. История, события и inventory имеют cursor pagination (до 200 записей на страницу); status polling — bounded; ответы не возвращают raw paths, connection strings или секреты. `GET` читает durable snapshot, а не открывает SFTP-соединение из HTTP handler.
- Catalog record содержит: `backup_id` (UUID), `origin` (`scheduled|manual|pre_deploy|pre_restore`), `created_at_utc`, `completed_at_utc`, `size_bytes`, `sha256`, `pg_major`, `schema_migrations_fingerprint`, `local_state`, `local_presence`, `remote_state`, `destination_revision`, `restore_test_at_utc` (если выполнен). `local_state`/`remote_state` — результаты создания/доставки, не доказательство текущего наличия файла; `local_presence=PRESENT|PRUNED|MISSING|UNKNOWN`, а remote presence описана ниже. `pg_restore --list` обозначается только как `structural_validated`, не как `restorable`.
Типизированные persisted records `v1` (примеры формы, не production payload; все timestamps только UTC):

```json
{
  "schema_version": 1,
  "revision": 12,
  "scheduler": {"enabled": true, "interval_hours": 24},
  "local": {"storage_root_alias": "managed_pgbackups", "keep_count": 30, "keep_days": 30},
  "remote": {
    "enabled": false,
    "protocol": "sftp",
    "destination_revision": 4,
    "host": "preapproved-nas-tailnet-name",
    "port": 22,
    "account": "backup-only",
    "root_alias": "preapproved-remote-root",
    "credential_ref": "server-secret-reference",
    "pinned_host_key_ref": "operator-approved-key-reference",
    "keep_count": 180,
    "keep_days": 180
  }
}
```

```json
{
  "schema_version": 1,
  "backup_id": "uuid",
  "origin": "scheduled",
  "created_at_utc": "RFC3339 UTC",
  "completed_at_utc": "RFC3339 UTC",
  "local_state": "LOCAL_VALID",
  "local_presence": "PRESENT",
  "archive_key": "managed-archive-id",
  "size_bytes": 88185241,
  "sha256": "64 lowercase hex chars",
  "pg_major": 16,
  "schema_migrations_fingerprint": "64 lowercase hex chars or null",
  "schema_migrations_state": "KNOWN|UNKNOWN",
  "structural_validated": true,
  "restore_test_at_utc": null,
  "remote_state": "PENDING",
  "destination_revision": 4
}
```

API job response `v1` содержит `request_id`, `job_id`, `operation`, `job_state`, `backup_id` (если создан), `requested_at_utc`, `started_at_utc`, `finished_at_utc`, `error_code` и `next_retry_at_utc`. Поля времени/backup ID для ещё не выполненных фаз равны `null`. `archive_key` — opaque идентификатор внутри managed root, а не произвольный путь из клиента; SHA-256 проверяется на exact lowercase 64 hex. Fingerprint миграций вычисляется из упорядоченных применённых `schema_migrations(version, filename)`; при невозможности корректно прочитать список сохраняются `schema_migrations_fingerprint=null`, `schema_migrations_state=UNKNOWN` и безопасная причина, но не фальшивый hash. Token `RFC3339 UTC` в примерах обозначает тип, а не буквальное значение при сериализации.

- Состояния local job: `QUEUED → RUNNING → VALIDATING → LOCAL_VALID` либо `LOCAL_FAILED`; crash в промежуточной фазе = `RECOVERING` с повторной сверкой staging/catalog, не фиктивный success. Для remote отдельно: `DISABLED|PENDING → UPLOADING → VERIFYING → VERIFIED` либо `RETRY_WAIT|REMOTE_FAILED`. `LOCAL_VALID` не превращается в `LOCAL_FAILED` из-за сетевой ошибки NAS; `VERIFIED` никогда не выставляется без сравнения прочитанных удалённых байтов.
- Typed error codes (без секретов и raw connection strings): `UNAUTHORIZED`, `FORBIDDEN`, `INVALID_REQUEST`, `UNSUPPORTED_SCHEMA`, `REQUEST_ID_CONFLICT`, `CONFIG_CONFLICT`, `WORKER_UNAVAILABLE`, `MAINTENANCE_PAUSED`, `BACKUP_BUSY`, `DB_UNAVAILABLE`, `STORAGE_LOW`, `DUMP_FAILED`, `DUMP_INVALID`, `REMOTE_AUTH_FAILED`, `REMOTE_HOST_IDENTITY_MISMATCH`, `REMOTE_UNREACHABLE`, `REMOTE_HASH_MISMATCH`, `REMOTE_INVENTORY_INCOMPLETE`, `REMOTE_ARCHIVE_MISSING`, `RESTORE_TEST_REQUIRED`. Ошибка с неопределённым результатом — `UNKNOWN_OUTCOME`, только lookup/reconciliation, не blind retry.

### Config/revisions, расписание, retry и retention

- Configuration хранится отдельно от business DB и не привязана к жизненному циклу WPF. Единственный authoritative **`applied_revision`** подтверждается worker после успешной атомарной проверки и применения. API выдаёт отдельно `requested_revision`, `applied_revision`, `apply_state` (`PENDING|APPLIED|REJECTED`) и фактическую конфигурацию. После `PUT` с `If-Match`/expected revision конкурентная правка возвращает `409 CONFIG_CONFLICT`; readback до worker ack не объявляет настройки применёнными. Нельзя тихо заменить старую работающую конфигурацию частично ошибочной.
- `schema_version=1` для config/catalog/commands. Неизвестная major schema или запись, которую старый worker не умеет безопасно читать, отклоняется fail-closed. Состояние мигрируется copy → validate → atomic publish с сохранением старого файла/архивов; rollback до версии, не понимающей новую схему, разрешён только по протестированной compatibility matrix или с явно восстановленным совместимым состоянием. Никакой silent downgrade.
- `enabled=true` по умолчанию только после отдельного операторского включения нового scheduler; целевой `interval_hours=24`, допустимо **6–168 часов**. Срок следующей плановой попытки считается от **последнего успешного `scheduled`** (UTC); `manual`, `pre_deploy`, `pre_restore` не сдвигают его. При restart до deadline — пропуск; после deadline — один catch-up, не цикл за все пропущенные интервалы.
- Failed dump не меняет `last_scheduled_success_at_utc`. Повторы scheduled failure с экспоненциальными интервалами **5, 15, 60 минут**, затем не чаще раза в **60 минут** до success/operator pause; никогда не ждать 24 часа только потому, что попытка провалилась. Каждая ошибка и `next_retry_at_utc` persisted. Одновременно разрешён только **один** dump/restore-critical operation для данного PostgreSQL/backup root.
- При утере или порче state scheduler **не выводит success из mtime/имени**. Разрешена reconciliation из уже проверенных manifests/catalog; без непротиворечивых доказательств — `DEGRADED / OPERATOR_ACTION_REQUIRED` и безопасный ограниченный fresh attempt только после проверки lock/barrier/free space. Не удалять файлы «для исправления каталога».
- Локальное хранение: `keep_count` **7–90** (по умолчанию 30), `keep_days` **7–365** (по умолчанию 30); удалять только managed `LOCAL_VALID` при превышении **хотя бы одного** ограничения (count или age), но только если архив не защищён pin/remote backlog/recovery и не является последней успешно проверенной копией. Поэтому ограничения являются целями, а сохранение защищённых копий имеет приоритет; показывать превышение квоты как degraded, а не удалять их. Сохранять **последнюю успешно проверенную** копию и явно pinned recovery/pre-deploy/pre-restore файлы. При удалённой очереди/pending archive не удалять единственный источник для отправки или восстановления. Legacy файлы вне managed catalog не подпадают под автоматическое удаление.
- **NAS retention независима:** отдельные `remote.keep_count` **7–3650** и `remote.keep_days` **7–3650**, предлагаемые начальные значения **180 копий / 180 дней** против локальных 30/30. Ограничения count/age также работают по «хотя бы одному»; частые manual dumps могут исчерпать count раньше срока age, поэтому WPF показывает оба лимита. Срок считается от `created_at_utc` архива, count — по собственным опубликованным архивам в конкретном destination, не по числу попыток. Изменение local retention и локальное удаление **не** удаляют NAS-копию; remote retention не удаляет Debian-копию. Уменьшение лимитов требует preview кандидатов и явного подтверждения настройки уполномоченным пользователем; это не запускает немедленный cleanup в HTTP-запросе.
- NAS cleanup выполняет worker только по применённой конфигурации и полному свежему inventory текущего destination. Удаляются только собственные managed, checksum-verified final archives, вышедшие за лимит; сохраняются pins и **последняя подтверждённая копия на NAS независимо от наличия локальной**. Перед удалением повторно проверяются ownership, identity/hash, отсутствие активного upload/restore/recovery pin и maintenance barrier; ошибка или неизвестный результат блокируют дальнейшее удаление до reconciliation. Нельзя удалять foreign/unmanaged, partial, missing/unknown или конфликтующие файлы. Недоступный NAS, неполный/stale inventory либо потерянный catalog запрещают remote cleanup, а не означают пустое хранилище.
- После local prune catalog/manifest и remote inventory сохраняют запись NAS-копии (`local_presence=PRUNED`), даже если ей больше не соответствует локальный файл. После штатного remote prune остаётся audit/tombstone `PRUNED` с причиной/временем; неожиданный `MISSING` не маскируется под retention. Каталог и сведения о pins не очищаются вместе с очередью завершённых jobs; их восстановление — часть DR.
- До dump вычислять ожидаемое рабочее место по максимальному размеру последних трёх валидных архивов (при отсутствии истории — отдельная operator-configured capacity gate). Если свободно менее **2 × ожидаемый архив** или менее **10%** тома, отказ `STORAGE_LOW` с диагностикой; нельзя пытаться исправлять это уничтожением safety/unknown backups. При длительной remote outage local backup продолжает работать, пока есть ёмкость, очередь bounded по действующей retention/disk policy; при исчерпании ёмкости — `DEGRADED` с алертом, не silent data loss.
- Отдельная remote retry-очередь повторяет **тот же** `backup_id`, не новый dump: 5 минут → 15 минут → 1 час → 6 часов → максимум раз в 24 часа (плюс bounded jitter). После успешной доставки `remote_state=VERIFIED`, `completed_at_utc` не меняется. Manual «повторить» не обходит проверку прав, дедупликацию или maintenance barrier.

### Архив, atomic publication, maintenance и restore

- Dump (`pg_dump -Fc` PostgreSQL 16) пишется в уникальный private staging file. Проверить exit code, размер >0, читаемый TOC (`pg_restore --list`), поддерживаемую проверку payload, вычислить SHA-256 по **закрытому** staging файлу, записать manifest и recoverable двухфазную публикацию. Только после flush/durable atomic rename и согласованного catalog commit можно выдавать `LOCAL_VALID`; при crash reconcile staging/archive/manifest без перезаписи другого backup ID. Имя с точностью до секунды **не** является уникальным идентификатором.
- Хеш и TOC не доказывают восстановимость: регулярно выполнять PostgreSQL 16 restore в отдельную disposable DB с проверкой данных, constraints, sequences, `schema_migrations` и ключевых invariants (`ledger`, закрытые документы, заказы/HU/ЧЗ). Результат такой проверки записывается **отдельно** от структурной валидации. На production для проверки нельзя делать `DROP DATABASE`.
- Shared exclusion: dump (scheduled/manual/operator), restore и потенциальные maintenance-записи используют единый storage-backed lock с owner/lease/fencing и durable `maintenance_state` (`NORMAL|PAUSED|RECOVERING`). Lock должен иметь безопасную crash recovery; тайм-аут/неизвестный владелец блокирует опасное продолжение. Restore переводит state в `PAUSED` **до** stop/DB mutation; failed restore остаётся `PAUSED`; возобновление — только после операторской проверки целостности, источника и целевой версии.
- Входной и pre-restore safety dump должны быть проверены **до** удаления БД; `pg_restore` fail-fast; приложения остаются остановленными до validation. До реализации этих guards применять действующий restore runbook только с отдельной операторской оценкой/backup; улучшение restore — отдельный PR.
- Канонический deploy всегда создаёт **новый** pre-deploy dump, даже если scheduled backup свежий, worker остановлен, удалённый сервер недоступен или расписание выключено. Перед code rollback проверять совместимость state/schema и точное соответствие target commit ↔ backup; metadata `latest_success/previous_success` legacy rollback helper нельзя предполагать обновлёнными каноническим PowerShell deploy.

### Первое remote destination: NAS через Tailscale и SFTP/SSH

- Сетевой маршрут — **Debian worker → NAS в общей приватной Tailscale-сети**, по заранее разрешённому tailnet IP/DNS и порту SFTP. Наличие сети на Debian host само по себе не доказывает доступность из `pgbackup`: до включения проверить именно worker → NAS, DNS/маршрутизацию и ограничение доступа этой service identity к нужному NAS/порту. Публичный SSH/SMB endpoint, fallback через публичный адрес и проброс портов не требуются. Фактические addresses, ACL, mounts и secrets задаются отдельным operator provisioning, а не этим документом или WPF.
- Для первого adapter выбирается **SFTP поверх SSH с pinned host public-key fingerprint** (operator provisioning). Создаётся отдельная учётная запись с доступом только в подготовленный каталог. Host key unknown/changed, неподдерживаемый алгоритм, недостаточные права или недоступный путь = fail-closed без `StrictHostKeyChecking=no` / silent TOFU. Host/port/account/relative allowed root — WPF-editable только в пределах operator-allowlist; выбранное имя и разрешённые адреса повторно проверяются при подключении, произвольные public targets, path traversal, symlink и wildcard destination запрещены. Tailscale обеспечивает приватный маршрут, но не заменяет SSH host authentication и ограничения учётной записи NAS.
- Credentials (private key/пароль, passphrase и key recovery) provision/rotate **только на сервере** через одобренный secret provider, вне Git, business DB, spool payload, WPF и логов. WPF видит `configured`/`missing`/`expired` и credential reference, но никогда secret value. Изменение destination/host key/credentials увеличивает `destination_revision`: pending jobs остаются привязаны к старой ревизии, пока оператор явно не выберет drain/cancel/rebind; нельзя молча отправлять старые архивы на новый host.
- SFTP upload: уникальный staging файл с exclusive create, закрытие, **обратное чтение всех байтов из удалённого staging через SFTP и локальный SHA-256** + сравнение размера с source; только после этого publish через поддерживаемое безопасное no-overwrite rename. Если выбранный SFTP сервер не обеспечивает требуемые readback/rename/permission semantics, endpoint считается **unsupported**. Одного SFTP ack, длины файла или `pg_restore --list` недостаточно для удалённой checksum-проверки.
- Remote final никогда не перезаписывает чужой или конфликтующий объект. Повтор по `backup_id + sha256 + destination_revision` идемпотентен; совпадающий уже verified объект распознаётся после readback, конфликт блокируется. Probe — server-initiated уникальный тестовый файл с проверкой read/write/delete **только собственного** тестового объекта, без проверки на production data. Отказ сети не останавливает local dump; retry без повторного `pg_dump`.
- SFTP шифрует **передачу, не хранение**. Перед включением remote replication оператор документально подтверждает encrypted destination volume/dataset, управление ключами/их независимую recovery copy, защищённые backup rights и тест восстановления после потери основного сервера. Невозможно доказать at-rest protection — не включать mirror, не объявлять полный DR-ready. Дополнительный формат encrypted archive — отдельное согласованное расширение; не обещать его в первом adapter.

### Серверная инвентаризация NAS и наблюдение из WPF

Журнал отправок отвечает на вопрос «что worker отправлял», inventory — «что сервер наблюдал на NAS и когда». Одного `remote_state=VERIFIED` в завершённом job недостаточно для списка сохранённых удалённых копий. Инвентаризация — обязательная server-side функция первого NAS/WPF этапа, а не необязательное открытие сетевой папки.

- Worker перечисляет только разрешённый destination root через adapter, без обхода symlink/чужих каталогов; сопоставляет final objects и manifests с durable catalog по `backup_id`, `archive_key`, size/hash и destination identity. Remote manifest публикуется recoverably вместе с архивом; отсутствие/конфликт manifest даёт `UNVERIFIED`, не право на удаление. Неизвестные/legacy объекты показываются как `UNMANAGED` без автоматического импорта, удаления или признания восстановимыми.
- Полное сканирование активного операторски подготовленного destination выполняется после включения/перезапуска, раз в **30 минут** и по `REFRESH_REMOTE_INVENTORY`; после publish/prune обновляется соответствующая запись. Refresh payload содержит только `destination_revision`, сверяемую с применённой конфигурацией, без host/path из запроса. Job проходит `QUEUED → RUNNING → SUCCEEDED|FAILED`, при crash — `RECOVERING` и повторное чтение; `SUCCEEDED` требует durable публикации полного snapshot и возвращает его `generation`. Запросы refresh объединяются для одного destination с сохранением lookup каждого `request_id`, не чаще одного запуска в **60 секунд**; один scan имеет deadline **10 минут**, ошибки используют remote backoff. Долгие сетевые операции не удерживают dump-critical lock и не задерживают local schedule/pre-deploy. Публикация generation и cleanup согласуются с upload: незавершённые пары archive/manifest и изменённые после начала scan записи нельзя признать `MISSING`.
- Snapshot содержит `schema_version=1`, `destination_revision`, `generation`, `scan_state=IDLE|RUNNING|COMPLETE|FAILED`, `last_attempt_at_utc`, `last_success_at_utc`, `error_code`, `is_stale` и записи. Freshness считается от последнего **полного успешного** scan; старше **1 часа**, после смены destination или последней ошибки результат stale. Первый неуспешный scan возвращает неизвестное состояние, а не подтверждённый пустой список. Успешный scan не равен checksum-проверке всех архивов.
- Inventory entry содержит opaque `archive_key`, nullable `backup_id` для unmanaged, `created_at_utc` (из проверенного manifest, иначе `null`), наблюдаемый `size_bytes`, ожидаемый `sha256` (если известен), `local_presence`, `remote_presence=PRESENT|MISSING|PRUNED|UNKNOWN`, `verification=VERIFIED|UNVERIFIED|MISMATCH|UNMANAGED`, `last_seen_at_utc`, `last_verified_at_utc` и `destination_revision`. `VERIFIED` требует фактического полного readback SHA-256; listing/mtime/size не обновляют `last_verified_at_utc`. Изменённый размер/identity сбрасывает verification до повторной проверки; несовпавший hash = `MISMATCH`. UI различает «наблюдался» и «checksum проверен тогда-то»; это не обещание текущей восстановимости.
- Только полный успешный scan может признать ранее известный final отсутствующим (`MISSING`, alert `REMOTE_ARCHIVE_MISSING`), с учётом согласования publish/prune. При outage/тайм-ауте/ошибке прав сохраняется последний успешный snapshot с stale/error и временем наблюдения; отсутствие ответа не превращает все файлы в missing и не разрешает очистку. При потере catalog содержимое NAS остаётся unmanaged до отдельного восстановления ownership/manifests/pins и readback-проверки.
- Смена `destination_revision` сохраняет старые inventory/jobs как историю с их destination; старые сведения не выдаются за содержимое нового NAS/root. Если сменились только credentials/host key для того же физического root, ownership и pins явно reconciled до включения cleanup: нельзя забыть старые копии или считать каждую ревизию независимым пустым каталогом. Retention считает все managed copies этого destination, а не только отправленные последней ревизией.

Целевой WPF раздел резервных копий включает:

| Возможность | Серверный контракт и отображение |
|---|---|
| Настройки | Расписание, разрешённые Debian/NAS roots и параметры подключения, отдельные local/NAS count/age; сохранить/отменить, requested/applied revision и ошибка применения. Secrets не вводятся и не возвращаются через WPF. |
| Сохранённые копии | Отдельные представления «Debian» и **«Копии на NAS»** с фильтром destination, датой, размером, наличием local/remote, verification и временем проверки. NAS-only запись остаётся после local prune. |
| Обновить список NAS | Защищённая серверная inventory job; показываются progress/result, last successful scan, stale/недоступность NAS. Это чтение remote, без probe-файла, нового dump, upload или retention cleanup. |
| Журнал | Durable события dump, mirror/retry, probe, inventory, config и retention: UTC-время, actor, request/job/backup ID, destination revision, исход и безопасная причина. История операций отделена от списка существующих копий; нет dump contents, credentials или raw paths. |
| Действия | «Создать сейчас», «Отправить несинхронизированные», «Проверить соединение» выполняются server-side; `202` означает принятие, закрытие WPF не отменяет job, неизвестный POST outcome разрешается lookup по `request_id`. |
| Состояние | Отдельно local success, remote delivery/backlog, inventory freshness, next run/retry, ошибки и доступное место. Если NAS не предоставляет capacity через поддерживаемый adapter, показывается «неизвестно», не выдуманное значение. |

«Копии на NAS» открывает представление API, а не Explorer/SMB/SFTP с credentials клиента. Read и refresh требуют `backup.read` с server-side rate limit/audit; остальные действия используют отдельные capabilities ниже. Restore, произвольное удаление архивов и скачивание не включаются в эту обязательную UI-часть; download требует отдельного sensitive permission/этапа. DEV preview использует только in-memory fixtures, включая NAS offline, stale, missing и NAS-only, без обращения к NAS/API/DB.

Проверки будущей реализации: реальный изолированный SFTP endpoint через согласованный сетевой маршрут; offline/resume, no permission/changed host key, partial scan, публикация во время scan, mismatch и неизвестные файлы; независимое удаление по local/NAS retention и сохранение pins/последней NAS-копии; NAS-only после local prune; restart/потеря catalog/смена destination; WPF refresh/unknown outcome/reconnect и прямые API permission checks. CI проверяет автоматизируемые сценарии; фактическая доступность production tailnet, at-rest/key recovery и isolated restore остаются отдельными operator gates, **не выполняемыми в docs-only PR**.

### Права, эксплуатация, rollout и DR

- Будущий API требует **реального server-side enforcement** персонального субъекта и capability `backup.read`, `backup.create`, `backup.configure`, `backup.probe`, `backup.replicate`; `backup.download` — отдельное sensitive право вне первого обязательного этапа. `backup.restore` через WPF/API не вводится. Проверять actor, active session/revocation и роли на каждом mutation, аудировать без credentials и dump contents. Пока безопасная модель для WPF/PC и её enforcement не внедрены, **нельзя публиковать новый backup control API на production**. Существующий WPF machine key не считать автоматически эквивалентом персональной роли.
- Operator-only подготовка volumes/permissions, SSH secrets, remote host identity, encrypted destination, firewall и аварийных средств восстановления **не выполняется** при сохранении настроек WPF. Downlevel server/client показывает `UNSUPPORTED`, а не скрытый local `pg_dump` fallback. Старые WPF copies/settings сохраняются; removal client startup dump только после работающего server-backed manual flow, CI, production-copy smoke и отдельного rollout с проверкой downlevel compatibility.
- Operator read-only pre-rollout inventory: actual deployed SHA и source identity; enabled/running `pgbackup` и effective Compose overrides; точный persistent volume/mount и владельцы; последние успешные/ошибочные worker logs; число/размер/последний timestamp копий; `pg_restore --list` последних файлов (read-only); disk capacity/free space; наличие отдельного pre-deploy dump; независимый isolated restore; version/permissions совместимость. Не читать и не публиковать production secrets или содержимое dump; никакого создания/удаления backup во время одного лишь аудита.
- Disaster recovery восстанавливает отдельно: PostgreSQL database dump, внешний worker catalog/config/jobs/maintenance state, PostgreSQL roles/cluster globals/extensions, infrastructure/TLS/SSH credentials и удалённые encryption keys. Заранее записать точные target source commit + backup provenance; не полагаться на неактуальные legacy rollback pointers. При потере remote key доступ к архивам может быть необратимо утрачен, поэтому key recovery проверяется отдельно.
- **Целевые операционные показатели до внедрения**: при `interval=24h` RPO ≤24 часа в штатном режиме; RTO ≤4 часа после доступной валидной копии и готовой инфраструктуры. Это **цели, не подтверждённые production SLO**: замерить на полном изолированном restore и повторять не реже одного раза в **90 дней**. Пропуск двух ожидаемых backup slots, последний валидный архив старше 1,5 × interval или remote backlog старше 24 часов выводит явный alert/degraded status; отсутствие успешного restore rehearsal — `RESTORE_TEST_REQUIRED`.
- Последовательность rollout: documentation → isolated dump/catalog/lock implementation → restore safety/coordination → durable scheduler → protected API → remote adapter + durable queue → WPF → legacy cutover → E2E isolated restore/rollback → **отдельно разрешённый** production acceptance/deploy. Не менять старый `pgbackup`, retention, `pgbackups`, pre-deploy/restore gates, NAS credentials или firewall одним docs-only изменением.

### Подготовленный low-level primitive архивов (#120; ещё не подключён к runtime)

`deploy/backup/managed_backup.py` — автономный **неподключённый** компонент для PostgreSQL 16, использующий только Python 3 standard library, `pg_dump`, `pg_restore` и, при наличии, `psql`. Это реализация атомарного dump/manifest/catalog примитива, **не** замена текущим `pg_backup.sh`, `common.create_backup`, ручному `backup_now.sh`, canonical PowerShell deploy или `restore_dump.sh`. PostgreSQL 16 Alpine контейнер не содержит Python3 по умолчанию: установка runtime и безопасная интеграция в существующие callers — отдельная задача #122. В production ничего автоматически не запускается и существующий volume `pgbackups` не изменяется.

- В заранее доступном absolute backup root создаются закрытые каталоги `managed/archives`, `managed/staging`, `managed/catalog`, `managed/jobs`, `managed/maintenance`. Existing legacy dumps остаются на прежних путях; явный импорт копирует, проверяет и регистрирует файл без удаления/переименования исходника. Для импортированной копии `origin=legacy_import` и `legacy_source` обозначают **неизвестное историческое происхождение**, не имитируют manual/scheduled.
- `create --origin scheduled|manual|pre_deploy|pre_restore --expected-bytes N` использует приватный staging; успешный код завершения `pg_dump` и полный `pg_restore --list` + вывод SQL/payload в `/dev/null` обязательны **до** durable ready-marker и публикации. SHA-256, размер, `schema_migrations` fingerprint (или `UNKNOWN`) и UTC записываются отдельно от результата проверки восстановления: `restore_test_at_utc=null` означает **нет доказанного isolated restore**.
- `flock` на `managed/maintenance/lock` действует на одном persistent filesystem и автоматически отпускается ядром после kill процесса. `pause --reason ...` сохраняет durable pause marker; `resume --operator-confirmed` только после явной operator recovery validation. Во время паузы обычные дампы запрещены; `pre_restore` допускается только с отдельным флагом `--during-maintenance`. Изоляция и координация всех существующих operator/worker callers будут добавлены в #122.
- Crash recovery `recover` восстанавливает публикацию **только при наличии durable ready-marker с проверенными size/hash**, появившегося после успешного `pg_dump`; unfinished или corrupt stage остаётся на operator review, существующие валидные архивы не удаляются. Время последнего успешного scheduled backup не меняется этим примитивом: формирование/commit расписания — #123.
- Минимальные права: только защищённый server-side process owner может изменять managed каталоги (0700) и файлы (0600); SQL-пользователь с разрешениями `CONNECT`, `USAGE` схем и `SELECT` необходимых таблиц/последовательностей достаточен для обычного `pg_dump` в CI fixture, но конкретные grants к production schema необходимо отдельно проверить перед cutover. Секреты не сохраняются в JSON, не передаются командной строкой и не пишутся в стандартные логи.
- `deploy/backup/tests/test_managed_backup.py` проверяет отказы, разрешения, collision, crash/lock/barrier/recovery и отсутствие удаления существующих копий; `integration_pg16.py` на disposable PostgreSQL 16 восстанавливает dump в **отдельную CI БД** и проверяет rows/PK/FK/sequences/migrations. Эти проверки не являются production-copy smoke/attestation или operator DR rehearsal.

## Ручной backup

```bash
cd <REMOTE_REPO_PATH>
bash deploy/scripts/backup_now.sh
```

Dump по конкретному пути:

```bash
bash deploy/scripts/backup_now.sh <REMOTE_BACKUP_DIR>/pre_release.dump
```

## Ручной запуск миграций

Применить pending migrations без пересоздания контейнеров приложения:

```bash
cd <REMOTE_REPO_PATH>
bash deploy/scripts/migrate.sh
```

Проверить применённые миграции:

```bash
$DC exec -T postgres \
  sh -lc 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "SELECT version, filename, applied_at FROM schema_migrations ORDER BY version;"'
```

## Восстановление из dump

```bash
cd <REMOTE_REPO_PATH>
bash deploy/scripts/restore_dump.sh <REMOTE_BACKUP_DIR>/pre_release.dump
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
cd <REMOTE_REPO_PATH>
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
cd <REMOTE_REPO_PATH>
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
bash deploy/scripts/rollback_release.sh <git-ref> <REMOTE_BACKUP_DIR>/pre_release.dump
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
cd <REMOTE_REPO_PATH>
bash deploy/scripts/renew_server_cert.sh --force
$DC restart nginx
```

Немедленно выпустить текущий серверный сертификат:

```bash
cd <REMOTE_REPO_PATH>
bash deploy/scripts/issue_server_cert.sh
$DC restart nginx
```

## Разовые операционные процедуры

Процедуры, привязанные к конкретным миграциям или разовым переходам, описаны отдельно:

- Cutover ЧЗ real-code workflow (`V0027`, `marking_cutover_state`, preflight, enforcement) — `deploy/docs/operations/marking-cutover.md`
- Production backfill статусов ЧЗ (`backfill_marking_status.sh`) — `deploy/docs/operations/marking-backfill.md`
- Полная проверка UDP discovery — `deploy/docs/operations/discovery-smoke.md`

Общее правило для любой миграции, помеченной в release notes как чувствительная: свежий backup перед применением обязателен и создаётся каноническим PowerShell deploy-процессом.

## Примечания

- Migration-файлы применяются в лексикографическом порядке; сохраняйте схему именования `V0001__name.sql`.
- Номера `V0036`–`V0044` уже использовались в исторических/параллельных линиях разработки и могут присутствовать в развёрнутых БД или будущей интеграции этих линий. Текущая active-main цепочка после `V0035` продолжает новые миграции с `V0045`; номера `V0036`–`V0044` повторно не использовать. Раннер пропускает миграцию только при точном совпадении version и filename; одинаковая version с другим filename является fatal collision.
- Каждый migration-файл выполняется раннером внутри транзакции. При любой SQL-ошибке процесс завершается с non-zero кодом, а файл не записывается в `schema_migrations`.
- `deploy/postgres/init/001_init.sql` намеренно минимален: он только bootstrap'ит `schema_migrations` для нового Postgres volume.
- WPF-клиент не отвечает за создание production schema. Если в БД нет миграций, приложение явно сообщает об этом состоянии вместо скрытого создания/обновления схемы.
- Держите server-side clone репозитория чистым: канонический PowerShell deploy-процесс должен остановиться при неожиданных tracked изменениях.
- Метаданные релизов хранятся в `deploy/runtime/releases/`.
- `deploy/nginx/gen_cert.sh` остаётся только как быстрый self-signed fallback; рекомендуемый режим для внутреннего production — `FLOWSTOCK_TLS_MODE=local_ca`.
