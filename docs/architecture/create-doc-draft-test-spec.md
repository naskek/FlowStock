# Purpose

This document explains the test-first strategy for the `CreateDocDraft` migration and the current automation boundary.

Reference inputs:

- `docs/architecture/create-doc-draft-current-state.md`
- `docs/architecture/create-doc-draft-server-contract.md`
- `docs/architecture/create-doc-draft-test-matrix.md`

# Strategy

Test design is split into three layers.

1. Executable HTTP integration tests for canonical draft create/upsert
   - Target:
     - `POST /api/docs`
   - Goal:
     - lock down canonical remote behavior before server-centric migration work;
     - verify authoritative state, not only response payload.

2. Executable compatibility/boundary tests
   - Target:
     - TSD skeletal create + richer upsert flow
     - `/api/ops` as non-canonical draft-creation boundary
   - Goal:
     - preserve working client behavior where it already depends on `POST /api/docs`;
     - keep non-canonical creation paths visible as boundaries, not as accidental canonical semantics.

3. Deferred compatibility tests
   - Target:
     - automated WPF create-bridge coverage
     - JSONL import boundary
   - Goal:
     - keep expected behavior explicit without forcing a WPF automation harness on this step.

Pragmatic constraint:

- the repo already contains a minimal real HTTP host and in-memory `IApiDocStore` for migration testing;
- this step reuses that infrastructure instead of introducing a new test framework;
- no production refactor is performed only to make tests elegant.

# What is executable now

Executable now in `apps/windows/FlowStock.Server.Tests/CreateDocDraft`:

- first create with new `doc_uid` creates one draft document;
- first create writes `api_docs` mapping;
- create writes no ledger;
- missing `doc_ref` generates a server `doc_ref`;
- colliding requested `doc_ref` is replaced and reported through `doc_ref_changed`;
- missing `doc_uid` fails;
- missing `event_id` fails;
- invalid `type` fails;
- `draft_only = false` enforces inbound partner requirement;
- unknown partner fails;
- same `event_id` replay does not duplicate `docs`, `api_docs`, or processed create events;
- same `event_id` with conflicting payload returns canonical `EVENT_ID_CONFLICT`;
- same `doc_uid` + new `event_id` + richer compatible header performs accepted upsert on the same draft;
- same `doc_uid` + conflicting identity returns `DUPLICATE_DOC_UID`;
- first create records `api_docs` and first-create `api_events`;
- accepted upsert with new `event_id` records a processed create/upsert event for the same `doc_uid`;
- accepted upsert reconciles `api_docs` header metadata and derived shipping metadata;
- TSD skeletal create then richer upsert keeps the document in `DRAFT`;
- `/api/ops` remains outside the normal `doc_uid` draft lifecycle and creates no `api_docs` mapping.

Verification focus:

- `docs` row count and identity;
- `docs.status`;
- `docs.closed_at`;
- `doc_ref` behavior;
- absence of `ledger` writes;
- `api_docs` mapping and metadata;
- `api_events` behavior where implemented today.

# What is deferred

Deferred or pending on this step:

- automated WPF create-bridge coverage;
- JSONL import integration coverage inside `FlowStock.Server.Tests`.

# Why it is deferred

These cases are deferred for concrete reasons:

- WPF bridge now exists under a feature flag, but there is still no lightweight adapter-level WPF automation harness in the current test project;
- JSONL import uses a different local ingestion model (`ImportService`, `imported_events`) and has no lightweight harness in the current server test project.

The deferred automated scenarios are still encoded as `Skip` tests so the migration path remains explicit. Manual coverage for the WPF bridge is documented in `docs/architecture/create-doc-draft-wpf-migration.md`.

# How this supports later WPF migration

These tests prepare the WPF migration in a practical way:

- they freeze the authoritative semantics of `POST /api/docs` before WPF is switched to it;
- they make `doc_uid`, `api_docs`, `api_events`, and replay behavior explicit;
- they let the implemented WPF bridge converge on the existing server contract instead of redefining draft creation semantics;
- they keep remaining legacy local-create behavior explicitly outside the canonical contract.

# Test grouping

The test project is grouped by migration concern:

- `CanonicalCreateIntegrationTests`
- `ValidationTests`
- `IdempotencyTests`
- `UpsertSemanticsTests`
- `ApiMetadataTests`
- `TsdCompatibilityTests`
- `WpfCompatibilityTests`
- `LegacyBoundaryTests`

This grouping matches the test matrix and is intended to survive the next implementation step.

# Next step expectations

The next implementation step should:

1. keep the executable `POST /api/docs` tests green while evolving the server-centric create path;
2. preserve the distinction between canonical `POST /api/docs` lifecycle and legacy `/api/ops` / JSONL import paths;
3. use the locked-down `POST /api/docs` behavior as the baseline for the feature-flagged WPF create bridge and for later adapter-level automation.
4. keep WPF bridge manual behavior aligned with the canonical server contract until adapter-level automation is added.
