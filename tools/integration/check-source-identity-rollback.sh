#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
COMMON="${REPO_DIR}/deploy/scripts/common.sh"
WORK_ROOT="$(mktemp -d)"

cleanup() {
    rm -rf -- "$WORK_ROOT"
}
trap cleanup EXIT

fail_check() {
    printf '[flowstock-source-identity-check] ERROR: %s\n' "$*" >&2
    exit 1
}

fixture_repo="${WORK_ROOT}/repo"
mkdir -p "$fixture_repo"
git -C "$fixture_repo" init -q
git -C "$fixture_repo" config user.email flowstock-tests@example.invalid
git -C "$fixture_repo" config user.name 'FlowStock Tests'
printf 'legacy\n' >"${fixture_repo}/README"
git -C "$fixture_repo" add README
git -C "$fixture_repo" commit -qm legacy
legacy_commit="$(git -C "$fixture_repo" rev-parse HEAD)"

mkdir -p "${fixture_repo}/apps/windows/FlowStock.DesktopUpdate"
printf '<Project Sdk="Microsoft.NET.Sdk" />\n' \
    >"${fixture_repo}/apps/windows/FlowStock.DesktopUpdate/FlowStock.DesktopUpdate.csproj"
git -C "$fixture_repo" add apps/windows/FlowStock.DesktopUpdate/FlowStock.DesktopUpdate.csproj
git -C "$fixture_repo" commit -qm modern
modern_commit="$(git -C "$fixture_repo" rev-parse HEAD)"

export FLOWSTOCK_ENV_FILE="${WORK_ROOT}/missing.env"
export FLOWSTOCK_REPO_DIR="$fixture_repo"
# shellcheck disable=SC1090
source "$COMMON"

known_legacy_commit="7aeb19879f829338b4187ec6fa972b170aafd914"
git -C "$REPO_DIR" cat-file -e "${known_legacy_commit}^{commit}" \
    || fail_check "known legacy rollback target is missing: $known_legacy_commit"
FLOWSTOCK_REPO_DIR="$REPO_DIR"
if target_supports_source_identity_contract "$known_legacy_commit"; then
    fail_check 'known production rollback target was classified as modern'
fi
FLOWSTOCK_REPO_DIR="$fixture_repo"

if target_supports_source_identity_contract "$legacy_commit"; then
    fail_check 'legacy target was classified as modern'
fi
target_supports_source_identity_contract "$modern_commit" \
    || fail_check 'modern target was classified as legacy'

legacy_payload='{"version":"1.0.0-0123456789abcdef","pc_web_version":"abcdef012345"}'
invalid_legacy_payload='{"version":"1.0.0-0123456789abcdef"}'
modern_payload="{\"server_build\":{\"source_commit\":\"${modern_commit}\"},\"desktop_update\":{\"target_commit\":\"${modern_commit}\",\"policy\":\"server_source_commit\",\"repository_url\":\"https://github.com/naskek/FlowStock.git\",\"branch\":\"main\"}}"

printf '%s' "$legacy_payload" | validate_legacy_version_payload \
    || fail_check 'valid legacy /api/version payload was rejected'
if printf '%s' "$invalid_legacy_payload" | validate_legacy_version_payload >/dev/null 2>&1; then
    fail_check 'invalid legacy /api/version payload was accepted'
fi
if printf '%s' 'not-json' | validate_legacy_version_payload >/dev/null 2>&1; then
    fail_check 'unparseable legacy /api/version payload was accepted'
fi
if (
    compose() { return 22; }
    assert_deployed_legacy_version_payload
) >/dev/null 2>&1; then
    fail_check 'unavailable legacy /api/version endpoint was accepted'
fi
if printf '%s' "$legacy_payload" | validate_source_identity_payload "$modern_commit" >/dev/null 2>&1; then
    fail_check 'modern validation accepted a response without server_build/desktop_update'
fi
printf '%s' "$modern_payload" | validate_source_identity_payload "$modern_commit" \
    || fail_check 'valid modern source identity payload was rejected'

printf '[flowstock-source-identity-check] ok\n'
