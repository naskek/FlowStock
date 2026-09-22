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
    "exact server update": require(r"git merge --ff-only --quiet \"\$expected_commit\"", "exact server update is missing"),
    "source commit": require(r"export FLOWSTOCK_SOURCE_COMMIT=\"\$expected_commit\"", "source commit export is missing"),
    "base compose": require(r"-f deploy/docker-compose\.yml", "base Compose file is missing"),
    "conditional Telegram": require(
        r'if test "\$\{FLOWSTOCK_TELEGRAM_ENABLED:-0\}" = 1; then.*compose\+=\(-f deploy/docker-compose\.telegram\.yml\).*fi',
        "Telegram overlay is not conditional",
    ),
    "config": require(r'"\$\{compose\[@\]\}" config -q', "Compose config gate is missing"),
    "resolved validator": require(r"validate_resolved_compose\.py", "resolved Compose validator is missing"),
    "deploy": require(r'"\$\{compose\[@\]\}" up -d --remove-orphans', "Compose deploy is missing"),
    "live": require(r"/health/live", "live check is missing"),
    "ready": require(r"/health/ready", "ready check is missing"),
    "identity": require(r"desktop\.get\(\"target_commit\"\)==expected", "strict source identity gate is missing"),
    "TSD": require(r"deployed TSD version", "deployed TSD check is missing"),
    "disk": require(r'df -h \"\$repo\" \"\$backup_path\"', "disk-space report is missing"),
    "external HTTPS": require(r"https://flowstock\.local:7154", "canonical external HTTPS URL is missing"),
}

if not checks["backup verification"] < checks["exact server update"] < checks["deploy"]:
    raise AssertionError("backup, exact update and deploy order is unsafe")

pre_backup = source[: checks["backup verification"]]
if re.search(r'"\$\{compose\[@\]\}" (?:up|build|pull|restart|create)', pre_backup):
    raise AssertionError("mutating Compose command is forbidden before verified backup")

if "config --format json | \"${validator[@]}\"" not in source:
    raise AssertionError("resolved Compose JSON must be piped directly to the validator")

print("deploy-production.ps1 static contract tests passed")
