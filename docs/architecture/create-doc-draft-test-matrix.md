# Purpose

This document defines the test matrix for the server-centric `CreateDocDraft` migration in FlowStock.

Scope:

- freeze expected behavior before implementation changes;
- cover canonical `POST /api/docs` behavior and wrapper compatibility;
- identify which scenarios are executable now and which remain pending;
- align test coverage with:
  - `docs/architecture/create-doc-draft-current-state.md`
  - `docs/architecture/create-doc-draft-server-contract.md`

Notes:

- authoritative state is verified against `docs`, `docs.status`, `doc_ref`, `ledger`, and where relevant `api_docs` / `api_events`;
- canonical remote semantics are `create-or-upsert by doc_uid`;
- pending rows are intentional where target contract is ahead of the current implementation or infrastructure.

# Test matrix

| Test ID | Category | Scenario name | Preconditions | Action | Expected result | Authoritative state to verify | Client relevance | Automation level |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `CDD-CAN-001` | Canonical create | First create with new `doc_uid` creates one draft doc | No existing `docs` or `api_docs` mapping for `doc_uid` | `POST /api/docs` with new `doc_uid`, valid `event_id`, valid `type` | One draft is created | one `docs` row, `docs.status = DRAFT`, `docs.closed_at = null` | Server / TSD / future WPF | `integration` |
| `CDD-CAN-002` | Canonical create | First create writes `api_docs` mapping | No existing mapping for `doc_uid` | `POST /api/docs` first-create call | One `api_docs` mapping is created for the new draft | `api_docs.doc_uid -> doc_id`, `api_docs.status = DRAFT` | Server / TSD / future WPF | `integration` |
| `CDD-CAN-003` | Canonical create | Create does not write ledger | Valid first-create request | `POST /api/docs` | Draft is created without stock effects | no new `ledger` rows | Server / TSD / future WPF | `integration` |
| `CDD-CAN-004` | Canonical create | Missing `doc_ref` generates server `doc_ref` | No existing draft for `doc_uid` | `POST /api/docs` without `doc_ref` | Server assigns canonical `doc_ref` | created `docs.doc_ref` matches returned `doc_ref`, `docs.status = DRAFT` | Server / TSD / future WPF | `integration` |
| `CDD-CAN-005` | Canonical create | Colliding requested `doc_ref` is replaced | Existing document already uses requested `doc_ref` | `POST /api/docs` with colliding requested `doc_ref` | Request succeeds with replacement `doc_ref` and `doc_ref_changed = true` | new `docs` row with distinct `doc_ref`, no overwrite of existing doc | Server / TSD / future WPF | `integration` |
| `CDD-VAL-001` | Validation | Missing `doc_uid` fails | None | `POST /api/docs` without `doc_uid` | Request is rejected | no new `docs`, no new `api_docs`, no new `api_events` | Server / TSD / future WPF | `integration` |
| `CDD-VAL-002` | Validation | Missing `event_id` fails | None | `POST /api/docs` without `event_id` | Request is rejected | no new `docs`, no new `api_docs`, no new `api_events` | Server / TSD / future WPF | `integration` |
| `CDD-VAL-003` | Validation | Invalid `type` fails | None | `POST /api/docs` with unsupported `type` | Request is rejected | no new `docs`, no new `api_docs` | Server / TSD / future WPF | `integration` |
| `CDD-VAL-004` | Validation | `draft_only = false` enforces required partner for inbound | Valid inbound create context, no partner in request | `POST /api/docs` with `draft_only = false` and no partner | Request is rejected | no new `docs`, no new `api_docs` | Server / TSD / future WPF | `integration` |
| `CDD-VAL-005` | Validation | Unknown partner fails | Unknown `partner_id` in request | `POST /api/docs` | Request is rejected | no new `docs`, no new `api_docs` | Server / TSD / future WPF | `integration` |
| `CDD-IDEM-001` | Idempotency / replay | Same `event_id` replay does not duplicate draft creation | First create already succeeded for `doc_uid` and `event_id` | Replay same `POST /api/docs` request | Idempotent success/no-op | same `doc_id`, exactly one `docs` row, one `api_docs` row, one processed create event | Server / TSD / future WPF | `integration` |
| `CDD-IDEM-002` | Idempotency / replay | Same `event_id` with conflicting payload returns `EVENT_CONFLICT` | First create already succeeded for `event_id`; replay changes semantic payload | Replay `POST /api/docs` with same `event_id` but conflicting request body | Request is rejected as replay conflict | no second `docs` row, no second `api_docs` row, no second processed create effect | Server / TSD / future WPF | `integration` |
| `CDD-UPS-001` | Upsert semantics | Same `doc_uid` + new `event_id` + richer compatible header performs accepted upsert | Skeletal draft already exists for `doc_uid` | `POST /api/docs` with same `doc_uid`, new `event_id`, richer compatible header | Existing draft is updated, not recreated | same `doc_id`, `docs.status = DRAFT`, updated compatible header fields, no ledger | Server / TSD / future WPF | `integration` |
| `CDD-UPS-002` | Upsert semantics | Same `doc_uid` + conflicting identity returns `DUPLICATE_DOC_UID` | Existing draft already mapped to `doc_uid` | `POST /api/docs` with same `doc_uid` but conflicting `doc_ref` / type / identity field | Request is rejected | same single `docs` row remains authoritative | Server / TSD / future WPF | `integration` |
| `CDD-META-001` | API metadata | First create records `api_docs` and `api_events` | No existing mapping | Successful `POST /api/docs` first create | Metadata is persisted exactly once | `api_docs` row exists, `api_events` contains processed create event | Server / TSD / future WPF | `integration` |
| `CDD-META-002` | API metadata | Accepted upsert reconciles `api_docs` header metadata | Existing skeletal draft with sparse metadata | Successful compatible upsert `POST /api/docs` | `api_docs` header fields are updated for the same mapping | same `doc_id`, updated `api_docs.partner/location/HU`, `docs.shipping_ref` when applicable | Server / TSD / future WPF | `integration` |
| `CDD-META-003` | API metadata | Accepted upsert writes `api_event` | Existing draft already mapped by `doc_uid` | Successful compatible upsert with new `event_id` | New accepted create/upsert event is recorded | `api_events` contains processed event for accepted upsert, no duplicate draft rows | Server / TSD / future WPF | `integration` |
| `CDD-TSD-001` | TSD compatibility | Skeletal create then richer upsert keeps document in `DRAFT` | TSD creates skeletal draft first | `POST /api/docs` skeletal call, then second `POST /api/docs` with richer header for same `doc_uid` | Same draft is enriched and remains draft | same `doc_id`, `docs.status = DRAFT`, no ledger, `api_docs.status = DRAFT` | TSD / Server | `integration` |
| `CDD-WPF-001` | WPF migration compatibility | WPF bridge create uses canonical `POST /api/docs` | WPF create feature flag is enabled | Trigger create from WPF migrated path | WPF create uses canonical remote semantics | generated `doc_uid`, processed create event, `api_docs` mapping, no direct local-only create authority | WPF | `manual` |
| `CDD-WPF-002` | WPF migration compatibility | WPF accepts server-authored `doc_ref` replacement | WPF create feature flag is enabled and requested `doc_ref` collides | Trigger create from WPF migrated path | WPF uses server-returned `doc_ref` as authoritative | returned `doc_ref` persisted in WPF-visible draft state, no duplicate local draft | WPF | `manual` |
| `CDD-WPF-003` | WPF migration compatibility | Legacy local create path remains available under feature flag | WPF create feature flag is disabled | Trigger create from WPF legacy path | WPF still creates draft locally | no canonical `doc_uid` contract required at create time, local create remains usable rollback path | WPF | `manual` |
| `CDD-WPF-004` | WPF migration compatibility | Newly created server draft can continue through server add-line and server close | WPF create/add-line/close feature flags are enabled | Create draft from WPF, then add line, then close | Draft continues canonical lifecycle without derived create bridge | created draft has `api_docs` mapping from create, later server line/close operate on canonical remote identity | WPF | `manual` |
| `CDD-LEG-001` | Legacy boundary | `/api/ops` remains outside normal draft lifecycle | `/api/ops` request uses its own op payload | `POST /api/ops` | Request may create and close a doc, but does not enter canonical `doc_uid` draft lifecycle | created doc closes immediately, no `api_docs` mapping, `OP` event only | OPS / Server | `integration` |
| `CDD-LEG-002` | Legacy boundary | JSONL import remains outside canonical draft lifecycle | Import file contains create-capable op event | Run import through `ImportService` | Import may create local draft, but not through canonical API docs flow | local `docs` row via import path, no `api_docs`, no `api_events`, uses `imported_events` | Import / WPF | `pending` |

# Coverage notes

- Executable now:
  - real HTTP `POST /api/docs` create behavior;
  - authoritative verification of `docs`, `docs.status`, `doc_ref`, and absence of `ledger`;
  - replay by same `event_id`;
  - `EVENT_CONFLICT` for same `event_id` with conflicting payload;
  - accepted upsert by same `doc_uid` with richer compatible header;
  - `api_docs` mapping and create/upsert `api_events`;
  - TSD-compatible skeletal create then richer upsert draft lifecycle;
  - `/api/ops` boundary as a non-canonical draft-lifecycle path.

- Deferred:
  - automated WPF bridge compatibility coverage;
  - JSONL import integration harness in `FlowStock.Server.Tests`.
