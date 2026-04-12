using System.Globalization;
using System.Windows;
using FlowStock.Core.Models;
using Npgsql;

namespace FlowStock.App;

public partial class ItemEditWindow : Window
{
    private readonly AppServices _services;
    private readonly Item? _item;
    private readonly List<Uom> _uoms = new();
    private readonly List<TaraOption> _taras = new();

    public long? SavedItemId { get; private set; }

    public ItemEditWindow(AppServices services, Item? item = null)
    {
        _services = services;
        _item = item;

        InitializeComponent();
        LoadLookups();
        FillData();
    }

    private void LoadLookups()
    {
        _uoms.Clear();
        _uoms.AddRange(_services.WpfCatalogApi.TryGetUoms(out var apiUoms)
            ? apiUoms
            : _services.Catalog.GetUoms());
        UomCombo.ItemsSource = _uoms;

        _taras.Clear();
        _taras.Add(TaraOption.Empty);
        var taras = _services.WpfCatalogApi.TryGetTaras(out var apiTaras)
            ? apiTaras
            : _services.Catalog.GetTaras();
        foreach (var tara in taras)
        {
            _taras.Add(new TaraOption(tara.Id, tara.Name));
        }
        TaraCombo.ItemsSource = _taras;
    }

    private void FillData()
    {
        if (_item == null)
        {
            Title = "Добавление товара";
            IdBox.Text = "(будет присвоен)";
            MaxQtyPerHuBox.Text = string.Empty;
            UomCombo.SelectedItem = _uoms.FirstOrDefault(u => string.Equals(u.Name, "шт", StringComparison.OrdinalIgnoreCase))
                                    ?? _uoms.FirstOrDefault();
            TaraCombo.SelectedItem = TaraOption.Empty;
            return;
        }

        Title = $"Редактирование товара #{_item.Id}";
        IdBox.Text = _item.Id.ToString(CultureInfo.InvariantCulture);
        NameBox.Text = _item.Name;
        BarcodeBox.Text = _item.Barcode ?? string.Empty;
        GtinBox.Text = _item.Gtin ?? string.Empty;
        BrandBox.Text = _item.Brand ?? string.Empty;
        VolumeBox.Text = _item.Volume ?? string.Empty;
        ShelfLifeBox.Text = _item.ShelfLifeMonths.HasValue
            ? _item.ShelfLifeMonths.Value.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        MaxQtyPerHuBox.Text = _item.MaxQtyPerHu.HasValue
            ? _item.MaxQtyPerHu.Value.ToString("0.###", CultureInfo.InvariantCulture)
            : string.Empty;
        MarkedCheck.IsChecked = _item.IsMarked;
        UomCombo.SelectedItem = _uoms.FirstOrDefault(u => string.Equals(u.Name, _item.BaseUom, StringComparison.OrdinalIgnoreCase))
                                ?? _uoms.FirstOrDefault();
        TaraCombo.SelectedItem = _taras.FirstOrDefault(t => t.Id == _item.TaraId) ?? TaraOption.Empty;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? string.Empty;
        var barcode = Normalize(BarcodeBox.Text);
        var gtin = Normalize(GtinBox.Text);
        var brand = Normalize(BrandBox.Text);
        var volume = Normalize(VolumeBox.Text);
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Введите наименование товара.", "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(barcode) && !string.IsNullOrWhiteSpace(gtin))
        {
            barcode = gtin;
        }

