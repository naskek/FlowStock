#!/usr/bin/env bash
set -euo pipefail

payload="$(mktemp)"
trap 'rm -f "$payload"' EXIT

cat >"$payload" <<'PAYLOAD'
set -euo pipefail
printf 'before\n'
cat >/dev/null
printf 'after\n'
PAYLOAD

direct_output="$(bash -s <"$payload")"
if test "$direct_output" != "before"; then
    printf 'expected direct bash -s execution to demonstrate stdin drain, got: %s\n' "$direct_output" >&2
    exit 1
fi

file_output="$(
    sh -c '
        set -eu
        tmp="$(mktemp)"
        trap '"'"'rm -f "$tmp"'"'"' EXIT
        cat >"$tmp"
        bash "$tmp"
    ' <"$payload"
)"

expected="$(printf 'before\nafter')"
if test "$file_output" != "$expected"; then
    printf 'script-file transport did not isolate script source from command stdin\nexpected:\n%s\nactual:\n%s\n' "$expected" "$file_output" >&2
    exit 1
fi

printf 'deploy script-file stdin isolation test passed\n'
