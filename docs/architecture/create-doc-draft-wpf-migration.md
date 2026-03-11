# Purpose

This document explains the first WPF migration step for `CreateDocDraft`: interactive WPF draft creation can call the canonical server path `POST /api/docs` under a feature flag, while the legacy local create path remains available.

# Scope of this step

Migrated in this step:

- interactive WPF new-document dialog from `NewDocWindow`
- feature-flag selection between:
  - legacy local `DocumentService.CreateDoc(...)`
  - canonical server `POST /api/docs`
- client-side `doc_uid` generation for WPF-created drafts
- client-side `event_id` generation and safe retry reuse within the same dialog/request
- acceptance of server-authored `doc_ref`

Explicitly not migrated in this step:

- JSONL import
- WPF update/delete line operations
- WPF order-fill / batch line flows
- broad WPF refactor away from direct DB reads
- server production semantics for `POST /api/docs`

# Files involved

WPF entry points:

- `apps/windows/FlowStock.App/MainWindow.xaml.cs`
  - still opens `NewDocWindow`
  - still reloads docs and opens details after successful create
- `apps/windows/FlowStock.App/NewDocWindow.xaml.cs`
  - feature-flagged branch for canonical server create
  - safe retry reuse of `doc_uid` / `event_id`
  - legacy local create retained

WPF services:

- `apps/windows/FlowStock.App/Services/WpfCreateDocDraftService.cs`
  - WPF-side orchestration for canonical create
  - timeout / transport / validation mapping
- `apps/windows/FlowStock.App/Services/CreateDocDraftApiClient.cs`
  - HTTP client for `POST /api/docs`
- `apps/windows/FlowStock.App/Services/SettingsService.cs`
  - `server.use_server_create_doc_draft`
- `apps/windows/FlowStock.App/AppServices.cs`
  - service wiring

WPF settings UI:

- `apps/windows/FlowStock.App/DbConnectionWindow.xaml`
- `apps/windows/FlowStock.App/DbConnectionWindow.xaml.cs`

Shared server contract consumed by WPF:

- `apps/windows/FlowStock.Server/DocumentDraftEndpoints.cs`
  - canonical `POST /api/docs`

# How the new mode is enabled

Saved settings path:

- `%APPDATA%\FlowStock\settings.json`

Relevant server settings block:

```json
{
  "server": {
    "use_server_create_doc_draft": true,
    "use_server_close_document": true,
    "use_server_add_doc_line": true,
    "base_url": "https://127.0.0.1:7154",
    "device_id": "WPF-MACHINE",
    "close_timeout_seconds": 15,
    "allow_invalid_tls": false
  }
}
```

Environment override:

- `FLOWSTOCK_USE_SERVER_CREATE_DOC_DRAFT=true`

Current practical note:

- draft create currently reuses the same server base URL, device id, TLS setting, and timeout setting as close/add-line
- timeout is currently shared through `close_timeout_seconds`

UI path:

- `Сервис -> Подключение к БД -> Server API mode -> Use Server API for new draft creation`

# WPF flow in server create mode

1. WPF keeps the existing dialog/data collection UX.
   - type
   - `doc_ref`
   - comment
   - local `GenerateDocRef()` suggestion remains a convenience only

2. When server create mode is enabled, WPF does not write the draft directly to PostgreSQL.

3. For one unchanged dialog payload, WPF generates and then reuses:
   - one `doc_uid`
   - one `event_id`

4. WPF sends canonical HTTP request:
   - `POST /api/docs`
   - `draft_only = true`
   - type, optional `doc_ref`, optional comment

5. On success, WPF accepts the server-authored draft identity.
   - authoritative `doc_id` comes from server response
   - authoritative `doc_ref` comes from server response
   - if server replaced `doc_ref`, WPF accepts it and opens the created draft

6. `MainWindow` reloads docs through the existing direct DB read path and opens the created draft by returned `doc_id`.

7. Because server create writes `api_docs`, newly created server drafts no longer need the temporary derived `doc_id -> doc_uid` bridge when later using server add-line or server close.

# Safe retry behavior in the dialog

This step explicitly protects the create flow against duplicate draft creation on retry.

