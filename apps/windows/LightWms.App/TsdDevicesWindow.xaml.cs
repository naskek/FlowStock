using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace LightWms.App;

public partial class TsdDevicesWindow : Window
{
    private static readonly Regex DeviceIdRegex = new("^[A-Z0-9-]+$", RegexOptions.Compiled);
    private readonly AppServices _services;
    private readonly ObservableCollection<TsdDeviceRow> _devices = new();
    private BackupSettings _settings = BackupSettings.Default();

    public TsdDevicesWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        DevicesGrid.ItemsSource = _devices;
        LoadDevices();
    }

    private void LoadDevices()
    {
        _settings = _services.Settings.Load();
        _devices.Clear();
        var devices = _settings.Tsd?.Devices ?? new List<TsdDevice>();
        foreach (var device in devices)
        {
            if (string.IsNullOrWhiteSpace(device.Id))
            {
                continue;
            }

            _devices.Add(new TsdDeviceRow
            {
                Id = device.Id,
                Name = device.Name
            });
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var row = new TsdDeviceRow();
        _devices.Add(row);
        DevicesGrid.SelectedItem = row;
        DevicesGrid.ScrollIntoView(row);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesGrid.SelectedItem is not TsdDeviceRow row)
        {
            return;
        }

        _devices.Remove(row);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingEdits();

        if (!TryBuildDeviceList(out var list, out var error))
        {
            MessageBox.Show(error, "Устройства ТСД", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.Tsd.Devices = list;
        if (!string.IsNullOrWhiteSpace(_settings.Tsd.LastDeviceId)
            && !list.Any(device => string.Equals(device.Id, _settings.Tsd.LastDeviceId, StringComparison.OrdinalIgnoreCase)))
        {
            _settings.Tsd.LastDeviceId = null;
        }

        try
        {
            _services.Settings.Save(_settings);
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("Failed to save TSD devices settings.", ex);
            MessageBox.Show("Не удалось сохранить настройки устройств ТСД.", "Устройства ТСД",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        LoadDevices();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private bool TryBuildDeviceList(out List<TsdDevice> devices, out string error)
    {
        devices = new List<TsdDevice>();
        error = string.Empty;

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < _devices.Count; index++)
        {
            var row = _devices[index];
            var rawId = row.Id?.Trim();
            if (string.IsNullOrWhiteSpace(rawId))
            {
                error = $"Заполните ID устройства в строке {index + 1}.";
                return false;
            }

            var id = rawId.ToUpperInvariant();
            if (!DeviceIdRegex.IsMatch(id))
            {
                error = $"Некорректный ID устройства \"{rawId}\". Допустимы только буквы, цифры и тире.";
                return false;
            }

            if (!ids.Add(id))
            {
                error = $"ID устройства \"{id}\" должен быть уникальным.";
                return false;
            }

            var name = string.IsNullOrWhiteSpace(row.Name) ? id : row.Name.Trim();
            devices.Add(new TsdDevice
            {
                Id = id,
                Name = name
            });
        }

        return true;
    }

    private void CommitPendingEdits()
    {
        DevicesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        DevicesGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private sealed class TsdDeviceRow
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }
}
