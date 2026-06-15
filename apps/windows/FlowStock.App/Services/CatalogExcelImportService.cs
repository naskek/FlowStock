using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ExcelDataReader;
using FlowStock.Core.Models;

namespace FlowStock.App;

public enum CatalogExcelImportStatus
{
    New,
    Error,
    Skipped
}

public sealed record CatalogExcelRawRow(int ExcelRowNumber, object?[] Cells);

public sealed class CatalogExcelColumnMap
{
    public int? SkuColumn { get; set; }
    public int? GtinColumn { get; set; }
    public int? NameColumn { get; set; }
    public int? BrandColumn { get; set; }
    public int? VolumeColumn { get; set; }
    public int? ShelfLifeColumn { get; set; }
    public int? TaraColumn { get; set; }
    public int? PermitDocumentationColumn { get; set; }
    public int? PermitFromColumn { get; set; }
    public int? PermitToColumn { get; set; }
    public int? MaterialColumn { get; set; }
    public int? CodeApplicationColumn { get; set; }
    public int? TnvedColumn { get; set; }
    public int? ShortNameColumn { get; set; }
    public int? StorageConditionsColumn { get; set; }
    public int? TechnicalConditionsColumn { get; set; }
    public int? CompositionColumn { get; set; }

    public static CatalogExcelColumnMap FromManualSelection(
        int? skuColumn,
        int? gtinColumn,
        int? nameColumn,
        int? brandColumn,
        int? volumeColumn,
        int? shelfLifeColumn,
        int? taraColumn) =>
        new()
        {
            SkuColumn = NormalizeIndex(skuColumn),
            GtinColumn = NormalizeIndex(gtinColumn),
            NameColumn = NormalizeIndex(nameColumn),
            BrandColumn = NormalizeIndex(brandColumn),
            VolumeColumn = NormalizeIndex(volumeColumn),
            ShelfLifeColumn = NormalizeIndex(shelfLifeColumn),
            TaraColumn = NormalizeIndex(taraColumn)
        };

    private static int? NormalizeIndex(int? index) => index is >= 0 ? index : null;
}

public sealed class CatalogExcelImportContext
{
    public HashSet<string> ExistingCodes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, Tara> TarasByName { get; init; } =
        new Dictionary<string, Tara>(StringComparer.OrdinalIgnoreCase);

    public static CatalogExcelImportContext FromCatalog(
        IReadOnlyList<Item> existingItems,
        IReadOnlyList<Tara> taras)
    {
        var existingCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in existingItems)
        {
            CatalogExcelImportService.RegisterExistingCode(existingCodes, item.Barcode);
            CatalogExcelImportService.RegisterExistingCode(existingCodes, item.Gtin);
        }

        var tarasByName = taras
            .Where(tara => !string.IsNullOrWhiteSpace(tara.Name))
            .GroupBy(tara => tara.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        return new CatalogExcelImportContext
        {
            ExistingCodes = existingCodes,
            TarasByName = tarasByName
        };
    }
}

public sealed class CatalogExcelImportRow
{
    public int ExcelRowNumber { get; init; }
    public CatalogExcelImportStatus Status { get; init; }
    public string? Reason { get; init; }
    public bool Include { get; init; }
    public string? Barcode { get; init; }
    public string? Gtin { get; init; }
    public string? Name { get; init; }
    public string? Brand { get; init; }
    public string? Volume { get; init; }
    public int? ShelfLifeMonths { get; init; }
    public string? TaraName { get; init; }
    public long? TaraId { get; init; }
    public string? PermitDocumentation { get; init; }
    public DateTime? PermitFrom { get; init; }
    public DateTime? PermitTo { get; init; }
    public string? Material { get; init; }
    public string? CodeApplication { get; init; }
    public string? Tnved { get; init; }
    public string? ShortName { get; init; }
    public string? StorageConditions { get; init; }
    public string? TechnicalConditions { get; init; }
    public string? Composition { get; init; }
}

