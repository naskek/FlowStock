using System.Security.Cryptography;
using System.Text;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models.Marking;

namespace FlowStock.Core.Services.Marking;

public sealed class MarkingImportService
{
    private readonly IDataStore _data;
    private readonly MarkingImportCoordinator _coordinator;

    public MarkingImportService(IDataStore data)
    {
        _data = data;
        _coordinator = new MarkingImportCoordinator(
            duplicateChecker: new DataStoreDuplicateImportChecker(data),
            orderMatcher: new ExactRequestNumberOrderMatcher(data));
    }

    public MarkingImportResult Import(byte[] fileBytes, string fileName, string? storagePath = null)
    {
        ArgumentNullException.ThrowIfNull(fileBytes);

        var normalizedFileName = string.IsNullOrWhiteSpace(fileName)
            ? string.Empty
            : fileName.Trim();
        var normalizedStoragePath = string.IsNullOrWhiteSpace(storagePath)
            ? "<memory>"
            : storagePath.Trim();

        var preliminary = _coordinator.Process(fileBytes, normalizedFileName);
        if (preliminary.Decision.DecisionType == MarkingImportDecisionType.DuplicateFile)
        {
            return preliminary;
        }

        MarkingImportResult finalResult = preliminary;
        _data.ExecuteInTransaction(store =>
        {
            var existingImport = store.FindMarkingCodeImportByHash(preliminary.FileHash);
            if (existingImport != null)
            {
                finalResult = new MarkingImportResult
                {
                    FileName = normalizedFileName,
                    FileHash = preliminary.FileHash,
                    ImportId = existingImport.Id,
                    ParsedFile = null,
                    Decision = new MarkingImportDecision
                    {
                        DecisionType = MarkingImportDecisionType.DuplicateFile,
                        Reason = "File hash already exists in Marking import storage."
                    }
                };
                return;
            }

            var now = DateTime.Now;
            var parsedFile = preliminary.ParsedFile;
            var importId = Guid.NewGuid();
            var initialImport = new MarkingCodeImport
            {
                Id = importId,
                OriginalFilename = normalizedFileName,
                StoragePath = normalizedStoragePath,
                FileHash = preliminary.FileHash,
                SourceType = ResolveSourceType(preliminary, normalizedFileName),
                DetectedRequestNumber = parsedFile?.DetectedRequestNumber,
                DetectedGtin = parsedFile?.DetectedGtin,
                DetectedQuantity = parsedFile?.DetectedQuantity,
                MatchedMarkingOrderId = null,
                MatchConfidence = null,
                Status = MarkingCodeImportStatus.Processing,
                ImportedRows = parsedFile?.TotalRows ?? 0,
                ValidCodeRows = parsedFile?.ValidRows ?? 0,
                DuplicateCodeRows = parsedFile?.DuplicateRowsInFile ?? 0,
                ErrorMessage = null,
                CreatedAt = now,
                ProcessedAt = null
            };

            importId = store.AddMarkingCodeImport(initialImport);

            var persistedCodes = 0;
            var skippedExistingCodes = 0;
            var finalStatus = preliminary.Decision.DecisionType switch
            {
                MarkingImportDecisionType.Bound => MarkingCodeImportStatus.Bound,
                MarkingImportDecisionType.ManualReview => MarkingCodeImportStatus.ManualReview,
                _ => MarkingCodeImportStatus.Failed
            };

            if (parsedFile != null
                && preliminary.Decision.DecisionType == MarkingImportDecisionType.Bound
                && preliminary.Decision.TargetMarkingOrderId.HasValue)
            {
                var targetOrderId = preliminary.Decision.TargetMarkingOrderId.Value;
                var newCodes = new List<MarkingCode>();
                for (var index = 0; index < parsedFile.AcceptedCodes.Count; index++)
                {
                    var code = parsedFile.AcceptedCodes[index];
                    if (store.ExistsMarkingCodeByRaw(code))
                    {
                        skippedExistingCodes++;
                        continue;
                    }

                    newCodes.Add(new MarkingCode
                    {
                        Id = Guid.NewGuid(),
                        Code = code,
                        CodeHash = ComputeCodeHash(code),
                        Gtin = parsedFile.DetectedGtin,
                        MarkingOrderId = targetOrderId,
                        ImportId = importId,
                        Status = MarkingCodeStatus.Reserved,
                        SourceRowNumber = index + 1,
                        CreatedAt = now,
                        UpdatedAt = now
                    });
                }

                if (newCodes.Count > 0)
                {
                    store.AddMarkingCodes(newCodes);
                    store.UpdateMarkingOrderStatus(targetOrderId, MarkingOrderStatus.CodesBound, now, now);
                    persistedCodes = newCodes.Count;
                }
            }

            var finalImport = new MarkingCodeImport
            {
                Id = importId,
                OriginalFilename = normalizedFileName,
                StoragePath = normalizedStoragePath,
                FileHash = preliminary.FileHash,
                SourceType = ResolveSourceType(preliminary, normalizedFileName),
                DetectedRequestNumber = parsedFile?.DetectedRequestNumber,
                DetectedGtin = parsedFile?.DetectedGtin,
                DetectedQuantity = parsedFile?.DetectedQuantity,
                MatchedMarkingOrderId = preliminary.Decision.TargetMarkingOrderId,
                MatchConfidence = preliminary.Decision.MatchConfidence,
                Status = finalStatus,
                ImportedRows = parsedFile?.TotalRows ?? 0,
                ValidCodeRows = parsedFile?.ValidRows ?? 0,
                DuplicateCodeRows = (parsedFile?.DuplicateRowsInFile ?? 0) + skippedExistingCodes,
                ErrorMessage = finalStatus == MarkingCodeImportStatus.Failed ? preliminary.Decision.Reason : null,
                CreatedAt = now,
                ProcessedAt = now
            };

            store.UpdateMarkingCodeImport(finalImport);

            finalResult = new MarkingImportResult
            {
                FileName = normalizedFileName,
                FileHash = preliminary.FileHash,
                ImportId = importId,
                ParsedFile = parsedFile,
                Decision = preliminary.Decision,
                PersistedCodeCount = persistedCodes,
                SkippedExistingCodeCount = skippedExistingCodes
            };
        });

        return finalResult;
    }

