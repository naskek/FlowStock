using System.Globalization;
using FlowStock.App;
using FlowStock.Core.Models;

namespace FlowStock.Server.Tests.Catalog;

public sealed class CatalogExcelImportServiceTests
{
    [Fact]
    public void BuildMapFromHeaderRow_MapsPermitDocumentationWithNewline()
    {
        var map = CatalogExcelImportService.BuildMapFromHeaderRow(
        [
            "GTIN",
            "Наименование",
            "Разрешительная\n документация"
        ]);

        Assert.Equal(0, map.GtinColumn);
        Assert.Equal(1, map.NameColumn);
        Assert.Equal(2, map.PermitDocumentationColumn);
    }

    [Fact]
    public void Parse_PreservesLeadingZeroInTextGtin()
    {
        var result = Parse(
            Header("GTIN", "Наименование"),
            Data("04600840372068", "Товар A"));

        var row = Assert.Single(result.Rows, row => row.Status == CatalogExcelImportStatus.New);
        Assert.Equal("04600840372068", row.Gtin);
        Assert.Equal("04600840372068", row.Barcode);
    }

    [Fact]
    public void Parse_ConvertsNumericGtinWithoutScientificNotation()
    {
        var result = Parse(
            Header("GTIN", "Наименование"),
            new object?[] { 4607046150575d, "Товар B" });

        var row = Assert.Single(result.Rows, row => row.Status == CatalogExcelImportStatus.New);
        Assert.Equal("04607046150575", row.Gtin);
        Assert.DoesNotContain("E", row.Gtin!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_SkipsEmptyFormattedRowsWithoutErrors()
    {
        var result = Parse(
            Header("GTIN", "Наименование"),
            Data("4607046150575", "Товар A"),
            new object?[] { null, null },
            new object?[] { "   ", string.Empty });

        Assert.Equal(1, result.NewRows);
        Assert.Equal(0, result.EmptyRows);
        Assert.Equal(0, result.ErrorRows);
        Assert.DoesNotContain(result.Rows, row => row.Reason == "Пустая строка");
        Assert.Single(result.Rows);
    }

    [Fact]
    public void Parse_AllowsOptionalBlankFieldsWhenGtinAndNamePresent()
    {
        var result = Parse(
            Header("GTIN", "Наименование", "Бренд", "Объем", "Упаковка"),
            Data("4607046150575", "Товар C", null, null, null));

        var row = Assert.Single(result.Rows, row => row.Status == CatalogExcelImportStatus.New);
        Assert.Equal("Товар C", row.Name);
        Assert.Null(row.Brand);
        Assert.Null(row.Volume);
        Assert.Null(row.TaraName);
    }

    [Fact]
    public void Parse_ParsesExcelSerialDates()
    {
        var expectedFrom = DateTime.FromOADate(46044).Date;
        var expectedTo = DateTime.FromOADate(47139).Date;

        var result = Parse(
            Header("GTIN", "Наименование", "от", "до"),
            new object?[] { "4607046150575", "Товар D", 46044d, 47139d });

        var row = Assert.Single(result.Rows, row => row.Status == CatalogExcelImportStatus.New);
        Assert.Equal(expectedFrom, row.PermitFrom);
        Assert.Equal(expectedTo, row.PermitTo);
    }

    [Fact]
    public void Parse_TrimsTrailingSpacesInPackaging()
    {
        var context = EmptyContext(taras: [new Tara { Id = 1, Name = "БАНКА" }]);
        var result = Parse(
            Header("GTIN", "Наименование", "Упаковка"),
            Data("4607046150575", "Товар E", "БАНКА "),
            context: context);

        var row = Assert.Single(result.Rows, row => row.Status == CatalogExcelImportStatus.New);
        Assert.Equal("БАНКА", row.TaraName);
        Assert.Equal(1L, row.TaraId);
    }

    [Fact]
    public void ResolveTara_MapsBankaAliasToGlassJar()
    {
        var context = EmptyContext(taras: [new Tara { Id = 10, Name = "Стеклянная банка" }]);

        var (taraId, warning) = CatalogExcelImportService.ResolveTara("БАНКА", context);

        Assert.Equal(10L, taraId);
        Assert.Null(warning);
    }

    [Fact]
    public void ResolveTara_MapsVedroAliasToPlasticBucket()
    {
        var context = EmptyContext(taras: [new Tara { Id = 11, Name = "Пластиковое ведро" }]);

        var (taraId, warning) = CatalogExcelImportService.ResolveTara("ВЕДРО", context);

        Assert.Equal(11L, taraId);
        Assert.Null(warning);
    }

    [Fact]
    public void Parse_UnknownPackageProducesNewWithWarningNotError()
    {
        var result = Parse(
            Header("GTIN", "Наименование", "Упаковка"),
            Data("4607046150575", "Товар N", "ПАКЕТ"));

        var row = Assert.Single(result.Rows, row => row.Name == "Товар N");
        Assert.Equal(CatalogExcelImportStatus.New, row.Status);
        Assert.True(row.Include);
        Assert.Null(row.TaraId);
        Assert.Contains("Тара не найдена: ПАКЕТ", row.Reason!, StringComparison.Ordinal);
        Assert.Equal(1, result.NewRows);
        Assert.Equal(0, result.ErrorRows);
    }

    [Fact]
    public void Parse_RowWithUnknownTaraRemainsImportable()
    {
        var result = Parse(
            Header("GTIN", "Наименование", "Срок годности", "Упаковка"),
            Data("4607046150575", "Товар O", "12 месяцев", "ПАКЕТ"));

        var row = Assert.Single(result.Rows, row => row.Status == CatalogExcelImportStatus.New);
        Assert.True(row.Include);
        Assert.Equal(12, row.ShelfLifeMonths);
        Assert.Null(row.TaraId);
        Assert.Contains("Тара не найдена: ПАКЕТ", row.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveTara_AmbiguousAliasMatchReturnsWarning()
    {
        var context = EmptyContext(taras:
        [
            new Tara { Id = 20, Name = "Стеклянная банка" },
            new Tara { Id = 21, Name = "Пластиковая банка" }
        ]);

        var (taraId, warning) = CatalogExcelImportService.ResolveTara("БАНКА", context);

        Assert.Null(taraId);
        Assert.Contains("Тара не найдена: БАНКА", warning!, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ReportsDuplicateGtinInsideFile()
    {
        var result = Parse(
            Header("GTIN", "Наименование"),
            Data("4607046150575", "Товар F"),
            Data("4607046150575", "Товар G"));

        Assert.Equal(1, result.NewRows);
        Assert.Equal(1, result.ErrorRows);
        var duplicate = Assert.Single(result.Rows, row => row.Status == CatalogExcelImportStatus.Error);
        Assert.Contains("Дубликат GTIN в файле, строка 2", duplicate.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_DiscoversValidRowsAfterBlankTail()
    {
        var result = Parse(
            Header("GTIN", "Наименование"),
            Data("4607046150575", "Товар H"),
            new object?[] { null, null },
            new object?[] { null, null },
            Data("4607046150576", "Товар I"));

        Assert.Equal(2, result.NewRows);
        Assert.Equal(0, result.EmptyRows);
        Assert.Contains(result.Rows, row => row.Name == "Товар I" && row.Status == CatalogExcelImportStatus.New);
        Assert.DoesNotContain(result.Rows, row => row.Reason == "Пустая строка");
    }

    [Fact]
    public void Parse_IgnoresTrailingBlankRowsWithoutCountingThem()
    {
        var matrix = new List<object?[]>
        {
            Header("GTIN", "Наименование"),
            Data("4607046150575", "Товар P")
        };
        matrix.AddRange(Enumerable.Range(0, 20).Select(_ => new object?[] { null, null }));

        var result = ParseMatrix(matrix);

        Assert.Equal(1, result.NewRows);
        Assert.Equal(0, result.EmptyRows);
        Assert.Single(result.Rows);
        Assert.DoesNotContain(result.Rows, row => row.Reason == "Пустая строка");
    }

    [Fact]
    public void Parse_InternalBlankRowDoesNotStopParsing()
    {
        var result = Parse(
            Header("GTIN", "Наименование"),
            Data("4607046150575", "Товар Q"),
            new object?[] { null, null },
            Data("4607046150576", "Товар R"));

        Assert.Equal(2, result.NewRows);
        Assert.Equal(0, result.EmptyRows);
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void FindLastMeaningfulRowIndex_StopsAtLastDataRow()
    {
        var rows = ToRows(
            Header("GTIN", "Наименование"),
            Data("4607046150575", "Товар S"),
            new object?[] { null, null },
            new object?[] { "   ", string.Empty });

        var map = CatalogExcelImportService.BuildMapFromHeaderRow(rows[0].Cells);
        var lastIndex = CatalogExcelImportService.FindLastMeaningfulRowIndex(rows, headerRowIndex: 0, map);

        Assert.Equal(1, lastIndex);
    }

    [Fact]
    public void Parse_BadGtinProducesRowErrorWithoutFailingWholeFile()
    {
        var result = Parse(
            Header("GTIN", "Наименование"),
            Data("ABC123", "Товар J"),
            Data("4607046150575", "Товар K"));

        Assert.Equal(1, result.NewRows);
        Assert.Equal(1, result.ErrorRows);
        var bad = Assert.Single(result.Rows, row => row.Name == "Товар J");
        Assert.Equal(CatalogExcelImportStatus.Error, bad.Status);
        Assert.Contains("GTIN должен содержать только цифры", bad.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void TryDetectHeader_FindsHeaderNotOnFirstRow()
    {
        var rows = ToRows(
            new object?[] { "Служебная строка", null },
            Header("GTIN", "Наименование"),
            Data("4607046150575", "Товар L"));

        var detected = CatalogExcelImportService.TryDetectHeader(rows, out var headerRowIndex, out var map);

        Assert.True(detected);
        Assert.Equal(1, headerRowIndex);
        Assert.Equal(0, map.GtinColumn);
        Assert.Equal(1, map.NameColumn);
    }

    [Theory]
    [InlineData("12 месяцев", 12)]
    [InlineData("9 мес.", 9)]
    [InlineData("6 месяцев", 6)]
    public void TryParseShelfLifeMonths_ExtractsMonthsFromText(string text, int expected)
    {
        var parsed = CatalogExcelImportService.TryParseShelfLifeMonths(text, text, out var months);

        Assert.True(parsed);
        Assert.Equal(expected, months);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(9.0)]
    public void TryParseShelfLifeMonths_AcceptsNumericCells(object cellValue)
    {
        var parsed = CatalogExcelImportService.TryParseShelfLifeMonths(cellValue, null, out var months);

        Assert.True(parsed);
        Assert.Equal(Convert.ToInt32(cellValue, CultureInfo.InvariantCulture), months);
    }

    [Fact]
    public void BuildMapFromHeaderRow_MapsRealWorldProductListColumns()
    {
        var map = CatalogExcelImportService.BuildMapFromHeaderRow(
        [
            "GTIN",
            "Наименование",
            "Бренд",
            "Объем",
            "Срок годности",
            "Разрешительная\n документация",
            "от",
            "до",
            "Упаковка",
            "Материал",
            "Нанесение кодов",
            "ТН ВЭД"
        ]);

        Assert.Null(map.SkuColumn);
        Assert.Equal(0, map.GtinColumn);
        Assert.Equal(1, map.NameColumn);
        Assert.Equal(2, map.BrandColumn);
        Assert.Equal(3, map.VolumeColumn);
        Assert.Equal(4, map.ShelfLifeColumn);
        Assert.Equal(8, map.TaraColumn);
        Assert.Equal(10, map.CodeApplicationColumn);
        Assert.Equal(11, map.TnvedColumn);
    }

    [Fact]
    public void TryGuessSkuColumnIndex_DoesNotMatchCodeApplicationOrTnved()
    {
        var headers = new[] { "GTIN", "Наименование", "Нанесение кодов", "ТН ВЭД" };

        Assert.Null(CatalogExcelImportService.TryGuessSkuColumnIndex(headers));
    }

    [Fact]
    public void Parse_AcceptsShelfLifeTextValues()
    {
        var result = Parse(
            Header("GTIN", "Наименование", "Срок годности"),
            Data("4607046150575", "Товар 1", "12 месяцев"),
            Data("4607046150576", "Товар 2", "9 мес."),
            Data("4607046150577", "Товар 3", "6 месяцев"));

        Assert.Equal(3, result.NewRows);
        Assert.Equal(0, result.ErrorRows);
        Assert.Equal(12, result.Rows[0].ShelfLifeMonths);
        Assert.Equal(9, result.Rows[1].ShelfLifeMonths);
        Assert.Equal(6, result.Rows[2].ShelfLifeMonths);
    }

    [Fact]
    public void Parse_SkipsExistingGtinFromDatabase()
    {
        var context = EmptyContext(existingItems:
        [
            new Item { Id = 10, Name = "Existing", Gtin = "04607046150575", Barcode = "04607046150575" }
        ]);

        var result = Parse(
            Header("GTIN", "Наименование"),
            Data("4607046150575", "Товар M"),
            context: context);

        var row = Assert.Single(result.Rows);
        Assert.Equal(CatalogExcelImportStatus.Skipped, row.Status);
        Assert.Contains("Дубликат в базе", row.Reason!, StringComparison.Ordinal);
    }

    private static CatalogExcelImportResult Parse(
        object?[] header,
        params object?[][] dataRows)
    {
        return Parse(header, dataRows, EmptyContext());
    }

    private static CatalogExcelImportResult Parse(
        object?[] header,
        object?[][] dataRows,
        CatalogExcelImportContext context)
    {
        var matrix = new[] { header }.Concat(dataRows).ToArray();
        return ParseMatrix(matrix, context);
    }

    private static CatalogExcelImportResult ParseMatrix(
        IReadOnlyList<object?[]> matrix,
        CatalogExcelImportContext? context = null)
    {
        var rows = ToRows(matrix.ToArray());
        var map = CatalogExcelImportService.BuildMapFromHeaderRow(matrix[0]);
        return CatalogExcelImportService.Parse(rows, headerRowIndex: 0, map, context ?? EmptyContext());
    }

    private static CatalogExcelImportResult Parse(
        object?[] header,
        object?[] dataRow,
        CatalogExcelImportContext context)
    {
        return Parse(header, new[] { dataRow }, context);
    }

    private static object?[] Header(params string[] values) => values.Cast<object?>().ToArray();

    private static object?[] Data(params object?[] values) => values;

    private static IReadOnlyList<CatalogExcelRawRow> ToRows(params object?[][] matrix) =>
        matrix.Select((cells, index) => new CatalogExcelRawRow(index + 1, cells)).ToArray();

    private static CatalogExcelImportContext EmptyContext(
        IReadOnlyList<Item>? existingItems = null,
        IReadOnlyList<Tara>? taras = null) =>
        CatalogExcelImportContext.FromCatalog(existingItems ?? [], taras ?? []);
}