public sealed class CatalogExcelImportResult
{
    public IReadOnlyList<CatalogExcelImportRow> Rows { get; init; } = Array.Empty<CatalogExcelImportRow>();
    public int EmptyRows { get; init; }
    public int ErrorRows { get; init; }
    public int SkippedRows { get; init; }
    public int NewRows { get; init; }
    public int DuplicateRows { get; init; }
}

public static class CatalogExcelImportService
{
    private const int HeaderSearchLimit = 20;

    private static readonly (string NormalizedHeader, Action<CatalogExcelColumnMap, int> Assign)[] HeaderMappings =
    [
        ("gtin", (map, index) => map.GtinColumn ??= index),
        ("наименование", (map, index) => map.NameColumn ??= index),
        ("бренд", (map, index) => map.BrandColumn ??= index),
        ("объем", (map, index) => map.VolumeColumn ??= index),
        ("объём", (map, index) => map.VolumeColumn ??= index),
        ("срок годности", (map, index) => map.ShelfLifeColumn ??= index),
        ("разрешительная документация", (map, index) => map.PermitDocumentationColumn ??= index),
        ("от", (map, index) => map.PermitFromColumn ??= index),
        ("до", (map, index) => map.PermitToColumn ??= index),
        ("упаковка", (map, index) => map.TaraColumn ??= index),
        ("материал", (map, index) => map.MaterialColumn ??= index),
        ("нанесение кодов", (map, index) => map.CodeApplicationColumn ??= index),
        ("тн вэд", (map, index) => map.TnvedColumn ??= index),
        ("shortname", (map, index) => map.ShortNameColumn ??= index),
        ("условия хранения", (map, index) => map.StorageConditionsColumn ??= index),
        ("ту", (map, index) => map.TechnicalConditionsColumn ??= index),
        ("состав", (map, index) => map.CompositionColumn ??= index)
    ];

    private static readonly string[] SkuHeaderNames =
    [
        "sku",
        "штрихкод",
        "штрих код",
        "barcode",
        "ean",
        "код товара"
    ];

    public static IReadOnlyList<CatalogExcelRawRow> LoadRows(Stream stream)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var rows = new List<CatalogExcelRawRow>();
        var excelRowNumber = 0;

        do
        {
            while (reader.Read())
            {
                excelRowNumber++;
                var cells = new object?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    cells[i] = reader.GetValue(i);
                }

                rows.Add(new CatalogExcelRawRow(excelRowNumber, cells));
            }

