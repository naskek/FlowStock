using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Microsoft.Data.Sqlite;

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
    private static readonly string[] DeleteOrder =
    {
        "doc_lines",
        "ledger",
        "docs",
        "order_lines",
        "orders",
        "imported_events",
        "import_errors",
        "items",
        "locations",
        "partners"
    };
    private static readonly Dictionary<string, string[]> TableDependencies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["docs"] = new[] { "doc_lines", "ledger" },
        ["orders"] = new[] { "order_lines", "docs" }
    };
    private static readonly Dictionary<string, string[]> LookupDependencies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["items"] = new[] { "doc_lines", "order_lines", "ledger" },
        ["locations"] = new[] { "doc_lines", "ledger" },
        ["partners"] = new[] { "orders", "docs" }
    };
    private static readonly HashSet<string> AllowedTables = new(TableOrder, StringComparer.OrdinalIgnoreCase);
    private static readonly string[] MovementResetTables =
    {
        "docs",
        "doc_lines",
        "ledger",
        "orders",
        "order_lines",
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

    private void CountsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateResetButton();
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
        UpdateSelectionMode();
        UpdateResetButton();
    }

    private void UpdateResetButton()
    {
        var confirmed = ConfirmCheck.IsChecked == true
                        && string.Equals(ConfirmTextBox.Text?.Trim(), "УДАЛИТЬ", StringComparison.Ordinal);
        var selective = SelectiveResetRadio.IsChecked == true;
        var modeSelected = ResetMovementsRadio.IsChecked == true || FullResetRadio.IsChecked == true || selective;
        var hasSelection = !selective || CountsGrid.SelectedItems.Count > 0;
        ExecuteResetButton.IsEnabled = confirmed && modeSelected && hasSelection;
    }

    private void UpdateSelectionMode()
    {
        var selective = SelectiveResetRadio.IsChecked == true;
        if (!selective)
        {
            if (CountsGrid.SelectionMode != System.Windows.Controls.DataGridSelectionMode.Single)
            {
                CountsGrid.SelectedItems.Clear();
            }
            else
            {
                CountsGrid.SelectedItem = null;
            }
        }
        CountsGrid.SelectionMode = selective
            ? System.Windows.Controls.DataGridSelectionMode.Extended
            : System.Windows.Controls.DataGridSelectionMode.Single;
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

        var reason = FullResetRadio.IsChecked == true
            ? "admin_before_full_reset"
            : SelectiveResetRadio.IsChecked == true
                ? "admin_before_selective_reset"
                : "admin_before_reset";
        if (backupDecision == MessageBoxResult.Yes && !TryCreateBackup(reason))
        {
            return;
        }

        try
        {
            if (ResetMovementsRadio.IsChecked == true)
            {
                var counts = _services.Admin.GetTableCounts();
                _services.Admin.ResetMovements();
                var summary = BuildDeleteSummary(counts, MovementResetTables);
                MessageBox.Show($"Сброс движений выполнен.\n{summary}", "Администрирование", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (SelectiveResetRadio.IsChecked == true)
            {
                var selected = GetSelectedTables();
                if (!TryBuildSelectiveDeletePlan(selected, out var plan, out var error))
                {
                    MessageBox.Show(error, "Администрирование", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var confirmText = $"Будут удалены данные из таблиц: {string.Join(", ", plan)}. Продолжить?";
                var confirm = MessageBox.Show(
                    confirmText,
                    "Администрирование",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (confirm != MessageBoxResult.Yes)
                {
                    return;
                }

                var counts = _services.Admin.GetTableCounts();
                _services.Admin.DeleteTables(plan);
                var summary = BuildDeleteSummary(counts, plan);
                MessageBox.Show($"Удаление выполнено.\n{summary}", "Администрирование", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                var counts = _services.Admin.GetTableCounts();
                var archive = _services.Admin.FullReset();
                var summary = BuildDeleteSummary(counts, TableOrder);
                MessageBox.Show(
                    $"Полный сброс выполнен.\nАрхив: {archive}\n{summary}\nРекомендуется перезапустить приложение.",
                    "Администрирование",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            _onReset?.Invoke();
            LoadCounts();
        }
        catch (SqliteException ex)
        {
            _services.AdminLogger.Error("admin_reset failed", ex);
            MessageBox.Show($"Не удалось выполнить удаление: {ex.Message}", "Администрирование", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            _services.AdminLogger.Error("admin_reset failed", ex);
            MessageBox.Show(ex.Message, "Администрирование", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string BuildDeleteSummary(Dictionary<string, long> counts, IEnumerable<string> tables)
    {
        var parts = tables
            .Select(table => counts.TryGetValue(table, out var count) ? $"{table}={count}" : $"{table}=0");
        var summary = $"Удалено: {string.Join(", ", parts)}.";
        return summary;
    }

    private List<string> GetSelectedTables()
    {
        return CountsGrid.SelectedItems
            .OfType<TableCountRow>()
            .Select(row => row.Table)
            .ToList();
    }

    private bool TryBuildSelectiveDeletePlan(IReadOnlyCollection<string> selected, out List<string> plan, out string error)
    {
        plan = new List<string>();
        error = string.Empty;

        if (selected.Count == 0)
        {
            error = "Выберите таблицы для удаления.";
            return false;
        }

        foreach (var table in selected)
        {
            if (!AllowedTables.Contains(table))
            {
                error = $"Недопустимая таблица: {table}.";
                return false;
            }
        }

        var expanded = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(selected);
        while (queue.Count > 0)
        {
            var table = queue.Dequeue();
            if (!TableDependencies.TryGetValue(table, out var deps))
            {
                continue;
            }

            foreach (var dep in deps)
            {
                if (expanded.Add(dep))
                {
                    queue.Enqueue(dep);
                }
            }
        }

        if (!TryValidateLookupDeletion(expanded, out error))
        {
            return false;
        }

        foreach (var table in DeleteOrder)
        {
            if (expanded.Contains(table))
            {
                plan.Add(table);
            }
        }

        return true;
    }

    private bool TryValidateLookupDeletion(HashSet<string> expanded, out string error)
    {
        error = string.Empty;
        var counts = _services.Admin.GetTableCounts();

        foreach (var entry in LookupDependencies)
        {
            if (!expanded.Contains(entry.Key))
            {
                continue;
            }

            var blocking = entry.Value
                .Where(table => !expanded.Contains(table)
                                && counts.TryGetValue(table, out var count)
                                && count > 0)
                .ToList();
            if (blocking.Count == 0)
            {
                continue;
            }

            error = $"Нельзя удалить {entry.Key}, т.к. есть связанные записи в {string.Join(", ", blocking)}.";
            return false;
        }

        return true;
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
