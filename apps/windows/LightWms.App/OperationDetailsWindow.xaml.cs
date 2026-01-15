using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using LightWms.Core.Models;

namespace LightWms.App;

public partial class OperationDetailsWindow : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<Location> _locations = new();
    private readonly ObservableCollection<Partner> _partners = new();
    private readonly ObservableCollection<DocLineDisplay> _docLines = new();
    private readonly long _docId;
    private Doc? _doc;
    private DocLineDisplay? _selectedDocLine;

    public OperationDetailsWindow(AppServices services, long docId)
    {
        _services = services;
        _docId = docId;
        InitializeComponent();

        DocLinesGrid.ItemsSource = _docLines;
        DocFromCombo.ItemsSource = _locations;
        DocToCombo.ItemsSource = _locations;
        DocPartnerCombo.ItemsSource = _partners;

        LoadCatalog();
        LoadDoc();
    }

    private void OperationDetailsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Enter)
        {
            e.Handled = true;
            TryCloseCurrentDoc();
        }
    }

    private void LoadCatalog()
    {
        _locations.Clear();
        foreach (var location in _services.Catalog.GetLocations())
        {
            _locations.Add(location);
        }

        _partners.Clear();
        foreach (var partner in _services.Catalog.GetPartners())
        {
            _partners.Add(partner);
        }
    }

    private void LoadDoc()
    {
        _doc = _services.Documents.GetDoc(_docId);
        if (_doc == null)
        {
            MessageBox.Show("Операция не найдена.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
            return;
        }

        Title = $"Операция: {_doc.DocRef} ({DocTypeMapper.ToDisplayName(_doc.Type)})";
        LoadDocLines();
        UpdateDocView();
    }

    private void LoadDocLines()
    {
        _docLines.Clear();
        foreach (var line in _services.Documents.GetDocLines(_docId))
        {
            _docLines.Add(new DocLineDisplay
            {
                Id = line.Id,
                ItemId = line.ItemId,
                ItemName = line.ItemName,
                QtyBase = line.Qty,
                QtyInput = line.QtyInput,
                UomCode = line.UomCode,
                BaseUom = line.BaseUom,
                QtyDisplay = FormatDocLineQty(line),
                FromLocation = line.FromLocation,
                ToLocation = line.ToLocation
            });
        }

        _selectedDocLine = null;
        UpdateLineButtons();
    }

    private void DocLines_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedDocLine = DocLinesGrid.SelectedItem as DocLineDisplay;
        UpdateLineButtons();
    }

    private void DocLinesGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            e.Handled = true;
            if (_doc?.Status != DocStatus.Draft)
            {
                return;
            }
            DocDeleteLine_Click(sender, new RoutedEventArgs());
        }
    }

    private void DocClose_Click(object sender, RoutedEventArgs e)
    {
        TryCloseCurrentDoc();
    }

    private void TryCloseCurrentDoc()
    {
        if (_doc == null)
        {
            MessageBox.Show("Операция не выбрана.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_doc.Status == DocStatus.Closed)
        {
            MessageBox.Show("Операция уже закрыта.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = _services.Documents.TryCloseDoc(_doc.Id, allowNegative: false);
        if (result.Errors.Count > 0)
        {
            MessageBox.Show(string.Join("\n", result.Errors), "Проверка операции", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (result.Warnings.Count > 0)
        {
            var warningText = "Остаток уйдет в минус:\n" + string.Join("\n", result.Warnings) + "\n\nЗакрыть операцию?";
            var confirm = MessageBox.Show(warningText, "Предупреждение", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            result = _services.Documents.TryCloseDoc(_doc.Id, allowNegative: true);
            if (!result.Success)
            {
                if (result.Errors.Count > 0)
                {
                    MessageBox.Show(string.Join("\n", result.Errors), "Проверка операции", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return;
            }
        }

        if (!result.Success)
        {
            return;
        }

        LoadDoc();
    }

    private void DocAddLine_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureDraftDocSelected())
        {
            return;
        }

        var picker = new ItemPickerWindow(_services)
        {
            Owner = this
        };
        if (picker.ShowDialog() != true || picker.SelectedItem is not Item item)
        {
            return;
        }

        var packagings = _services.Packagings.GetPackagings(item.Id);
        var defaultUomCode = ResolveDefaultUomCode(item, packagings);
        var qtyDialog = new QuantityUomDialog(item.BaseUom, packagings, 1, defaultUomCode)
        {
            Owner = this
        };
        if (qtyDialog.ShowDialog() != true)
        {
            return;
        }

        var qtyBase = qtyDialog.QtyBase;
        var qtyInput = qtyDialog.QtyInput;
        var uomCode = qtyDialog.UomCode;
        if (!TryGetLineLocations(out var fromLocation, out var toLocation))
        {
            return;
        }

        try
        {
            var existing = _services.DataStore.GetDocLines(_doc!.Id)
                .FirstOrDefault(line => line.ItemId == item.Id
                                        && line.FromLocationId == fromLocation?.Id
                                        && line.ToLocationId == toLocation?.Id);
            if (existing != null)
            {
                var sameUom = IsSameUom(existing.UomCode, uomCode);
                var mergedInput = sameUom
                    ? (existing.QtyInput ?? 0) + qtyInput
                    : (double?)null;
                var mergedCode = sameUom ? uomCode : null;
                _services.Documents.UpdateDocLineQty(_doc!.Id, existing.Id, existing.Qty + qtyBase, mergedInput, mergedCode);
            }
            else
            {
                _services.Documents.AddDocLine(_doc!.Id, item!.Id, qtyBase, fromLocation?.Id, toLocation?.Id, qtyInput, uomCode);
            }
            LoadDocLines();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Операция", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DocDeleteLine_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureDraftDocSelected())
        {
            return;
        }

        if (_selectedDocLine == null)
        {
            MessageBox.Show("Выберите строку.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _services.Documents.DeleteDocLine(_doc!.Id, _selectedDocLine.Id);
            LoadDocLines();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Операция", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DocEditLine_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureDraftDocSelected())
        {
            return;
        }

        if (_selectedDocLine == null)
        {
            MessageBox.Show("Выберите строку.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var item = _services.DataStore.FindItemById(_selectedDocLine.ItemId);
        if (item == null)
        {
            MessageBox.Show("Товар не найден.", "Операция", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var packagings = _services.Packagings.GetPackagings(item.Id);
        var defaultQty = _selectedDocLine.QtyInput ?? _selectedDocLine.QtyBase;
        var defaultUom = string.IsNullOrWhiteSpace(_selectedDocLine.UomCode) ? "BASE" : _selectedDocLine.UomCode;
        var qtyDialog = new QuantityUomDialog(item.BaseUom, packagings, defaultQty, defaultUom)
        {
            Owner = this
        };
        if (qtyDialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _services.Documents.UpdateDocLineQty(_doc!.Id, _selectedDocLine.Id, qtyDialog.QtyBase, qtyDialog.QtyInput, qtyDialog.UomCode);
            LoadDocLines();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Операция", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DocHeaderSave_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null)
        {
            MessageBox.Show("Операция не выбрана.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_doc.Status != DocStatus.Draft)
        {
            MessageBox.Show("Операция уже закрыта.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var partnerId = (DocPartnerCombo.SelectedItem as Partner)?.Id;
        try
        {
            _services.Documents.UpdateDocHeader(_doc.Id, partnerId, DocOrderRefBox.Text, DocShippingRefBox.Text);
            LoadDoc();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Операция", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateDocView()
    {
        if (_doc == null)
        {
            return;
        }

        DocInfoText.Text = FormatDocHeader(_doc);
        var isDraft = _doc.Status == DocStatus.Draft;
        DocCloseButton.IsEnabled = isDraft;
        DocHeaderPanel.IsEnabled = isDraft;

        ConfigureHeaderFields(_doc, isDraft);
        DocPartnerCombo.SelectedItem = _partners.FirstOrDefault(p => p.Id == _doc.PartnerId);
        DocOrderRefBox.Text = _doc.OrderRef ?? string.Empty;
        DocShippingRefBox.Text = _doc.ShippingRef ?? string.Empty;
        UpdateLineButtons();

        if (_doc.Status == DocStatus.Draft)
        {
            AddItemButton.Focus();
        }
    }

    private void ConfigureHeaderFields(Doc doc, bool isDraft)
    {
        var showPartner = false;
        var showOrder = false;
        var showShipping = false;
        var showFrom = false;
        var showTo = false;
        var partnerLabel = "Контрагент";
        var fromLabel = "Откуда";
        var toLabel = "Куда";

        switch (doc.Type)
        {
            case DocType.Inbound:
                showPartner = true;
                partnerLabel = "Поставщик";
                showTo = true;
                toLabel = "Место хранения";
                break;
            case DocType.Outbound:
                showPartner = true;
                partnerLabel = "Покупатель";
                showOrder = true;
                showShipping = true;
                showFrom = true;
                fromLabel = "Место хранения";
                break;
            case DocType.Move:
                showFrom = true;
                showTo = true;
                fromLabel = "Откуда";
                toLabel = "Куда";
                break;
            case DocType.WriteOff:
                showFrom = true;
                fromLabel = "Место хранения";
                break;
            case DocType.Inventory:
                showTo = true;
                toLabel = "Место хранения";
                break;
        }

        DocPartnerPanel.Visibility = showPartner ? Visibility.Visible : Visibility.Collapsed;
        DocOrderPanel.Visibility = showOrder ? Visibility.Visible : Visibility.Collapsed;
        DocShippingPanel.Visibility = showShipping ? Visibility.Visible : Visibility.Collapsed;
        DocFromPanel.Visibility = showFrom ? Visibility.Visible : Visibility.Collapsed;
        DocToPanel.Visibility = showTo ? Visibility.Visible : Visibility.Collapsed;

        DocPartnerLabel.Text = partnerLabel;
        DocFromLabel.Text = fromLabel;
        DocToLabel.Text = toLabel;

        if (!showFrom)
        {
            DocFromCombo.SelectedItem = null;
        }

        if (!showTo)
        {
            DocToCombo.SelectedItem = null;
        }

        DocHeaderSaveButton.Visibility = showPartner || showOrder || showShipping
            ? Visibility.Visible
            : Visibility.Collapsed;
        DocHeaderSaveButton.IsEnabled = isDraft;

        DocFromColumn.Visibility = showFrom ? Visibility.Visible : Visibility.Collapsed;
        DocToColumn.Visibility = showTo ? Visibility.Visible : Visibility.Collapsed;
        DocFromColumn.Header = fromLabel;
        DocToColumn.Header = toLabel;
    }

    private static string FormatDocHeader(Doc doc)
    {
        var createdAt = doc.CreatedAt.ToString("dd'/'MM'/'yyyy HH':'mm", CultureInfo.InvariantCulture);
        var closedAt = doc.ClosedAt.HasValue
            ? doc.ClosedAt.Value.ToString("dd'/'MM'/'yyyy HH':'mm", CultureInfo.InvariantCulture)
            : "-";
        return $"Номер: {doc.DocRef} | Тип: {DocTypeMapper.ToDisplayName(doc.Type)} | Статус: {DocTypeMapper.StatusToDisplayName(doc.Status)} | Создан: {createdAt} | Проведена: {closedAt}";
    }

    private void UpdateLineButtons()
    {
        var isDraft = _doc?.Status == DocStatus.Draft;
        AddItemButton.IsEnabled = isDraft;
        EditLineButton.IsEnabled = isDraft && _selectedDocLine != null;
        DeleteLineButton.IsEnabled = isDraft && _selectedDocLine != null;
    }

    private string FormatDocLineQty(DocLineView line)
    {
        var baseUom = string.IsNullOrWhiteSpace(line.BaseUom) ? "шт" : line.BaseUom;
        var baseDisplay = $"{FormatQty(line.Qty)} {baseUom}";

        if (line.QtyInput.HasValue && !string.IsNullOrWhiteSpace(line.UomCode) && !IsBaseUomCode(line.UomCode))
        {
            var packaging = _services.Packagings
                .GetPackagings(line.ItemId, includeInactive: true)
                .FirstOrDefault(p => string.Equals(p.Code, line.UomCode, StringComparison.OrdinalIgnoreCase));
            if (packaging != null)
            {
                var inputDisplay = FormatQty(line.QtyInput.Value);
                return $"{inputDisplay} × {packaging.Name} ({baseDisplay})";
            }
        }

        return baseDisplay;
    }

    private static string ResolveDefaultUomCode(Item item, IReadOnlyList<ItemPackaging> packagings)
    {
        if (item.DefaultPackagingId.HasValue)
        {
            var packaging = packagings.FirstOrDefault(p => p.Id == item.DefaultPackagingId.Value);
            if (packaging != null)
            {
                return packaging.Code;
            }
        }

        return "BASE";
    }

    private static bool IsSameUom(string? left, string? right)
    {
        return string.Equals(NormalizeUomCode(left), NormalizeUomCode(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBaseUomCode(string? code)
    {
        return string.Equals(NormalizeUomCode(code), "BASE", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeUomCode(string? code)
    {
        return string.IsNullOrWhiteSpace(code) ? "BASE" : code.Trim();
    }

    private static string FormatQty(double value)
    {
        return value.ToString("0.###", CultureInfo.CurrentCulture);
    }

    private bool TryGetLineLocations(out Location? fromLocation, out Location? toLocation)
    {
        fromLocation = DocFromCombo.SelectedItem as Location;
        toLocation = DocToCombo.SelectedItem as Location;

        if (_doc == null)
        {
            return false;
        }

        if (_doc.Type == DocType.Inbound || _doc.Type == DocType.Inventory)
        {
            fromLocation = null;
        }
        else if (_doc.Type == DocType.WriteOff || _doc.Type == DocType.Outbound)
        {
            toLocation = null;
        }

        return ValidateLineLocations(_doc, fromLocation, toLocation);
    }

    private bool EnsureDraftDocSelected()
    {
        if (_doc == null)
        {
            MessageBox.Show("Операция не выбрана.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        if (_doc.Status != DocStatus.Draft)
        {
            MessageBox.Show("Операция уже закрыта.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        return true;
    }

    private bool ValidateLineLocations(Doc doc, Location? fromLocation, Location? toLocation)
    {
        switch (doc.Type)
        {
            case DocType.Inbound:
                if (toLocation == null)
                {
                    MessageBox.Show("Для приемки выберите место хранения получателя.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                return true;
            case DocType.WriteOff:
                if (fromLocation == null)
                {
                    MessageBox.Show("Для списания выберите место хранения источника.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                return true;
            case DocType.Outbound:
                if (fromLocation == null)
                {
                    MessageBox.Show("Для отгрузки выберите место хранения источника.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                return true;
            case DocType.Move:
                if (fromLocation == null || toLocation == null)
                {
                    MessageBox.Show("Для перемещения выберите места хранения откуда/куда.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                if (fromLocation.Id == toLocation.Id)
                {
                    MessageBox.Show("Для перемещения места хранения откуда/куда должны быть разными.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                return true;
            default:
                return true;
        }
    }

    private sealed class DocLineDisplay
    {
        public long Id { get; init; }
        public long ItemId { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public double QtyBase { get; init; }
        public double? QtyInput { get; init; }
        public string? UomCode { get; init; }
        public string BaseUom { get; init; } = "шт";
        public string QtyDisplay { get; init; } = string.Empty;
        public string? FromLocation { get; init; }
        public string? ToLocation { get; init; }
    }
}
