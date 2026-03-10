# Purpose

This note documents the minimal HTTP integration fixture introduced for the `CloseDocument` migration step.

Scope:

- how the HTTP test host is built;
- which production code is exercised by the fixture;
- which end-to-end checks are now automated;
- which gaps remain outside full automation on this step.

# Why this fixture exists

The repository already had executable application-level tests around:

- `FlowStock.Core.Services.DocumentService.TryCloseDoc()`

The next pragmatic step was to add real HTTP coverage for the canonical remote wrapper:

- `POST /api/docs/{docUid}/close`

The current step extends that fixture to the TSD-style server lifecycle:

- `POST /api/docs`
- `POST /api/docs/{docUid}/lines`
- `POST /api/docs/{docUid}/close`

The goal is still not to refactor the whole server startup or migrate all wrappers. It is to prove the real HTTP lifecycle around canonical close with authoritative state checks.

# Production code shape used by the fixture

The close wrapper logic is now extracted into a reusable production endpoint mapper:

- `apps/windows/FlowStock.Server/DocumentDraftEndpoints.cs`
- `apps/windows/FlowStock.Server/CloseDocumentEndpoint.cs`
- `apps/windows/FlowStock.Server/OpsEndpoint.cs`
- `apps/windows/FlowStock.Server/CanonicalCloseBehavior.cs`

`Program.cs` still uses those same production endpoint mappers, so the HTTP fixture does not duplicate wrapper logic.

This was chosen instead of a larger server bootstrap refactor.

# Test host composition

The HTTP fixture lives in:

- `apps/windows/FlowStock.Server.Tests/CloseDocument/Infrastructure/CloseDocumentHttpHost.cs`

Host characteristics:

- real ASP.NET Core host on Kestrel;
- real `HttpClient` calls against a localhost ephemeral port;
- `Production` environment to avoid `appsettings.Development.json` Kestrel endpoints from binding to fixed ports;
- only the draft/close-related endpoints under test are mapped on this host:
  - `POST /api/docs`
  - `POST /api/docs/{docUid}/lines`
  - `POST /api/docs/{docUid}/close`
  - `POST /api/ops`

Dependency replacement:

- `IDataStore` is backed by the existing stateful `CloseDocumentHarness`;
- `IApiDocStore` is backed by `InMemoryApiDocStore`;
- `DocumentService` is the real production service.

This keeps the business path realistic while avoiding Postgres and unrelated server startup dependencies.

# What is now automated end-to-end

The fixture now executes real HTTP coverage for:

1. successful `POST /api/docs/{docUid}/close`;
2. repeated close replay with the same `event_id` without duplicate ledger writes;
3. repeated close with a new `event_id` on an already-closed document with canonical success/no-op semantics;
4. `api_docs.status` update to `CLOSED`;
5. `api_events` recording for `DOC_CLOSE`;
6. metadata reconciliation when `api_docs.status` is stale but authoritative `docs.status` is already `CLOSED`;
7. TSD-style `POST /api/docs` -> `/lines` -> `/close` ending in `CLOSED`;
8. TSD-style `POST /api/docs` -> `/lines` without `/close` leaving the draft unposted;
9. TSD full-flow repeated close replay without duplicate ledger writes;
10. TSD full-flow new `event_id` on an already-closed document returning canonical success/no-op;
11. TSD full-flow write-off without `reason_code` failing without close-side posting or `DOC_CLOSE` metadata;
12. successful `POST /api/ops` request producing canonical closed authoritative state;
13. `/api/ops` replay with the same `event_id` without duplicate ledger writes;
14. `/api/ops` new `event_id` against an already-closed document returning canonical success/no-op.

Authoritative state checks performed by the tests:

- `docs.status`;
- `docs.closed_at`;
- `ledger`;
- `api_docs.status`;
- `api_events`.

For the TSD-style full flow, the fixture now proves that the final `POST /api/docs/{docUid}/close` response matches the canonical `CloseDocResponse` contract after the real `create -> lines` lifecycle, not only after directly seeded drafts.

# Current already-closed semantics

Current wrapper behavior is now canonical for successful close/no-op outcomes:

- same `event_id` replay for the same `docUid` returns success with:
  - `result = CLOSED`
  - `idempotent_replay = true`
  - no duplicate ledger rows
- a new `event_id` against an already-closed document returns success/no-op with:
  - `result = ALREADY_CLOSED`
  - `already_closed = true`
  - no duplicate ledger rows
  - `api_docs.status` reconciled to `CLOSED`
  - a new processed `DOC_CLOSE` event recorded in `api_events`

This means the target canonical `ALREADY_CLOSED => success/no-op` contract is now implemented in the production wrapper for the HTTP close path.

# `/api/ops` relation to canonical close

Current state after this step:

- the TSD-style flow now exercises the real document draft lifecycle through `POST /api/docs` and `/lines`, then closes through the same canonical close endpoint used by the standalone close tests;
- `/api/ops` still acts as a compatibility facade that creates the draft and appends the line inline;
- the final close phase is now executed through the same canonical close helper used by `POST /api/docs/{docUid}/close`;
- `/api/ops` keeps its own compatibility event type (`OP`) in `api_events`;
- `/api/ops` does not use `api_docs` or `doc_uid`.

Still-existing differences versus the canonical draft + close path:

- `/api/ops` does not reuse `/api/docs` and `/api/docs/{docUid}/close` as HTTP endpoints;
- `/api/ops` has no `api_docs` synchronization metadata;
- `/api/ops` replay metadata is keyed only by `OP` events, not by `doc_uid`;
- same-`event_id` `/api/ops` replay is accepted by processed event id and is not yet validated against full payload equivalence.

# What remains outside full automation

Still pending after this step:

- WPF wrapper compatibility automation.

Reason:

- WPF-specific save/recount/preview behavior still lives outside the current HTTP fixture;
- TSD UI/client-side orchestration is still outside automation even though the server-side TSD lifecycle is now executable end-to-end.

# Practical outcome of this step

The migration test stack now has two executable layers:

1. application-level tests around `DocumentService.TryCloseDoc()`;
2. HTTP integration tests for the TSD-style draft lifecycle, the canonical close wrapper, and the `/api/ops` compatibility facade.

This is enough to validate the remote close path, including canonical already-closed no-op behavior, full TSD `create -> lines -> close` convergence, and `/api/ops` convergence for the close phase, without broadening the production refactor into WPF migration.
