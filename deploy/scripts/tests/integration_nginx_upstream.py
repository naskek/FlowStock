#!/usr/bin/env python3
"""Реальный nginx:1.27 upstream-cache regression на изолированной Docker-сети.

No FlowStock production services, database, credentials or persistent volumes.
The TLS certificate is disposable, and verification is disabled ONLY for this fixture.
"""
import json
from pathlib import Path
import re
import ssl
import subprocess
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen
import uuid

ROOT = Path(__file__).resolve().parents[3]
IMAGE = "nginx:1.27-alpine"
OLD_COMMIT = "1" * 40
NEW_COMMIT = "2" * 40


def command(*args, check=True, timeout=120):
    result = subprocess.run(args, text=True, capture_output=True, timeout=timeout)
    if check and result.returncode != 0:
        raise AssertionError(f"Command {args[:3]} failed ({result.returncode}):\n{result.stdout}\n{result.stderr}")
    return result


def container_ip(name):
    return command("docker", "inspect", "-f", "{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}", name).stdout.strip()


def get_https(port, endpoint):
    # Self-signed certificate is for the disposable test fixture, never production.
    url = f"https://127.0.0.1:{port}{endpoint}"
    request = Request(url, headers={"Host": "flowstock.local"})
    try:
        with urlopen(request, timeout=3, context=ssl._create_unverified_context()) as response:
            return response.status, response.read().decode("utf-8")
    except HTTPError as error:
        return error.code, error.read().decode("utf-8")
    except (URLError, TimeoutError, ConnectionError):
        return None, ""


def eventually(check, description, seconds=20):
    deadline = time.monotonic() + seconds
    last = None
    while time.monotonic() < deadline:
        last = check()
        if last:
            return
        time.sleep(0.3)
    raise AssertionError(f"{description} (last={last})")


def backend_config(commit):
    return f"""server {{
    listen 8080;
    location = /health/live {{ return 200 'live'; }}
    location = /health/ready {{ return 200 'ready'; }}
    location = /api/version {{
        default_type application/json;
        return 200 '{{"server_build":{{"source_commit":"{commit}"}}}}';
    }}
}}
"""


def reload_from_canonical_deploy(nginx_id):
    # Exercise the *actual* remote Bash function in deploy-production.ps1.
    source = (ROOT / "deploy/scripts/deploy-production.ps1").read_text(encoding="utf-8")
    start = source.index("refresh_nginx_upstream() {")
    end = source.index("\n}\n", start) + 3
    helper = source[start:end]
    script = ("set -euo pipefail\n"
              "fail() { echo \"$*\" >&2; exit 1; }\n"
              "log() { :; }\n"
              + helper + "\nrefresh_nginx_upstream \"$1\"\n")
    return command("bash", "-c", script, "bash", nginx_id, check=False)


def main():
    if command("docker", "image", "inspect", IMAGE, check=False).returncode:
        command("docker", "pull", IMAGE)

    suffix = uuid.uuid4().hex[:12]
    network = f"fs-upstream-173-{suffix}"
    old = f"fs-upstream-old-{suffix}"
    new = f"fs-upstream-new-{suffix}"
    proxy = f"fs-upstream-proxy-{suffix}"
    blocker = f"fs-upstream-blocker-{suffix}"
    names = [old, new, proxy, blocker]

    with tempfile.TemporaryDirectory(prefix="flowstock-nginx-upstream-") as tmp:
        root = Path(tmp)
        certdir = root / "certs"
        certdir.mkdir()
        command("openssl", "req", "-x509", "-nodes", "-newkey", "rsa:2048",
                "-days", "1", "-subj", "/CN=flowstock.local",
                "-keyout", str(certdir / "flowstock.key"),
                "-out", str(certdir / "flowstock.crt"))
        old_conf = root / "old.conf"
        old_conf.write_text(backend_config(OLD_COMMIT), encoding="utf-8")
        new_conf = root / "new.conf"
        new_conf.write_text(backend_config(NEW_COMMIT), encoding="utf-8")
        proxy_conf = root / "flowstock.conf"
        proxy_conf.write_text((ROOT / "deploy/nginx/flowstock.conf").read_text(encoding="utf-8"), encoding="utf-8")

        command("docker", "network", "create", network)
        try:
            command("docker", "run", "-d", "--pull", "never", "--name", old,
                    "--network", network, "--network-alias", "flowstock",
                    "-v", f"{old_conf}:/etc/nginx/conf.d/default.conf:ro", IMAGE)
            old_ip = container_ip(old)
            command("docker", "run", "-d", "--pull", "never", "--name", proxy,
                    "--network", network, "-p", "127.0.0.1::443",
                    "-v", f"{proxy_conf}:/etc/nginx/conf.d/default.conf:ro",
                    "-v", f"{certdir}:/etc/nginx/certs:ro", IMAGE)
            exposed = command("docker", "port", proxy, "443/tcp").stdout
            port_match = re.search(r"127\\.0\\.0\\.1:(\\d+)", exposed)
            if port_match is None:
                raise AssertionError(f"nginx port was not published to localhost: {exposed}")
            port = int(port_match.group(1))

            def matches(commit):
                status, body = get_https(port, "/api/version")
                return status == 200 and json.loads(body)["server_build"]["source_commit"] == commit

            eventually(lambda: matches(OLD_COMMIT), "initial HTTPS must route to old backend")

            # Kill old backend but keep *exactly the same nginx container*.
            command("docker", "rm", "-f", old)
            command("docker", "run", "-d", "--pull", "never", "--name", blocker,
                    "--network", network, IMAGE)
            command("docker", "run", "-d", "--pull", "never", "--name", new,
                    "--network", network, "--network-alias", "flowstock",
                    "-v", f"{new_conf}:/etc/nginx/conf.d/default.conf:ro", IMAGE)
            new_ip = container_ip(new)
            if new_ip == old_ip:
                raise AssertionError("fixture did not force a backend IP change")

            eventually(lambda: get_https(port, "/health/ready")[0] == 502,
                       "without reload, nginx must reproduce stale-upstream 502")
            result = reload_from_canonical_deploy(proxy)
            if result.returncode:
                raise AssertionError("canonical reload failed: " + result.stderr + result.stdout)
            eventually(lambda: matches(NEW_COMMIT), "reloaded HTTPS must route to NEW exact source commit")
            for endpoint in ("/health/live", "/health/ready"):
                status, _ = get_https(port, endpoint)
                if status != 200:
                    raise AssertionError(f"{endpoint} after reload returned {status}")

            # Invalid mounted nginx config must make the canonical helper fail,
            # not silently send a HUP or bypass external HTTPS gates.
            proxy_conf.write_text("server { invalid_config_directive on; }\n", encoding="utf-8")
            bad = reload_from_canonical_deploy(proxy)
            if bad.returncode == 0 or "nginx config validation failed" not in bad.stderr:
                raise AssertionError(f"invalid config was accepted: {bad.stdout} {bad.stderr}")
            if not matches(NEW_COMMIT):
                raise AssertionError("invalid reload interrupted the last validated nginx worker")
            print("nginx stale-upstream 502 reproduced; canonical reload restored HTTPS/identity; invalid config fails closed")
        finally:
            for name in names:
                command("docker", "rm", "-f", name, check=False)
            command("docker", "network", "rm", network, check=False)


if __name__ == "__main__":
    main()
