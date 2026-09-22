[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Server,

    [string]$SshUser,

    [string]$ExpectedCommit,

    [ValidatePattern('^https://flowstock\.local:7154$')]
    [string]$PublicUrl = 'https://flowstock.local:7154'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-CheckedNative {
    param(
        [Parameter(Mandatory = $true)] [string]$FilePath,
        [Parameter(Mandatory = $true)] [string[]]$ArgumentList
    )

    $result = & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed ($LASTEXITCODE): $FilePath"
    }
    return $result
}

foreach ($command in @('git', 'ssh')) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "Required command is not installed: $command"
    }
}

$repoRoot = (Invoke-CheckedNative git @('rev-parse', '--show-toplevel')).Trim()
Push-Location $repoRoot
try {
    $branch = (Invoke-CheckedNative git @('branch', '--show-current')).Trim()
    if ($branch -ne 'main') {
        throw "Canonical production deploy must run from local main, current branch: $branch"
    }
    if (@(Invoke-CheckedNative git @('status', '--porcelain')).Count -ne 0) {
        throw 'Local worktree must be clean'
    }

    Invoke-CheckedNative git @('fetch', '--quiet', 'origin', 'main') | Out-Null
    $localMain = (Invoke-CheckedNative git @('rev-parse', '--verify', 'main^{commit}')).Trim().ToLowerInvariant()
    $originMain = (Invoke-CheckedNative git @('rev-parse', '--verify', 'origin/main^{commit}')).Trim().ToLowerInvariant()
    if ($localMain -ne $originMain) {
        throw "Local main must exactly match origin/main ($originMain)"
    }

    if ([string]::IsNullOrWhiteSpace($ExpectedCommit)) {
        $ExpectedCommit = $originMain
    }
    $ExpectedCommit = $ExpectedCommit.Trim().ToLowerInvariant()
    if ($ExpectedCommit -notmatch '^[0-9a-f]{40}$' -or $ExpectedCommit -ne $originMain) {
        throw 'ExpectedCommit must be the full lowercase SHA of origin/main'
    }

    $appVersion = Get-Content -Raw 'apps/android/tsd/app-version.js'
    $serviceWorker = Get-Content -Raw 'apps/android/tsd/service-worker.js'
    $appMatch = [regex]::Match($appVersion, 'var version = "([0-9]+)";')
    $workerMatch = [regex]::Match($serviceWorker, 'TSD_SERVICE_WORKER_VERSION = "([0-9]+)";')
    if (-not $appMatch.Success -or -not $workerMatch.Success -or $appMatch.Groups[1].Value -ne $workerMatch.Groups[1].Value) {
        throw 'Local TSD source versions are missing or inconsistent'
    }
    $expectedTsdVersion = $appMatch.Groups[1].Value

    $sshTarget = if ([string]::IsNullOrWhiteSpace($SshUser)) { $Server } else { "${SshUser}@${Server}" }
    Write-Host "Deploying exact origin/main commit $ExpectedCommit to $sshTarget"

    $remoteScript = @'
set -Eeuo pipefail
expected_commit="$1"
expected_tsd_version="$2"
repo=/opt/FlowStock
cd "$repo"

fail() { printf '[deploy] ERROR: %s\n' "$*" >&2; exit 1; }
log() { printf '[deploy] %s\n' "$*"; }
command -v git >/dev/null || fail 'git is required'
command -v docker >/dev/null || fail 'docker is required'
command -v python3 >/dev/null || fail 'python3 is required'
command -v curl >/dev/null || fail 'curl is required'
docker compose version >/dev/null || fail 'docker compose is required'
test -f deploy/.env || fail 'deploy/.env is missing'
test -f deploy/docker-compose.yml || fail 'base Compose file is missing'
test -z "$(git status --porcelain)" || fail 'server worktree is not clean'

# Select the one Compose invocation once, without executing deploy/.env as shell code.
export FLOWSTOCK_SOURCE_COMMIT="$expected_commit"
compose=(docker compose -p flowstock --env-file deploy/.env -f deploy/docker-compose.yml)
declare -A compose_env=()
while IFS='=' read -r key value; do
    compose_env["$key"]="$value"
done < <("${compose[@]}" config --environment)
telegram_enabled_value="${compose_env[FLOWSTOCK_TELEGRAM_ENABLED]:-0}"
telegram_enabled=0
if test "$telegram_enabled_value" = 1; then
    telegram_enabled=1
    test -f deploy/docker-compose.telegram.yml || fail 'Telegram overlay is missing'
    token_file="${compose_env[FLOWSTOCK_TELEGRAM_BOT_TOKEN_SECRET_FILE]:-}"
    case "$token_file" in
        /*) ;;
        *) fail 'Telegram token secret file path must be absolute' ;;
    esac
    test -f "$token_file" && test -r "$token_file" && test -s "$token_file" || fail 'Telegram token secret file is unavailable'
    token_mode="$(stat -c '%a' "$token_file")" || fail 'Cannot read Telegram token secret file mode'
    test "$token_mode" = 600 || fail 'Telegram token secret file mode must be 600'
    test -n "${compose_env[FLOWSTOCK_TELEGRAM_CHAT_ID]:-}" || fail 'Telegram chat id is missing'
    telegram_proxy="${compose_env[FLOWSTOCK_TELEGRAM_PROXY_URL]:-}"
    case "$telegram_proxy" in
        socks5://?*) ;;
        *) fail 'Telegram proxy must be a non-empty socks5:// URL' ;;
    esac
    telegram_network="${compose_env[FLOWSTOCK_TELEGRAM_EGRESS_NETWORK]:-reg-ru-imap-telegram_default}"
    docker network inspect "$telegram_network" >/dev/null 2>&1 || fail 'Telegram egress network is unavailable'
    docker inspect reg-ru-imap-telegram-tailscale-egress --format '{{json .NetworkSettings.Networks}}' |
        python3 -c 'import json,sys; raise SystemExit(0 if sys.argv[1] in json.load(sys.stdin) else 1)' "$telegram_network" ||
        fail 'Telegram egress container is not attached to its network'
    compose+=(-f deploy/docker-compose.telegram.yml)
fi

"${compose[@]}" config -q
postgres_id="$("${compose[@]}" ps -q postgres)"
test -n "$postgres_id" || fail 'running production postgres container is missing; refusing to mutate the stack before backup'
status="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$postgres_id")"
test "$status" = healthy || fail 'production postgres is not healthy; refusing to mutate the stack before backup'
backup_dir="/opt/flowstock-backups/manual"
mkdir -p "$backup_dir"
backup_path="$backup_dir/flowstock-before-${expected_commit:0:12}-$(date -u +%Y%m%dT%H%M%SZ).dump"
"${compose[@]}" exec -T postgres sh -eu -c 'pg_dump -Fc -U "$POSTGRES_USER" -d "$POSTGRES_DB"' >"$backup_path"
test -s "$backup_path" || fail 'backup is empty'
"${compose[@]}" exec -T postgres pg_restore --list <"$backup_path" >/dev/null
log "fresh PostgreSQL backup verified: $backup_path"

# Update the server clone to exactly the caller-verified origin/main commit.
git fetch --quiet origin main
remote_main="$(git rev-parse --verify 'origin/main^{commit}')"
test "$remote_main" = "$expected_commit" || fail 'server origin/main differs from expected commit'
git checkout --quiet main
git merge --ff-only --quiet "$expected_commit"
server_head="$(git rev-parse --verify 'HEAD^{commit}')"
test "$server_head" = "$expected_commit" || fail 'server HEAD differs from expected commit'
test -z "$(git status --porcelain)" || fail 'server worktree became dirty after update'

source_tsd_version="$(sed -n 's/.*var version = "\([0-9][0-9]*\)";.*/\1/p' apps/android/tsd/app-version.js)"
worker_tsd_version="$(sed -n 's/.*TSD_SERVICE_WORKER_VERSION = "\([0-9][0-9]*\)";.*/\1/p' apps/android/tsd/service-worker.js)"
test "$source_tsd_version" = "$expected_tsd_version" || fail 'server TSD source version differs from local source'
test "$worker_tsd_version" = "$expected_tsd_version" || fail 'server TSD source versions are inconsistent'

"${compose[@]}" config -q
postgres_bind_host="${compose_env[FLOWSTOCK_PG_BIND_HOST]:-127.0.0.1}"
postgres_second_bind_host="${compose_env[FLOWSTOCK_PG_SECOND_BIND_HOST]:-}"
validator=(python3 deploy/scripts/validate_resolved_compose.py --postgres-host "$postgres_bind_host")
test -z "$postgres_second_bind_host" || validator+=(--postgres-host "$postgres_second_bind_host")
test "$telegram_enabled" = 0 || validator+=(--telegram-enabled)
"${compose[@]}" config --format json | "${validator[@]}" >/dev/null
log 'secret-safe resolved Compose validation passed'

"${compose[@]}" pull postgres nginx pgbackup
"${compose[@]}" build flowstock discovery-relay
"${compose[@]}" up -d --remove-orphans
"${compose[@]}" ps

for service in postgres flowstock discovery-relay nginx pgbackup; do
    id="$("${compose[@]}" ps -q "$service")"
    test -n "$id" || fail "container is missing: $service"
    state="$(docker inspect -f '{{.State.Status}}' "$id")"
    test "$state" = running || fail "container is not running: $service"
done
if test "$telegram_enabled" = 1; then
    flowstock_id="$("${compose[@]}" ps -q flowstock)"
    docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$flowstock_id" | grep -qx 'FLOWSTOCK_TELEGRAM_ENABLED=1' || fail 'Telegram runtime flag is missing'
    docker inspect -f '{{json .NetworkSettings.Networks}}' "$flowstock_id" |
        python3 -c 'import json,sys; raise SystemExit(0 if sys.argv[1] in json.load(sys.stdin) else 1)' "$telegram_network" ||
        fail 'FlowStock is not attached to Telegram egress at runtime'
fi

curl -fsS http://127.0.0.1:18080/health/live >/dev/null
curl -fsS http://127.0.0.1:18080/health/ready >/dev/null
version_json="$(curl -fsS http://127.0.0.1:18080/api/version)"
printf '%s' "$version_json" | python3 -c '
import json,re,sys
expected=sys.argv[1]
data=json.load(sys.stdin)
server=data.get("server_build") or {}
desktop=data.get("desktop_update") or {}
valid=(re.fullmatch(r"[0-9a-f]{40}", expected) is not None
 and server.get("source_commit")==expected
 and desktop.get("target_commit")==expected
 and desktop.get("policy")=="server_source_commit"
 and desktop.get("repository_url")=="https://github.com/naskek/FlowStock.git"
 and desktop.get("branch")=="main")
raise SystemExit(0 if valid else 1)
' "$expected_commit" || fail '/api/version source identity gate failed'

deployed_tsd="$(curl -fsS http://127.0.0.1:18080/tsd/app-version.js | sed -n 's/.*var version = "\([0-9][0-9]*\)";.*/\1/p')"
test "$deployed_tsd" = "$expected_tsd_version" || fail 'deployed TSD version differs from source'
log "deployed TSD version: $deployed_tsd"
df -h "$repo" "$backup_path"
log "verified backup path: $backup_path"
log "deployed source commit: $expected_commit"
'@

    $remoteScript | & ssh $sshTarget 'bash' '-s' '--' $ExpectedCommit $expectedTsdVersion 2>&1 |
        Tee-Object -Variable remoteOutput | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Remote production deploy failed with exit code $LASTEXITCODE"
    }

    foreach ($endpoint in @('/health/live', '/health/ready')) {
        Invoke-WebRequest -UseBasicParsing -Uri "$PublicUrl$endpoint" | Out-Null
    }
    $versionPayload = Invoke-RestMethod -Uri "$PublicUrl/api/version"
    if ($versionPayload.server_build.source_commit -ne $ExpectedCommit -or
        $versionPayload.desktop_update.target_commit -ne $ExpectedCommit -or
        $versionPayload.desktop_update.policy -ne 'server_source_commit' -or
        $versionPayload.desktop_update.repository_url -ne 'https://github.com/naskek/FlowStock.git' -or
        $versionPayload.desktop_update.branch -ne 'main') {
        throw 'External /api/version source identity gate failed'
    }
    $deployedTsdSource = (Invoke-WebRequest -UseBasicParsing -Uri "$PublicUrl/tsd/app-version.js").Content
    $deployedTsdMatch = [regex]::Match($deployedTsdSource, 'var version = "([0-9]+)";')
    if (-not $deployedTsdMatch.Success -or $deployedTsdMatch.Groups[1].Value -ne $expectedTsdVersion) {
        throw 'External deployed TSD version gate failed'
    }
    Write-Host "Production deploy verified: commit=$ExpectedCommit, TSD=$expectedTsdVersion"
}
finally {
    Pop-Location
}
