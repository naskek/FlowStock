#!/usr/bin/env bash
set -euo pipefail

source "$(cd -- "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/common.sh"
fixture="$(mktemp -d)"
trap 'rm -rf -- "$fixture"' EXIT
data="$fixture/managed.dump"
printf 'PGDMPmanaged-test-archive' >"$data"
good_hash="$(sha256sum "$data")"
good_hash="${good_hash%% *}"
good_size="$(stat -c %s "$data")"
id="0123456789abcdef0123456789abcdef"
calls="$fixture/calls"

compose() {
    printf 'compose %s\n' "$*" >>"$calls"
    case " $* " in
        *" --entrypoint python3 pgbackup "*)
            printf '{"backup_id":"%s","sha256":"%s","size_bytes":%s,"local_state":"LOCAL_VALID"}\n' \
                "$id" "$good_hash" "$good_size"
            ;;
        *" --entrypoint cat pgbackup "*)
            if [[ "${CORRUPT_EXPORT:-0}" == 1 ]]; then
                printf 'corrupt'
            else
                cat "$data"
            fi
            ;;
        *) echo "unexpected compose call" >&2; return 22 ;;
    esac
}

dest="$fixture/operator.dump"
create_backup "$dest"
test -f "$dest"
test "$(sha256sum "$dest" | cut -d' ' -f1)" = "$good_hash"
test "$(stat -c %a "$dest")" = 600
grep -q -- '--origin manual' "$calls"

before="$(wc -l <"$calls")"
if (create_backup "$dest") >/dev/null 2>&1; then
    echo 'existing backup was overwritten' >&2
    exit 1
fi
test "$(wc -l <"$calls")" = "$before"
test "$(sha256sum "$dest" | cut -d' ' -f1)" = "$good_hash"

export CORRUPT_EXPORT=1
bad="$fixture/corrupt.dump"
if (create_backup "$bad") >/dev/null 2>&1; then
    echo 'corrupt transfer was published' >&2
    exit 1
fi
test ! -e "$bad"
test -z "$(find "$fixture" -name '*.part.*' -print -quit)"
test "$(sha256sum "$data" | cut -d' ' -f1)" = "$good_hash"

echo 'managed operator backup export collision/corruption tests PASS'
