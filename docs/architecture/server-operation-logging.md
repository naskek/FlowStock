# Purpose

This document describes the structured server-side business logging added for canonical document lifecycle operations in FlowStock:

- `CreateDocDraft` via `POST /api/docs`
- `AddDocLine` via `POST /api/docs/{docUid}/lines`
- `CloseDocument` via `POST /api/docs/{docUid}/close`

The goal is to keep the existing HTTP request log while adding compact business-outcome logs that are useful for debugging, replay analysis, and operational support.

# Logging points

Existing HTTP-level request log remains in:

- `apps/windows/FlowStock.Server/Program.cs`

Structured business logs are emitted from:

- `apps/windows/FlowStock.Server/DocumentDraftEndpoints.cs`
- `apps/windows/FlowStock.Server/CloseDocumentEndpoint.cs`

Shared formatting is centralized in:

- `apps/windows/FlowStock.Server/ServerOperationLogging.cs`

Additional close-only diagnostics use:

- `apps/windows/FlowStock.Data/PostgresDataStore.cs`

# Logged fields

Each business log record uses the `doc_lifecycle` message template and may include:

- `operation`
- `path`
- `doc_uid`
- `doc_id`
- `doc_ref`
- `doc_type`
- `result`
- `doc_status_before`
- `doc_status_after`
- `line_count`
- `line_id`
- `ledger_rows_written`
- `event_id`
- `device_id`
- `api_event_written`
- `appended`
- `idempotent_replay`
- `already_closed`
- `elapsed_ms`
- `errors`

Notes:

- `ledger_rows_written` is always `0` for draft create and add-line operations.
- `ledger_rows_written` is populated for close by counting `ledger` rows before and after the accepted close wrapper call.
- `errors` is logged as a compact joined string, not as raw request JSON.
- Raw request bodies are not logged at `Information` level.

# Result mapping

## CreateDocDraft

Wrapper-level results used in logs:

- `CREATED`
- `UPSERTED`
- `IDEMPOTENT_REPLAY`
- `EVENT_ID_CONFLICT`
- `VALIDATION_FAILED`
- `DOC_NOT_FOUND`

## AddDocLine

Wrapper-level results used in logs:

- `CREATED`
- `IDEMPOTENT_REPLAY`
- `EVENT_ID_CONFLICT`
- `VALIDATION_FAILED`
- `DOC_NOT_FOUND`

## CloseDocument

Wrapper-level results used in logs:

- `CLOSED`
- `ALREADY_CLOSED`
- `IDEMPOTENT_REPLAY`
- `VALIDATION_FAILED`
- `EVENT_ID_CONFLICT`
- `DOC_NOT_FOUND`

# Example logs

## CreateDocDraft success

```text
doc_lifecycle operation=CreateDocDraft path=/api/docs result=CREATED doc_uid=tsd-doc-001 doc_id=412 doc_ref=IN-2026-000123 doc_type=INBOUND doc_status_before= doc_status_after=DRAFT line_count=0 line_id= ledger_rows_written=0 event_id=evt-create-001 device_id=TSD-01 api_event_written=True appended= idempotent_replay=False already_closed= elapsed_ms=18 errors=
```

## CreateDocDraft accepted upsert

```text
doc_lifecycle operation=CreateDocDraft path=/api/docs result=UPSERTED doc_uid=tsd-doc-001 doc_id=412 doc_ref=IN-2026-000123 doc_type=INBOUND doc_status_before=DRAFT doc_status_after=DRAFT line_count=0 line_id= ledger_rows_written=0 event_id=evt-create-002 device_id=TSD-01 api_event_written=True appended= idempotent_replay=False already_closed= elapsed_ms=9 errors=
```

## CreateDocDraft replay

```text
doc_lifecycle operation=CreateDocDraft path=/api/docs result=IDEMPOTENT_REPLAY doc_uid=tsd-doc-001 doc_id=412 doc_ref=IN-2026-000123 doc_type=INBOUND doc_status_before=DRAFT doc_status_after=DRAFT line_count=0 line_id= ledger_rows_written=0 event_id=evt-create-001 device_id=TSD-01 api_event_written=False appended= idempotent_replay=True already_closed= elapsed_ms=4 errors=
```

## CreateDocDraft conflict

```text
doc_lifecycle operation=CreateDocDraft path=/api/docs result=EVENT_ID_CONFLICT doc_uid=tsd-doc-001 doc_id= doc_ref= doc_type=INBOUND doc_status_before= doc_status_after= line_count= line_id= ledger_rows_written=0 event_id=evt-create-001 device_id=TSD-01 api_event_written= appended= idempotent_replay= already_closed= elapsed_ms=2 errors=EVENT_ID_CONFLICT
```

