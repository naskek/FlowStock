# Purpose

This note documents the first WPF migration step for `CloseDocument`.

Scope:

- feature-flagged WPF routing to the canonical server close path;
- what remains client-side in WPF before the server call;
- what is now authoritative on the server;
- temporary compatibility behavior that still exists before the legacy WPF close path can be removed.

# Current step

WPF close now has two production paths:

1. legacy in-process close
   - `DocumentService.TryCloseDoc(...)`
2. feature-flagged server close
   - WPF -> `WpfCloseDocumentService` -> `POST /api/docs/{docUid}/close`

The legacy path remains intact and is still the default.

# Files involved

WPF entry points:

- `apps/windows/FlowStock.App/MainWindow.xaml.cs`
- `apps/windows/FlowStock.App/OperationDetailsWindow.xaml.cs`

New WPF services:

- `apps/windows/FlowStock.App/Services/CloseDocumentApiClient.cs`
- `apps/windows/FlowStock.App/Services/WpfCloseDocumentService.cs`

Configuration model:

- `apps/windows/FlowStock.App/Services/SettingsService.cs`

# How the feature flag works

Default:

- `UseServerCloseDocument = false`

Config sources:

1. environment variables
2. `%APPDATA%\\FlowStock\\settings.json`

Supported environment variables:

- `FLOWSTOCK_USE_SERVER_CLOSE_DOCUMENT`
- `FLOWSTOCK_SERVER_BASE_URL`
- `FLOWSTOCK_SERVER_DEVICE_ID`
- `FLOWSTOCK_SERVER_CLOSE_TIMEOUT_SECONDS`
- `FLOWSTOCK_SERVER_ALLOW_INVALID_TLS`

Supported `settings.json` block:

```json
{
  "server": {
    "use_server_close_document": true,
    "base_url": "https://127.0.0.1:7154",
    "device_id": "WPF-PC-01",
    "close_timeout_seconds": 15,
    "allow_invalid_tls": false
  }
}
```

Current defaults when the `server` block is omitted:

- `use_server_close_document = false`
- `base_url = https://127.0.0.1:7154`
- `device_id = WPF-<MachineName>`
- `close_timeout_seconds = 15`
- `allow_invalid_tls = false`

Local development note:

- if `FlowStock.Server` uses an untrusted development certificate, either trust the certificate or set `allow_invalid_tls = true` only for local development.

# Developer toggle for server close mode

WPF now exposes a minimal developer-facing toggle inside the existing DB/service window:

- open `Сервис -> Подключение к БД...`
- use the `CloseDocument server mode` block in the same window

The block shows:

- `Close mode: Legacy` or `Close mode: Server API`
- `Config loaded: yes/no`
- `Active close path: Legacy / Server API`

Available actions:

- `Switch to Server API` / `Switch to Legacy`
- edit `Base URL`
- edit `Device ID`
- edit `Timeout (sec)`
- toggle `Allow invalid TLS (dev-only / unsafe)`
- `Check server`
- `Save`

Saved file:

- `%APPDATA%\\FlowStock\\settings.json`

Saved fields:

```json
{
  "server": {
    "use_server_close_document": true,
    "base_url": "https://127.0.0.1:7154",
    "device_id": "WPF-PC-01",
    "close_timeout_seconds": 15,
    "allow_invalid_tls": false
  }
}
```

`Save` writes or updates that block through `SettingsService` and shows:

- `Settings saved to %APPDATA%\FlowStock\settings.json`

`Check server` behavior:

- uses the values currently entered in the WPF form, not only the last saved file;
- sends real HTTP `GET {base_url}/api/ping`;
- respects `Timeout (sec)`;
- respects `allow_invalid_tls`;
- reports one of:
  - reachable
  - not reachable
  - TLS/certificate error
  - timeout
  - bad URL

Important note about overrides:

- runtime environment variables still have priority over `settings.json`;
- when any `FLOWSTOCK_SERVER_*` or `FLOWSTOCK_USE_SERVER_CLOSE_DOCUMENT` override is set, the window shows an explicit warning that the active runtime path may differ from the saved settings.

# WPF flow in this step

## MainWindow

Current wrapper responsibilities kept client-side:

- current document selection;
- draft/closed guard;
- recount guard.

If the feature flag is enabled:

- WPF calls `WpfCloseDocumentService.CloseAsync(doc)`;
- the service ensures a temporary `api_docs` mapping exists for the local document;
- WPF sends `POST /api/docs/{docUid}/close`;
- success/no-op outcomes refresh the main list and stock/order views from the local read model.

If the feature flag is disabled:

- the previous `DocumentService.TryCloseDoc(...)` path is used unchanged.

## OperationDetailsWindow

Current pre-close behavior intentionally remains client-side:

- `TrySaveHeader()`
- dirty-header save
- recount guard
- line presence check
- partner requirement check
- outbound preview check (`TryValidateOutboundStock()`)
- current context/document selection

After that preparation:

