#!/usr/bin/env bash
set -euo pipefail

script="$(cd -- "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/rollback_release.sh"
bash -n "$script"

reject() {
    local expected="$1"
    shift
    local output
    if output="$(bash "$script" "$@" 2>&1)"; then
        echo "unsafe rollback command succeeded: $*" >&2
        exit 1
    fi
    if [[ "$output" != *"$expected"* ]]; then
        printf 'unexpected rollback error: %s\n' "$output" >&2
        exit 1
    fi
}
sha="0123456789abcdef0123456789abcdef01234567"
reject "exactly one explicit target" --no-restore
reject "40-character lowercase" --no-restore not-a-commit
reject "automatic SQL restore/restart is unsafe" "$sha"
reject "exactly one explicit target" --no-restore "$sha" extra
reject "automatic SQL restore/restart is unsafe" --allow-downlevel-app-only "$sha"

python3 - "$script" <<'PY'
import pathlib,sys
source=pathlib.Path(sys.argv[1]).read_text()
assert source.index('(( ${#positionals[@]} == 1 ))') < source.index('ensure_docker')
assert 'source "$latest_success_file"' not in source
assert 'source "$previous_success_file"' not in source
PY
echo "rollback explicit-target preflight regressions PASS"
