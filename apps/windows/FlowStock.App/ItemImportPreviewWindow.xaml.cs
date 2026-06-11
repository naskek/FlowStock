using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FlowStock.Core.Models;
using Npgsql;
using WpfComboBox = System.Windows.Controls.ComboBox;

namespace FlowStock.App;

public partial class ItemImportPreviewWindow : Window
{
    private readonly AppServices _services;
    private readonly string _filePath;
    private readonly Dictionary<int, bool> _includeOverrides = new();
    private IReadOnlyList<CatalogExcelRawRow> _rawRows = Array.Empty<CatalogExcelRawRow>();
    private IReadOnlyList<CatalogExcelImportRow> _previewRows = Array.Empty<CatalogExcelImportRow>();
    private int _headerRowIndex = -1;
    private bool _suppressUpdates;

    public ItemImportSummary? ImportSummary { get; private set; }

    public ItemImportPreviewWindow(AppServices services, string filePath)
    {
        _services = services;
        _filePath = filePath;
        InitializeComponent();

        PreviewGrid.CanUserAddRows = false;
        PreviewGrid.AutoGeneratingColumn += PreviewGrid_AutoGeneratingColumn;
        PreviewGrid.CellEditEnding += PreviewGrid_CellEditEnding;

        HeaderRowCheck.Checked += HeaderRowCheck_Changed;
        HeaderRowCheck.Unchecked += HeaderRowCheck_Changed;
        SkuColumnCombo.SelectionChanged += ColumnSelectionChanged;
        GtinColumnCombo.SelectionChanged += ColumnSelectionChanged;
        NameColumnCombo.SelectionChanged += ColumnSelectionChanged;
        BrandColumnCombo.SelectionChanged += ColumnSelectionChanged;
        VolumeColumnCombo.SelectionChanged += ColumnSelectionChanged;
        ShelfLifeColumnCombo.SelectionChanged += ColumnSelectionChanged;
        TaraColumnCombo.SelectionChanged += ColumnSelectionChanged;

        LoadExcelData();
        SetupColumnCombos();
        RebuildPreview();
    }

