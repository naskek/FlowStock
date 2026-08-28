using System.Security.Cryptography;
using System.Text;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models.Marking;

namespace FlowStock.Core.Services.Marking;

public sealed class OrderScopedMarkingImportService
{
    private readonly IOrderScopedMarkingImportStore _store;
    private readonly MarkingFileParser _parser = new();

    public OrderScopedMarkingImportService(IOrderScopedMarkingImportStore store)
    {
        _store = store;
    }

    public OrderScopedMarkingImportPreviewResult Preview(
        long relatedOrderId,
        IReadOnlyList<MarkingImportUploadFile> files)
    {
        if (_store is IMarkingCutoverRuntimeGuard cutoverGuard)
        {
            try
            {
                cutoverGuard.RequireEnforcedMarkingWorkflow("marking_import_preview");
            }
            catch (InvalidOperationException ex)
            {
                return Invalid(MarkingCutoverRuntimeErrors.MaintenanceRequired, ex.Message);
            }
        }

        if (relatedOrderId <= 0)
        {
            return Invalid("ORDER_REQUIRED", "Требуется действующий заказ.");
        }

        if (files == null || files.Count == 0)
        {
            return Invalid("FILES_REQUIRED", "Выберите хотя бы один файл КМ.");
        }

        var relatedRequests = GetOperationalRequests(relatedOrderId);
        if (relatedRequests.Count == 0)
        {
            return Invalid("NO_RELATED_REQUEST_SCOPE", "У заказа нет связанного незавершённого запроса КМ.");
        }

        var filePreviews = new List<OrderScopedMarkingImportFilePreview>();
        var parsedFiles = new List<(MarkingImportUploadFile File, MarkingParsedFile Parsed)>();
        var fileHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file.Content == null || file.Content.Length == 0)
            {
                return Invalid("EMPTY_FILE", $"Файл '{file.FileName}' пуст.");
            }

            var fileHash = ComputeHash(file.Content);
            if (!fileHashes.Add(fileHash))
            {
                return Invalid("DUPLICATE_FILE", "Один и тот же файл добавлен в batch несколько раз.");
            }

            MarkingParsedFile parsed;
            try
            {
                parsed = _parser.Parse(file.Content, fileHash);
            }
            catch (DecoderFallbackException)
            {
                return Invalid("INVALID_ENCODING", $"Файл '{file.FileName}' должен быть UTF-8.");
            }

