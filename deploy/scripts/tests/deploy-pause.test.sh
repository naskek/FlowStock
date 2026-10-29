#!/usr/bin/env bash
set -euo pipefail
repo="$(cd -- "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
fixture="$(mktemp -d)"
trap 'rm -rf -- "$fixture"' EXIT
# Execute the actual canonical deploy helper, not a test reimplementation.
python3 - "$repo/deploy/scripts/deploy-production.ps1" >"$fixture/helper.sh" <<'PY'
from pathlib import Path
import sys
text=Path(sys.argv[1]).read_text(encoding="utf-8")
start=text.index('ensure_deploy_pause() {')
end=text.index('\n}\n', start)+3
print(text[start:end])
PY
source "$fixture/helper.sh"
fail() { echo "$*" >&2; exit 1; }
pause_reason=pre_deploy:fixture
managed() {
    case "$1" in
        status) printf '%s\n' "$mock_state"; return "${status_exit:-0}" ;;
        pause) printf '%s\n' "$*" >>"$fixture/calls" ;;
        *) return 99 ;;
    esac
}
mock_state='{"paused":false}'
ensure_deploy_pause managed
grep -qx 'pause --reason pre_deploy:fixture' "$fixture/calls"
mock_state='{"paused":true,"pause_reason":"pre_deploy:fixture"}'
ensure_deploy_pause managed
test "$(wc -l <"$fixture/calls")" = 1
for mock_state in '{"paused":true,"pause_reason":"restore:fixture"}' \
             '{"paused":true,"pause_reason":"pre_deploy:other"}' \
             '{"paused":"false"}' '{}' 'not-json'; do
    if (ensure_deploy_pause managed) >"$fixture/output" 2>&1; then
        echo 'unsafe/malformed maintenance state accepted' >&2
        exit 1
    fi
done
mock_state='{"paused":false}'
status_exit=1
if (ensure_deploy_pause managed) >"$fixture/output" 2>&1; then
    echo 'status failure accepted' >&2
    exit 1
fi
test "$(wc -l <"$fixture/calls")" = 1
echo 'canonical deploy pause ownership/retry regressions PASS'
