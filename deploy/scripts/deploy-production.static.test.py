#!/usr/bin/env python3
"""Небольшой static contract test канонического production entrypoint."""

from pathlib import Path
import re


SCRIPT = Path(__file__).with_name("deploy-production.ps1")
source = SCRIPT.read_text(encoding="utf-8")


def require(pattern: str, message: str) -> int:
    match = re.search(pattern, source, re.MULTILINE | re.DOTALL)
    if match is None:
        raise AssertionError(message)
    return match.start()


checks = {
    "local main": require(r"\$branch -ne 'main'", "local main gate is missing"),
    "clean local tree": require(r"git @\('status', '--porcelain'\)", "local clean-tree gate is missing"),
    "origin/main identity": require(r"\$ExpectedCommit -ne \$originMain", "origin/main identity gate is missing"),
    "backup verification": require(r"pg_restore --list", "backup verification is missing"),
    "backup path": require(r'backup_dir="/opt/flowstock-backups/manual"', "canonical backup path is missing"),
    "exact server update": require(r"git merge --ff-only --quiet \"\$expected_commit\"", "exact server update is missing"),
    "source commit": require(r"export FLOWSTOCK_SOURCE_COMMIT=\"\$expected_commit\"", "source commit export is missing"),
    "dotenv parser": require(r'compose_environment="\$\("\$\{compose\[@\]\}" config --environment\)" \|\| fail', "Compose/dotenv parser must fail closed"),
    "base compose": require(r"-f deploy/docker-compose\.yml", "base Compose file is missing"),
    "conditional Telegram": require(
        r'if test "\$telegram_enabled_value" = 1; then.*compose\+=\(-f deploy/docker-compose\.telegram\.yml\).*fi',
        "Telegram overlay is not conditional",
    ),
    "config": require(r'"\$\{compose\[@\]\}" config -q', "Compose config gate is missing"),
    "resolved validator": require(r"validate_resolved_compose\.py", "resolved Compose validator is missing"),
    "deploy": require(r'"\$\{compose\[@\]\}" up -d --remove-orphans', "Compose deploy is missing"),
    "loopback port": require(r'flowstock_port="\$\{compose_env\[FLOWSTOCK_PORT\]:-8080\}"', "loopback port must come from resolved Compose environment"),
    "loopback URL": require(r'loopback_url="http://127\.0\.0\.1:\$flowstock_port"', "canonical loopback URL is missing"),
    "internal live": require(r'curl -fsS\s+"?\$loopback_url/health/live"?', "server-side live check must use canonical loopback URL"),
    "internal ready": require(r'curl -fsS\s+"?\$loopback_url/health/ready"?', "server-side ready check must use canonical loopback URL"),
    "internal identity endpoint": require(r'curl -fsS\s+"?\$loopback_url/api/version"?', "server-side source identity check must use canonical loopback URL"),
    "internal TSD endpoint": require(r'curl -fsS\s+"?\$loopback_url/tsd/app-version\.js"?', "server-side TSD check must use canonical loopback URL"),
    "identity": require(r"desktop\.get\(\"target_commit\"\)==expected", "strict source identity gate is missing"),
    "TSD": require(r"deployed TSD version", "deployed TSD check is missing"),
    "disk": require(r'df -h \"\$repo\" \"\$backup_path\"', "disk-space report is missing"),
    "external HTTPS": require(r"https://flowstock\.local:7154", "canonical external HTTPS URL is missing"),
    "external live/ready": require(r'Invoke-WebRequest -UseBasicParsing -Uri "\$PublicUrl\$endpoint"', "external operator health gate is missing"),
    "external identity": require(r'Invoke-RestMethod -Uri "\$PublicUrl/api/version"', "external operator identity gate is missing"),
    "external TSD": require(r'\$PublicUrl/tsd/app-version\.js', "external operator TSD gate is missing"),
    "LF normalization": require(
        r'\$remoteScript = \$remoteScript\.Replace\("\x60r\x60n", "\x60n"\)\.Replace\("\x60r", "\x60n"\)',
        "remote bash script must normalize Windows CRLF/CR to LF",
    ),
    "UTF-8 base64 transport": require(
        r'\[System\.Text\.Encoding\]::UTF8\.GetBytes\(\$remoteScript\).*?ToBase64String\(\$remoteBytes\)',
        "normalized remote script must be transported as UTF-8 base64",
    ),
    "base64 stdin transport": require(
        r'\$remoteBase64\s*\|\s*&\s*ssh\s+\$sshTarget\s+\$remoteCommand',
        "base64 payload must travel over SSH stdin instead of the remote command line",
    ),
    "PowerShell CRLF cleanup": require(
        r'\$remoteCommand = "tr -d \'\\\\r\\\\n\' \| base64 -d \| bash -s --',
        "remote transport must strip PowerShell-added CR/LF before base64 decoding",
    ),
    "remote completion marker": require(
        r"FLOWSTOCK_DEPLOY_OK=%s",
        "remote deploy must emit an exact completion marker",
    ),
    "local completion marker gate": require(
        r'\$successMarker = "FLOWSTOCK_DEPLOY_OK=\$ExpectedCommit".*?\$remoteOutput -notcontains \$successMarker',
        "local deploy must require the exact remote completion marker",
    ),
}