    private static string ResolveSourceType(MarkingImportResult preliminary, string fileName)
    {
        if (preliminary.ParsedFile != null)
        {
            return preliminary.ParsedFile.SourceType == MarkingFileSourceType.Tsv ? "tsv" : "csv";
        }

        var extension = Path.GetExtension(fileName);
        if (string.Equals(extension, ".tsv", StringComparison.OrdinalIgnoreCase))
        {
            return "tsv";
        }

        if (string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase))
        {
            return "csv";
        }

        return "unknown";
    }

    private static string ComputeCodeHash(string code)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(code);
        return Convert.ToHexString(sha256.ComputeHash(bytes));
    }

    private sealed class DataStoreDuplicateImportChecker : IMarkingDuplicateImportChecker
    {
        private readonly IDataStore _data;

        public DataStoreDuplicateImportChecker(IDataStore data)
        {
            _data = data;
        }

        public bool IsDuplicateFileHash(string fileHash)
        {
            return _data.FindMarkingCodeImportByHash(fileHash) != null;
        }
    }

    private sealed class ExactRequestNumberOrderMatcher : IMarkingOrderMatcher
    {
        private readonly IDataStore _data;

        public ExactRequestNumberOrderMatcher(IDataStore data)
        {
            _data = data;
        }

        public MarkingImportDecision Decide(MarkingParsedFile parsedFile, string fileName)
        {
            if (string.IsNullOrWhiteSpace(parsedFile.DetectedRequestNumber))
            {
                return new MarkingImportDecision
                {
                    DecisionType = MarkingImportDecisionType.ManualReview,
                    Reason = "No request number was detected in file name or parsed data."
                };
            }

            var order = _data.FindMarkingOrderByRequestNumber(parsedFile.DetectedRequestNumber);
            if (order == null)
            {
                return new MarkingImportDecision
                {
                    DecisionType = MarkingImportDecisionType.ManualReview,
                    Reason = $"No Marking order found for request number '{parsedFile.DetectedRequestNumber}'."
                };
            }

            return new MarkingImportDecision
            {
                DecisionType = MarkingImportDecisionType.Bound,
                TargetMarkingOrderId = order.Id,
                MatchConfidence = 1.0m,
                Reason = "Exact request number match."
            };
        }
    }
}