            filePreviews.Add(new OrderScopedMarkingImportFilePreview(
                file.FileName?.Trim() ?? string.Empty,
                fileHash,
                file.Content.LongLength,
                parsed.TotalRows,
                parsed.ValidRows,
                parsed.InvalidRows,
                parsed.DuplicateRowsInFile,
                parsed.Warnings));
            parsedFiles.Add((file, parsed));
        }

        if (parsedFiles.Any(value => value.Parsed.InvalidRows > 0))
        {
            return Invalid(
                "INVALID_MARKING_ROWS",
                "Импорт содержит невалидные строки; исправьте файл до Confirm.",
                filePreviews);
        }

        if (parsedFiles.Any(value => value.Parsed.DuplicateRowsInFile > 0))
        {
            return Invalid(
                "DUPLICATE_CODES",
                "Импорт содержит повторяющиеся DataMatrix.",
                filePreviews);
        }

        var requestsByGtin = relatedRequests
            .GroupBy(value => value.Gtin, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var codes = new List<(string Code, string Hash, string Gtin, string FileHash, int RowNumber)>();
        var batchCodeHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, parsed) in parsedFiles)
        {
            for (var index = 0; index < parsed.AcceptedCodes.Count; index++)
            {
                var code = parsed.AcceptedCodes[index];
                if (!MarkingFileParser.TryExtractGs1Gtin(code, out var gtin))
                {
                    return Invalid("INVALID_DM", "Не удалось извлечь GTIN из DataMatrix.", filePreviews);
                }

                if (!requestsByGtin.TryGetValue(gtin, out var candidates))
                {
                    return Invalid(
                        "GTIN_MISMATCH",
                        $"GTIN {gtin} не относится к outstanding scope выбранного заказа.",
                        filePreviews);
                }

                var codeHash = ComputeHash(Encoding.UTF8.GetBytes(code));
                if (!batchCodeHashes.Add(codeHash))
                {
                    return Invalid("DUPLICATE_CODES", "DataMatrix повторяется между файлами batch.", filePreviews);
                }

                codes.Add((code, codeHash, gtin, parsed.FileHash, index + 1));
            }
        }

        if (codes.Count == 0)
        {
            return Invalid("NO_VALID_CODES", "В batch нет валидных DataMatrix.", filePreviews);
        }

        var existingHashes = _store.FindExistingRealMarkingCodeHashes(batchCodeHashes.ToArray());
        if (existingHashes.Count > 0)
        {
            return Invalid("DUPLICATE_CODES", "Один или несколько DataMatrix уже импортированы.", filePreviews);
        }

        var allocation = AllocateCodes(relatedRequests, codes);
        if (allocation.ErrorCode != null)
        {
            return Invalid(allocation.ErrorCode, allocation.Message!, filePreviews);
        }

        var requestPreviews = new List<OrderScopedMarkingImportRequestPreview>();
        var importedGtins = codes.Select(value => value.Gtin).ToHashSet(StringComparer.Ordinal);
        foreach (var request in relatedRequests
                     .Where(request => importedGtins.Contains(request.Gtin))
                     .OrderBy(request => request.CreatedAt)
                     .ThenBy(request => request.RequestNumber, StringComparer.Ordinal)
                     .ThenBy(request => request.MarkingOrderId))
        {
            allocation.CountByRequest.TryGetValue(request.MarkingOrderId, out var validInBatch);
            var importedAfter = checked(request.ImportedQuantity + validInBatch);
            if (importedAfter > request.RequestedQuantity)
            {
                return Invalid(
                    "REQUEST_QUANTITY_EXCEEDED",
                    $"Для request {request.RequestNumber} импортировано больше requested_qty.",
                    filePreviews);
            }

            requestPreviews.Add(new OrderScopedMarkingImportRequestPreview(
                request.MarkingOrderId,
                request.RequestNumber,
                request.Gtin,
                request.RequiredQuantity,
                request.ReserveQuantity,
                request.RequestedQuantity,
                request.ImportedQuantity,
                validInBatch,
                importedAfter,
                importedAfter >= request.EffectiveActiveScopedQuantity,
                importedAfter >= request.EffectiveActiveScopedQuantity && importedAfter < request.RequestedQuantity,
                request.ScopeSnapshotHash,
                request.EffectiveActiveScopedQuantity));
        }

        var activeById = relatedRequests.ToDictionary(value => value.MarkingOrderId);
        var requiresRecovery = requestPreviews.Any(value =>
            value.ImportedAfter < activeById[value.MarkingOrderId].EffectiveActiveScopedQuantity);
        var warnings = requestPreviews
            .Where(value => value.ReserveShort)
            .Select(value => $"RESERVE_SHORT:{value.RequestNumber}:{value.ImportedAfter}/{value.RequestedQuantity}")
            .ToArray();
        var snapshotHash = ComputeSnapshotHash(relatedOrderId, filePreviews, requestPreviews, relatedRequests);
        return new OrderScopedMarkingImportPreviewResult(
            true,
            null,
            requiresRecovery
                ? "Недостаточно КМ для required_qty; Confirm разрешён только как recovery/anomaly."
                : "Импорт готов к подтверждению.",
            snapshotHash,
            requiresRecovery,
            filePreviews,
            requestPreviews,
            warnings);
    }

    public OrderScopedMarkingImportConfirmResult Confirm(
        long relatedOrderId,
        Guid batchId,
        string snapshotHash,
        string idempotencyKey,
        bool confirmRecovery,
        IReadOnlyList<MarkingImportUploadFile> files)
    {
        if (_store is IMarkingCutoverRuntimeGuard cutoverGuard)
        {
            cutoverGuard.RequireEnforcedMarkingWorkflow("marking_import_confirm");
        }

        if (batchId == Guid.Empty || string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new InvalidOperationException("MARKING_IMPORT_IDEMPOTENCY_REQUIRED");
        }

        var alreadyConfirmed = _store.FindConfirmedOrderScopedMarkingImport(
            relatedOrderId,
            batchId,
            idempotencyKey.Trim(),
            snapshotHash?.Trim() ?? string.Empty);
        if (alreadyConfirmed != null)
        {
            return alreadyConfirmed;
        }

        var preview = Preview(relatedOrderId, files);
        if (!preview.IsValid)
        {
            throw new InvalidOperationException(preview.ErrorCode ?? "MARKING_IMPORT_INVALID");
        }

        if (!string.Equals(preview.SnapshotHash, snapshotHash?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("MARKING_IMPORT_SNAPSHOT_CHANGED");
        }

        if (preview.RequiresRecoveryConfirmation && !confirmRecovery)
        {
            throw new InvalidOperationException("MARKING_IMPORT_RECOVERY_CONFIRMATION_REQUIRED");
        }

        var parsedCodes = new List<(string Code, string Hash, string Gtin, string FileHash, int RowNumber)>();
        foreach (var file in files)
        {
            var fileHash = ComputeHash(file.Content);
            var parsed = _parser.Parse(file.Content, fileHash);
            for (var index = 0; index < parsed.AcceptedCodes.Count; index++)
            {
                var code = parsed.AcceptedCodes[index];
                MarkingFileParser.TryExtractGs1Gtin(code, out var gtin);
                parsedCodes.Add((code, ComputeHash(Encoding.UTF8.GetBytes(code)), gtin, fileHash, index + 1));
            }
        }

        var relatedRequests = GetOperationalRequests(relatedOrderId);
        var allocation = AllocateCodes(relatedRequests, parsedCodes);
        if (allocation.ErrorCode != null)
        {
            throw new InvalidOperationException(allocation.ErrorCode);
        }
        var codes = parsedCodes.Select(code => new OrderScopedMarkingImportCode(
            allocation.RequestByCodeHash[code.Hash], code.Gtin, code.Code, code.Hash,
            code.FileHash, code.RowNumber)).ToArray();

        return _store.ConfirmOrderScopedMarkingImport(new OrderScopedMarkingImportConfirmCommand(
            relatedOrderId,
            batchId,
            preview.SnapshotHash,
            idempotencyKey.Trim(),
            confirmRecovery,
            files,
            codes,
            preview.Requests,
            DateTime.UtcNow));
    }

    private static AllocationResult AllocateCodes(
        IReadOnlyList<RelatedMarkingRequest> requests,
        IReadOnlyList<(string Code, string Hash, string Gtin, string FileHash, int RowNumber)> codes)
    {
        var requestByCodeHash = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var countByRequest = new Dictionary<Guid, int>();
        foreach (var gtinGroup in codes.GroupBy(value => value.Gtin, StringComparer.Ordinal))
        {
            var candidates = requests
                .Where(value => value.EffectiveActiveScopedQuantity > 0
                                && string.Equals(value.Gtin, gtinGroup.Key, StringComparison.Ordinal))
                .OrderBy(value => value.CreatedAt)
                .ThenBy(value => value.RequestNumber, StringComparer.Ordinal)
                .ThenBy(value => value.MarkingOrderId)
                .ToArray();
            if (candidates.Length == 0)
            {
                return AllocationResult.Failure("GTIN_MISMATCH", $"GTIN {gtinGroup.Key} не относится к active scope заказа.");
            }

            var orderedCodes = new Queue<(string Code, string Hash, string Gtin, string FileHash, int RowNumber)>(
                gtinGroup.OrderBy(value => value.FileHash, StringComparer.Ordinal)
                    .ThenBy(value => value.RowNumber)
                    .ThenBy(value => value.Hash, StringComparer.Ordinal));
            foreach (var request in candidates)
            {
                Assign(request, request.OperationalDeficit, orderedCodes, requestByCodeHash, countByRequest);
            }
            foreach (var request in candidates)
            {
                var alreadyAssigned = countByRequest.GetValueOrDefault(request.MarkingOrderId);
                var remainingCapacity = Math.Max(0, request.RemainingRequestedCapacity - alreadyAssigned);
                Assign(request, remainingCapacity, orderedCodes, requestByCodeHash, countByRequest);
            }

            if (orderedCodes.Count > 0)
            {
                return AllocationResult.Failure(
                    "REQUEST_QUANTITY_EXCEEDED",
                    $"Для GTIN {gtinGroup.Key} передано больше КМ, чем допускают current requests.");
            }
        }

        return new AllocationResult(requestByCodeHash, countByRequest, null, null);
    }

    private IReadOnlyList<RelatedMarkingRequest> GetOperationalRequests(long relatedOrderId) =>
        _store.GetRelatedOutstandingMarkingRequests(relatedOrderId)
            .Where(request => request.EffectiveActiveScopedQuantity > 0
                              && request.ImportedQuantity < request.RequestedQuantity)
            .ToArray();

    private static void Assign(
        RelatedMarkingRequest request,
        int quantity,
        Queue<(string Code, string Hash, string Gtin, string FileHash, int RowNumber)> codes,
        IDictionary<string, Guid> requestByCodeHash,
        IDictionary<Guid, int> countByRequest)
    {
        for (var index = 0; index < quantity && codes.Count > 0; index++)
        {
            var code = codes.Dequeue();
            requestByCodeHash[code.Hash] = request.MarkingOrderId;
            countByRequest.TryGetValue(request.MarkingOrderId, out var current);
            countByRequest[request.MarkingOrderId] = current + 1;
        }
    }

    private sealed record AllocationResult(
        IReadOnlyDictionary<string, Guid> RequestByCodeHash,
        IReadOnlyDictionary<Guid, int> CountByRequest,
        string? ErrorCode,
        string? Message)
    {
        public static AllocationResult Failure(string code, string message) =>
            new(new Dictionary<string, Guid>(), new Dictionary<Guid, int>(), code, message);
    }

    private static OrderScopedMarkingImportPreviewResult Invalid(
        string errorCode,
        string message,
        IReadOnlyList<OrderScopedMarkingImportFilePreview>? files = null) =>
        new(false, errorCode, message, string.Empty, false, files ?? Array.Empty<OrderScopedMarkingImportFilePreview>(),
            Array.Empty<OrderScopedMarkingImportRequestPreview>(), Array.Empty<string>());

    private static string ComputeSnapshotHash(
        long orderId,
        IReadOnlyList<OrderScopedMarkingImportFilePreview> files,
        IReadOnlyList<OrderScopedMarkingImportRequestPreview> requestPreviews,
        IReadOnlyList<RelatedMarkingRequest> relatedRequests)
    {
        var relatedById = relatedRequests.ToDictionary(value => value.MarkingOrderId);
        var payload = new StringBuilder().Append(orderId).Append('|');
        foreach (var file in files.OrderBy(value => value.FileHash, StringComparer.Ordinal))
        {
            payload.Append(file.FileHash).Append(':').Append(file.FileSizeBytes).Append('|');
        }

        foreach (var request in requestPreviews.OrderBy(value => value.MarkingOrderId))
        {
            var related = relatedById[request.MarkingOrderId];
            payload.Append(request.MarkingOrderId).Append(':')
                .Append(request.RequiredQuantity).Append(':')
                .Append(request.ReserveQuantity).Append(':')
                .Append(request.RequestedQuantity).Append(':')
                .Append(request.OperationalRequiredQuantity).Append(':')
                .Append(request.ImportedBefore).Append(':')
                .Append(request.ValidInBatch).Append(':')
                .Append(related.ScopeSnapshotHash).Append('|');
        }

        return ComputeHash(Encoding.UTF8.GetBytes(payload.ToString())).ToLowerInvariant();
    }

    private static string ComputeHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