telegram_gate = re.search(
    r'if test "\$telegram_enabled_value" = 1; then(?P<body>.*?)\nfi',
    source,
    re.MULTILINE | re.DOTALL,
)
if telegram_gate is None:
    raise AssertionError("Telegram enabled-only preflight block is missing")
telegram_block = telegram_gate.group("body")
for marker, message in {
    'case "$token_file" in': "absolute Telegram secret path gate is missing",
    'test -f "$token_file" && test -r "$token_file" && test -s "$token_file"': "Telegram secret metadata gate is missing",
    'test "$token_mode" = 600': "Telegram secret mode 600 gate is missing",
    'FLOWSTOCK_TELEGRAM_CHAT_ID': "Telegram chat id gate is missing",
    'socks5://?*) ;;': "Telegram socks5 proxy gate is missing",
    'docker network inspect "$telegram_network"': "Telegram external network gate is missing",
    'reg-ru-imap-telegram-tailscale-egress': "Telegram egress attachment gate is missing",
}.items():
    if marker not in telegram_block:
        raise AssertionError(message)

remote_match = re.search(r"\$remoteScript = @\'\n(?P<body>.*?)\n\'@", source, re.MULTILINE | re.DOTALL)
if remote_match is None:
    raise AssertionError("remote deploy script block is missing")
if "https://flowstock.local:7154" in remote_match.group("body"):
    raise AssertionError("server-side post-deploy gates must not depend on external HTTPS")

if not checks["backup verification"] < checks["exact server update"] < checks["deploy"]:
    raise AssertionError("backup, exact update and deploy order is unsafe")

pre_backup = source[: checks["backup verification"]]
if re.search(r'"\$\{compose\[@\]\}" (?:up|build|pull|restart|create)', pre_backup):
    raise AssertionError("mutating Compose command is forbidden before verified backup")

if re.search(r'\$remoteScript\s*\|\s*&\s*ssh', source):
    raise AssertionError("raw PowerShell text must not be piped directly to remote bash")
if re.search(r'\$remoteCommand\s*=.*\$remoteBase64', source):
    raise AssertionError("base64 payload must not be embedded in the SSH remote command")
if "tr -d '\\\\r\\\\n' | base64 -d | bash -s --" not in source:
    raise AssertionError("SSH stdin transport must remove Windows CR/LF before base64 decoding")

if 'done < <("${compose[@]}" config --environment)' in source:
    raise AssertionError("Compose environment parsing must not hide parser failure in process substitution")
if re.search(r'(?m)^\s*(?:source|\.)\s+deploy/\.env(?:\s|$)', source):
    raise AssertionError("deploy/.env must never be executed as shell code")
if re.search(r'(?m)^\s*set\s+-a(?:\s|$)', source):
    raise AssertionError("automatic export of dotenv values is forbidden")
exports = re.findall(r'(?m)^\s*export\s+([A-Za-z_][A-Za-z0-9_]*)=', source)
if exports != ["FLOWSTOCK_SOURCE_COMMIT"]:
    raise AssertionError("only FLOWSTOCK_SOURCE_COMMIT may be exported by the remote deploy script")

if "config --format json | \"${validator[@]}\"" not in source:
    raise AssertionError("resolved Compose JSON must be piped directly to the validator")

print("deploy-production.ps1 static contract tests passed")
