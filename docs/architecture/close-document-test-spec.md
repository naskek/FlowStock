# Purpose

This document explains the test-first strategy for the `CloseDocument` migration and the current automation boundary.

Reference inputs:

- `docs/architecture/close-document-current-state.md`
- `docs/architecture/close-document-implementation-notes.md`
- `docs/architecture/close-document-server-contract.md`
- `docs/architecture/close-document-test-matrix.md`
- `docs/architecture/close-document-http-fixture-notes.md`
- `docs/architecture/close-document-wpf-migration.md`

# Strategy

Test design is split into three layers.

1. Executable application-level close tests
   - Target: `FlowStock.Core.Services.DocumentService.TryCloseDoc()`
   - Goal: lock down authoritative close semantics that already exist behind one shared core path
   - Verification focus:
     - `docs.status`
     - `docs.closed_at`
     - `ledger`

2. Executable HTTP close-wrapper tests
   - Target:
     - `POST /api/docs`
     - `POST /api/docs/{docUid}/lines`
     - `POST /api/docs/{docUid}/close`
     - `POST /api/ops`
   - Goal: prove the TSD-style draft lifecycle, the canonical remote close wrapper, and the `/api/ops` compatibility facade through real HTTP calls without introducing a broad production refactor
   - Verification focus:
     - HTTP response payload
     - `docs.status`
     - `docs.closed_at`
     - `ledger`
     - `api_docs.status`
     - `api_events`

3. Deferred wrapper compatibility tests
   - Target wrappers:
     - WPF close initiators
   - Goal: keep the remaining WPF compatibility scenarios visible while this step uses feature-flagged production rollout plus a manual checklist instead of a new WPF automation harness

Pragmatic constraint:

- the repo did not contain an existing reusable HTTP integration host for the close wrapper;
- broad server bootstrap refactoring was intentionally avoided on this step;
- therefore the executable foundation now uses:
  - a stateful `IDataStore` harness around the shared close service;
  - a minimal real HTTP host around the extracted production endpoint mappers for draft lifecycle, canonical close, and `/api/ops`.

# What is executable now

Executable now in `apps/windows/FlowStock.Server.Tests`:

- canonical close success updates `docs.status` to `CLOSED`;
- canonical close sets `docs.closed_at`;
- successful close writes ledger exactly once for a simple inbound case;
- inventory close writes ledger delta against existing balance;
- close with no lines fails without posting;
- write-off without reason fails without posting;
- production receipt without HU fails without posting;
- production receipt exceeding `max_qty_per_hu` fails without posting;
- repeated close does not duplicate ledger;
- KM-disabled behavior remains preserved for close;
- `warnings` remain empty under the current migration contract.
- HTTP `POST /api/docs/{docUid}/close` successfully closes a valid persisted draft;
- HTTP close replay with the same `event_id` does not duplicate ledger;
- HTTP close replay with the same `event_id` returns canonical success with `idempotent_replay = true`;
- HTTP close with a new `event_id` against an already-closed document returns canonical `ALREADY_CLOSED` success/no-op;
- HTTP close updates `api_docs.status` to `CLOSED`;
- HTTP close records a `DOC_CLOSE` entry in `api_events`;
- HTTP close reconciles stale `api_docs.status` to `CLOSED` without reposting ledger.
- TSD-style `POST /api/docs` -> `/api/docs/{docUid}/lines` -> `/api/docs/{docUid}/close` ends in `CLOSED`;
- TSD-style `POST /api/docs` -> `/lines` without `/close` remains `DRAFT`;
- TSD full-flow repeated close with the same `event_id` is idempotent and does not duplicate ledger;
- TSD full-flow close with a new `event_id` against an already-closed document returns canonical `ALREADY_CLOSED` success/no-op;
- TSD full-flow write-off without `reason_code` fails through the real endpoint lifecycle and does not record close-side metadata.
- `/api/ops` valid request closes the document successfully through canonical close-phase semantics;
- `/api/ops` replay with the same `event_id` does not duplicate ledger and returns idempotent success;
- `/api/ops` with a new `event_id` against an already-closed document returns canonical `ALREADY_CLOSED` success/no-op;
- `/api/ops` records processed `OP` events without duplicating posting.
- WPF production code now has a feature-flagged server-close path for `MainWindow` and `OperationDetailsWindow`;
- WPF manual coverage is documented for legacy path, server path, validation failure, already-closed no-op, server unavailable, timeout/network error, and UI refresh behavior.

Execution style:

- these are integration-style tests at the application-service boundary;
- the harness is stateful, so tests verify post-call authoritative state instead of only return values.

# What is deferred

Deferred or pending on this step:

- WPF compatibility automation.

# Why it is deferred

The remaining scenarios still depend on infrastructure that does not yet exist in executable form without production changes:

- WPF compatibility checks require adapter-level automation or manual verification strategy;
- this step intentionally uses feature-flagged production rollout plus a documented manual checklist instead of introducing a dedicated WPF UI-test framework;
- TSD client-UI orchestration is still not automated, even though the server-side TSD HTTP lifecycle is now covered end-to-end.

# Test grouping

The test project is grouped by migration concern:

- `CanonicalCloseIntegrationTests`
- `CloseDocumentHttpIntegrationTests`
- `ValidationTests`
- `IdempotencyTests`
- `ApiMetadataTests`
- `WpfCompatibilityTests`
- `TsdCompatibilityTests`
- `OpsCompatibilityTests`
- `MigrationInvariantTests`

This grouping matches the test matrix and is intended to survive the next implementation step.

# Next step expectations

The next implementation step should:

1. keep the currently executable tests green;
2. keep the new HTTP close-wrapper tests green while evolving the remote contract;
3. preserve `KM disabled` and `warnings empty` as migration invariants unless a separate design change explicitly updates the contract;
4. use the executable TSD lifecycle tests as the server-side baseline before migrating WPF wrappers.

Current WPF status:

- the first migration step is now implemented behind `UseServerCloseDocument`;
- remaining WPF verification is manual and is tracked in `docs/architecture/close-document-wpf-migration.md`.
