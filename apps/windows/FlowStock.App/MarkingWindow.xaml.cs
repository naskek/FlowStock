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
    private bool _liveRefreshPending;
    private readonly IDisposable _liveRefreshSubscription;

    public MarkingWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        OrdersGrid.ItemsSource = _orders;
        _liveRefreshSubscription = _services.LiveRefresh.Register(
            () => IsVisible && IsActive && ExportButton.IsEnabled && !WpfLiveRefreshGuard.IsDataGridEditing(OrdersGrid),
            ApplyLiveRefresh,
            () => _liveRefreshPending = true);
        Activated += (_, _) => ApplyPendingLiveRefresh();
        Closed += (_, _) => _liveRefreshSubscription.Dispose();
        LoadOrders(showErrorMessage: false);
    }

    private void ApplyLiveRefresh()
    {
        _liveRefreshPending = false;
        LoadOrders(showErrorMessage: false);
    }

    private void ApplyPendingLiveRefresh()
    {
        if (_liveRefreshPending && IsVisible && IsActive && ExportButton.IsEnabled)
        {
            ApplyLiveRefresh();
        }
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
            .ToArray();
        var selectedTasks = selected
            .Select(row => row.MarkingOrderId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        var selectedOrders = selected
            .Where(row => !row.MarkingOrderId.HasValue)
            .Select(row => row.OrderId)
            .Where(id => id.HasValue && id.Value > 0)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        if (selectedTasks.Length == 0 && selectedOrders.Length == 0)
        {
            MessageBox.Show("Выберите хотя бы одну задачу маркировки.", "Маркировка", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ExportButton.IsEnabled = false;
        try
        {
            var result = await _services.WpfMarkingApi.TryExportAsync(selectedTasks, selectedOrders).ConfigureAwait(true);
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
                MessageBox.Show("Файл ЧЗ сформирован. Маркировка проведена.", "Маркировка", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            LoadOrders(showErrorMessage: false);
        }
        finally
        {
            ExportButton.IsEnabled = true;
            ApplyPendingLiveRefresh();
        }
    }

    private void CreateMarking_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "Маркировка формируется из окна заказа. Этот раздел оставлен как журнал/legacy-view.",
            "Маркировка",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
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

        SummaryText.Text = $"Задач: {_orders.Count}.";
    }

    private sealed class MarkingOrderDisplayRow
    {
        private readonly MarkingOrderQueueRow _row;

        public MarkingOrderDisplayRow(MarkingOrderQueueRow row)
        {
            _row = row;
        }

        public long? OrderId => _row.OrderId;
        public Guid? MarkingOrderId => _row.MarkingOrderId;
        public string OrderRef => string.IsNullOrWhiteSpace(_row.OrderRef) ? SourceDisplay : _row.OrderRef;
        public string PartnerDisplay => string.IsNullOrWhiteSpace(_row.PartnerDisplay) ? SourceDisplay : _row.PartnerDisplay;
        public string SourceDisplay => string.IsNullOrWhiteSpace(_row.DisplaySource) ? "-" : _row.DisplaySource;
        public string OrderStatusDisplay => OrderStatusMapper.StatusToDisplayName(_row.OrderStatus);
        public string DueDateDisplay => _row.DueDate?.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture) ?? "-";
        public string MarkingStatusDisplay => !string.IsNullOrWhiteSpace(_row.DisplayStatus)
            ? _row.DisplayStatus
            : string.IsNullOrWhiteSpace(_row.TaskStatus)
            ? MarkingStatusMapper.ToDisplayName(_row.MarkingStatus)
            : _row.TaskStatus;
        public int MarkingLineCount => _row.MarkingLineCount;
        public string MarkingCodeCountDisplay => _row.MarkingCodeCount.ToString("0.###", CultureInfo.CurrentCulture);
        public string RequestedQuantityDisplay => _row.RequestedQuantity.ToString(CultureInfo.CurrentCulture);
        public string CodesDisplay => $"{_row.CodesTotal} / {_row.CodesFree} / {_row.CodesBound}";
        public string ItemDisplay => string.IsNullOrWhiteSpace(_row.ItemName) && string.IsNullOrWhiteSpace(_row.Gtin)
            ? "-"
            : $"{_row.ItemName} {_row.Gtin}".Trim();
        public string LastGeneratedAtDisplay => _row.LastGeneratedAt?.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture) ?? "-";
    }
}
