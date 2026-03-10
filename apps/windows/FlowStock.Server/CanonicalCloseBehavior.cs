using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.Server;

internal static class CanonicalCloseBehavior
{
    public const string ResultClosed = "CLOSED";
    public const string ResultAlreadyClosed = "ALREADY_CLOSED";
    public const string ResultValidationFailed = "VALIDATION_FAILED";

    public static CloseDocResponse BuildReplayResponse(
        string? docUid,
        string? docRefHint,
        Doc? currentDoc,
        string result,
        bool idempotentReplay,
        bool alreadyClosed)
    {
        return BuildSuccessResponse(
            docUid,
            docRefHint,
            currentDoc,
            result,
            idempotentReplay,
            alreadyClosed,
            warnings: null);
    }

    public static CloseDocResponse Execute(
        long docId,
        string? docUid,
        string? docRefHint,
        IDataStore store,
        DocumentService docs,
        Action onAcceptedClose)
    {
        var currentDoc = store.GetDoc(docId);
        if (currentDoc == null)
        {
            return new CloseDocResponse
            {
                Ok = false,
                Closed = false,
                DocUid = docUid,
                DocRef = docRefHint,
                DocStatus = null,
                Result = "NOT_FOUND",
                Errors = new[] { "DOC_NOT_FOUND" },
                Warnings = Array.Empty<string>(),
                IdempotentReplay = false,
                AlreadyClosed = false
            };
        }

        if (currentDoc.Status == DocStatus.Closed)
        {
            onAcceptedClose();
            return BuildSuccessResponse(
                docUid,
                docRefHint,
                currentDoc,
                ResultAlreadyClosed,
                idempotentReplay: false,
                alreadyClosed: true,
                warnings: null);
        }

        var result = docs.TryCloseDoc(docId, allowNegative: false);
        if (!result.Success)
        {
            currentDoc = store.GetDoc(docId) ?? currentDoc;
            if (currentDoc.Status == DocStatus.Closed)
            {
                onAcceptedClose();
                return BuildSuccessResponse(
                    docUid,
                    docRefHint,
                    currentDoc,
                    ResultAlreadyClosed,
                    idempotentReplay: false,
                    alreadyClosed: true,
                    warnings: null);
            }

            return new CloseDocResponse
            {
                Ok = false,
                Closed = false,
                DocUid = docUid,
                DocRef = currentDoc.DocRef,
                DocStatus = ResolveDocStatus(currentDoc),
                Result = ResultValidationFailed,
                Errors = result.Errors.Count > 0 ? result.Errors.ToArray() : new[] { "CLOSE_FAILED" },
                Warnings = result.Warnings.ToArray(),
                IdempotentReplay = false,
                AlreadyClosed = false
            };
        }

        onAcceptedClose();
        currentDoc = store.GetDoc(docId) ?? currentDoc;

        return BuildSuccessResponse(
            docUid,
            docRefHint,
            currentDoc,
            ResultClosed,
            idempotentReplay: false,
            alreadyClosed: false,
            warnings: result.Warnings);
    }

    private static CloseDocResponse BuildSuccessResponse(
        string? docUid,
        string? docRefHint,
        Doc? currentDoc,
        string result,
        bool idempotentReplay,
        bool alreadyClosed,
        IReadOnlyList<string>? warnings)
    {
        return new CloseDocResponse
        {
            Ok = true,
            Closed = true,
            DocUid = docUid,
            DocRef = currentDoc?.DocRef ?? docRefHint,
            DocStatus = ResolveDocStatus(currentDoc),
            Result = result,
            Errors = Array.Empty<string>(),
            Warnings = warnings?.ToArray() ?? Array.Empty<string>(),
            IdempotentReplay = idempotentReplay,
            AlreadyClosed = alreadyClosed
        };
    }

    private static string? ResolveDocStatus(Doc? currentDoc)
    {
        return currentDoc == null ? null : DocTypeMapper.StatusToString(currentDoc.Status);
    }
}
