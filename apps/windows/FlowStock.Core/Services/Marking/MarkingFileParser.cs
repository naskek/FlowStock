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
            var parts = ParseDelimitedRow(line, delimiter);
            if (delimiter != '\t' && parts.Length > 1)
            {
                invalidRows++;
                warnings.Add($"Row {totalRows}: production Kontur rows must use a tab delimiter.");
                continue;
            }
            var normalizedCode = parts.Length > 0
                ? MarkingCodeNormalizer.NormalizeCode(parts[0])
                : string.Empty;

            if (string.IsNullOrWhiteSpace(normalizedCode)
                || !TryExtractGs1Gtin(normalizedCode, out var dmGtin))
            {
                invalidRows++;
                continue;
            }

            // Three columns are the observed Kontur contract. A one-column row is
            // intentionally supported by FlowStock as a compatibility extension.
            if (parts.Length != 1 && parts.Length != 3)
            {
                invalidRows++;
                warnings.Add($"Row {totalRows}: expected one compatibility column or three Kontur columns.");
                continue;
            }

            if (!seenCodes.Add(normalizedCode))
            {
                duplicateRowsInFile++;
                continue;
            }

            if (parts.Length == 3)
            {
                var normalizedGtin = MarkingCodeNormalizer.NormalizeGtin(parts[1]);
                if (normalizedGtin == null || !string.Equals(normalizedGtin, dmGtin, StringComparison.Ordinal))
                {
                    invalidGtinRows++;
                    invalidRows++;
                    warnings.Add($"Row {totalRows}: GTIN column does not match DataMatrix AI(01).");
                    continue;
                }
            }

            detectedGtins.Add(dmGtin);
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

    private static string[] ParseDelimitedRow(string row, char delimiter)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < row.Length; index++)
        {
            var ch = row[index];
            if (ch == '"')
            {
                if (quoted && index + 1 < row.Length && row[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }

                continue;
            }

            if (ch == delimiter && !quoted)
            {
                fields.Add(field.ToString());
                field.Clear();
                continue;
            }

            field.Append(ch);
        }

        fields.Add(field.ToString());
        return fields.ToArray();
    }

    public static bool TryExtractGs1Gtin(string code, out string gtin)
    {
        gtin = string.Empty;
        if (code.Length < 24
            || !code.StartsWith("01", StringComparison.Ordinal)
            || !code.AsSpan(2, 14).ToString().All(char.IsDigit)
            || !code.AsSpan(16).StartsWith("21", StringComparison.Ordinal))
        {
            return false;
        }

        var groupSeparator = code.IndexOf('\u001D', 18);
        if (groupSeparator <= 18
            || groupSeparator + 3 >= code.Length
            || !code.AsSpan(groupSeparator + 1).StartsWith("93", StringComparison.Ordinal))
        {
            return false;
        }

        gtin = code.Substring(2, 14);
        return true;
    }
}