## AddDocLine success

```text
doc_lifecycle operation=AddDocLine path=/api/docs/tsd-doc-001/lines result=CREATED doc_uid=tsd-doc-001 doc_id=412 doc_ref=IN-2026-000123 doc_type=INBOUND doc_status_before=DRAFT doc_status_after=DRAFT line_count=1 line_id=955 ledger_rows_written=0 event_id=evt-line-001 device_id=TSD-01 api_event_written=True appended=True idempotent_replay=False already_closed= elapsed_ms=11 errors=
```

## AddDocLine replay

```text
doc_lifecycle operation=AddDocLine path=/api/docs/tsd-doc-001/lines result=IDEMPOTENT_REPLAY doc_uid=tsd-doc-001 doc_id=412 doc_ref=IN-2026-000123 doc_type=INBOUND doc_status_before=DRAFT doc_status_after=DRAFT line_count=1 line_id=955 ledger_rows_written=0 event_id=evt-line-001 device_id=TSD-01 api_event_written=False appended=False idempotent_replay=True already_closed= elapsed_ms=3 errors=
```

## AddDocLine validation failure

```text
doc_lifecycle operation=AddDocLine path=/api/docs/tsd-doc-001/lines result=VALIDATION_FAILED doc_uid=tsd-doc-001 doc_id=412 doc_ref=IN-2026-000123 doc_type=INBOUND doc_status_before=DRAFT doc_status_after= line_count= line_id= ledger_rows_written=0 event_id=evt-line-002 device_id=TSD-01 api_event_written= appended= idempotent_replay= already_closed= elapsed_ms=2 errors=UNKNOWN_ITEM
```

## CloseDocument success

```text
doc_lifecycle operation=CloseDocument path=/api/docs/tsd-doc-001/close result=CLOSED doc_uid=tsd-doc-001 doc_id=412 doc_ref=IN-2026-000123 doc_type=INBOUND doc_status_before=DRAFT doc_status_after=CLOSED line_count=3 line_id= ledger_rows_written=3 event_id=evt-close-001 device_id=TSD-01 api_event_written=True appended= idempotent_replay=False already_closed=False elapsed_ms=24 errors=
```

## CloseDocument already closed

```text
doc_lifecycle operation=CloseDocument path=/api/docs/tsd-doc-001/close result=ALREADY_CLOSED doc_uid=tsd-doc-001 doc_id=412 doc_ref=IN-2026-000123 doc_type=INBOUND doc_status_before=CLOSED doc_status_after=CLOSED line_count=3 line_id= ledger_rows_written=0 event_id=evt-close-002 device_id=TSD-01 api_event_written=True appended= idempotent_replay=False already_closed=True elapsed_ms=5 errors=
```

## CloseDocument replay

```text
doc_lifecycle operation=CloseDocument path=/api/docs/tsd-doc-001/close result=IDEMPOTENT_REPLAY doc_uid=tsd-doc-001 doc_id=412 doc_ref=IN-2026-000123 doc_type=INBOUND doc_status_before=CLOSED doc_status_after=CLOSED line_count=3 line_id= ledger_rows_written=0 event_id=evt-close-001 device_id=TSD-01 api_event_written=False appended= idempotent_replay=True already_closed=False elapsed_ms=3 errors=
```

## CloseDocument validation failure

```text
doc_lifecycle operation=CloseDocument path=/api/docs/tsd-doc-001/close result=VALIDATION_FAILED doc_uid=tsd-doc-001 doc_id=412 doc_ref=IN-2026-000123 doc_type=OUTBOUND doc_status_before=DRAFT doc_status_after=DRAFT line_count=2 line_id= ledger_rows_written=0 event_id=evt-close-003 device_id=TSD-01 api_event_written=False appended= idempotent_replay=False already_closed=False elapsed_ms=8 errors=MISSING_LOCATION
```

# Operational guidance

- Use the existing HTTP middleware log to correlate request path and status code.
- Use `doc_lifecycle` logs to understand business outcome and replay/conflict behavior.
- For duplicate or suspected replay cases, correlate by `event_id`, `doc_uid`, and `device_id`.
- For close diagnostics, compare `doc_status_before`, `doc_status_after`, and `ledger_rows_written`.

# Follow-up improvements

Possible later improvements, not required for this step:

- add trace/correlation id propagation from HTTP middleware into business logs
- emit a compact `warning_count` field for close responses once warnings become active again
- add dedicated logging tests if logging stability becomes operationally important
- export these structured fields to centralized log search dashboards
