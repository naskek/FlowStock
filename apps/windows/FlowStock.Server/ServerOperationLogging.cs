namespace FlowStock.Server;

internal static class ServerOperationLogging
{
    public static void TryLogOrderPartialOutboundPermissionOperation(
        ILogger logger,
        LogLevel level,
        string phase,
        string result,
        long orderId,
        string? orderRef = null,
        bool? oldValue = null,
        bool? requestedValue = null,
        bool? resultingValue = null,
        bool? changed = null,
        string? errorCode = null,
        string? deviceId = null)
    {
        try
        {
            logger.Log(
                level,
                "order_operation operation_code={OperationCode} phase={Phase} result={Result} order_id={OrderId} order_ref={OrderRef} old_value={OldValue} requested_value={RequestedValue} resulting_value={ResultingValue} changed={Changed} error_code={ErrorCode} device_id={DeviceId} actor_id={ActorId} server_timestamp={ServerTimestamp}",
                "ORDER_PARTIAL_OUTBOUND_PERMISSION_CHANGE",
                phase,
                result,
                orderId,
                orderRef,
                oldValue,
                requestedValue,
                resultingValue,
                changed,
                errorCode,
                deviceId,
                null,
                DateTimeOffset.UtcNow);
        }
        catch
        {
            // Diagnostic logging must never change the permission command result.
        }
    }

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
        long? replacesLineId = null,
        int? ledgerRowsWritten = null,
        string? eventId = null,
        string? deviceId = null,
        bool? apiEventWritten = null,
        bool? appended = null,
        bool? idempotentReplay = null,
        bool? alreadyClosed = null,
        long? elapsedMs = null,
        long? validateBuildCheckMs = null,
        long? ledgerTransactionMs = null,
        long? collectAffectedOrdersMs = null,
        long? refreshStatusMs = null,
        long? refreshReceiptPlansMs = null,
        long? orderId = null,
        string? orderStatusAfter = null,
        bool? allowPartialOutboundAfter = null,
        bool? partialOutboundPermissionAutoReset = null,
        IEnumerable<string>? errors = null)
    {
        var errorText = JoinErrors(errors);
        logger.Log(
            level,
            "doc_lifecycle operation={Operation} path={Path} result={Result} doc_uid={DocUid} doc_id={DocId} doc_ref={DocRef} doc_type={DocType} doc_status_before={DocStatusBefore} doc_status_after={DocStatusAfter} line_count={LineCount} line_id={LineId} replaces_line_id={ReplacesLineId} ledger_rows_written={LedgerRowsWritten} event_id={EventId} device_id={DeviceId} api_event_written={ApiEventWritten} appended={Appended} idempotent_replay={IdempotentReplay} already_closed={AlreadyClosed} elapsed_ms={ElapsedMs} validate_build_check_ms={ValidateBuildCheckMs} ledger_transaction_ms={LedgerTransactionMs} collect_affected_orders_ms={CollectAffectedOrdersMs} refresh_status_ms={RefreshStatusMs} refresh_receipt_plans_ms={RefreshReceiptPlansMs} order_id={OrderId} order_status_after={OrderStatusAfter} allow_partial_outbound_after={AllowPartialOutboundAfter} partial_outbound_permission_auto_reset={PartialOutboundPermissionAutoReset} errors={Errors}",
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
            replacesLineId,
            ledgerRowsWritten,
            eventId,
            deviceId,
            apiEventWritten,
            appended,
            idempotentReplay,
            alreadyClosed,
            elapsedMs,
            validateBuildCheckMs,
            ledgerTransactionMs,
            collectAffectedOrdersMs,
            refreshStatusMs,
            refreshReceiptPlansMs,
            orderId,
            orderStatusAfter,
            allowPartialOutboundAfter,
            partialOutboundPermissionAutoReset,
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
