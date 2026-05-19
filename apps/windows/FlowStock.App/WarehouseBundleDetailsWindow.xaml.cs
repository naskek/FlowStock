using System.Windows;
using FlowStock.App.Services;

namespace FlowStock.App;

public partial class WarehouseBundleDetailsWindow : Window
{
    private readonly AppServices _services;
    private readonly long _bundleId;
    private WarehouseBundleListRow? _bundle;

    public bool DataChanged { get; private set; }

    public WarehouseBundleDetailsWindow(AppServices services, long bundleId)
    {
        _services = services;
        _bundleId = bundleId;
        InitializeComponent();
        Loaded += async (_, _) => await ReloadAsync().ConfigureAwait(true);
    }

    private async Task ReloadAsync()
    {
        var result = await _services.WpfWarehouseTasks.TryGetBundleAsync(_bundleId).ConfigureAwait(true);
        if (!result.IsSuccess || result.Bundle == null)
        {
            MessageBox.Show(result.ErrorMessage ?? "Не удалось загрузить пакет.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _bundle = result.Bundle;
        BundleTitleText.Text = $"{result.Bundle.BundleRef} (id {_bundleId})";
        BundleStatusText.Text = $"Статус: {result.Bundle.StatusDisplay} ({result.Bundle.Status})";
        BundleMetaText.Text = $"Создан: {result.Bundle.CreatedAt:g}  •  {result.Bundle.CreatedBy}";

        LinesGrid.ItemsSource = result.Lines;
        TasksGrid.ItemsSource = result.Tasks.Select(task => new TaskGridRow(task)).ToArray();
        EventsList.ItemsSource = result.Tasks.SelectMany(task => task.Events).ToArray();

        UpdateButtons(result.Bundle.Status);
    }

    private void UpdateButtons(string status)
    {
        var normalized = status.ToUpperInvariant();
        ApproveButton.IsEnabled = normalized == "SUBMITTED";
        RejectButton.IsEnabled = normalized == "SUBMITTED";
        ConfirmExecutionButton.IsEnabled = normalized is "EXECUTED" or "APPROVED";
    }

    private async void Approve_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(() => _services.WpfWarehouseTasks.TryApproveBundleAsync(_bundleId)).ConfigureAwait(true);
    }

    private async void Reject_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(() => _services.WpfWarehouseTasks.TryRejectBundleAsync(_bundleId, null)).ConfigureAwait(true);
    }

    private async void ConfirmExecution_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(() => _services.WpfWarehouseTasks.TryConfirmExecutionAsync(_bundleId)).ConfigureAwait(true);
    }

    private async Task RunOperationAsync(Func<Task<WpfWarehouseBundleOperationApiResult>> operation)
    {
        try
        {
            ApproveButton.IsEnabled = false;
            RejectButton.IsEnabled = false;
            ConfirmExecutionButton.IsEnabled = false;
            var result = await operation().ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                MessageBox.Show(result.ErrorMessage ?? "Операция не выполнена.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DataChanged = true;
            MessageBox.Show(result.Message ?? "Готово.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await ReloadAsync().ConfigureAwait(true);
        }
        finally
        {
            if (_bundle != null)
            {
                UpdateButtons(_bundle.Status);
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = DataChanged;
        Close();
    }

    private sealed class TaskGridRow
    {
        public TaskGridRow(WarehouseBundleTaskDetailRow task)
        {
            TaskRef = task.TaskRef;
            Status = task.Status;
            ExpectedHuCode = task.ExpectedHuCode;
            EventsDisplay = string.Join("; ", task.Events);
        }

        public string TaskRef { get; }
        public string Status { get; }
        public string? ExpectedHuCode { get; }
        public string EventsDisplay { get; }
    }
}
