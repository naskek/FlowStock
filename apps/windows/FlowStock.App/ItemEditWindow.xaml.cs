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
    private readonly List<ItemTypeOption> _itemTypes = new();

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
            : Array.Empty<Uom>());
        UomCombo.ItemsSource = _uoms;

        _taras.Clear();
        _taras.Add(TaraOption.Empty);
        var taras = _services.WpfCatalogApi.TryGetTaras(out var apiTaras)
            ? apiTaras
            : Array.Empty<Tara>();
        foreach (var tara in taras)
        {
            _taras.Add(new TaraOption(tara.Id, tara.Name));
        }
        TaraCombo.ItemsSource = _taras;

        _itemTypes.Clear();
        _itemTypes.Add(ItemTypeOption.Empty);
        var itemTypes = _services.WpfCatalogApi.TryGetItemTypes(includeInactive: true, out var apiItemTypes)
            ? apiItemTypes
            : Array.Empty<ItemType>();
        foreach (var itemType in itemTypes)
        {
            _itemTypes.Add(new ItemTypeOption(itemType.Id, itemType.Name, itemType.EnableMinStockControl, itemType.EnableHuDistribution, itemType.EnableMarking));
        }
        ItemTypeCombo.ItemsSource = _itemTypes;
    }

    private void FillData()
    {
        if (_item == null)
        {
            Title = "Добавление товара";
            MaxQtyPerHuBox.Text = string.Empty;
            UomCombo.SelectedItem = _uoms.FirstOrDefault(u => string.Equals(u.Name, "шт", StringComparison.OrdinalIgnoreCase))
                                    ?? _uoms.FirstOrDefault();
            TaraCombo.SelectedItem = TaraOption.Empty;
            ItemTypeCombo.SelectedItem = _itemTypes.FirstOrDefault(t => t.Id.HasValue) ?? _itemTypes.FirstOrDefault();
            MinStockQtyBox.Text = string.Empty;
            IsActiveCheck.IsChecked = true;
            UpdateTypeDrivenControls();
            return;
        }

        Title = "Редактирование товара";
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
        UomCombo.SelectedItem = _uoms.FirstOrDefault(u => string.Equals(u.Name, _item.BaseUom, StringComparison.OrdinalIgnoreCase))
                                ?? _uoms.FirstOrDefault();
        TaraCombo.SelectedItem = _taras.FirstOrDefault(t => t.Id == _item.TaraId) ?? TaraOption.Empty;
        ItemTypeCombo.SelectedItem = _itemTypes.FirstOrDefault(t => t.Id == _item.ItemTypeId) ?? _itemTypes.FirstOrDefault();
        MinStockQtyBox.Text = _item.MinStockQty.HasValue
            ? _item.MinStockQty.Value.ToString("0.###", CultureInfo.InvariantCulture)
            : string.Empty;
        IsActiveCheck.IsChecked = _item.IsActive;
        UpdateTypeDrivenControls();
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

        var baseUom = (UomCombo.SelectedItem as Uom)?.Name;
        var taraId = (TaraCombo.SelectedItem as TaraOption)?.Id;
        var itemType = ItemTypeCombo.SelectedItem as ItemTypeOption;
        var itemTypeId = itemType?.Id;
        var isActive = IsActiveCheck.IsChecked != false;

        double? minStockQty = null;
        if (itemType?.EnableMinStockControl == true
            && !TryParseMinStockQty(MinStockQtyBox.Text, out minStockQty))
        {
            return;
        }

        double? maxQtyPerHu;
        if (itemType?.EnableHuDistribution == true)
        {
            if (!TryParseMaxQtyPerHu(MaxQtyPerHuBox.Text, out maxQtyPerHu))
            {
                return;
            }

            if (!maxQtyPerHu.HasValue)
            {
                MessageBox.Show("Для выбранного типа номенклатуры обязательно заполнить поле \"Макс. в 1 HU\".", "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        else
        {
            maxQtyPerHu = _item?.MaxQtyPerHu;
        }

        if (!TryValidateItemIdentifiers(barcode, gtin, _item?.Id))
        {
            return;
        }

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
                IsMarked = false,
                IsActive = isActive,
                ItemTypeId = itemTypeId,
                MinStockQty = minStockQty
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
                    : 0;
                if (itemId <= 0)
                {
                    throw new InvalidOperationException("Сервер не вернул идентификатор нового товара.");
                }
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
                    IsMarked = _item.IsMarked,
                    IsActive = candidate.IsActive,
                    ItemTypeId = candidate.ItemTypeId,
                    MinStockQty = candidate.MinStockQty
                };
                var result = await _services.WpfCatalogApi.TryUpdateItemAsync(updateCandidate).ConfigureAwait(true);
                if (!result.IsSuccess)
                {
                    throw new InvalidOperationException(result.Error ?? "Не удалось обновить товар через сервер.");
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

    private bool TryParseMinStockQty(string? value, out double? minStockQty)
    {
        minStockQty = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var raw = value.Trim();
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed)
            && !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
        {
            MessageBox.Show("Минимальный остаток должен быть числом.", "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (parsed < 0)
        {
            MessageBox.Show("Минимальный остаток не может быть отрицательным.", "Товары", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        minStockQty = parsed;
        return true;
    }

    private bool TryValidateItemIdentifiers(string? barcode, string? gtin, long? currentItemId)
    {
        var items = _services.WpfReadApi.TryGetItems(null, out var apiItems)
            ? apiItems
            : Array.Empty<Item>();

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

        var duplicate = (_services.WpfReadApi.TryGetItems(null, out var apiItems) ? apiItems : Array.Empty<Item>())
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

    private void ItemTypeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateTypeDrivenControls();
    }

    private void GtinBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateMarkingStatusText();
    }

    private void UpdateTypeDrivenControls()
    {
        var selectedType = ItemTypeCombo.SelectedItem as ItemTypeOption;
        var minStockVisibility = selectedType?.EnableMinStockControl == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        MinStockQtyLabel.Visibility = minStockVisibility;
        MinStockQtyBox.Visibility = minStockVisibility;
        MinStockQtyBox.IsEnabled = minStockVisibility == Visibility.Visible;
        if (minStockVisibility != Visibility.Visible)
        {
            MinStockQtyBox.Text = string.Empty;
        }

        var maxQtyPerHuVisibility = selectedType?.EnableHuDistribution == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        MaxQtyPerHuLabel.Visibility = maxQtyPerHuVisibility;
        MaxQtyPerHuBox.Visibility = maxQtyPerHuVisibility;
        MaxQtyPerHuBox.IsEnabled = maxQtyPerHuVisibility == Visibility.Visible;

        UpdateMarkingStatusText();
    }

    private void UpdateMarkingStatusText()
    {
        if (MarkingStatusText == null)
        {
            return;
        }

        var selectedType = ItemTypeCombo.SelectedItem as ItemTypeOption;
        var visibility = selectedType?.EnableMarking == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        MarkingStatusLabel.Visibility = visibility;
        MarkingStatusText.Visibility = visibility;

        if (selectedType?.EnableMarking == true)
        {
            MarkingStatusText.Text = string.IsNullOrWhiteSpace(GtinBox.Text)
                ? "нет, GTIN не заполнен"
                : "да";
            return;
        }

        MarkingStatusText.Text = string.Empty;
    }

    private sealed record TaraOption(long? Id, string Name)
    {
        public static TaraOption Empty { get; } = new(null, "Не выбрана");
    }

    private sealed record ItemTypeOption(long? Id, string Name, bool EnableMinStockControl, bool EnableHuDistribution, bool EnableMarking)
    {
        public static ItemTypeOption Empty { get; } = new(null, "Не выбран", false, false, false);
    }
}
