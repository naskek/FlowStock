namespace FlowStock.Server;

internal static class ServerOperationLogging
{
    public static void LogDocumentLifecycleOperation(
        ILogger logger,
        LogLevel level,
        string operation,
        string path,
        string result,
        string? docUid = null,
        long? docId = null,
        string? docRef = null,
        string? docType = null,
        string? docStatusBefore = null,
        string? docStatusAfter = null,
        int? lineCount = null,
        long? lineId = null,
        int? ledgerRowsWritten = null,
        string? eventId = null,
        string? deviceId = null,
        bool? apiEventWritten = null,
        bool? appended = null,
        bool? idempotentReplay = null,
        bool? alreadyClosed = null,
        long? elapsedMs = null,
        IEnumerable<string>? errors = null)
    {
        var errorText = JoinErrors(errors);
        logger.Log(
            level,
            "doc_lifecycle operation={Operation} path={Path} result={Result} doc_uid={DocUid} doc_id={DocId} doc_ref={DocRef} doc_type={DocType} doc_status_before={DocStatusBefore} doc_status_after={DocStatusAfter} line_count={LineCount} line_id={LineId} ledger_rows_written={LedgerRowsWritten} event_id={EventId} device_id={DeviceId} api_event_written={ApiEventWritten} appended={Appended} idempotent_replay={IdempotentReplay} already_closed={AlreadyClosed} elapsed_ms={ElapsedMs} errors={Errors}",
            operation,
            path,
            result,
            docUid,
            docId,
            docRef,
            docType,
            docStatusBefore,
            docStatusAfter,
            lineCount,
            lineId,
            ledgerRowsWritten,
            eventId,
            deviceId,
            apiEventWritten,
            appended,
            idempotentReplay,
            alreadyClosed,
            elapsedMs,
            errorText);
    }

    private static string? JoinErrors(IEnumerable<string>? errors)
    {
        if (errors == null)
        {
            return null;
        }

        var values = errors
            .Where(error => !string.IsNullOrWhiteSpace(error))
            .Select(error => error.Trim())
            .ToArray();

        return values.Length == 0 ? null : string.Join(" | ", values);
    }
}
