#!/usr/bin/env python3
"""Fail CI if known production-specific fingerprints re-enter tracked text files."""

from pathlib import Path
import subprocess


FORBIDDEN = {
    "operator SSH alias": "debian" + "-server",
    "operator username": "semi" + "on",
    "production LAN address": "192.168." + "1.3",
    "production overlay address": "100.66." + "142.112",
    "production checkout path": "/opt/" + "FlowStock",
    "production backup path": "/opt/" + "flowstock-backups",
    "external Telegram project network": "reg-ru-imap-telegram" + "_default",
    "external Telegram container": "reg-ru-imap-telegram-" + "tailscale-egress",
}

tracked = subprocess.check_output(["git", "ls-files", "-z"]).decode("utf-8").split("\0")
violations = []

for raw_path in tracked:
    if not raw_path:
        continue
    path = Path(raw_path)
    try:
        text = path.read_text(encoding="utf-8")
    except (UnicodeDecodeError, OSError):
        continue
    for label, needle in FORBIDDEN.items():
        if needle in text:
            violations.append((raw_path, label))

if violations:
    for path, label in violations:
        print(f"{path}: forbidden public-repository fingerprint: {label}")
    raise SystemExit(1)

print("public repository hygiene contract passed")
