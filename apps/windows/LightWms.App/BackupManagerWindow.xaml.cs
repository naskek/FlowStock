using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace LightWms.App;

public partial class BackupManagerWindow : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<BackupInfo> _backups = new();
    private BackupInfo? _selectedBackup;

    public BackupManagerWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        BackupsGrid.ItemsSource = _backups;
        BackupFolderText.Text = $"Папка бэкапов: {_services.BackupsDir}";

        LoadBackups();
        LoadSettings();
    }

    private void LoadBackups()
    {
        _backups.Clear();
        foreach (var backup in _services.Backups.ListBackups())
        {
            _backups.Add(backup);
        }

        UpdateDeleteButton();
    }

    private void LoadSettings()
    {
        var settings = _services.Settings.Load();
        BackupsEnabledCheck.IsChecked = settings.BackupsEnabled;
        ModeEveryStartRadio.IsChecked = settings.BackupMode == BackupMode.OnEveryStart;
        ModeIfOlderRadio.IsChecked = settings.BackupMode == BackupMode.OnStartIfOlderThanHours;
        BackupHoursBox.Text = settings.BackupIfOlderThanHours.ToString();
        KeepLastBox.Text = settings.KeepLastNBackups.ToString();

        UpdateModeControls();
    }

    private void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _services.Backups.CreateBackup("manual");
            var settings = _services.Settings.Load();
            _services.Backups.ApplyRetention(settings.KeepLastNBackups);
            _services.AppLogger.Info($"Manual backup created: {path}");
            LoadBackups();
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("Manual backup failed", ex);
            MessageBox.Show(ex.Message, "Резервные копии", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        _services.Backups.OpenBackupsFolder();
    }

    private void DeleteBackup_Click(object sender, RoutedEventArgs e)
    {
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
            File.Delete(_selectedBackup.FullPath);
            _services.AppLogger.Info($"Backup deleted: {_selectedBackup.FullPath}");
            LoadBackups();
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("Backup delete failed", ex);
            MessageBox.Show(ex.Message, "Резервные копии", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
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
        UpdateModeControls();
    }

    private void BackupModeChanged(object sender, RoutedEventArgs e)
    {
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
        _selectedBackup = BackupsGrid.SelectedItem as BackupInfo;
        UpdateDeleteButton();
    }

    private void UpdateDeleteButton()
    {
        DeleteBackupButton.IsEnabled = _selectedBackup != null;
    }
}
