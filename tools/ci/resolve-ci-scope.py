#!/usr/bin/env python3
"""Resolve which canonical CI jobs are relevant to a change set."""

from __future__ import annotations

import argparse
from dataclasses import dataclass
from pathlib import Path
import subprocess
from typing import Iterable


@dataclass
class Scope:
    windows: bool = False
    postgres: bool = False
    web: bool = False
    android: bool = False
    docker: bool = False

    @classmethod
    def full(cls) -> "Scope":
        return cls(windows=True, postgres=True, web=True, android=True, docker=True)

    def merge(self, other: "Scope") -> None:
        self.windows = self.windows or other.windows
        self.postgres = self.postgres or other.postgres
        self.web = self.web or other.web
        self.android = self.android or other.android
        self.docker = self.docker or other.docker

    def as_outputs(self) -> dict[str, str]:
        return {
            "windows": _bool_text(self.windows),
            "postgres": _bool_text(self.postgres),
            "web": _bool_text(self.web),
            "android": _bool_text(self.android),
            "docker": _bool_text(self.docker),
        }


def _bool_text(value: bool) -> str:
    return "true" if value else "false"


def _under(path: str, prefix: str) -> bool:
    return path == prefix.rstrip("/") or path.startswith(prefix)


def classify_paths(paths: Iterable[str]) -> Scope:
    scope = Scope()

    for raw_path in paths:
        path = raw_path.strip().replace("\\", "/")
        while path.startswith("./"):
            path = path[2:]
        path = path.lstrip("/")
        if not path:
            continue

        # CI definitions and CI tooling can change the routing logic itself.
        # Fail closed by exercising every canonical check.
        if _under(path, ".github/") or _under(path, "tools/ci/"):
            return Scope.full()

        # Pure documentation/agent metadata does not affect runtime artifacts.
        if (
            path.endswith(".md")
            or _under(path, "docs/")
            or _under(path, ".agents/")
            or _under(path, "img/")
            or _under(path, "artifacts/")
            or path in {".gitattributes", ".gitignore", "skills-lock.json"}
        ):
            continue

        if _under(path, "apps/android/tsd-native/"):
            scope.android = True
            continue

        if _under(path, "apps/android/tsd/"):
            scope.web = True
            continue

        if _under(path, "apps/windows/FlowStock.Server.Tests/"):
            scope.windows = True
            scope.postgres = True
            continue

        if (
            _under(path, "apps/windows/FlowStock.Core/")
            or _under(path, "apps/windows/FlowStock.Data/")
            or _under(path, "apps/windows/FlowStock.Server/")
        ):
            scope.windows = True
            scope.postgres = True
            scope.docker = True
            continue

        if _under(path, "apps/windows/FlowStock.DiscoveryRelay/"):
            scope.windows = True
            scope.docker = True
            continue

        if _under(path, "apps/windows/"):
            scope.windows = True
            continue

        if _under(path, "deploy/postgres/"):
            scope.postgres = True
            scope.docker = True
            continue

        if _under(path, "deploy/scripts/"):
            scope.windows = True
            scope.docker = True
            if path in {
                "deploy/scripts/run_migrations.sh",
                "deploy/scripts/wait_for_postgres.sh",
            }:
                scope.postgres = True
            continue

        if _under(path, "deploy/") or path == ".dockerignore":
            scope.docker = True
            continue

        if path == "FLOWSTOCK.cmd":
            scope.windows = True
            continue

        if path in {"global.json", "NuGet.Config", "Directory.Build.props", "Directory.Build.targets"}:
            scope.merge(Scope(windows=True, postgres=True, docker=True))
            continue

        # Unknown files are intentionally conservative. A new shared/build path
        # must not silently bypass regression coverage.
        return Scope.full()

    return scope


def changed_paths(base_sha: str, head_sha: str) -> list[str]:
    if not base_sha or not head_sha:
        raise ValueError("pull_request scope requires both base and head SHA")

    result = subprocess.run(
        ["git", "diff", "--name-only", "--diff-filter=ACMRD", f"{base_sha}...{head_sha}"],
        check=True,
        capture_output=True,
        text=True,
    )
    return [line for line in result.stdout.splitlines() if line.strip()]


def write_github_outputs(output_path: Path, scope: Scope) -> None:
    with output_path.open("a", encoding="utf-8", newline="\n") as handle:
        for name, value in scope.as_outputs().items():
            handle.write(f"{name}={value}\n")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--event-name", required=True)
    parser.add_argument("--base-sha", default="")
    parser.add_argument("--head-sha", default="")
    parser.add_argument("--github-output", required=True)
    args = parser.parse_args()

    if args.event_name == "pull_request":
        paths = changed_paths(args.base_sha, args.head_sha)
        scope = classify_paths(paths)
        print("Changed paths:")
        for path in paths:
            print(f"  {path}")
    else:
        # workflow_dispatch is the explicit full-regression entry point.
        scope = Scope.full()
        print(f"Event {args.event_name!r}: using full CI scope")

    outputs = scope.as_outputs()
    print("Resolved scope: " + ", ".join(f"{name}={value}" for name, value in outputs.items()))
    write_github_outputs(Path(args.github_output), scope)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
