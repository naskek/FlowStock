# AGENTS.md (FlowStock)

## Product invariant
- Stock is derived from ledger only.
- Documents affect stock only on Close.
- Closed documents are immutable; corrections are separate docs.

## Source of truth
- Read and follow: ./docs/spec.md and ./docs/spec_orders.md
- If behavior changes, update specs in the same PR.

## Repo map
- apps/windows/* : WPF client (.NET 8)
- apps/windows/FlowStock.Server : Minimal API + Postgres
- apps/android/tsd : offline PWA (IndexedDB + SW)
- deploy/ : Dockerfile + compose

## Workflow rules
- Always propose a short plan first (bullets).
- Prefer minimal diffs; avoid drive-by refactors.
- After code changes, run: build + relevant tests (or explain if none).
- Never run destructive commands (rm -rf, git reset --hard, docker system prune) without explicit ask.

## Commands
- dotnet build
- dotnet test (if tests exist)
- docker compose up -d (if working on server+postgres)
