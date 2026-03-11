# Purpose

This document describes the current WPF migration state for `AddDocLine` after enabling server-centric API usage for:

- manual line add (`+ Товар`)
- batch line creation from order-driven flows

Canonical server path remains:

- `POST /api/docs/{docUid}/lines`

Server semantics remain unchanged:

- append-only
- idempotency by `event_id`
- no ledger writes
- no `docs.status` transition

# Scope of migrated WPF flows

Migrated to canonical server add-line under feature flags:

- manual WPF line add from `OperationDetailsWindow`
- outbound order fill / refill flow previously backed by `ApplyOrderToDoc()`
- production receipt fill-from-order flow previously backed by `ApplyOrderToProductionReceipt()`
- outbound order re-apply from `TrySaveHeaderAsync()` when order-bound lines must be rebuilt

Still legacy-local in this step:

- line update
- line delete
- HU split / reassignment
- JSONL import
- batch delete / replace part of order-fill flows

Important boundary:

- batch line creation itself now uses API calls per line
- destructive pre-steps required by current WPF UX, such as `DeleteDocLines()` and order/header binding writes, remain local for now because delete/update APIs are not migrated in this step

# Files involved

WPF entry points:

- `apps/windows/FlowStock.App/OperationDetailsWindow.xaml.cs`
  - `DocAddLine_Click()`
  - `TryApplyOrderSelectionAsync()`
  - `TryApplyOrderSelectionViaServerAsync()`
  - `TryApplyReceiptOrderSelectionAsync()`
  - `FillProductionReceiptFromOrderAsync()`
  - `TrySaveHeaderAsync()`

WPF API orchestration:

- `apps/windows/FlowStock.App/Services/WpfAddDocLineService.cs`
  - `AddLineAsync()` for manual add
  - shared temporary `api_docs` / `doc_uid` mapping bridge
- `apps/windows/FlowStock.App/Services/WpfBatchAddDocLineService.cs`
  - `AddLinesBatchAsync()` for batch flows
  - one HTTP `POST /api/docs/{docUid}/lines` per batch line
  - shared temporary `api_docs` / `doc_uid` mapping bridge

WPF HTTP client:

- `apps/windows/FlowStock.App/Services/AddDocLineApiClient.cs`

WPF settings:

- `apps/windows/FlowStock.App/Services/SettingsService.cs`
  - `server.use_server_add_doc_line`

WPF settings UI:

- `apps/windows/FlowStock.App/DbConnectionWindow.xaml`
- `apps/windows/FlowStock.App/DbConnectionWindow.xaml.cs`

Legacy local services still used in hybrid mode:

- `apps/windows/FlowStock.Core/Services/DocumentService.cs`
  - `ApplyOrderToDoc()`
  - `ApplyOrderToProductionReceipt()`
  - `UpdateDocOrderBinding()`
- `apps/windows/FlowStock.Data/PostgresDataStore.cs`
  - `DeleteDocLines()`

# Feature flags

Saved in `%APPDATA%\\FlowStock\\settings.json`:

```json
{
  "server": {
    "use_server_add_doc_line": true,
    "base_url": "https://127.0.0.1:7154",
    "device_id": "WPF-MACHINE",
    "close_timeout_seconds": 15,
    "allow_invalid_tls": false
  }
}
```

Environment overrides:

- `FLOWSTOCK_USE_SERVER_ADD_DOC_LINE=true`

UI path:

- `Сервис -> Подключение к БД`
- `Use Server API for manual '+ Товар'`

Batch flows use the same flag as manual add-line. There is no separate batch toggle.

# WPF behavior in batch server mode

## Batch line flows (ApplyOrderToDoc, ApplyOrderToProductionReceipt)

## Outbound order fill

When batch server mode is enabled and WPF needs to rebuild outbound lines from an order:

1. WPF validates current location/HU UI state.
2. WPF builds intended line contexts from `GetOrderShipmentRemaining(orderId)`.
3. WPF still performs current local pre-steps:
   - update header/order binding
   - clear existing draft lines through local delete
4. WPF then sends one `POST /api/docs/{docUid}/lines` request per line via `WpfBatchAddDocLineService.AddLinesBatchAsync()`.
5. WPF reloads the document and line grid after the batch attempt.

## Production receipt fill from order

When batch server mode is enabled for production receipt fill:

