using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using ExcelDataReader;
using FlowStock.Core.Models;
using Npgsql;
using WpfComboBox = System.Windows.Controls.ComboBox;

namespace FlowStock.App;

public partial class ItemImportPreviewWindow : Window
{
    private readonly AppServices _services;
    private readonly string _filePath;
    private readonly List<string[]> _rows = new();
    private readonly Dictionary<int, bool> _includeOverrides = new();
    private readonly List<PreviewRow> _previewRows = new();
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
        EnsureExcelEncoding();
        using var stream = File.Open(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream);

        do
        {
            while (reader.Read())
            {
                var row = new string[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[i] = ReadExcelCell(reader, i) ?? string.Empty;
                }
                _rows.Add(row);
            }

            break;
        } while (reader.NextResult());

        if (_rows.Count == 0)
        {
            HeaderRowCheck.IsChecked = false;
            ImportStatusText.Text = "Файл пустой.";
            ImportButton.IsEnabled = false;
            return;
        }

        HeaderRowCheck.IsChecked = GuessHeaderRow(_rows[0]);
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
        var headerValues = HeaderRowCheck.IsChecked == true ? _rows[0] : null;

        SetComboSelection(SkuColumnCombo, options, GuessColumnIndex(headerValues, "sku", "штрих", "barcode", "код") ?? 0);
        SetComboSelection(GtinColumnCombo, options, GuessColumnIndex(headerValues, "gtin"));
        SetComboSelection(NameColumnCombo, options, GuessColumnIndex(headerValues, "наимен", "name") ?? 1);
        SetComboSelection(BrandColumnCombo, options, GuessColumnIndex(headerValues, "бренд", "brand"));
        SetComboSelection(VolumeColumnCombo, options, GuessColumnIndex(headerValues, "объем", "объём", "volume"));
        SetComboSelection(ShelfLifeColumnCombo, options, GuessColumnIndex(headerValues, "срок", "годност", "shelf", "expiry"));
        SetComboSelection(TaraColumnCombo, options, GuessColumnIndex(headerValues, "тара", "упаков", "pack"));
    }

    private void SetComboSelection(WpfComboBox combo, IReadOnlyList<ColumnOption> options, int? index)
    {
        if (combo == null)
        {
            return;
        }

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
        var maxCols = _rows.Count == 0 ? 0 : _rows.Max(row => row.Length);
        var hasHeader = HeaderRowCheck.IsChecked == true;
        var headers = hasHeader && _rows.Count > 0 ? _rows[0] : Array.Empty<string>();
        var list = new List<ColumnOption>
        {
            new(-1, "Не импортировать")
        };

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < maxCols; i++)
        {
            var name = hasHeader && i < headers.Length ? NormalizeIdentifier(headers[i]) : null;
            var label = string.IsNullOrWhiteSpace(name) ? $"Колонка {GetColumnLabel(i)}" : name;
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
            var value = headers[i];
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }
            var lower = value.Trim().ToLowerInvariant();
            if (markers.Any(marker => lower.Contains(marker)))
            {
                return i;
            }
        }

        return null;
    }

    private static bool GuessHeaderRow(string[] row)
    {
        if (row.Length == 0)
        {
            return false;
        }

        var joined = string.Join(" ", row).ToLowerInvariant();
        return joined.Contains("sku")
               || joined.Contains("gtin")
               || joined.Contains("штрих")
               || joined.Contains("наимен")
               || joined.Contains("brand")
               || joined.Contains("объем")
               || joined.Contains("срок")
               || joined.Contains("тара");
    }

    private void HeaderRowCheck_Changed(object sender, RoutedEventArgs e)
    {
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

    private string? GetValue(string[] row, int index)
    {
        if (index < 0 || index >= row.Length)
        {
            return null;
        }

        return NormalizeIdentifier(row[index]);
    }

    private void RebuildPreview()
    {
        if (_rows.Count == 0)
        {
            ImportButton.IsEnabled = false;
            return;
        }

        _previewRows.Clear();
        var startIndex = HeaderRowCheck.IsChecked == true ? 1 : 0;
        var skuIndex = GetSelectedColumnIndex(SkuColumnCombo);
        var gtinIndex = GetSelectedColumnIndex(GtinColumnCombo);
        var nameIndex = GetSelectedColumnIndex(NameColumnCombo);
        var brandIndex = GetSelectedColumnIndex(BrandColumnCombo);
        var volumeIndex = GetSelectedColumnIndex(VolumeColumnCombo);
        var shelfIndex = GetSelectedColumnIndex(ShelfLifeColumnCombo);
        var taraIndex = GetSelectedColumnIndex(TaraColumnCombo);

        var existing = _services.Catalog.GetItems(null);
        var existingCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in existing)
        {
            AddBarcodeVariants(existingCodes, item.Barcode);
            AddBarcodeVariants(existingCodes, item.Gtin);
        }
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var taras = _services.Catalog.GetTaras();
        var tarasByName = taras.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        var emptyRows = 0;
        var invalidRows = 0;
        var duplicates = 0;
        var validRows = 0;

        for (var i = startIndex; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var rowNumber = i + 1;
            var hasAny = row.Any(value => !string.IsNullOrWhiteSpace(value));
            if (!hasAny)
            {
                emptyRows++;
                _previewRows.Add(new PreviewRow(rowNumber, row, false, "Пустая строка"));
                continue;
            }

            var name = GetValue(row, nameIndex);
            var barcode = NormalizeImportedBarcode(GetValue(row, skuIndex));
            var gtin = NormalizeImportedBarcode(GetValue(row, gtinIndex));
            var brand = GetValue(row, brandIndex);
            var volume = GetValue(row, volumeIndex);
            var tara = GetValue(row, taraIndex);
            var shelfLife = GetValue(row, shelfIndex);

            if (string.IsNullOrWhiteSpace(barcode) && !string.IsNullOrWhiteSpace(gtin))
            {
                barcode = gtin;
            }

            string? error = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "Нет наименования";
            }
            else if (string.IsNullOrWhiteSpace(barcode))
            {
                error = "Нет SKU / GTIN";
            }
            else if (!string.IsNullOrWhiteSpace(shelfLife) && (!int.TryParse(shelfLife, NumberStyles.Integer, CultureInfo.InvariantCulture, out var shelfValue) || shelfValue <= 0))
            {
                error = "Срок годности: целое число месяцев";
            }
            else if (!string.IsNullOrWhiteSpace(tara) && !tarasByName.ContainsKey(tara))
            {
                error = "Тара не найдена";
            }

            if (error == null)
            {
                if (IsBarcodeSeen(seenCodes, barcode))
                {
                    error = "Дубликат SKU / GTIN (в файле)";
                    duplicates++;
                }
                else if (existingCodes.Contains(barcode!))
                {
                    error = "Дубликат SKU / GTIN (в базе)";
                    duplicates++;
                }
                else
                {
                    AddBarcodeVariants(seenCodes, barcode);
                }
            }

            if (error == null && !string.IsNullOrWhiteSpace(gtin) && !string.Equals(gtin, barcode, StringComparison.OrdinalIgnoreCase))
            {
                if (IsBarcodeSeen(seenCodes, gtin))
                {
                    error = "Дубликат GTIN (в файле)";
                    duplicates++;
                }
                else if (existingCodes.Contains(gtin))
                {
                    error = "Дубликат GTIN (в базе)";
                    duplicates++;
                }
                else
                {
                    AddBarcodeVariants(seenCodes, gtin);
                }
            }

            var include = error == null;
            if (_includeOverrides.TryGetValue(rowNumber, out var overrideValue) && error == null)
            {
                include = overrideValue;
            }

            if (error != null)
            {
                invalidRows++;
            }
            else if (include)
            {
                validRows++;
            }

            _previewRows.Add(new PreviewRow(rowNumber, row, include, error, barcode, gtin, name, brand, volume, shelfLife, tara));
        }

        PreviewGrid.ItemsSource = BuildPreviewTable(_previewRows).DefaultView;
        ImportButton.IsEnabled = validRows > 0;
        ImportStatusText.Text = $"Строк: {(_previewRows.Count)} · К импорту: {validRows} · Ошибок: {invalidRows} · Пустые: {emptyRows} · Дубликаты: {duplicates}";
    }

    private DataTable BuildPreviewTable(IReadOnlyList<PreviewRow> rows)
    {
        var table = new DataTable();
        var maxCols = _rows.Count == 0 ? 0 : _rows.Max(row => row.Length);
        var hasHeader = HeaderRowCheck.IsChecked == true;
        var headers = hasHeader && _rows.Count > 0 ? _rows[0] : Array.Empty<string>();

        table.Columns.Add("Строка", typeof(int)).ReadOnly = true;
        table.Columns.Add("Импорт", typeof(bool));
        table.Columns.Add("Ошибка", typeof(string)).ReadOnly = true;

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columnNames = new List<string>();
        for (var i = 0; i < maxCols; i++)
        {
            var name = hasHeader && i < headers.Length ? NormalizeIdentifier(headers[i]) : null;
            var label = string.IsNullOrWhiteSpace(name) ? $"Колонка {GetColumnLabel(i)}" : name;
            if (!usedNames.Add(label))
            {
                label = $"{label} ({GetColumnLabel(i)})";
            }
            columnNames.Add(label);
            table.Columns.Add(label, typeof(string)).ReadOnly = true;
        }

        foreach (var row in rows)
        {
            var dataRow = table.NewRow();
            dataRow["Строка"] = row.RowNumber;
            dataRow["Импорт"] = row.Include;
            dataRow["Ошибка"] = row.Error ?? string.Empty;
            for (var i = 0; i < columnNames.Count; i++)
            {
                dataRow[columnNames[i]] = i < row.Values.Length ? row.Values[i] : string.Empty;
            }
            table.Rows.Add(dataRow);
        }

        return table;
    }

    private void PreviewGrid_AutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (e.PropertyName == "Строка")
        {
            e.Column.Width = new DataGridLength(70);
            e.Column.IsReadOnly = true;
            return;
        }

        if (e.PropertyName == "Импорт")
        {
            e.Column.Width = new DataGridLength(70);
            e.Column.IsReadOnly = false;
            return;
        }

        if (e.PropertyName == "Ошибка")
        {
            e.Column.Width = new DataGridLength(200);
            e.Column.IsReadOnly = true;
            return;
        }

        e.Column.IsReadOnly = true;
    }

    private void PreviewGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
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
        var value = rowView.Row.Field<bool>("Импорт");
        _includeOverrides[rowNumber] = value;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_previewRows.Count == 0)
        {
            return;
        }

        var skuIndex = GetSelectedColumnIndex(SkuColumnCombo);
        var gtinIndex = GetSelectedColumnIndex(GtinColumnCombo);
        var nameIndex = GetSelectedColumnIndex(NameColumnCombo);
        var brandIndex = GetSelectedColumnIndex(BrandColumnCombo);
        var volumeIndex = GetSelectedColumnIndex(VolumeColumnCombo);
        var shelfIndex = GetSelectedColumnIndex(ShelfLifeColumnCombo);
        var taraIndex = GetSelectedColumnIndex(TaraColumnCombo);

        var taras = _services.Catalog.GetTaras();
        var tarasByName = taras.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        var created = 0;
        var duplicates = 0;
        var emptyRows = 0;
        var invalidRows = 0;
        var errors = 0;

        foreach (var row in _previewRows)
        {
            if (!row.Include)
            {
                if (row.Error == "Пустая строка")
                {
                    emptyRows++;
                }
                else if (row.Error != null && row.Error.Contains("Дубликат", StringComparison.OrdinalIgnoreCase))
                {
                    duplicates++;
                }
                else if (!string.IsNullOrWhiteSpace(row.Error))
                {
                    invalidRows++;
                }
                continue;
            }

            var raw = row.Values;
            var name = NormalizeIdentifier(GetValue(raw, nameIndex));
            var barcode = NormalizeImportedBarcode(GetValue(raw, skuIndex));
            var gtin = NormalizeImportedBarcode(GetValue(raw, gtinIndex));
            var brand = NormalizeIdentifier(GetValue(raw, brandIndex));
            var volume = NormalizeIdentifier(GetValue(raw, volumeIndex));
            var shelfLife = NormalizeIdentifier(GetValue(raw, shelfIndex));
            var taraName = NormalizeIdentifier(GetValue(raw, taraIndex));

            if (string.IsNullOrWhiteSpace(barcode) && !string.IsNullOrWhiteSpace(gtin))
            {
                barcode = gtin;
            }

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(barcode))
            {
                invalidRows++;
                continue;
            }

            int? shelfLifeMonths = null;
            if (!string.IsNullOrWhiteSpace(shelfLife))
            {
                if (!int.TryParse(shelfLife, NumberStyles.Integer, CultureInfo.InvariantCulture, out var shelfValue) || shelfValue <= 0)
                {
                    invalidRows++;
                    continue;
                }
                shelfLifeMonths = shelfValue;
            }

            long? taraId = null;
            if (!string.IsNullOrWhiteSpace(taraName))
            {
                if (!tarasByName.TryGetValue(taraName, out var tara))
                {
                    invalidRows++;
                    continue;
                }
                taraId = tara.Id;
            }

            try
            {
                _services.Catalog.CreateItem(name, barcode, gtin, null, brand, volume, shelfLifeMonths, taraId);
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

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static bool IsPostgresConstraint(PostgresException ex)
    {
        return ex.SqlState == "23505";
    }

    private static void EnsureExcelEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static string? ReadExcelCell(IExcelDataReader reader, int index)
    {
        if (index < 0 || index >= reader.FieldCount)
        {
            return null;
        }

        var value = reader.GetValue(index);
        if (value == null)
        {
            return null;
        }

        if (value is double number)
        {
            return number.ToString("0", CultureInfo.InvariantCulture);
        }

        if (value is float numberFloat)
        {
            return numberFloat.ToString("0", CultureInfo.InvariantCulture);
        }

        if (value is decimal numberDecimal)
        {
            return numberDecimal.ToString("0", CultureInfo.InvariantCulture);
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static string? NormalizeIdentifier(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool IsDigitsOnly(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (!char.IsDigit(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static string? NormalizeImportedBarcode(string? value)
    {
        var trimmed = NormalizeIdentifier(value);
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

    private static void AddBarcodeVariants(HashSet<string> target, string? code)
    {
        var trimmed = NormalizeIdentifier(code);
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
        else if (trimmed.Length == 14 && trimmed.StartsWith("0", StringComparison.Ordinal))
        {
            target.Add(trimmed.Substring(1));
        }
    }

    private static bool IsBarcodeSeen(HashSet<string> seen, string? code)
    {
        var trimmed = NormalizeIdentifier(code);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        if (seen.Contains(trimmed))
        {
            return true;
        }

        if (!IsDigitsOnly(trimmed))
        {
            return false;
        }

        if (trimmed.Length == 13)
        {
            return seen.Contains("0" + trimmed);
        }

        if (trimmed.Length == 14 && trimmed.StartsWith("0", StringComparison.Ordinal))
        {
            return seen.Contains(trimmed.Substring(1));
        }

        return false;
    }

    private sealed class ColumnOption
    {
        public int Index { get; }
        public string Name { get; }

        public ColumnOption(int index, string name)
        {
            Index = index;
            Name = name;
        }

        public override string ToString()
        {
            return Name;
        }
    }

    private sealed class PreviewRow
    {
        public int RowNumber { get; }
        public string[] Values { get; }
        public bool Include { get; }
        public string? Error { get; }
        public string? Barcode { get; }
        public string? Gtin { get; }
        public string? Name { get; }
        public string? Brand { get; }
        public string? Volume { get; }
        public string? ShelfLife { get; }
        public string? Tara { get; }

        public PreviewRow(int rowNumber, string[] values, bool include, string? error,
            string? barcode = null,
            string? gtin = null,
            string? name = null,
            string? brand = null,
            string? volume = null,
            string? shelfLife = null,
            string? tara = null)
        {
            RowNumber = rowNumber;
            Values = values;
            Include = include;
            Error = error;
            Barcode = barcode;
            Gtin = gtin;
            Name = name;
            Brand = brand;
            Volume = volume;
            ShelfLife = shelfLife;
            Tara = tara;
        }
    }
}

public sealed record ItemImportSummary(int Created, int Duplicates, int EmptyRows, int InvalidRows, int Errors);