    private void LoadExcelData()
    {
        using var stream = File.Open(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _rawRows = CatalogExcelImportService.LoadRows(stream);

        if (_rawRows.Count == 0)
        {
            HeaderRowCheck.IsChecked = false;
            ImportStatusText.Text = "Файл пустой.";
            ImportButton.IsEnabled = false;
            return;
        }

        if (CatalogExcelImportService.TryDetectHeader(_rawRows, out var headerRowIndex, out _))
        {
            _headerRowIndex = headerRowIndex;
            HeaderRowCheck.IsChecked = true;
        }
        else
        {
            _headerRowIndex = 0;
            HeaderRowCheck.IsChecked = GuessHeaderRow(_rawRows[0].Cells);
        }
    }

    private void SetupColumnCombos()
    {
        _suppressUpdates = true;
        var options = BuildColumnOptions();
        SkuColumnCombo.ItemsSource = options;
        GtinColumnCombo.ItemsSource = options;
        NameColumnCombo.ItemsSource = options;
        BrandColumnCombo.ItemsSource = options;
        VolumeColumnCombo.ItemsSource = options;
        ShelfLifeColumnCombo.ItemsSource = options;
        TaraColumnCombo.ItemsSource = options;

        ApplyDefaultMappings(options);
        _suppressUpdates = false;
    }

    private void ApplyDefaultMappings(IReadOnlyList<ColumnOption> options)
    {
        var map = _headerRowIndex >= 0 && _headerRowIndex < _rawRows.Count
            ? CatalogExcelImportService.BuildMapFromHeaderRow(_rawRows[_headerRowIndex].Cells)
            : new CatalogExcelColumnMap();

        SetComboSelection(SkuColumnCombo, options, map.SkuColumn ?? CatalogExcelImportService.TryGuessSkuColumnIndex(GetHeaderCells()));
        SetComboSelection(GtinColumnCombo, options, map.GtinColumn ?? GuessColumnIndex(GetHeaderCells(), "gtin"));
        SetComboSelection(NameColumnCombo, options, map.NameColumn ?? GuessColumnIndex(GetHeaderCells(), "наимен", "name") ?? 1);
        SetComboSelection(BrandColumnCombo, options, map.BrandColumn ?? GuessColumnIndex(GetHeaderCells(), "бренд", "brand"));
        SetComboSelection(VolumeColumnCombo, options, map.VolumeColumn ?? GuessColumnIndex(GetHeaderCells(), "объем", "объём", "volume"));
        SetComboSelection(ShelfLifeColumnCombo, options, map.ShelfLifeColumn ?? GuessColumnIndex(GetHeaderCells(), "срок", "годност", "shelf", "expiry"));
        SetComboSelection(TaraColumnCombo, options, map.TaraColumn ?? GuessColumnIndex(GetHeaderCells(), "тара", "упаков", "pack"));
    }

    private string[] GetHeaderCells()
    {
        if (_headerRowIndex < 0 || _headerRowIndex >= _rawRows.Count)
        {
            return Array.Empty<string>();
        }

        return _rawRows[_headerRowIndex].Cells
            .Select(CatalogExcelImportService.GetCellDisplayText)
            .Select(value => value ?? string.Empty)
            .ToArray();
    }

    private void SetComboSelection(WpfComboBox combo, IReadOnlyList<ColumnOption> options, int? index)
    {
        if (index.HasValue)
        {
            combo.SelectedItem = options.FirstOrDefault(option => option.Index == index.Value) ?? options.First();
        }
        else
        {
            combo.SelectedItem = options.First();
        }
    }

    private IReadOnlyList<ColumnOption> BuildColumnOptions()
    {
        var maxCols = _rawRows.Count == 0 ? 0 : _rawRows.Max(row => row.Cells.Length);
        var headers = GetHeaderCells();
        var hasHeader = HeaderRowCheck.IsChecked == true && _headerRowIndex >= 0;
        var list = new List<ColumnOption>
        {
            new(-1, "Не импортировать")
        };

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < maxCols; i++)
        {
            var name = hasHeader && i < headers.Length
                ? CatalogExcelImportService.NormalizeHeader(headers[i])
                : null;
            var label = string.IsNullOrWhiteSpace(name) ? $"Колонка {GetColumnLabel(i)}" : headers[i].Trim();
            if (!usedNames.Add(label))
            {
                label = $"{label} ({GetColumnLabel(i)})";
            }

            list.Add(new ColumnOption(i, $"{label} ({GetColumnLabel(i)})"));
        }

        return list;
    }

    private static string GetColumnLabel(int index)
    {
        var value = index + 1;
        var label = string.Empty;
        while (value > 0)
        {
            var mod = (value - 1) % 26;
            label = (char)('A' + mod) + label;
            value = (value - mod) / 26;
        }

        return label;
    }