1. WPF validates receipt location/HU UI state.
2. WPF builds intended line contexts from `GetOrderReceiptRemaining(orderId)`.
3. WPF still performs current local pre-steps:
   - update order binding
   - optionally clear existing draft lines when user chose replace
4. WPF sends one canonical add-line request per line.
5. WPF reloads the document and line grid after the batch attempt.

# Client-side vs server-side responsibility

Client-side in WPF:

- item/order selection UX
- quantity and location/HU UI validation before call
- deciding whether a batch should rebuild lines
- local pre-steps for not-yet-migrated destructive operations
- showing timeout / validation / partial-failure messages

Authoritative on server:

- append-only add-line write
- line validation
- replay / idempotency
- `same event_id + same payload -> IDEMPOTENT_REPLAY`
- `same event_id + different payload -> EVENT_ID_CONFLICT`

# Temporary hybrid boundary

This step is intentionally hybrid.

Why:

- WPF batch flows historically combine several actions in one UX gesture:
  - order binding update
  - line replacement
  - line creation
- only canonical line creation is migrated in this step
- update/delete APIs are still not migrated

Current consequence:

- a batch rebuild is not yet atomic end-to-end
- local delete may happen before all server add-line requests complete
- after timeout or mid-batch failure, WPF must reload the document and the user must inspect the resulting line set before retrying

# Manual checklist

## Legacy batch path

1. Disable `use_server_add_doc_line`.
2. Open outbound or production receipt draft.
3. Run `Заполнить из заказа`.
4. Verify old local behavior still works.

## Server batch path: outbound

1. Enable `use_server_add_doc_line`.
2. Open outbound draft.
3. Select source location and optional HU.
4. Choose order.
5. Verify lines are rebuilt and reloaded from DB.
6. Verify document stays `DRAFT`.
7. Verify ledger is still untouched until close.

## Server batch path: production receipt

1. Enable `use_server_add_doc_line`.
2. Open production receipt draft.
3. Select receipt location.
4. Run `Заполнить из заказа`.
5. Verify lines are created through server path and reloaded from DB.
6. Verify document stays `DRAFT`.

## Large order

1. Enable `use_server_add_doc_line`.
2. Open a draft tied to an order with 20+ remaining lines.
3. Run `Заполнить из заказа`.
4. Verify all expected lines appear after refresh.
5. Verify the UI remains responsive while requests are processed.

## Replace existing lines

1. In production receipt server batch mode, create some lines first.
2. Run `Заполнить из заказа` again and choose replace.
3. Verify WPF clears old lines locally, then recreates target lines through API.
4. Verify final visible state matches refreshed DB state.

## Same semantic line twice

1. Use server batch mode on a scenario that produces the same semantic line twice through two separate user actions.
2. Verify final persisted state is append-only.
3. Do not expect local merge semantics on server-created rows.

## Validation fail

1. Trigger batch add with missing required location/HU context.
2. Verify WPF shows validation failure.
3. Verify document is reloaded after the attempt.

## Timeout / server unavailable

1. Enable `use_server_add_doc_line`.
2. Stop `FlowStock.Server` or force timeout.
3. Run batch fill.
4. Verify WPF shows transport/timeout error.
5. Verify WPF reloads document and lines after the attempt.
6. Verify the operator is expected to inspect the document before retrying.

## Idempotency after partial success

1. Enable `use_server_add_doc_line`.
2. Start a batch fill and interrupt the server after some lines are accepted.
3. Restart the server and repeat the batch action.
4. Refresh the document after each attempt.
5. Verify the final visible state does not contain duplicates beyond the expected append-only result of accepted events.

## End-to-end with canonical lifecycle

1. Enable:
   - `use_server_create_doc_draft`
   - `use_server_add_doc_line`
   - `use_server_close_document`
2. Create draft in WPF.
3. Fill lines from order through batch mode.
4. Optionally add one manual line.
5. Close document through server close.
6. Verify lifecycle works without switching back to legacy create/add/close for those steps.

# Remaining gaps before removing legacy WPF AddLine path

- line delete is still local
- line update is still local
- batch rebuilds are not atomic because destructive pre-steps remain local
- JSONL import stays outside canonical add-line lifecycle
- HU reassignment/split flows are still local
- timeout recovery is safe but conservative: user must inspect refreshed lines before retrying a failed batch
