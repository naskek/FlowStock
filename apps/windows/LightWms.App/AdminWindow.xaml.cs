using System.Collections.ObjectModel;
using System.Windows;

namespace LightWms.App;

public partial class AdminWindow : Window
{
    private static readonly string[] TableOrder =
    {
        "docs",
        "doc_lines",
        "ledger",
        "orders",
        "order_lines",
        "items",
        "locations",
        "partners",
        "imported_events",
        "import_errors"
    };

    private readonly AppServices _services;
    private readonly Action? _onReset;
    private readonly ObservableCollection<TableCountRow> _counts = new();

    public AdminWindow(AppServices services, Action? onReset)
    {
        _services = services;
        _onReset = onReset;
        InitializeComponent();

        CountsGrid.ItemsSource = _counts;
        DatabasePathBox.Text = _services.DatabasePath;
        ResetMovementsRadio.IsChecked = true;

        LoadCounts();
        UpdateResetButton();
    }

    private void LoadCounts()
    {
        _counts.Clear();
        var counts = _services.Admin.GetTableCounts();
        foreach (var table in TableOrder)
        {
            counts.TryGetValue(table, out var value);
            _counts.Add(new TableCountRow(table, value));
        }
    }

    private void RefreshCounts_Click(object sender, RoutedEventArgs e)
    {
        LoadCounts();
    }

    private void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _services.Backups.CreateBackup("admin_manual");
            var settings = _services.Settings.Load();
            _services.Backups.ApplyRetention(settings.KeepLastNBackups);
            _services.AdminLogger.Info($"admin_backup path={path}");
            MessageBox.Show("Резервная копия создана.", "Администрирование", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _services.AdminLogger.Error("admin_backup failed", ex);
            MessageBox.Show(ex.Message, "Администрирование", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ConfirmChanged(object sender, RoutedEventArgs e)
    {
        UpdateResetButton();
    }

    private void ConfirmTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateResetButton();
    }

    private void ResetModeChanged(object sender, RoutedEventArgs e)
    {
        UpdateResetButton();
    }

    private void UpdateResetButton()
    {
        var confirmed = ConfirmCheck.IsChecked == true
                        && string.Equals(ConfirmTextBox.Text?.Trim(), "УДАЛИТЬ", StringComparison.Ordinal);
        var modeSelected = ResetMovementsRadio.IsChecked == true || FullResetRadio.IsChecked == true;
        ExecuteResetButton.IsEnabled = confirmed && modeSelected;
    }

    private void ExecuteReset_Click(object sender, RoutedEventArgs e)
    {
        if (!ExecuteResetButton.IsEnabled)
        {
            return;
        }

        var backupDecision = MessageBox.Show(
            "Сделать резервную копию перед удалением?",
            "Администрирование",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);

        if (backupDecision == MessageBoxResult.Cancel)
        {
            return;
        }

        var reason = FullResetRadio.IsChecked == true ? "admin_before_full_reset" : "admin_before_reset";
        if (backupDecision == MessageBoxResult.Yes && !TryCreateBackup(reason))
        {
            return;
        }

        try
        {
            if (ResetMovementsRadio.IsChecked == true)
            {
                _services.Admin.ResetMovements();
                MessageBox.Show("Сброс движений выполнен.", "Администрирование", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                var archive = _services.Admin.FullReset();
                MessageBox.Show(
                    $"Полный сброс выполнен.\nАрхив: {archive}\nРекомендуется перезапустить приложение.",
                    "Администрирование",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            _onReset?.Invoke();
            LoadCounts();
        }
        catch (Exception ex)
        {
            _services.AdminLogger.Error("admin_reset failed", ex);
            MessageBox.Show(ex.Message, "Администрирование", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool TryCreateBackup(string reason)
    {
        try
        {
            var path = _services.Backups.CreateBackup(reason);
            var settings = _services.Settings.Load();
            _services.Backups.ApplyRetention(settings.KeepLastNBackups);
            _services.AdminLogger.Info($"admin_backup reason={reason} path={path}");
            return true;
        }
        catch (Exception ex)
        {
            _services.AdminLogger.Error("admin_backup failed", ex);
            MessageBox.Show(ex.Message, "Администрирование", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private sealed record TableCountRow(string Table, long Count);
}
