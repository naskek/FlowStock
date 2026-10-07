using System.Collections.ObjectModel;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace FlowStock.App;

public partial class BackupManagerWindow : Window
{
    private readonly AppServices? _productionServices;
    public bool IsUiPreview => _productionServices is null;
    private AppServices _services => _productionServices
        ?? throw new InvalidOperationException(UiPreviewContext.OperationUnavailable);
    private readonly SettingsPageLoading _loading = null!;
    private readonly ObservableCollection<BackupInfo> _backups = new();
    private BackupInfo? _selectedBackup;

    public BackupManagerWindow(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _productionServices = services;
        InitializeComponent();
        _loading = new SettingsPageLoading((FrameworkElement)Content, _services.AppLogger);

        BackupsGrid.ItemsSource = _backups;
        BackupFolderText.Text = $"Папка бэкапов: {_services.BackupsDir}";

        _loading.InitializeOnLoaded(async () =>
        {
            await LoadBackupsAsync();
            await LoadSettingsAsync();
        });
    }

    private Task LoadBackupsAsync() => _loading.RunAsync(async () =>
    {
        var backups = await Task.Run(() => _services.Backups.ListBackups().ToArray());
        _backups.Clear();
        foreach (var backup in backups)
        {
            _backups.Add(backup);
        }

        UpdateDeleteButton();
    });

    private Task LoadSettingsAsync() => _loading.RunAsync(async () =>
    {
        var settings = await Task.Run(_services.Settings.Load);
        BackupsEnabledCheck.IsChecked = settings.BackupsEnabled;
        ModeEveryStartRadio.IsChecked = settings.BackupMode == BackupMode.OnEveryStart;
        ModeIfOlderRadio.IsChecked = settings.BackupMode == BackupMode.OnStartIfOlderThanHours;
        BackupHoursBox.Text = settings.BackupIfOlderThanHours.ToString();
        KeepLastBox.Text = settings.KeepLastNBackups.ToString();

        UpdateModeControls();
    });

    private async void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        if (IsUiPreview) return;

        try
        {
            await _loading.RunAsync(() => Task.Run(() =>
            {
                var path = _services.Backups.CreateBackup("manual");
                var settings = _services.Settings.Load();
                _services.Backups.ApplyRetention(settings.KeepLastNBackups);
                _services.AppLogger.Info($"Manual backup created: {path}");
            }));
            await LoadBackupsAsync();
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("Manual backup failed", ex);
            MessageBox.Show(ex.Message, "Резервные копии", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        if (IsUiPreview) return;
        OpenFolder(_services.BaseDir, "Папка данных не найдена.");
    }

    private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (IsUiPreview) return;
        OpenFolder(_services.LogsDir, "Папка логов не найдена.");
    }

    private static void OpenFolder(string? path, string notFoundMessage)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            MessageBox.Show(notFoundMessage, "Настройки FlowStock", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (IsUiPreview) return;

        _services.Backups.OpenBackupsFolder();
    }

    private async void DeleteBackup_Click(object sender, RoutedEventArgs e)
    {
        if (IsUiPreview) return;

        if (_selectedBackup == null)
        {
            MessageBox.Show("Выберите бэкап.", "Резервные копии", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show("Удалить выбранный бэкап?", "Резервные копии", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var path = _selectedBackup.FullPath;
            await _loading.RunAsync(() => Task.Run(() => File.Delete(path)));
            _services.AppLogger.Info($"Backup deleted: {path}");
            await LoadBackupsAsync();
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("Backup delete failed", ex);
            MessageBox.Show(ex.Message, "Резервные копии", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (IsUiPreview) return;

        if (!TryParseSettings(out var settings))
        {
            return;
        }

        try
        {
            _services.Settings.Save(settings);
            MessageBox.Show("Настройки сохранены.", "Резервные копии", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("Save backup settings failed", ex);
            MessageBox.Show(ex.Message, "Резервные копии", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BackupsEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (IsUiPreview) return;

        UpdateModeControls();
    }

    private void BackupModeChanged(object sender, RoutedEventArgs e)
    {
        if (IsUiPreview) return;

        UpdateModeControls();
    }

    private void UpdateModeControls()
    {
        var enabled = BackupsEnabledCheck.IsChecked == true;
        ModeEveryStartRadio.IsEnabled = enabled;
        ModeIfOlderRadio.IsEnabled = enabled;
        BackupHoursBox.IsEnabled = enabled && ModeIfOlderRadio.IsChecked == true;
    }

    private bool TryParseSettings(out BackupSettings settings)
    {
        settings = _services.Settings.Load();
        settings.BackupsEnabled = BackupsEnabledCheck.IsChecked == true;
        settings.BackupMode = ModeEveryStartRadio.IsChecked == true
            ? BackupMode.OnEveryStart
            : BackupMode.OnStartIfOlderThanHours;

        if (!int.TryParse(BackupHoursBox.Text, out var hours) || hours < 1)
        {
            MessageBox.Show("Введите корректное количество часов.", "Резервные копии", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (!int.TryParse(KeepLastBox.Text, out var keepLast) || keepLast < 1)
        {
            MessageBox.Show("Введите корректное количество бэкапов для хранения.", "Резервные копии", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        settings.BackupIfOlderThanHours = hours;
        settings.KeepLastNBackups = keepLast;
        return true;
    }

    private void BackupsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsUiPreview) return;

        _selectedBackup = BackupsGrid.SelectedItem as BackupInfo;
        UpdateDeleteButton();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsUiPreview) return;

        if (!DeleteKeyGesture.IsDeleteGesture(e)
            || !BackupsGrid.IsKeyboardFocusWithin
            || _selectedBackup == null)
        {
            return;
        }

        e.Handled = true;
        DeleteBackup_Click(BackupsGrid, new RoutedEventArgs());
    }

    private void UpdateDeleteButton()
    {
        DeleteBackupButton.IsEnabled = _selectedBackup != null;
    }
}