- feature-flag off => legacy in-process close
- feature-flag on => canonical server close through `WpfCloseDocumentService`

This preserves WPF-specific UX/orchestration while moving close business semantics to the server path when the feature is enabled.

# Temporary compatibility bridge

Current WPF create/edit flow still writes directly to PostgreSQL and does not guarantee `doc_uid` / `api_docs` metadata for every local document.

To make `POST /api/docs/{docUid}/close` usable without migrating the whole WPF document lifecycle yet, the new WPF close service temporarily bootstraps `api_docs` metadata directly against PostgreSQL before the HTTP close call:

- reuse existing `api_docs.doc_uid` if one already exists for `doc_id`;
- otherwise create deterministic compatibility uid `wpf-doc-{docId}`;
- synchronize minimal metadata:
  - `doc_id`
  - `status`
  - `doc_type`
  - `doc_ref`
  - `partner_id`
  - `device_id`

Important limitation:

- this is a temporary compatibility bridge only;
- WPF still does not create documents through `/api/docs`;
- removing this bridge requires earlier WPF lifecycle migration or another compatibility design.

# What is authoritative now

When `UseServerCloseDocument` is enabled and the WPF request reaches the server, authoritative close semantics are now server-side:

- `docs.status`
- `docs.closed_at`
- `ledger`
- canonical success / validation-failure / replay / already-closed semantics

WPF must not reinterpret those outcomes as separate business logic.

Current WPF handling:

- `CLOSED` => success, refresh UI
- `ALREADY_CLOSED` => success/no-op, refresh UI, show info
- `idempotent_replay = true` => success replay, refresh UI, show info
- `VALIDATION_FAILED` => show server validation error, keep document open
- HTTP/network/timeout/configuration failure => show client error, keep document open

# What remains client-side in WPF

Still client-side on this step:

- `TrySaveHeader()`
- dirty-state save orchestration
- recount gating before the server call
- partner requirement prompt/check in details window
- outbound preview check in details window
- screen-specific refresh/close behavior

# Current behavior differences versus legacy close

Intentional differences in the feature-flagged server path:

- WPF now creates/reuses temporary `api_docs` metadata before close;
- no automatic fallback to legacy close happens when the server call fails;
- already-closed and replay responses follow canonical server no-op semantics instead of legacy in-process exception behavior;
- the final close decision is server-authoritative once the HTTP request is sent.

Legacy path is still available by turning the feature flag off.

# Manual test checklist

1. MainWindow legacy path:
   - keep `UseServerCloseDocument = false`
   - close a valid draft from the main list
   - verify document becomes `CLOSED`

2. MainWindow server path:
   - enable `UseServerCloseDocument = true`
   - close the same kind of valid draft from the main list
   - verify document becomes `CLOSED`
   - verify docs/stock refresh in the main window

3. OperationDetailsWindow legacy path:
   - keep `UseServerCloseDocument = false`
   - edit a draft header if needed
   - close from details window
   - verify final state matches the legacy path

4. OperationDetailsWindow server path:
   - enable `UseServerCloseDocument = true`
   - edit a draft header so `TrySaveHeader()` runs
   - close from details window
   - verify header changes persist before close
   - verify final state is `CLOSED`

5. Validation error from server:
   - enable `UseServerCloseDocument = true`
   - attempt to close a server-invalid draft such as write-off without reason
   - verify WPF shows server validation text
   - verify document remains `DRAFT`

6. Already closed result:
   - enable `UseServerCloseDocument = true`
   - close a document once
   - trigger close again from WPF after reloading the list/window state
   - verify WPF shows informational no-op outcome
   - verify no second posting occurs

7. Server unavailable:
   - enable `UseServerCloseDocument = true`
   - stop `FlowStock.Server` or point `base_url` to an unavailable endpoint
   - attempt close
   - verify WPF shows transport/server error
   - verify document remains `DRAFT`

8. Timeout / network error:
   - enable `UseServerCloseDocument = true`
   - use an unreachable host or force a timeout
   - verify WPF shows timeout/network error
   - verify no silent fallback to legacy close happens

9. UI refresh after success:
   - MainWindow:
     - verify docs list refreshes
     - verify stock refreshes
     - for outbound/production receipt verify orders refresh
   - OperationDetailsWindow:
     - verify the window closes after successful server close
     - verify the owner window reflects the closed state after returning

10. Same final outcome between legacy and server path:
    - run the same valid draft once with feature flag off and once with it on
    - compare:
      - `docs.status`
      - `docs.closed_at`
      - `ledger`
      - stock/order-facing refresh

# Remaining gaps before removing legacy WPF close

- WPF create/edit lifecycle still writes directly to PostgreSQL instead of `/api/docs` and `/lines`;
- temporary local `api_docs` bootstrap still exists;
- MainWindow and OperationDetailsWindow remain asymmetric in pre-close checks;
- no executable WPF adapter/UI automation harness exists yet;
- legacy in-process close must remain available as rollback until the flagged server path is validated manually in real workflows.
