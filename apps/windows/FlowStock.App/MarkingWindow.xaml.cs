using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using FlowStock.Core.Models;
using Microsoft.Win32;

namespace FlowStock.App;

public partial class MarkingWindow : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<MarkingOrderDisplayRow> _orders = new();

    public MarkingWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        OrdersGrid.ItemsSource = _orders;
        LoadOrders(showErrorMessage: false);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadOrders(showErrorMessage: true);
    }

    private void IncludeCompletedCheck_Changed(object sender, RoutedEventArgs e)
    {
        LoadOrders(showErrorMessage: false);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var selected = OrdersGrid.SelectedItems
            .OfType<MarkingOrderDisplayRow>()
            .Select(row => row.OrderId)
            .Distinct()
            .ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show("Выберите один или несколько заказов.", "Маркировка", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ExportButton.IsEnabled = false;
        try
        {
            var result = await _services.WpfMarkingApi.TryExportAsync(selected).ConfigureAwait(true);
            if (!result.IsSuccess || result.FileBytes == null)
            {
                MessageBox.Show(result.Error ?? "Нет строк для формирования файла ЧЗ.", "Маркировка", MessageBoxButton.OK, MessageBoxImage.Information);
                LoadOrders(showErrorMessage: false);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "Сохранить Excel ЧЗ",
                Filter = "Excel (*.xlsx)|*.xlsx",
                FileName = string.IsNullOrWhiteSpace(result.FileName) ? $"chestny_znak_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx" : result.FileName
            };
            if (dialog.ShowDialog(this) == true)
            {
                File.WriteAllBytes(dialog.FileName, result.FileBytes);
                MessageBox.Show("Файл ЧЗ сформирован. Заказы помечены как обработанные.", "Маркировка", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            LoadOrders(showErrorMessage: false);
        }
        finally
        {
            ExportButton.IsEnabled = true;
        }
    }

    private void LoadOrders(bool showErrorMessage)
    {
        _orders.Clear();
        if (!_services.WpfMarkingApi.TryGetOrders(IncludeCompletedCheck.IsChecked == true, out var rows))
        {
            SummaryText.Text = "Не удалось загрузить очередь маркировки.";
            if (showErrorMessage)
            {
                MessageBox.Show("Не удалось загрузить очередь маркировки. Проверьте доступность FlowStock Server API.", "Маркировка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return;
        }

        foreach (var row in rows)
        {
            _orders.Add(new MarkingOrderDisplayRow(row));
        }

        SummaryText.Text = $"Заказов: {_orders.Count}.";
    }

    private sealed class MarkingOrderDisplayRow
    {
        private readonly MarkingOrderQueueRow _row;

        public MarkingOrderDisplayRow(MarkingOrderQueueRow row)
        {
            _row = row;
        }

        public long OrderId => _row.OrderId;
        public string OrderRef => _row.OrderRef;
        public string PartnerDisplay => string.IsNullOrWhiteSpace(_row.PartnerDisplay) ? "-" : _row.PartnerDisplay;
        public string OrderStatusDisplay => OrderStatusMapper.StatusToDisplayName(_row.OrderStatus);
        public string DueDateDisplay => _row.DueDate?.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture) ?? "-";
        public string MarkingStatusDisplay => MarkingStatusMapper.ToDisplayName(_row.MarkingStatus);
        public int MarkingLineCount => _row.MarkingLineCount;
        public string MarkingCodeCountDisplay => _row.MarkingCodeCount.ToString("0.###", CultureInfo.CurrentCulture);
        public string LastGeneratedAtDisplay => _row.LastGeneratedAt?.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture) ?? "-";
    }
}
