#!/usr/bin/env python3
"""Static regression tests for CI scope classification."""

from importlib.util import module_from_spec, spec_from_file_location
from pathlib import Path
import sys
import tempfile


MODULE_PATH = Path("tools/ci/resolve-ci-scope.py")
spec = spec_from_file_location("resolve_ci_scope", MODULE_PATH)
if spec is None or spec.loader is None:
    raise AssertionError(f"Cannot load {MODULE_PATH}")
module = module_from_spec(spec)
sys.modules[spec.name] = module
spec.loader.exec_module(module)


def assert_scope(paths, *, windows=False, postgres=False, web=False, android=False, docker=False):
    actual = module.classify_paths(paths).as_outputs()
    expected = {
        "windows": str(windows).lower(),
        "postgres": str(postgres).lower(),
        "web": str(web).lower(),
        "android": str(android).lower(),
        "docker": str(docker).lower(),
    }
    if actual != expected:
        raise AssertionError(f"scope mismatch for {paths}: expected {expected}, got {actual}")


assert_scope(["apps/android/tsd/pc/pc-order-modal.js"], web=True)
assert_scope(["apps/android/tsd-native/app/src/main/AndroidManifest.xml"], android=True)
assert_scope(["apps/windows/FlowStock.App/MainWindow.xaml.cs"], windows=True)
assert_scope(
    ["apps/windows/FlowStock.Server/Program.cs"],
    windows=True,
    postgres=True,
    docker=True,
)
assert_scope(
    ["apps/windows/FlowStock.Data/PostgresDataStore.cs"],
    windows=True,
    postgres=True,
    docker=True,
)
assert_scope(
    ["apps/windows/FlowStock.Server.Tests/Catalog/CatalogCutoverPostgresTests.cs"],
    windows=True,
    postgres=True,
)
assert_scope(
    ["apps/windows/FlowStock.DiscoveryRelay/Program.cs"],
    windows=True,
    docker=True,
)
assert_scope(
    ["deploy/postgres/migrations/V9999__test.sql"],
    postgres=True,
    docker=True,
)
assert_scope(
    ["deploy/scripts/run_migrations.sh"],
    windows=True,
    postgres=True,
    docker=True,
)
assert_scope(
    ["deploy/docker-compose.yml"],
    docker=True,
)
assert_scope(["FLOWSTOCK.cmd"], windows=True)
assert_scope(
    ["global.json"],
    windows=True,
    postgres=True,
    docker=True,
)
assert_scope(["docs/deployment.md"])
assert_scope(["AGENTS.md"])
assert_scope(
    ["apps/android/tsd/pc/pc-order-modal.js", "apps/android/tsd-native/settings.gradle.kts"],
    web=True,
    android=True,
)

# CI/self-routing and unknown paths must fail closed to the full regression set.
for paths in (
    [".github/workflows/ci.yml"],
    ["tools/ci/resolve-ci-scope.py"],
    ["new-shared-root-config.toml"],
):
    assert_scope(
        paths,
        windows=True,
        postgres=True,
        web=True,
        android=True,
        docker=True,
    )

full = module.Scope.full().as_outputs()
if full != {
    "windows": "true",
    "postgres": "true",
    "web": "true",
    "android": "true",
    "docker": "true",
}:
    raise AssertionError(f"manual/full scope is invalid: {full}")

with tempfile.TemporaryDirectory() as temp_dir:
    output = Path(temp_dir) / "github-output"
    module.write_github_outputs(output, module.Scope(web=True, android=True))
    lines = output.read_text(encoding="utf-8").splitlines()
    if lines != [
        "windows=false",
        "postgres=false",
        "web=true",
        "android=true",
        "docker=false",
    ]:
        raise AssertionError(f"unexpected GITHUB_OUTPUT payload: {lines}")

print("CI scope static contract passed")
