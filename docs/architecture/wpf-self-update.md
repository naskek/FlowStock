# ADR: source-based самообновление FlowStock WPF

## Статус

Принято для v1.

## Контекст

Production WPF исторически запускался через `dotnet run` из repository root `D:\Projects\FlowStock`; GitHub Releases и подписанного artifact pipeline нет. Этот путь является legacy source-run/bootstrap path и не должен быть обязательным на обычном production-клиенте с установленным active runtime. GitHub `main` может опережать реально развёрнутый production server, а developer working tree может содержать локальные изменения. Source-run repository, managed update repository и active runtime — разные сущности.

## Решение

Authority — embedded full source commit фактически запущенного production server. Server получает SHA явно из canonical deploy invocation и публикует его в `/api/version`. WPF сравнивает manifest repository metadata с локальными constants, но не исполняет URL/branch из payload.

Authority endpoint отделён от operational WPF API configuration. Default — `https://flowstock.local:7154`, единственный override — `FLOWSTOCK_UPDATE_SERVER_BASE_URL`; `server.base_url`, `FLOWSTOCK_SERVER_BASE_URL` и operational `AllowInvalidTls` updater не читает. Non-loopback endpoint разрешён только по HTTPS с обычной platform certificate validation. Resolved endpoint является частью check result, затем сохраняется в `UpdateRequest` и без повторного resolution используется во всех updater recheck, исключая смену authority между initial check и active switch.

После появления active runtime source-based update использует собственный managed Git repository `%LOCALAPPDATA%\FlowStock\Desktop\repository`. Если каталог отсутствует, updater создаёт временный clone только из canonical `https://github.com/naskek/FlowStock.git`, проверяет repository root и `origin`, затем атомарно переносит его в managed path. Существующий непустой каталог никогда не перезаписывается: он обязан пройти canonical repository/remote validation, иначе update останавливается fail-closed. Пользовательский произвольный repository path не поддерживается.

Для первого source-run/bootstrap, когда active runtime ещё отсутствует, сохраняется legacy root `D:\Projects\FlowStock`. Это нужно только для начальной доставки validated updater и source-run recovery. После успешной установки active runtime обычные production updates больше не зависят от developer-specific каталога.

v1 доставляет source: fetch canonical `origin/main`, validation exact target/ancestry, detached updater-owned worktree и `dotnet publish`. Developer/source-run checkout не очищается и не переключается; его dirty/index/branch state не используется как production update repository. Client ahead/diverged блокируется без downgrade.

Runtime устанавливается per-user side-by-side. `active-runtime.json` и `last-known-good.json` содержат только schema/version/commit; executable paths выводятся из фиксированного local root. Active pointer меняется atomic replace. Candidate подтверждает identity/session/token после показа первого рабочего окна, updater ждёт stability window и автоматически возвращает LKG при ошибке.

Каждая transaction заранее имеет отдельный recovery bundle текущего validated updater. Pending state содержит session/phase/identities/commit references и bundle hash, но не произвольные executable paths, URL, branch или shell command. Launcher использует recovery bundle, затем LKG updater, затем source-run bootstrap; candidate updater не является единственной recovery-копией. Windows service/watchdog не вводится.

Recovery terminalization двухфазна. При неподтверждённом candidate pointer сначала возвращается на LKG/source-run и pending получает `fallback-ready`; при уже существующем valid ACK target остаётся active, success result сохраняется, а pending получает `success-ready`. Recovery updater не запускает runtime: он автоматически завершается с code `0`, launcher передаёт runtime `--update-session` и лишь после успешной инициации процесса удаляет pending. Recovery failure возвращает ненулевой code и сохраняет pending. Для `success-ready` повторный startup ACK не нужен, поскольку recovery decision уже основан на предыдущем ACK; session handoff нужен для однократного показа result. Ошибка запуска runtime сохраняет recovery state и диагностику для безопасного повтора.

## Последствия

- Первый source-run rollout требует одной ручной доставки bootstrap commit в legacy root.
- Обычный production-клиент с active runtime не требует `D:\Projects\FlowStock`.
- Локально нужны Git, совместимый .NET SDK, NuGet restore, права и disk space.
- Source-based v1 доверяет production-approved commit canonical repository; signing и готовые artifacts остаются v2.
- Hard kill/reboot восстанавливается при следующем запуске launcher.
- Update subsystem не выполняет DB migrations, не изменяет ledger/документы и не запускает production deploy.
