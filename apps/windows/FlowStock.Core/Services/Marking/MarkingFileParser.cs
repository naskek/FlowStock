using System.IO;
using System.Text;
using FlowStock.Core.Models.Marking;

namespace FlowStock.Core.Services.Marking;

public sealed class MarkingFileParser
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public MarkingParsedFile Parse(byte[] fileBytes, string fileHash)
    {
        ArgumentNullException.ThrowIfNull(fileBytes);

        if (string.IsNullOrWhiteSpace(fileHash))
        {
            throw new ArgumentException("File hash is required.", nameof(fileHash));
        }

        var text = ReadUtf8Text(fileBytes);
        return ParseText(text, fileHash);
    }

    public MarkingParsedFile ParseText(string text, string fileHash)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (string.IsNullOrWhiteSpace(fileHash))
        {
            throw new ArgumentException("File hash is required.", nameof(fileHash));
        }

        var lines = SplitLines(text);
        var firstNonEmptyLine = lines.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        var delimiter = DetectDelimiter(firstNonEmptyLine);
        var sourceType = delimiter == '\t'
            ? MarkingFileSourceType.Tsv
            : MarkingFileSourceType.Csv;

        var acceptedCodes = new List<string>();
        var seenCodes = new HashSet<string>(StringComparer.Ordinal);
        var detectedGtins = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();

        var totalRows = 0;
        var validRows = 0;
        var invalidRows = 0;
        var duplicateRowsInFile = 0;
        var invalidGtinRows = 0;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            totalRows++;
            var parts = line.Split(delimiter);
            var normalizedCode = parts.Length > 0
                ? MarkingCodeNormalizer.NormalizeCode(parts[0])
                : string.Empty;

            if (string.IsNullOrWhiteSpace(normalizedCode))
            {
                invalidRows++;
                continue;
            }

            if (!seenCodes.Add(normalizedCode))
            {
                duplicateRowsInFile++;
                continue;
            }

            if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
            {
                var normalizedGtin = MarkingCodeNormalizer.NormalizeGtin(parts[1]);
                if (normalizedGtin == null)
                {
                    invalidGtinRows++;
                }
                else
                {
                    detectedGtins.Add(normalizedGtin);
                }
            }

            acceptedCodes.Add(normalizedCode);
            validRows++;
        }

        string? detectedGtin = null;
        if (detectedGtins.Count == 1)
        {
            detectedGtin = detectedGtins.First();
        }
        else if (detectedGtins.Count > 1)
        {
            warnings.Add("Multiple GTIN values detected; auto-detection was left empty.");
        }

        if (invalidGtinRows > 0)
        {
            warnings.Add($"Ignored {invalidGtinRows} row(s) with invalid GTIN values.");
        }

        return new MarkingParsedFile
        {
            SourceType = sourceType,
            FileHash = fileHash,
            TotalRows = totalRows,
            ValidRows = validRows,
            InvalidRows = invalidRows,
            DuplicateRowsInFile = duplicateRowsInFile,
            DetectedRequestNumber = null,
            DetectedGtin = detectedGtin,
            DetectedQuantity = acceptedCodes.Count,
            AcceptedCodes = acceptedCodes,
            Warnings = warnings
        };
    }

    public static char DetectDelimiter(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return '\t';
        }

        var tabCount = line.Count(ch => ch == '\t');
        var semicolonCount = line.Count(ch => ch == ';');
        var commaCount = line.Count(ch => ch == ',');
        var maxCount = Math.Max(tabCount, Math.Max(semicolonCount, commaCount));

        if (maxCount == 0 || tabCount == maxCount)
        {
            return '\t';
        }

        return semicolonCount == maxCount ? ';' : ',';
    }

    private static string ReadUtf8Text(byte[] fileBytes)
    {
        using var stream = new MemoryStream(fileBytes, writable: false);
        using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static IReadOnlyList<string> SplitLines(string text)
    {
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }
}
