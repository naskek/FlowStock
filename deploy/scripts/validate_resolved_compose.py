#!/usr/bin/env python3
import argparse
import json
import sys
from typing import List, Set


WILDCARD_HOSTS = {"", "0.0.0.0", "::", "[::]"}
TOKEN_FILE = "/run/secrets/flowstock_telegram_bot_token"


def fail(message: str) -> None:
    print(f"resolved Compose validation failed: {message}", file=sys.stderr)
    raise SystemExit(1)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--postgres-host", action="append", required=True)
    parser.add_argument("--telegram-enabled", action="store_true")
    parser.add_argument("--forbidden-environment-value")
    return parser.parse_args()


def normalize_ports(document: dict, expected_hosts: Set[str]) -> List[dict]:
    try:
        ports = document["services"]["postgres"]["ports"]
    except (KeyError, TypeError):
        fail("services.postgres.ports is missing")
    if not isinstance(ports, list):
        fail("services.postgres.ports is not a list")

    normalized = []
    for port in ports:
        if not isinstance(port, dict):
            fail("PostgreSQL port mapping is not an object")
        host = str(port.get("host_ip") or "")
        target = str(port.get("target") or "")
        published = str(port.get("published") or "")
        protocol = str(port.get("protocol") or "tcp").lower()
        if host in WILDCARD_HOSTS:
            fail("PostgreSQL HostIp is empty or wildcard")
        if target != "5432" or published != "5432" or protocol != "tcp":
            fail("PostgreSQL mapping must be explicit TCP 5432:5432")
        normalized.append({"host_ip": host, "target": 5432, "published": 5432, "protocol": "tcp"})

    actual_hosts = {port["host_ip"] for port in normalized}
    if len(normalized) != len(actual_hosts):
        fail("PostgreSQL mappings contain duplicate HostIp values")
    if actual_hosts != expected_hosts:
        fail("resolved PostgreSQL HostIp set does not match configured bind hosts")
    return sorted(normalized, key=lambda item: item["host_ip"])


def validate_telegram(document: dict, forbidden_environment_value: str = None) -> None:
    try:
        flowstock = document["services"]["flowstock"]
    except (KeyError, TypeError):
        fail("services.flowstock is missing")

    environment = flowstock.get("environment") or {}
    if not isinstance(environment, dict):
        fail("services.flowstock.environment is not an object")
    if environment.get("FLOWSTOCK_TELEGRAM_ENABLED") != "1":
        fail("Telegram overlay does not enable the channel exactly")
    if environment.get("FLOWSTOCK_TELEGRAM_BOT_TOKEN_FILE") != TOKEN_FILE:
        fail("Telegram token file path is not fixed to /run/secrets")
    if "FLOWSTOCK_TELEGRAM_BOT_TOKEN" in environment:
        fail("Telegram token value must not be present in environment")
    if forbidden_environment_value is not None and forbidden_environment_value in {
        str(value) for value in environment.values()
    }:
        fail("forbidden secret value is present in FlowStock environment")

    secrets = flowstock.get("secrets") or []
    if not any(
        isinstance(secret, dict)
        and secret.get("source") == "flowstock_telegram_bot_token"
        and secret.get("target") == "flowstock_telegram_bot_token"
        for secret in secrets
    ):
        fail("FlowStock Telegram secret mount is missing")

    networks = flowstock.get("networks") or {}
    network_names = set(networks if isinstance(networks, list) else networks.keys())
    if not {"default", "telegram-egress"}.issubset(network_names):
        fail("FlowStock must retain default network and attach telegram-egress")

    telegram_network = (document.get("networks") or {}).get("telegram-egress") or {}
    if telegram_network.get("external") is not True or not telegram_network.get("name"):
        fail("telegram-egress must resolve to a named external network")

    telegram_secret = (document.get("secrets") or {}).get("flowstock_telegram_bot_token") or {}
    if not telegram_secret.get("file"):
        fail("Telegram secret must resolve from a host file")


def main() -> None:
    args = parse_args()
    expected_hosts = {host.strip() for host in args.postgres_host if host.strip()}
    if not expected_hosts or expected_hosts & WILDCARD_HOSTS:
        fail("configured PostgreSQL bind hosts are empty or wildcard")
    try:
        document = json.load(sys.stdin)
    except (json.JSONDecodeError, UnicodeDecodeError):
        fail("input is not valid Compose JSON")

    ports = normalize_ports(document, expected_hosts)
    if args.telegram_enabled:
        validate_telegram(document, args.forbidden_environment_value)

    print(json.dumps({"services": {"postgres": {"ports": ports}}}, ensure_ascii=False))
    print("resolved Compose validation: ok")


if __name__ == "__main__":
    main()