Behavior:

- the dialog computes a fingerprint from:
  - doc type
  - trimmed `doc_ref`
  - trimmed comment
- as long as the fingerprint does not change, the dialog reuses the same generated:
  - `doc_uid`
  - `event_id`

Why:

- if the first request timed out after reaching the server, retrying with the same payload and the same `event_id` lets canonical replay semantics converge safely
- if the user changes the create payload, WPF generates a new request identity for the new logical create intent

# What remains client-side in WPF

These behaviors remain client-side in this step:

- dialog field collection
- local `GenerateDocRef()` suggestion for convenience
- safe retry identity reuse inside the dialog
- display of validation/transport messages

These are WPF UX concerns, not authoritative create semantics.

# What is authoritative on the server

Server-authoritative rules remain unchanged:

- `POST /api/docs` remains canonical create-or-upsert by `doc_uid`
- `doc_uid` and `api_docs` mapping are authoritative for remote draft lifecycle
- server decides the final persisted `doc_ref`
- accepted replay by `event_id` remains idempotent
- `EVENT_ID_CONFLICT` remains authoritative for conflicting replay
- create writes no ledger
- create leaves `docs.status = DRAFT`

# Manual test checklist

## Legacy create path

1. Disable `use_server_create_doc_draft`.
2. Open `Новый документ`.
3. Create a draft.
4. Verify the old local path still works.
5. Verify the draft opens as before.

## Server create path

1. Enable `use_server_create_doc_draft`.
2. Open `Новый документ`.
3. Create a draft with valid type and comment.
4. Verify the draft opens after the dialog closes.
5. Verify the document is visible through normal WPF DB reads and remains `DRAFT`.

## Missing `doc_ref` -> server generated `doc_ref`

1. Enable server create mode.
2. Open `Новый документ`.
3. Clear the `Номер` field completely.
4. Create the draft.
5. Verify the opened draft has a non-empty server-assigned `doc_ref`.

## Colliding requested `doc_ref` -> replacement `doc_ref`

1. Enable server create mode.
2. Request a `doc_ref` that already exists.
3. Create the draft.
4. Verify the draft is created successfully.
5. Verify WPF accepts the replacement `doc_ref` returned by the server.

## Validation error

1. Trigger a server-side create validation scenario if available.
2. Verify WPF shows the server validation error.
3. Verify no extra draft is opened.

## Same `event_id` replay

1. Enable server create mode.
2. Start create and force a timeout or transport interruption after the request is sent.
3. Without changing dialog fields, click `Создать` again in the same dialog.
4. Verify WPF reuses the same request identity.
5. Verify only one draft exists/opened for that logical create intent.

## Timeout / server unavailable

1. Enable server create mode.
2. Stop `FlowStock.Server` or make it unreachable, or force a timeout.
3. Attempt draft creation.
4. Verify WPF shows a transport/timeout error.
5. For timeout, verify the message explicitly tells the user to retry in the same dialog so the same request identity is reused.

## Opening newly created draft and continuing lifecycle

1. Enable:
   - `use_server_create_doc_draft`
   - `use_server_add_doc_line`
   - `use_server_close_document`
2. Create a new draft through WPF.
3. Open the created draft.
4. Add a line through server add-line mode.
5. Close the draft through server close mode.
6. Verify the draft no longer relies on a derived `wpf-doc-{docId}` bridge.

## Same visible UX outcome where appropriate

1. Compare legacy and server create modes for a normal successful create.
2. Verify both result in:
   - a new draft
   - the draft opening automatically
   - `DRAFT` status
3. Do not expect identical retry behavior, because server mode intentionally preserves canonical `doc_uid` / `event_id` semantics.

# Remaining gaps before removing legacy WPF CreateDocDraft path

- legacy local draft create still exists under the feature flag and is intentionally kept for rollback
- no automated WPF adapter-level harness exists yet
- JSONL import is still outside canonical create lifecycle
- richer WPF header composition beyond `type` / `doc_ref` / comment is still handled after create, not in the create dialog
- legacy-created drafts may still require temporary add-line/close bridge behavior when they later move into server paths
