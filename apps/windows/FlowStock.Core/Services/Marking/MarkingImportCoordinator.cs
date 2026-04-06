using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FlowStock.Core.Models.Marking;

namespace FlowStock.Core.Services.Marking;

public sealed class MarkingImportCoordinator
{
    private readonly MarkingFileParser _parser;
    private readonly IMarkingDuplicateImportChecker? _duplicateChecker;
    private readonly IMarkingOrderMatcher? _orderMatcher;

    public MarkingImportCoordinator(
        MarkingFileParser? parser = null,
        IMarkingDuplicateImportChecker? duplicateChecker = null,
        IMarkingOrderMatcher? orderMatcher = null)
    {
        _parser = parser ?? new MarkingFileParser();
        _duplicateChecker = duplicateChecker;
        _orderMatcher = orderMatcher;
    }

    public MarkingImportResult Process(byte[] fileBytes, string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileBytes);

        var normalizedFileName = string.IsNullOrWhiteSpace(fileName)
            ? string.Empty
            : fileName.Trim();
        var fileHash = ComputeFileHash(fileBytes);

        if (_duplicateChecker?.IsDuplicateFileHash(fileHash) == true)
        {
            return new MarkingImportResult
            {
                FileName = normalizedFileName,
                FileHash = fileHash,
                ParsedFile = null,
                Decision = new MarkingImportDecision
                {
                    DecisionType = MarkingImportDecisionType.DuplicateFile,
                    Reason = "File hash already exists in Marking import storage."
                }
            };
        }

        try
        {
            var parsedFile = ApplyFileNameHints(_parser.Parse(fileBytes, fileHash), normalizedFileName);
            if (parsedFile.ValidRows == 0)
            {
                return new MarkingImportResult
                {
                    FileName = normalizedFileName,
                    FileHash = fileHash,
                    ParsedFile = parsedFile,
                    Decision = new MarkingImportDecision
                    {
                        DecisionType = MarkingImportDecisionType.Failed,
                        Reason = "File does not contain any valid marking codes."
                    }
                };
            }

            var decision = _orderMatcher?.Decide(parsedFile, normalizedFileName)
                ?? new MarkingImportDecision
                {
                    DecisionType = MarkingImportDecisionType.ManualReview,
                    Reason = "Order matching is not implemented yet."
                };

            return new MarkingImportResult
            {
                FileName = normalizedFileName,
                FileHash = fileHash,
                ParsedFile = parsedFile,
                Decision = decision
            };
        }
        catch (Exception ex) when (ex is DecoderFallbackException or InvalidDataException)
        {
            return new MarkingImportResult
            {
                FileName = normalizedFileName,
                FileHash = fileHash,
                ParsedFile = null,
                Decision = new MarkingImportDecision
                {
                    DecisionType = MarkingImportDecisionType.Failed,
                    Reason = ex.Message
                }
            };
        }
    }

    public MarkingImportResult ProcessText(string text, string fileName)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Process(Encoding.UTF8.GetBytes(text), fileName);
    }

    public static string ComputeFileHash(byte[] fileBytes)
    {
        ArgumentNullException.ThrowIfNull(fileBytes);

        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(fileBytes);
        return Convert.ToHexString(hash);
    }

    private static MarkingParsedFile ApplyFileNameHints(MarkingParsedFile parsedFile, string fileName)
    {
        var detectedRequestNumber = string.IsNullOrWhiteSpace(parsedFile.DetectedRequestNumber)
            ? ExtractRequestNumberFromFileName(fileName)
            : parsedFile.DetectedRequestNumber;

        if (string.Equals(detectedRequestNumber, parsedFile.DetectedRequestNumber, StringComparison.Ordinal))
        {
            return parsedFile;
        }

        return new MarkingParsedFile
        {
            SourceType = parsedFile.SourceType,
            FileHash = parsedFile.FileHash,
            TotalRows = parsedFile.TotalRows,
            ValidRows = parsedFile.ValidRows,
            InvalidRows = parsedFile.InvalidRows,
            DuplicateRowsInFile = parsedFile.DuplicateRowsInFile,
            DetectedRequestNumber = detectedRequestNumber,
            DetectedGtin = parsedFile.DetectedGtin,
            DetectedQuantity = parsedFile.DetectedQuantity,
            AcceptedCodes = parsedFile.AcceptedCodes,
            Warnings = parsedFile.Warnings
        };
    }

    private static string? ExtractRequestNumberFromFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return null;
        }

        var match = Regex.Match(
            baseName,
            @"(?:^|[._\s])(request|req)[-_=](?<value>[A-Za-z0-9][A-Za-z0-9._-]*)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups["value"].Value.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
