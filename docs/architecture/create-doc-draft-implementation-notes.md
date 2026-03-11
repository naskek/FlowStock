# Purpose

This document records the implementation decisions made while closing the first red/pending contract gaps for `CreateDocDraft`.

Scope of this step:

- `POST /api/docs` only
- no `AddDocLine`
- no initial WPF bridge in the original hardening step
- no JSONL import redesign

# Implemented production changes

Files touched in production code:

- `apps/windows/FlowStock.Server/DocumentDraftEndpoints.cs`
- `apps/windows/FlowStock.Server/ApiDocModels.cs`
- `apps/windows/FlowStock.Server/PostgresApiDocStore.cs`

Files touched in test infrastructure:

- `apps/windows/FlowStock.Server.Tests/CloseDocument/Infrastructure/InMemoryApiDocStore.cs`
- `apps/windows/FlowStock.Server.Tests/CloseDocument/Infrastructure/CloseDocumentHarness.cs`

# Exact semantics

## First create

Conditions:

- `event_id` not seen before
- `doc_uid` not mapped yet

Behavior:

- validate request
- create one `docs` row in `DRAFT`
- create one `api_docs` mapping for `doc_uid -> doc_id`
- record one `DOC_CREATE` event in `api_events`
- write no `ledger`

## Replay with same `event_id`

Conditions:

- `event_id` already exists
- recorded event type is `DOC_CREATE`
- recorded `doc_uid` matches request `doc_uid`
- normalized stored request payload matches normalized incoming request payload

Behavior:

- return idempotent success
- do not create another `docs` row
- do not create another `api_docs` row
- do not create another `api_events` row
- do not write `ledger`

## Same `event_id` with conflicting payload

Conditions:

- `event_id` already exists
- recorded event type is `DOC_CREATE`
- recorded `doc_uid` matches request `doc_uid`
- normalized stored request payload differs from normalized incoming request payload

Behavior:

- return `EVENT_ID_CONFLICT`
- do not create another `docs` row
- do not create another `api_docs` row
- do not create another `api_events` row
- do not write `ledger`

Compared fields for conflict detection in this step:

- `doc_uid`
- `event_id`
- `device_id`
- `type`
- `doc_ref`
- `comment`
- `reason_code`
- `partner_id`
- `order_id`
- `order_ref`
- `from_location_id`
- `to_location_id`
- `from_hu`
- `to_hu`
- `draft_only`

Comparison rule in this step:

- strings are trimmed before comparison
- identity-like fields are compared case-insensitively
- `comment` is compared trimmed and case-sensitively
- numeric ids and `draft_only` are compared exactly

## Same `doc_uid` with new `event_id` accepted upsert

Conditions:

- `doc_uid` already mapped
- request does not conflict with existing draft identity
- request is accepted as compatible header enrichment/reconciliation

Behavior:

- keep the same `doc_id`
- keep `docs.status = DRAFT`
- update only compatible draft metadata
- do not create another `api_docs` row
- record a new processed `DOC_CREATE` event for the accepted upsert request
- do not write `ledger`

Rationale:

- this preserves the target contract that `POST /api/docs` is canonical create-or-upsert by `doc_uid`
- it also makes accepted upserts visible in `api_events` for replay/idempotency handling

# Non-goals of this step

- no change to `/api/docs/{docUid}/lines`
- no change to WPF create path
- no change to TSD UI flow
- no change to JSONL import
- no broad refactor of create lifecycle

# Resulting behavior boundaries

- `docs.status` remains authoritative and stays `DRAFT` during create/upsert
- `api_docs` remains one sync mapping per `doc_uid`
- `api_events` now distinguishes:
  - first accepted create
  - replay of the same event
  - accepted upsert with a new event id
  - conflicting replay with the same event id

# Remaining gaps after the original server-hardening step

1. WPF still uses legacy-local create and is not bridged to `POST /api/docs`.
2. JSONL import still sits outside canonical draft lifecycle.
3. Accepted-upsert event type remains `DOC_CREATE`; protocol split to `DOC_UPSERT` is still a future decision.
4. Transactional hardening across `docs`, `api_docs`, and `api_events` is still unchanged in this step.

# WPF bridge follow-up

Follow-up scope:

- WPF interactive draft create only
- feature-flagged wrapper convergence to canonical `POST /api/docs`
- no server production changes

Files touched in the WPF follow-up:

- `apps/windows/FlowStock.App/NewDocWindow.xaml.cs`
- `apps/windows/FlowStock.App/Services/CreateDocDraftApiClient.cs`
- `apps/windows/FlowStock.App/Services/WpfCreateDocDraftService.cs`
- `apps/windows/FlowStock.App/Services/SettingsService.cs`
- `apps/windows/FlowStock.App/AppServices.cs`
- `apps/windows/FlowStock.App/DbConnectionWindow.xaml`
- `apps/windows/FlowStock.App/DbConnectionWindow.xaml.cs`

Resulting WPF behavior:

- WPF can create drafts through canonical `POST /api/docs` under feature flag;
- WPF generates `doc_uid` client-side;
- WPF generates `event_id` client-side;
- WPF reuses the same `doc_uid` / `event_id` for the same unchanged dialog payload, so timeout retry in the same dialog converges on canonical replay semantics instead of creating duplicate drafts;
- WPF accepts server-authored `doc_ref`;
- successful server-created drafts open through the existing DB read path by returned `doc_id`;
- newly created server drafts no longer require the temporary derived `doc_id -> doc_uid` bridge for later server add-line or server close.

Non-goals of the WPF follow-up:

- no removal of legacy local create path;
- no JSONL import migration;
- no server contract changes;
- no WPF automation harness.