            break;
        } while (reader.NextResult());

        return rows;
    }

    public static string NormalizeHeader(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value
            .Replace('\u00A0', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ')
            .Trim()
            .ToLowerInvariant()
            .Replace('ё', 'е');

        return Regex.Replace(normalized, "\\s+", " ");
    }

    public static bool TryDetectHeader(
        IReadOnlyList<CatalogExcelRawRow> rows,
        out int headerRowIndex,
        out CatalogExcelColumnMap map)
    {
        headerRowIndex = -1;
        map = new CatalogExcelColumnMap();

        var searchLimit = Math.Min(rows.Count, HeaderSearchLimit);
        for (var rowIndex = 0; rowIndex < searchLimit; rowIndex++)
        {
            var candidate = BuildMapFromHeaderRow(rows[rowIndex].Cells);
            if (candidate.GtinColumn.HasValue && candidate.NameColumn.HasValue)
            {
                headerRowIndex = rowIndex;
                map = candidate;
                return true;
            }
        }

        return false;
    }

    public static CatalogExcelColumnMap BuildMapFromHeaderRow(object?[] headerCells)
    {
        var map = new CatalogExcelColumnMap();
        for (var index = 0; index < headerCells.Length; index++)
        {
            var normalized = NormalizeHeader(GetCellDisplayText(headerCells[index]));
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            foreach (var mapping in HeaderMappings)
            {
                if (string.Equals(normalized, mapping.NormalizedHeader, StringComparison.Ordinal))
                {
                    mapping.Assign(map, index);
                    break;
                }
            }

            if (!map.SkuColumn.HasValue && IsSkuHeader(normalized))
            {
                map.SkuColumn = index;
            }
        }

        return map;
    }

    public static bool IsSkuHeader(string? header) =>
        SkuHeaderNames.Contains(NormalizeHeader(header), StringComparer.Ordinal);

    public static int? TryGuessSkuColumnIndex(IReadOnlyList<string> headers)
    {
        for (var index = 0; index < headers.Count; index++)
        {
            if (IsSkuHeader(headers[index]))
            {
                return index;
            }
        }

        return null;
    }

    public static bool TryParseShelfLifeMonths(object? cellValue, string? normalizedText, out int months)
    {
        months = 0;

        if (cellValue is double number && number > 0 && Math.Abs(number - Math.Round(number)) < 0.0001)
        {
            months = (int)Math.Round(number);
            return months > 0;
        }

        if (cellValue is float numberFloat && numberFloat > 0 && Math.Abs(numberFloat - Math.Round(numberFloat)) < 0.0001)
        {
            months = (int)Math.Round(numberFloat);
            return months > 0;
        }

        if (cellValue is decimal numberDecimal && numberDecimal > 0 && numberDecimal == decimal.Truncate(numberDecimal))
        {
            months = (int)numberDecimal;
            return months > 0;
        }

        if (cellValue is int intValue && intValue > 0)
        {
            months = intValue;
            return true;
        }

        if (cellValue is long longValue && longValue > 0 && longValue <= int.MaxValue)
        {
            months = (int)longValue;
            return true;
        }

        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return false;
        }

        if (int.TryParse(normalizedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var directValue) && directValue > 0)
        {
            months = directValue;
            return true;
        }

        var match = Regex.Match(normalizedText, "(\\d+)");
        if (match.Success
            && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var extractedValue)
            && extractedValue > 0)
        {
            months = extractedValue;
            return true;
        }

        return false;
    }

    public static (long? TaraId, string? Warning) ResolveTara(string? packageName, CatalogExcelImportContext context)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return (null, null);
        }

        var normalizedPackage = NormalizeHeader(packageName);
        if (string.IsNullOrWhiteSpace(normalizedPackage))
        {
            return (null, null);
        }

        if (context.TarasByName.TryGetValue(packageName.Trim(), out var exactMatch))
        {
            return (exactMatch.Id, null);
        }

        var aliasMatches = context.TarasByName.Values
            .Where(tara => NormalizeHeader(tara.Name).Contains(normalizedPackage, StringComparison.Ordinal))
            .ToArray();

        if (aliasMatches.Length == 1)
        {
            return (aliasMatches[0].Id, null);
        }

        return (null, $"Тара не найдена: {packageName.Trim()}; товар будет создан без тары");
    }

    public static int FindLastMeaningfulRowIndex(
        IReadOnlyList<CatalogExcelRawRow> rows,
        int headerRowIndex,
        CatalogExcelColumnMap map)
    {
        for (var rowIndex = rows.Count - 1; rowIndex > headerRowIndex; rowIndex--)
        {
            if (IsMeaningfulRow(rows[rowIndex].Cells, map))
            {
                return rowIndex;
            }
        }

        return headerRowIndex;
    }

    public static bool IsMeaningfulRow(object?[] cells, CatalogExcelColumnMap map)
    {
        foreach (var columnIndex in EnumerateMappedColumnIndexes(map))
        {
            if (!IsBlankCell(ReadCell(cells, columnIndex)))
            {
                return true;
            }
        }

        return false;
    }

    public static CatalogExcelImportResult Parse(
        IReadOnlyList<CatalogExcelRawRow> rows,
        int headerRowIndex,
        CatalogExcelColumnMap map,
        CatalogExcelImportContext context)
    {
        if (rows.Count == 0 || headerRowIndex < 0 || headerRowIndex >= rows.Count)
        {
            return new CatalogExcelImportResult();
        }

        if (!map.NameColumn.HasValue)
        {
            return new CatalogExcelImportResult();
        }

        var parsedRows = new List<CatalogExcelImportRow>();
        var seenCodes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var emptyRows = 0;
        var errorRows = 0;
        var skippedRows = 0;
        var newRows = 0;
        var duplicateRows = 0;
        var lastMeaningfulRowIndex = FindLastMeaningfulRowIndex(rows, headerRowIndex, map);

        for (var rowIndex = headerRowIndex + 1; rowIndex <= lastMeaningfulRowIndex; rowIndex++)
        {
            var rawRow = rows[rowIndex];
            if (!IsMeaningfulRow(rawRow.Cells, map))
            {
                continue;
            }

            var warnings = new List<string>();
            var name = NormalizeText(ReadText(rawRow.Cells, map.NameColumn));
            var gtinRaw = ReadText(rawRow.Cells, map.GtinColumn);
            var gtin = NormalizeImportedGtin(gtinRaw);
            var barcode = NormalizeImportedBarcode(ReadText(rawRow.Cells, map.SkuColumn));
            if (string.IsNullOrWhiteSpace(barcode) && !string.IsNullOrWhiteSpace(gtin))
            {
                barcode = gtin;
            }

            var brand = NormalizeText(ReadText(rawRow.Cells, map.BrandColumn));
            var volume = NormalizeText(ReadText(rawRow.Cells, map.VolumeColumn));
            var shelfLifeCell = ReadCell(rawRow.Cells, map.ShelfLifeColumn);
            var shelfLifeText = NormalizeText(ReadText(rawRow.Cells, map.ShelfLifeColumn));
            var taraName = NormalizeText(ReadText(rawRow.Cells, map.TaraColumn));
            var permitDocumentation = NormalizeText(ReadText(rawRow.Cells, map.PermitDocumentationColumn));
            var material = NormalizeText(ReadText(rawRow.Cells, map.MaterialColumn));
            var codeApplication = NormalizeText(ReadText(rawRow.Cells, map.CodeApplicationColumn));
            var tnved = NormalizeText(ReadText(rawRow.Cells, map.TnvedColumn));
            var shortName = NormalizeText(ReadText(rawRow.Cells, map.ShortNameColumn));
            var storageConditions = NormalizeText(ReadText(rawRow.Cells, map.StorageConditionsColumn));
            var technicalConditions = NormalizeText(ReadText(rawRow.Cells, map.TechnicalConditionsColumn));
            var composition = NormalizeText(ReadText(rawRow.Cells, map.CompositionColumn));

            var permitFrom = ReadDate(rawRow.Cells, map.PermitFromColumn, "от", warnings);
            var permitTo = ReadDate(rawRow.Cells, map.PermitToColumn, "до", warnings);

            string? error = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "Нет наименования";
            }
            else if (!string.IsNullOrWhiteSpace(gtinRaw) && gtin == null)
            {
                error = "GTIN должен содержать только цифры";
            }
            else if (string.IsNullOrWhiteSpace(barcode))
            {
                error = "Нет GTIN";
            }
            else if (HasShelfLifeValue(shelfLifeCell, shelfLifeText)
                     && (!TryParseShelfLifeMonths(shelfLifeCell, shelfLifeText, out var parsedShelfLife) || parsedShelfLife <= 0))
            {
                error = "Срок годности: целое число месяцев";
            }

            int? shelfLifeMonths = null;
            if (error == null
                && HasShelfLifeValue(shelfLifeCell, shelfLifeText)
                && TryParseShelfLifeMonths(shelfLifeCell, shelfLifeText, out var shelfLifeMonthsValue))
            {
                shelfLifeMonths = shelfLifeMonthsValue;
            }

            long? taraId = null;
            if (error == null)
            {
                var (resolvedTaraId, taraWarning) = ResolveTara(taraName, context);
                taraId = resolvedTaraId;
                if (taraWarning != null)
                {
                    warnings.Add(taraWarning);
                }
            }

            if (error == null)
            {
                var duplicateInFile = FindDuplicateInFile(seenCodes, barcode, gtin);
                if (duplicateInFile.HasValue)
                {
                    error = $"Дубликат GTIN в файле, строка {duplicateInFile.Value}";
                    duplicateRows++;
                }
                else if (IsExistingCode(context.ExistingCodes, barcode) || IsExistingCode(context.ExistingCodes, gtin))
                {
                    parsedRows.Add(BuildParsedRow(
                        rawRow.ExcelRowNumber,
                        CatalogExcelImportStatus.Skipped,
                        AppendWarnings("Дубликат в базе", warnings),
                        false,
                        barcode,
                        gtin,
                        name,
                        brand,
                        volume,
                        shelfLifeMonths,
                        taraName,
                        taraId,
                        permitDocumentation,
                        permitFrom,
                        permitTo,
                        material,
                        codeApplication,
                        tnved,
                        shortName,
                        storageConditions,
                        technicalConditions,
                        composition));
                    skippedRows++;
                    duplicateRows++;
                    RegisterSeenCodes(seenCodes, rawRow.ExcelRowNumber, barcode, gtin);
                    continue;
                }
                else
                {
                    RegisterSeenCodes(seenCodes, rawRow.ExcelRowNumber, barcode, gtin);
                }
            }

            if (error != null)
            {
                parsedRows.Add(BuildParsedRow(
                    rawRow.ExcelRowNumber,
                    CatalogExcelImportStatus.Error,
                    AppendWarnings(error, warnings),
                    false,
                    barcode,
                    gtin,
                    name,
                    brand,
                    volume,
                    shelfLifeMonths,
                    taraName,
                    taraId,
                    permitDocumentation,
                    permitFrom,
                    permitTo,
                    material,
                    codeApplication,
                    tnved,
                    shortName,
                    storageConditions,
                    technicalConditions,
                    composition));
                errorRows++;
                continue;
            }

            parsedRows.Add(BuildParsedRow(
                rawRow.ExcelRowNumber,
                CatalogExcelImportStatus.New,
                AppendWarnings(null, warnings),
                true,
                barcode,
                gtin,
                name,
                brand,
                volume,
                shelfLifeMonths,
                taraName,
                taraId,
                permitDocumentation,
                permitFrom,
                permitTo,
                material,
                codeApplication,
                tnved,
                shortName,
                storageConditions,
                technicalConditions,
                composition));
            newRows++;
        }

        return new CatalogExcelImportResult
        {
            Rows = parsedRows,
            EmptyRows = emptyRows,
            ErrorRows = errorRows,
            SkippedRows = skippedRows,
            NewRows = newRows,
            DuplicateRows = duplicateRows
        };
    }

    public static string? GetCellDisplayText(object? value)
    {
        if (value == null)
        {
            return null;
        }

        return value switch
        {
            string text => text,
            double number => number.ToString("0.############################", CultureInfo.InvariantCulture),
            float number => number.ToString("0.############################", CultureInfo.InvariantCulture),
            decimal number => number.ToString("0.############################", CultureInfo.InvariantCulture),
            DateTime dateTime => dateTime.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
            bool boolean => boolean ? "true" : "false",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
    }

    public static string? NormalizeImportedGtin(string? value)
    {
        var trimmed = NormalizeGtinInput(value);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        if (!IsDigitsOnly(trimmed))
        {
            return null;
        }

        return trimmed.Length < 14 ? trimmed.PadLeft(14, '0') : trimmed;
    }

    public static string? NormalizeImportedBarcode(string? value)
    {
        var trimmed = NormalizeGtinInput(value);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        if (!IsDigitsOnly(trimmed))
        {
            return trimmed;
        }

        return trimmed.Length < 14 ? trimmed.PadLeft(14, '0') : trimmed;
    }

    private static CatalogExcelImportRow BuildParsedRow(
        int excelRowNumber,
        CatalogExcelImportStatus status,
        string? reason,
        bool include,
        string? barcode,
        string? gtin,
        string? name,
        string? brand,
        string? volume,
        int? shelfLifeMonths,
        string? taraName,
        long? taraId,
        string? permitDocumentation,
        DateTime? permitFrom,
        DateTime? permitTo,
        string? material,
        string? codeApplication,
        string? tnved,
        string? shortName,
        string? storageConditions,
        string? technicalConditions,
        string? composition) =>
        new()
        {
            ExcelRowNumber = excelRowNumber,
            Status = status,
            Reason = reason,
            Include = include,
            Barcode = barcode,
            Gtin = gtin,
            Name = name,
            Brand = brand,
            Volume = volume,
            ShelfLifeMonths = shelfLifeMonths,
            TaraName = taraName,
            TaraId = taraId,
            PermitDocumentation = permitDocumentation,
            PermitFrom = permitFrom,
            PermitTo = permitTo,
            Material = material,
            CodeApplication = codeApplication,
            Tnved = tnved,
            ShortName = shortName,
            StorageConditions = storageConditions,
            TechnicalConditions = technicalConditions,
            Composition = composition
        };

    private static string? ReadText(object?[] cells, int? columnIndex) =>
        GetCellDisplayText(ReadCell(cells, columnIndex));

    private static object? ReadCell(object?[] cells, int? columnIndex)
    {
        if (!columnIndex.HasValue || columnIndex.Value < 0 || columnIndex.Value >= cells.Length)
        {
            return null;
        }

        return cells[columnIndex.Value];
    }

    private static bool HasShelfLifeValue(object? cellValue, string? normalizedText) =>
        !IsBlankCell(cellValue) || !string.IsNullOrWhiteSpace(normalizedText);

    private static DateTime? ReadDate(object?[] cells, int? columnIndex, string fieldName, ICollection<string> warnings)
    {
        if (!columnIndex.HasValue || columnIndex.Value < 0 || columnIndex.Value >= cells.Length)
        {
            return null;
        }

        var value = cells[columnIndex.Value];
        if (IsBlankCell(value))
        {
            return null;
        }

        if (value is DateTime dateTime)
        {
            return dateTime.Date;
        }

        if (value is double serial)
        {
            try
            {
                return DateTime.FromOADate(serial).Date;
            }
            catch
            {
                warnings.Add($"Неверная дата в поле «{fieldName}»");
                return null;
            }
        }

        if (value is float serialFloat)
        {
            try
            {
                return DateTime.FromOADate(serialFloat).Date;
            }
            catch
            {
                warnings.Add($"Неверная дата в поле «{fieldName}»");
                return null;
            }
        }

        if (value is decimal serialDecimal)
        {
            try
            {
                return DateTime.FromOADate((double)serialDecimal).Date;
            }
            catch
            {
                warnings.Add($"Неверная дата в поле «{fieldName}»");
                return null;
            }
        }

        var text = NormalizeText(GetCellDisplayText(value));
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (DateTime.TryParseExact(text, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedExact)
            || DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsedExact))
        {
            return parsedExact.Date;
        }

        warnings.Add($"Неверная дата в поле «{fieldName}»");
        return null;
    }

    private static IEnumerable<int> EnumerateMappedColumnIndexes(CatalogExcelColumnMap map)
    {
        int?[] columns =
        [
            map.SkuColumn,
            map.GtinColumn,
            map.NameColumn,
            map.BrandColumn,
            map.VolumeColumn,
            map.ShelfLifeColumn,
            map.TaraColumn,
            map.PermitDocumentationColumn,
            map.PermitFromColumn,
            map.PermitToColumn,
            map.MaterialColumn,
            map.CodeApplicationColumn,
            map.TnvedColumn,
            map.ShortNameColumn,
            map.StorageConditionsColumn,
            map.TechnicalConditionsColumn,
            map.CompositionColumn
        ];

        foreach (var column in columns)
        {
            if (column is >= 0)
            {
                yield return column.Value;
            }
        }
    }

    private static bool IsBlankCell(object? value)
    {
        if (value == null)
        {
            return true;
        }

        return value is string text && string.IsNullOrWhiteSpace(text);
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value
            .Replace('\u00A0', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ')
            .Trim();

        return Regex.Replace(trimmed, "\\s+", " ");
    }

    private static string? NormalizeGtinInput(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value
            .Replace("\u00A0", string.Empty)
            .Replace(" ", string.Empty)
            .Trim();

        if (trimmed.StartsWith("'", StringComparison.Ordinal))
        {
            trimmed = trimmed[1..];
        }

        return trimmed;
    }

    private static bool IsDigitsOnly(string value)
    {
        foreach (var ch in value)
        {
            if (!char.IsDigit(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static int? FindDuplicateInFile(IReadOnlyDictionary<string, int> seenCodes, string? barcode, string? gtin)
    {
        foreach (var code in EnumerateCodes(barcode, gtin))
        {
            if (seenCodes.TryGetValue(code, out var firstRow))
            {
                return firstRow;
            }
        }

        return null;
    }

    private static bool IsExistingCode(HashSet<string> existingCodes, string? code)
    {
        var trimmed = NormalizeGtinInput(code);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        if (existingCodes.Contains(trimmed))
        {
            return true;
        }

        if (!IsDigitsOnly(trimmed))
        {
            return false;
        }

        if (trimmed.Length == 13)
        {
            return existingCodes.Contains("0" + trimmed);
        }

        if (trimmed.Length == 14 && trimmed.StartsWith('0'))
        {
            return existingCodes.Contains(trimmed[1..]);
        }

        return false;
    }

    private static void RegisterSeenCodes(IDictionary<string, int> seenCodes, int rowNumber, string? barcode, string? gtin)
    {
        foreach (var code in EnumerateCodes(barcode, gtin))
        {
            if (!seenCodes.ContainsKey(code))
            {
                seenCodes[code] = rowNumber;
            }
        }
    }

    private static IEnumerable<string> EnumerateCodes(string? barcode, string? gtin)
    {
        foreach (var code in new[] { barcode, gtin })
        {
            var normalized = NormalizeGtinInput(code);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            yield return normalized;
            if (IsDigitsOnly(normalized))
            {
                if (normalized.Length == 13)
                {
                    yield return "0" + normalized;
                }
                else if (normalized.Length == 14 && normalized.StartsWith('0'))
                {
                    yield return normalized[1..];
                }
            }
        }
    }

    internal static void RegisterExistingCode(HashSet<string> target, string? code)
    {
        AddBarcodeVariants(target, code);
    }

    private static void AddBarcodeVariants(HashSet<string> target, string? code)
    {
        var trimmed = NormalizeGtinInput(code);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return;
        }

        target.Add(trimmed);
        if (!IsDigitsOnly(trimmed))
        {
            return;
        }

        if (trimmed.Length == 13)
        {
            target.Add("0" + trimmed);
        }
        else if (trimmed.Length == 14 && trimmed.StartsWith('0'))
        {
            target.Add(trimmed[1..]);
        }
    }

    private static string? AppendWarnings(string? message, IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return message;
        }

        var warningText = string.Join("; ", warnings.Select(warning => $"Предупреждение: {warning}"));
        return string.IsNullOrWhiteSpace(message) ? warningText : $"{message}; {warningText}";
    }
}