        if (string.IsNullOrWhiteSpace(barcode))
        {
            MessageBox.Show("Введите SKU / штрихкод или GTIN.", "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParseShelfLifeMonths(ShelfLifeBox.Text, out var shelfLifeMonths))
        {
            return;
        }

        if (!TryParseMaxQtyPerHu(MaxQtyPerHuBox.Text, out var maxQtyPerHu))
        {
            return;
        }

        if (!TryValidateItemIdentifiers(barcode, gtin, _item?.Id))
        {
            return;
        }

        var baseUom = (UomCombo.SelectedItem as Uom)?.Name;
        var taraId = (TaraCombo.SelectedItem as TaraOption)?.Id;
        var isMarked = _item?.IsMarked ?? false;

        try
        {
            var candidate = new Item
            {
                Id = _item?.Id ?? 0,
                Name = name,
                Barcode = barcode,
                Gtin = gtin,
                BaseUom = string.IsNullOrWhiteSpace(baseUom) ? "шт" : baseUom.Trim(),
                Brand = brand,
                Volume = volume,
                ShelfLifeMonths = shelfLifeMonths,
                MaxQtyPerHu = maxQtyPerHu,
                TaraId = taraId,
                IsMarked = isMarked
            };

            if (_item == null)
            {
                var result = await _services.WpfCatalogApi.TryCreateItemAsync(candidate).ConfigureAwait(true);
                if (!result.IsSuccess && !string.IsNullOrWhiteSpace(result.Error))
                {
                    throw new InvalidOperationException(result.Error);
                }

                var itemId = result.IsSuccess
                    ? (result.CreatedId ?? 0)
                    : _services.Catalog.CreateItem(name, barcode, gtin, baseUom, brand, volume, shelfLifeMonths, taraId, isMarked, maxQtyPerHu);
                SavedItemId = itemId;
            }
            else
            {
                var updateCandidate = new Item
                {
                    Id = _item.Id,
                    Name = candidate.Name,
                    Barcode = candidate.Barcode,
                    Gtin = candidate.Gtin,
                    BaseUom = candidate.BaseUom,
                    Brand = candidate.Brand,
                    Volume = candidate.Volume,
                    ShelfLifeMonths = candidate.ShelfLifeMonths,
                    MaxQtyPerHu = candidate.MaxQtyPerHu,
                    TaraId = candidate.TaraId,
                    IsMarked = candidate.IsMarked
                };
                var result = await _services.WpfCatalogApi.TryUpdateItemAsync(updateCandidate).ConfigureAwait(true);
                if (!result.IsSuccess)
                {
                    if (!string.IsNullOrWhiteSpace(result.Error))
                    {
                        throw new InvalidOperationException(result.Error);
                    }

                    _services.Catalog.UpdateItem(_item.Id, name, barcode, gtin, baseUom, brand, volume, shelfLifeMonths, taraId, isMarked, maxQtyPerHu);
                }

                SavedItemId = _item.Id;
            }

            DialogResult = true;
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (PostgresException ex) when (IsPostgresConstraint(ex))
        {
            if (TryShowItemBarcodeDuplicate(barcode, _item?.Id))
            {
                return;
            }

            MessageBox.Show("Не удалось сохранить товар. Нарушено ограничение базы данных.", "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Товары", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool TryParseShelfLifeMonths(string? value, out int? months)
    {
        months = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            MessageBox.Show("Срок годности должен быть целым числом месяцев.", "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        months = parsed;
        return true;
    }

    private bool TryParseMaxQtyPerHu(string? value, out double? maxQtyPerHu)
    {
        maxQtyPerHu = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var raw = value.Trim();
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed)
            && !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
        {
            MessageBox.Show("Максимум на HU должен быть числом.", "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (parsed <= 0)
        {
            MessageBox.Show("Максимум на HU должен быть больше 0.", "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        maxQtyPerHu = parsed;
        return true;
    }

    private bool TryValidateItemIdentifiers(string? barcode, string? gtin, long? currentItemId)
    {
        var items = _services.WpfReadApi.TryGetItems(null, out var apiItems)
            ? apiItems
            : _services.Catalog.GetItems(null);

        if (!string.IsNullOrWhiteSpace(barcode))
        {
            var duplicate = items.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Barcode)
                                                         && string.Equals(item.Barcode, barcode, StringComparison.OrdinalIgnoreCase)
                                                         && (!currentItemId.HasValue || item.Id != currentItemId.Value));
            if (duplicate != null)
            {
                MessageBox.Show($"Товар с таким SKU / штрихкодом уже существует: {duplicate.Name}. Продолжить нельзя.",
                    "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(gtin))
        {
            var duplicate = items.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Gtin)
                                                         && string.Equals(item.Gtin, gtin, StringComparison.OrdinalIgnoreCase)
                                                         && (!currentItemId.HasValue || item.Id != currentItemId.Value));
            if (duplicate != null)
            {
                MessageBox.Show($"Товар с таким GTIN уже существует: {duplicate.Name}. Продолжить нельзя.",
                    "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        return true;
    }

    private bool TryShowItemBarcodeDuplicate(string? barcode, long? currentItemId)
    {
        if (string.IsNullOrWhiteSpace(barcode))
        {
            return false;
        }

        var duplicate = (_services.WpfReadApi.TryGetItems(null, out var apiItems) ? apiItems : _services.Catalog.GetItems(null))
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Barcode)
                                    && string.Equals(item.Barcode, barcode, StringComparison.OrdinalIgnoreCase)
                                    && (!currentItemId.HasValue || item.Id != currentItemId.Value));
        if (duplicate == null)
        {
            return false;
        }

        MessageBox.Show($"Товар с таким SKU / штрихкодом уже существует: {duplicate.Name}. Продолжить нельзя.",
            "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
        return true;
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool IsPostgresConstraint(PostgresException ex)
    {
        return string.Equals(ex.SqlState, PostgresErrorCodes.UniqueViolation, StringComparison.Ordinal);
    }

    private sealed record TaraOption(long? Id, string Name)
    {
        public static TaraOption Empty { get; } = new(null, "Не выбрана");
    }
}
