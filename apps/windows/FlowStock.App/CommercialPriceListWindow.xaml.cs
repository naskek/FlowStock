using System.IO;
using System.Windows;
using System.Windows.Controls;
using FlowStock.Core.Models;
using Microsoft.Win32;

namespace FlowStock.App;

public partial class CommercialPriceListWindow : Window
{
    private readonly AppServices _services;
    private WpfCommercialPriceListReport? _snapshot;
    private bool _busy;
    private long _filterGeneration;

    public CommercialPriceListWindow(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await Task.Run(() =>
            {
                var gotPartners = _services.WpfReadApi.TryGetPartners(out var partners);
                var gotItems = _services.WpfReadApi.TryGetItems(null, out var items);
                return (gotPartners, partners, gotItems, items);
            });
            if (!result.gotPartners || !result.gotItems)
                throw new InvalidOperationException("Не удалось загрузить справочники клиентов и товаров.");

            PartnerCombo.ItemsSource = result.partners
                .Where(p => !string.Equals(p.PartnerRole, "SUPPLIER", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            ItemCombo.ItemsSource = result.items.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("Price list lookup load failed", ex);
            StatusText.Text = "Справочники не загружены.";
            MessageBox.Show(ex.Message, "Прайс-листы", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private WpfCommercialPriceListQuery BuildQuery()
    {
        var mode = (ModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        var selectedPartner = (PartnerCombo.SelectedItem as Partner)?.Id;
        var selectedItem = (ItemCombo.SelectedItem as Item)?.Id;
        var (partner, item) = CommercialPriceListSelection.ForMode(mode, selectedPartner, selectedItem);
        if (mode == "partner" && !partner.HasValue)
            throw new InvalidOperationException("Выберите клиента для его прайс-листа.");
        if (mode == "item" && !item.HasValue)
            throw new InvalidOperationException("Выберите товар для просмотра цен.");

        return new WpfCommercialPriceListQuery(
            mode, partner, item,
            GtinBox.Text, SearchBox.Text, BrandBox.Text, VolumeBox.Text,
            ActiveItemsCheck.IsChecked == true,
            InactiveOverridesCheck.IsChecked == true,
            UnpricedCheck.IsChecked == true);
    }

    private void Invalidate()
    {
        _filterGeneration++;
        _snapshot = null;
        ReportGrid.ItemsSource = null;
        ExportExcelButton.IsEnabled = false;
        ExportPdfButton.IsEnabled = false;
        if (IsLoaded && !_busy)
            StatusText.Text = "Фильтры изменены. Нажмите «Сформировать».";
    }

    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;
        // A selection in another mode is not an implicit cross-filter.
        PartnerCombo.SelectedIndex = -1;
        ItemCombo.SelectedIndex = -1;
        Invalidate();
    }

    private void Filters_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
            Invalidate();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        PartnerCombo.SelectedIndex = -1;
        ItemCombo.SelectedIndex = -1;
        Invalidate();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        WpfCommercialPriceListQuery query;
        try
        {
            query = BuildQuery();
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Прайс-листы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Invalidate();
        var generation = _filterGeneration;
        var selectedPartner = PartnerCombo.SelectedItem as Partner;
        var selectedItem = ItemCombo.SelectedItem as Item;
        var selectionDescription = CommercialPriceListSelection.Describe(
            query, selectedPartner?.DisplayName, selectedPartner?.Code,
            selectedItem?.Name, selectedItem?.Gtin);
        _busy = true;
        RefreshButton.IsEnabled = false;
        StatusText.Text = "Загрузка актуального прайс-листа...";
        try
        {
            var result = await _services.WpfCommercialCatalogApi.GetPriceListAsync(query);
            if (result.TotalCount != result.Items.Count)
                throw new InvalidOperationException("Неполный ответ сервера прайс-листов.");
            if (generation != _filterGeneration)
            {
                StatusText.Text = "Фильтры изменены во время загрузки. Сформируйте отчёт заново.";
                return;
            }
            result.SelectionDescription = selectionDescription;
            _snapshot = result;
            ReportGrid.ItemsSource = result.Items;
            ExportExcelButton.IsEnabled = true;
            ExportPdfButton.IsEnabled = true;
            StatusText.Text = $"Получено {result.TotalCount} строк. Excel и PDF используют эту же выборку.";
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("Price list read failed", ex);
            StatusText.Text = "Не удалось загрузить прайс-лист.";
            MessageBox.Show(ex.Message, "Прайс-листы", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private async void ExportExcel_Click(object sender, RoutedEventArgs e) =>
        await ExportAsync(pdf: false);

    private async void ExportPdf_Click(object sender, RoutedEventArgs e) =>
        await ExportAsync(pdf: true);

    private async Task ExportAsync(bool pdf)
    {
        if (_busy || _snapshot is null)
            return;
        var dialog = new SaveFileDialog
        {
            Title = pdf ? "Сохранить прайс PDF" : "Сохранить прайс Excel",
            Filter = pdf ? "PDF (*.pdf)|*.pdf" : "Excel (*.xlsx)|*.xlsx",
            DefaultExt = pdf ? ".pdf" : ".xlsx",
            FileName = pdf ? "FlowStock-price-list.pdf" : "FlowStock-price-list.xlsx",
            OverwritePrompt = true,
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true)
            return;
        _busy = true;
        ExportExcelButton.IsEnabled = ExportPdfButton.IsEnabled = false;
        var report = _snapshot;
        try
        {
            StatusText.Text = pdf ? "Формирование PDF..." : "Формирование Excel...";
            var bytes = await Task.Run(() => pdf
                ? CommercialPriceListPdfExporter.Create(report)
                : CommercialPriceListExcelExporter.Create(report));
            CommercialStatisticsExportFileWriter.WriteAtomically(dialog.FileName, bytes);
            StatusText.Text = $"Сохранено: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("Price list export failed", ex);
            MessageBox.Show(ex.Message, "Экспорт прайс-листа", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            ExportExcelButton.IsEnabled = ExportPdfButton.IsEnabled = _snapshot != null;
        }
    }
}
