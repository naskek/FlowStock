# Purpose

This document explains the test-first strategy for the `AddDocLine` migration and the current automation boundary.

Reference inputs:

- `docs/architecture/add-doc-line-current-state.md`
- `docs/architecture/add-doc-line-server-contract.md`
- `docs/architecture/add-doc-line-test-matrix.md`

# Strategy

Test design is split into four layers.

1. Executable HTTP integration tests for canonical line append
   - Target:
     - `POST /api/docs/{docUid}/lines`
   - Goal:
     - lock down canonical append-only line behavior before server hardening;
     - verify authoritative state, not only HTTP success.

2. Executable validation and metadata tests
   - Target:
     - request validation
     - `api_events` write behavior
     - no-ledger and no-status-change invariants
   - Goal:
     - keep the add-line contract narrow and testable;
     - ensure line add stays a draft mutation, not a posting operation.

3. Executable replay/idempotency tests
   - Target:
     - `same event_id + same payload -> IDEMPOTENT_REPLAY`
     - `same event_id + different payload -> EVENT_ID_CONFLICT`
   - Goal:
     - freeze canonical replay semantics before any wider client migration;
     - verify no duplicate `doc_lines` rows and no duplicate `DOC_LINE` events.

4. Compatibility and migration-boundary coverage
   - Target:
     - TSD `create -> add-lines -> still DRAFT`
     - `/api/ops` as a non-canonical path
     - WPF manual add-line bridge under feature flag
     - JSONL import as a deferred boundary
   - Goal:
     - preserve current client flows where they already depend on the line endpoint;
     - keep legacy boundaries visible instead of silently making them canonical.

Pragmatic constraint:

- the repo already contains a minimal real HTTP host and in-memory `IApiDocStore`;
- this step reuses that infrastructure instead of introducing a new test framework;
- WPF adapter automation does not exist yet, so WPF coverage is currently documented/manual rather than executable.

# What is executable now

Executable now in `apps/windows/FlowStock.Server.Tests/CreateDocLine`:

- first add-line creates one `doc_lines` row;
- add-line writes no `ledger` rows;
- add-line keeps `docs.status = DRAFT`;
- add-line writes one `DOC_LINE` event to `api_events`;
- missing `event_id` fails;
- unknown `doc_uid` fails;
- non-draft document rejects line add;
- unknown item fails;
- `qty <= 0` fails;
- same `event_id` plus same payload returns canonical `IDEMPOTENT_REPLAY` and does not duplicate `doc_lines` or `DOC_LINE` events;
- same `event_id` plus different payload returns `EVENT_ID_CONFLICT` without changing authoritative state;
- two different `event_id` values for the same semantic line create two rows;
- TSD flow `create -> add-lines` keeps the server document in `DRAFT`;
- `/api/ops` remains outside canonical add-line lifecycle and produces no `DOC_LINE` events.

Verification focus:

- `doc_lines` row count and identity;
- `docs.status` and `docs.closed_at`;
- absence of `ledger` writes;
- `api_events` for `DOC_LINE`;
- stable replay response shape;
- continued separation of canonical line append from `/api/ops`.

# What is deferred

Deferred or pending on this step:

- JSONL import integration coverage inside `FlowStock.Server.Tests`;
- automated WPF adapter-level tests for the new feature-flagged manual add-line bridge.

# Why it is deferred

These cases are deferred for concrete reasons:

- JSONL import uses a different local ingestion model (`ImportService`, `imported_events`) and has no lightweight harness in the current server test project;
- WPF line-add migration currently has no dedicated test project or UI automation harness, so this step uses build verification plus documented manual checklist instead of mock-heavy test scaffolding.

# How this supports later migration

These tests and docs prepare the next migration steps in a practical way:

- they freeze append-only behavior and no-ledger/no-status-change invariants;
- they freeze canonical replay semantics directly on the real HTTP endpoint;
- they allow WPF to start consuming the canonical line-add contract without redefining it;
- they keep TSD compatibility visible while refusing to treat `/api/ops` or JSONL import as canonical line lifecycle.

# WPF coverage in this step

WPF coverage for this step is manual/documented in:

- `docs/architecture/add-doc-line-wpf-migration.md`

Manual checklist covers:

- legacy manual add-line path;
- server manual add-line path;
- repeated same semantic line under append-only semantics;
- replay behavior;
- validation and unknown-item failures;
- timeout / server unavailable handling;
- visible UI outcome and refresh behavior.

# Test grouping

The test project is grouped by migration concern:

- `CanonicalAddLineIntegrationTests`
- `ValidationTests`
- `IdempotencyTests`
- `AppendOnlyBehaviorTests`
- `MetadataTests`
- `TsdCompatibilityTests`
- `LegacyBoundaryTests`

This grouping matches the test matrix and is intended to survive the next implementation step.

# Next step expectations

The next migration steps should:

1. keep the executable add-line tests green while moving more WPF line operations toward canonical API semantics;
2. preserve append-only semantics and no-ledger/no-status-change invariants;
3. leave `/api/ops` and JSONL import clearly outside canonical `AddDocLine` lifecycle unless explicitly redesigned;
4. add WPF-side automated coverage only if a lightweight adapter or integration harness becomes available without forcing a UI test framework on the repo.