    private static int? GuessColumnIndex(string[]? headers, params string[] markers)
    {
        if (headers == null || headers.Length == 0)
        {
            return null;
        }

        for (var i = 0; i < headers.Length; i++)
        {
            var normalized = CatalogExcelImportService.NormalizeHeader(headers[i]);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (markers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal)))
            {
                return i;
            }
        }

        return null;
    }

    private static bool GuessHeaderRow(object?[] cells)
    {
        var joined = string.Join(" ", cells.Select(CatalogExcelImportService.GetCellDisplayText)).ToLowerInvariant();
        return joined.Contains("gtin")
               || joined.Contains("sku")
               || joined.Contains("штрих")
               || joined.Contains("наимен")
               || joined.Contains("brand")
               || joined.Contains("объем")
               || joined.Contains("срок")
               || joined.Contains("тара")
               || joined.Contains("упаков");
    }

    private void HeaderRowCheck_Changed(object sender, RoutedEventArgs e)
    {
        _headerRowIndex = HeaderRowCheck.IsChecked == true
            ? Math.Max(_headerRowIndex, 0)
            : -1;

        _suppressUpdates = true;
        var options = BuildColumnOptions();
        var previous = new[]
        {
            GetSelectedColumnIndex(SkuColumnCombo),
            GetSelectedColumnIndex(GtinColumnCombo),
            GetSelectedColumnIndex(NameColumnCombo),
            GetSelectedColumnIndex(BrandColumnCombo),
            GetSelectedColumnIndex(VolumeColumnCombo),
            GetSelectedColumnIndex(ShelfLifeColumnCombo),
            GetSelectedColumnIndex(TaraColumnCombo)
        };

        SkuColumnCombo.ItemsSource = options;
        GtinColumnCombo.ItemsSource = options;
        NameColumnCombo.ItemsSource = options;
        BrandColumnCombo.ItemsSource = options;
        VolumeColumnCombo.ItemsSource = options;
        ShelfLifeColumnCombo.ItemsSource = options;
        TaraColumnCombo.ItemsSource = options;

        SetComboSelection(SkuColumnCombo, options, previous[0]);
        SetComboSelection(GtinColumnCombo, options, previous[1]);
        SetComboSelection(NameColumnCombo, options, previous[2]);
        SetComboSelection(BrandColumnCombo, options, previous[3]);
        SetComboSelection(VolumeColumnCombo, options, previous[4]);
        SetComboSelection(ShelfLifeColumnCombo, options, previous[5]);
        SetComboSelection(TaraColumnCombo, options, previous[6]);

        _suppressUpdates = false;
        RebuildPreview();
    }

    private void ColumnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUpdates)
        {
            return;
        }

        RebuildPreview();
    }

    private int GetSelectedColumnIndex(WpfComboBox combo)
    {
        return combo.SelectedItem is ColumnOption option ? option.Index : -1;
    }

    private CatalogExcelColumnMap BuildCurrentMap()
    {
        var map = _headerRowIndex >= 0 && _headerRowIndex < _rawRows.Count
            ? CatalogExcelImportService.BuildMapFromHeaderRow(_rawRows[_headerRowIndex].Cells)
            : new CatalogExcelColumnMap();

        var manual = CatalogExcelColumnMap.FromManualSelection(
            GetSelectedColumnIndex(SkuColumnCombo),
            GetSelectedColumnIndex(GtinColumnCombo),
            GetSelectedColumnIndex(NameColumnCombo),
            GetSelectedColumnIndex(BrandColumnCombo),
            GetSelectedColumnIndex(VolumeColumnCombo),
            GetSelectedColumnIndex(ShelfLifeColumnCombo),
            GetSelectedColumnIndex(TaraColumnCombo));

        map.SkuColumn = manual.SkuColumn;
        map.GtinColumn = manual.GtinColumn;
        map.NameColumn = manual.NameColumn;
        map.BrandColumn = manual.BrandColumn;
        map.VolumeColumn = manual.VolumeColumn;
        map.ShelfLifeColumn = manual.ShelfLifeColumn;
        map.TaraColumn = manual.TaraColumn;
        return map;
    }

    private void RebuildPreview()
    {
        if (_rawRows.Count == 0)
        {
            ImportButton.IsEnabled = false;
            return;
        }

        var map = BuildCurrentMap();
        if (!map.NameColumn.HasValue)
        {
            ImportButton.IsEnabled = false;
            ImportStatusText.Text = "Выберите колонку «Наименование».";
            PreviewGrid.ItemsSource = null;
            return;
        }

        var context = CatalogExcelImportContext.FromCatalog(GetExistingItems(), GetTaras());
        var parseResult = CatalogExcelImportService.Parse(
            _rawRows,
            HeaderRowCheck.IsChecked == true ? _headerRowIndex : -1,
            map,
            context);

        _previewRows = parseResult.Rows;

        PreviewGrid.ItemsSource = BuildPreviewTable(_previewRows, GetInclude).DefaultView;
        ImportButton.IsEnabled = _previewRows.Any(row =>
            row.Status == CatalogExcelImportStatus.New && GetInclude(row));
        ImportStatusText.Text =
            $"Строк: {_previewRows.Count} · К импорту: {parseResult.NewRows} · Ошибок: {parseResult.ErrorRows} · Пропущено: {parseResult.SkippedRows} · Пустые: {parseResult.EmptyRows} · Дубликаты: {parseResult.DuplicateRows}";
    }

    private static DataTable BuildPreviewTable(
        IReadOnlyList<CatalogExcelImportRow> rows,
        Func<CatalogExcelImportRow, bool> includeSelector)
    {
        var table = new DataTable();
        table.Columns.Add("Строка", typeof(int)).ReadOnly = true;
        table.Columns.Add("Импорт", typeof(bool));
        table.Columns.Add("Статус", typeof(string)).ReadOnly = true;
        table.Columns.Add("Причина", typeof(string)).ReadOnly = true;
        table.Columns.Add("GTIN", typeof(string)).ReadOnly = true;
        table.Columns.Add("Наименование", typeof(string)).ReadOnly = true;
        table.Columns.Add("Бренд", typeof(string)).ReadOnly = true;
        table.Columns.Add("Объем", typeof(string)).ReadOnly = true;
        table.Columns.Add("Срок годности", typeof(string)).ReadOnly = true;
        table.Columns.Add("Упаковка", typeof(string)).ReadOnly = true;
        table.Columns.Add("Разрешительная документация", typeof(string)).ReadOnly = true;
        table.Columns.Add("от", typeof(string)).ReadOnly = true;
        table.Columns.Add("до", typeof(string)).ReadOnly = true;
        table.Columns.Add("Материал", typeof(string)).ReadOnly = true;
        table.Columns.Add("Нанесение кодов", typeof(string)).ReadOnly = true;
        table.Columns.Add("ТН ВЭД", typeof(string)).ReadOnly = true;
        table.Columns.Add("ShortName", typeof(string)).ReadOnly = true;
        table.Columns.Add("Условия хранения", typeof(string)).ReadOnly = true;
        table.Columns.Add("ТУ", typeof(string)).ReadOnly = true;
        table.Columns.Add("Состав", typeof(string)).ReadOnly = true;

        foreach (var row in rows)
        {
            var dataRow = table.NewRow();
            dataRow["Строка"] = row.ExcelRowNumber;
            dataRow["Импорт"] = includeSelector(row);
            dataRow["Статус"] = FormatStatus(row.Status);
            dataRow["Причина"] = row.Reason ?? string.Empty;
            dataRow["GTIN"] = row.Gtin ?? string.Empty;
            dataRow["Наименование"] = row.Name ?? string.Empty;
            dataRow["Бренд"] = row.Brand ?? string.Empty;
            dataRow["Объем"] = row.Volume ?? string.Empty;
            dataRow["Срок годности"] = row.ShelfLifeMonths?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            dataRow["Упаковка"] = row.TaraName ?? string.Empty;
            dataRow["Разрешительная документация"] = row.PermitDocumentation ?? string.Empty;
            dataRow["от"] = FormatDate(row.PermitFrom);
            dataRow["до"] = FormatDate(row.PermitTo);
            dataRow["Материал"] = row.Material ?? string.Empty;
            dataRow["Нанесение кодов"] = row.CodeApplication ?? string.Empty;
            dataRow["ТН ВЭД"] = row.Tnved ?? string.Empty;
            dataRow["ShortName"] = row.ShortName ?? string.Empty;
            dataRow["Условия хранения"] = row.StorageConditions ?? string.Empty;
            dataRow["ТУ"] = row.TechnicalConditions ?? string.Empty;
            dataRow["Состав"] = row.Composition ?? string.Empty;
            table.Rows.Add(dataRow);
        }

        return table;
    }

    private static string FormatStatus(CatalogExcelImportStatus status) =>
        status switch
        {
            CatalogExcelImportStatus.New => "NEW",
            CatalogExcelImportStatus.Error => "ERROR",
            CatalogExcelImportStatus.Skipped => "SKIPPED",
            _ => status.ToString()
        };

    private static string FormatDate(DateTime? value) =>
        value?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? string.Empty;

    private void PreviewGrid_AutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (e.PropertyName is "Строка" or "Импорт")
        {
            e.Column.Width = new DataGridLength(70);
            e.Column.IsReadOnly = e.PropertyName == "Строка";
            return;
        }

        if (e.PropertyName is "Статус")
        {
            e.Column.Width = new DataGridLength(80);
            e.Column.IsReadOnly = true;
            return;
        }

        if (e.PropertyName is "Причина")
        {
            e.Column.Width = new DataGridLength(220);
            e.Column.IsReadOnly = true;
            return;
        }

        e.Column.IsReadOnly = true;
    }

    private void PreviewGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.Column.Header?.ToString() != "Импорт")
        {
            return;
        }

        if (e.Row.Item is not DataRowView rowView)
        {
            return;
        }

        var rowNumber = rowView.Row.Field<int>("Строка");
        if (!string.Equals(rowView.Row.Field<string>("Статус"), "NEW", StringComparison.Ordinal))
        {
            e.Cancel = true;
            return;
        }

        var value = rowView.Row.Field<bool>("Импорт");
        _includeOverrides[rowNumber] = value;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_previewRows.Count == 0)
        {
            return;
        }

        var created = 0;
        var duplicates = 0;
        var emptyRows = 0;
        var invalidRows = 0;
        var errors = 0;

        foreach (var row in _previewRows)
        {
            if (row.Status == CatalogExcelImportStatus.Skipped && row.Reason == "Пустая строка")
            {
                emptyRows++;
                continue;
            }

            if (row.Status != CatalogExcelImportStatus.New || !GetInclude(row))
            {
                if (row.Reason != null && row.Reason.Contains("Дубликат", StringComparison.OrdinalIgnoreCase))
                {
                    duplicates++;
                }
                else if (row.Status == CatalogExcelImportStatus.Error)
                {
                    invalidRows++;
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(row.Name) || string.IsNullOrWhiteSpace(row.Barcode))
            {
                invalidRows++;
                continue;
            }

            try
            {
                var candidate = new Item
                {
                    Name = row.Name,
                    Barcode = row.Barcode,
                    Gtin = row.Gtin,
                    BaseUom = "шт",
                    Brand = row.Brand,
                    Volume = row.Volume,
                    ShelfLifeMonths = row.ShelfLifeMonths,
                    TaraId = row.TaraId,
                    IsMarked = false
                };
                var result = _services.WpfCatalogApi.TryCreateItemAsync(candidate)
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
                if (!result.IsSuccess)
                {
                    if (string.Equals(result.Error, "ITEM_ALREADY_EXISTS", StringComparison.OrdinalIgnoreCase))
                    {
                        duplicates++;
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(result.Error))
                    {
                        throw new InvalidOperationException(result.Error);
                    }

                    throw new InvalidOperationException("Не удалось создать товар через сервер.");
                }

                created++;
            }
            catch (ArgumentException)
            {
                invalidRows++;
            }
            catch (PostgresException ex) when (IsPostgresConstraint(ex))
            {
                duplicates++;
            }
            catch
            {
                errors++;
            }
        }

        ImportSummary = new ItemImportSummary(created, duplicates, emptyRows, invalidRows, errors);
        DialogResult = true;
        Close();
    }

    private bool GetInclude(CatalogExcelImportRow row)
    {
        if (row.Status != CatalogExcelImportStatus.New)
        {
            return false;
        }

        return _includeOverrides.TryGetValue(row.ExcelRowNumber, out var include)
            ? include
            : row.Include;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static bool IsPostgresConstraint(PostgresException ex) => ex.SqlState == "23505";

    private IReadOnlyList<Item> GetExistingItems() =>
        _services.WpfReadApi.TryGetItems(null, out var apiItems)
            ? apiItems
            : Array.Empty<Item>();

    private IReadOnlyList<Tara> GetTaras() =>
        _services.WpfCatalogApi.TryGetTaras(out var apiTaras)
            ? apiTaras
            : Array.Empty<Tara>();

    private sealed class ColumnOption
    {
        public int Index { get; }
        public string Name { get; }

        public ColumnOption(int index, string name)
        {
            Index = index;
            Name = name;
        }

        public override string ToString() => Name;
    }
}

public sealed record ItemImportSummary(int Created, int Duplicates, int EmptyRows, int InvalidRows, int Errors);
