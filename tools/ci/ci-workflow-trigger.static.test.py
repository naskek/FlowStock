#!/usr/bin/env python3
"""Static contract for canonical CI triggers and scope-aware routing."""

from pathlib import Path
import re


WORKFLOW = Path(".github/workflows/ci.yml")
POSTGRES_PROVISIONER = Path("tools/ci/provision-postgres.ps1")
source = WORKFLOW.read_text(encoding="utf-8")
postgres_provisioner = POSTGRES_PROVISIONER.read_text(encoding="utf-8")


def require(pattern: str, message: str, *, text: str = source) -> None:
    if re.search(pattern, text, re.MULTILINE | re.DOTALL) is None:
        raise AssertionError(message)


match = re.search(r"(?ms)^on:\s*\n(?P<body>.*?)(?=^permissions:\s*$)", source)
if match is None:
    raise AssertionError("CI trigger block is missing")

trigger_block = match.group("body")

if re.search(r"(?m)^\s{2}push:\s*$", trigger_block):
    raise AssertionError("canonical CI must not run automatically on push to main")

if re.search(r"(?m)^\s{2}pull_request:\s*$", trigger_block) is None:
    raise AssertionError("canonical CI must run for pull requests")

if re.search(r"(?m)^\s{4}branches:\s*\[main\]\s*$", trigger_block) is None:
    raise AssertionError("canonical CI pull_request trigger must target main")

if re.search(r"(?m)^\s{2}workflow_dispatch:\s*$", trigger_block) is None:
    raise AssertionError("canonical CI must remain manually dispatchable")

if re.search(r"(?m)^\s{4}paths(?:-ignore)?:\s*$", trigger_block):
    raise AssertionError("workflow-level path filtering would leave required checks pending")


def job_block(job_id: str) -> str:
    match = re.search(
        rf"(?ms)^  {re.escape(job_id)}:\s*\n(?P<body>.*?)(?=^  [A-Za-z0-9_-]+:\s*\n|\Z)",
        source,
    )
    if match is None:
        raise AssertionError(f"required canonical CI job is missing: {job_id}")
    return match.group("body")


required_jobs = (
    "windows-build-test",
    "postgres-regression",
    "web-tests",
    "android-native",
    "docker-migrations",
)
for job_id in required_jobs:
    block = job_block(job_id)
    require(
        rf"(?m)^\s{{4}}name:\s*{re.escape(job_id)}\s*$",
        f"canonical check name must remain stable: {job_id}",
        text=block,
    )

expected_checkout_pin = "3d3c42e5aac5ba805825da76410c181273ba90b1"
checkout_pins = re.findall(r"actions/checkout@([0-9a-f]{40})\s+# v7", source)
if len(checkout_pins) != len(required_jobs):
    raise AssertionError("every canonical CI job must use one pinned actions/checkout@v7 step")
if any(pin != expected_checkout_pin for pin in checkout_pins):
    raise AssertionError("all canonical CI checkout steps must use the approved pinned v7 SHA")

web = job_block("web-tests")
if re.search(r"(?m)^\s{4}if:\s*", web):
    raise AssertionError("web-tests is the always-on required policy/scope gate and must not be job-skipped")
require(r"fetch-depth:\s*0", "scope detection requires full git history", text=web)
require(r"name:\s*Resolve CI scope", "web-tests must resolve PR scope", text=web)
require(r"id:\s*scope", "scope step id is required for job outputs", text=web)
require(
    r"python3 tools/ci/resolve-ci-scope\.py",
    "web-tests must use the tracked scope resolver",
    text=web,
)
for output in ("windows", "postgres", "web", "android", "docker"):
    require(
        rf"(?m)^\s{{6}}{output}:\s*\$\{{\{{ steps\.scope\.outputs\.{output} \}}\}}\s*$",
        f"web-tests must publish {output} scope output",
        text=web,
    )
require(
    r"if:\s*\$\{\{\s*steps\.scope\.outputs\.web == 'true'\s*\}\}",
    "tracked Node tests must be conditional on web scope",
    text=web,
)
require(
    r"python3 tools/ci/ci-scope\.static\.test\.py",
    "scope classification regression test must run in the always-on gate",
    text=web,
)

