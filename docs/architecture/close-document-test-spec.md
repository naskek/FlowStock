# Purpose

This document explains the test-first strategy for the `CloseDocument` migration and the current automation boundary.

Reference inputs:

- `docs/architecture/close-document-current-state.md`
- `docs/architecture/close-document-server-contract.md`
- `docs/architecture/close-document-test-matrix.md`

# Strategy

Test design is split into two layers.

1. Executable application-level close tests
   - Target: `FlowStock.Core.Services.DocumentService.TryCloseDoc()`
   - Goal: lock down authoritative close semantics that already exist behind one shared core path
   - Verification focus:
     - `docs.status`
     - `docs.closed_at`
     - `ledger`

2. Deferred wrapper compatibility tests
   - Target wrappers:
     - `POST /api/docs/{docUid}/close`
     - `POST /api/ops`
     - WPF close initiators
     - TSD upload-and-close flow
   - Goal: prepare test slots for the migration without forcing production refactoring on this step

Pragmatic constraint:

- the repo did not contain an existing test project or an HTTP integration test host;
- production code must not be changed on this step;
- therefore the executable foundation uses a stateful `IDataStore` test harness around the shared close service instead of a real HTTP + Postgres stack.

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

Execution style:

- these are integration-style tests at the application-service boundary;
- the harness is stateful, so tests verify post-call authoritative state instead of only return values.

# What is deferred

Deferred or pending on this step:

- HTTP contract tests for `POST /api/docs/{docUid}/close`;
- API idempotency tests based on `event_id`;
- `api_docs` / `api_events` metadata tests;
- `/api/ops` convergence tests;
- TSD upload compatibility tests;
- WPF compatibility automation;
- target-contract test for `already closed => success/no-op`.

# Why it is deferred

The remaining scenarios depend on infrastructure that does not yet exist in executable form without production changes:

- no established server test host in the solution;
- no dedicated test DB harness for `api_docs` / `api_events`;
- current server entrypoint is not yet exposed for low-friction HTTP integration testing;
- WPF compatibility checks require adapter-level automation or manual verification strategy;
- target-contract idempotent `already closed` behavior is not implemented yet, so the test is intentionally marked pending instead of asserting the current error path as canonical.

# Test grouping

The test project is grouped by migration concern:

- `CanonicalCloseIntegrationTests`
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
2. replace pending HTTP and metadata skeletons with real integration tests;
3. add server-host and storage fixtures only as needed to automate `/api/docs/{docUid}/close`, `/api/ops`, and TSD compatibility flows;
4. preserve `KM disabled` and `warnings empty` as migration invariants unless a separate design change explicitly updates the contract.