heavy_outputs = {
    "windows-build-test": "windows",
    "postgres-regression": "postgres",
    "android-native": "android",
    "docker-migrations": "docker",
}
for job_id, output in heavy_outputs.items():
    block = job_block(job_id)
    require(
        r"(?m)^\s{4}needs:\s*web-tests\s*$",
        f"{job_id} must depend on the always-on web-tests gate",
        text=block,
    )
    require(
        rf"(?m)^\s{{4}}if:\s*\$\{{\{{\s*needs\['web-tests'\]\.outputs\.{output} == 'true'\s*\}}\}}\s*$",
        f"{job_id} must be gated by {output} scope",
        text=block,
    )

postgres = job_block("postgres-regression")
build_index = postgres.find("Restore and build PostgreSQL test target")
provision_index = postgres.find("Provision PostgreSQL 16")
if build_index < 0 or provision_index < 0 or build_index >= provision_index:
    raise AssertionError("PostgreSQL compile gate must run before PostgreSQL provisioning")
if "ikalnytskyi/action-setup-postgres" in postgres:
    raise AssertionError("PostgreSQL regression must not use the Chocolatey-backed setup action")
require(
    r"name:\s*Restore PostgreSQL 16 portable binaries.*"
    r"id:\s*postgres-binaries-cache.*"
    r"actions/cache@caa296126883cff596d87d8935842f9db880ef25\s+# v5.*"
    r"path:\s*\$\{\{ runner\.temp \}\}\\postgresql-16\.15-5.*"
    r"key:\s*postgresql-windows-x64-16\.15-5-43BB45F173A6F08CF1D29A97A6D8DEB119E8E8093A24C00D2D1001A0CCAA8281",
    "PostgreSQL 16 binaries must use the exact pinned portable cache",
    text=postgres,
)
if "restore-keys:" in postgres:
    raise AssertionError("PostgreSQL binary cache must not fall back to a different archive")
for required in (
    'postgresql-$postgresVersion-$packageRevision-windows-x64-binaries.zip',
    'https://get.enterprisedb.com/postgresql/$archiveName',
    '43BB45F173A6F08CF1D29A97A6D8DEB119E8E8093A24C00D2D1001A0CCAA8281',
    'Get-FileHash -LiteralPath $archivePath -Algorithm SHA256',
    'initdb.exe',
    'pg_ctl.exe',
    'createdb.exe',
    'SHOW server_version_num',
):
    if required not in postgres_provisioner:
        raise AssertionError(f"portable PostgreSQL provisioner contract is missing: {required}")
if re.search(r"(?i)\\b(?:choco|chocolatey|winget)\\b", postgres_provisioner):
    raise AssertionError("portable PostgreSQL provisioner must not invoke a package manager")
require(
    r"\.\/tools\/ci\/provision-postgres\.ps1.*"
    r"-InstallRoot.*postgresql-16\.15-5.*"
    r"-DataRoot.*postgresql-data.*"
    r"-Port 15432.*"
    r"-CacheHit",
    "PostgreSQL provisioning must use the tracked portable provisioner",
    text=postgres,
)
if "dotnet test apps/windows/FlowStock.sln" in postgres:
    raise AssertionError("postgres-regression must not rerun the full Windows solution test suite")
require(
    r'dotnet test apps/windows/FlowStock\.Server\.Tests/FlowStock\.Server\.Tests\.csproj.*'
    r'--filter "FullyQualifiedName~Postgres"',
    "postgres-regression must run only convention-protected PostgreSQL tests",
    text=postgres,
)
require(
    r"-- xUnit\.ParallelizeTestCollections=false",
    "PostgreSQL-only regression run must serialize xUnit test collections sharing the test database",
    text=postgres,
)
require(
    r"-RequiredTestClasses @\(.*CatalogCutoverPostgresTests.*UomCatalogPostgresTests.*"
    r"CommercialCatalogPostgresConcurrencyTests.*CommercialPriceShipmentConcurrencyPostgresTests",
    "PostgreSQL regression gate must retain the focused release classes",
    text=postgres,
)

android = job_block("android-native")
require(
    r"android-actions/setup-android@be39fa834029ff78f1a44aa3bb0819b8fc2bd8fd\s+# v4\.0\.4",
    "android-native must use the pinned Node.js 24 setup-android release",
    text=android,
)
if "android-actions/setup-android@9fc6c4e9069bf8d3d10b2204b1fb8f6ef7065407" in android:
    raise AssertionError("deprecated Node.js 20 setup-android pin must not remain")

print("CI trigger and scope-routing static contract passed")
