using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace FlowStock.App;

public partial class TsdDeviceWindow : Window
{
    private readonly AppServices _services;
    private readonly TsdDeviceService _deviceService;
    private readonly ObservableCollection<TsdDeviceInfo> _devices = new();
    private TsdDeviceInfo? _selected;

    public TsdDeviceWindow(AppServices services)
    {
        _services = services;
        _deviceService = new TsdDeviceService(_services.ConnectionString, _services.AppLogger);
        InitializeComponent();

        DevicesGrid.ItemsSource = _devices;
        LoadDevices();
        ClearForm();
    }

    private void LoadDevices()
    {
        _devices.Clear();
        foreach (var device in _deviceService.GetDevices())
        {
            _devices.Add(device);
        }
    }

    private void DevicesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var device = DevicesGrid.SelectedItem as TsdDeviceInfo;
        _selected = device;
        if (device == null)
        {
            ClearForm();
            return;
        }

        DeviceIdBox.Text = device.DeviceId;
        LoginBox.Text = device.Login;
        PasswordBox.Text = string.Empty;
        IsActiveCheck.IsChecked = device.IsActive;
        SetPlatformSelection(device.Platform);
        UpdateBlockButton();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadDevices();
        UpdateBlockButton();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        DevicesGrid.SelectedItem = null;
        _selected = null;
        ClearForm();
    }

    private void ToggleActive_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null)
        {
            return;
        }

        var next = !_selected.IsActive;
        try
        {
            _deviceService.SetDeviceActive(_selected.Id, next);
            LoadDevices();
            SelectDevice(_selected.Id);
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("tsd_device_toggle_failed", ex);
            MessageBox.Show(ex.Message, "Аккаунты", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var deviceId = DeviceIdBox.Text?.Trim() ?? string.Empty;
        var login = LoginBox.Text?.Trim() ?? string.Empty;
        var password = PasswordBox.Text ?? string.Empty;
        var isActive = IsActiveCheck.IsChecked == true;
        var platform = GetSelectedPlatform();

        try
        {
            if (_selected == null)
            {
                _deviceService.AddDevice(deviceId, login, password, isActive, platform);
            }
            else
            {
                var passwordToUpdate = string.IsNullOrWhiteSpace(password) ? null : password;
                _deviceService.UpdateDevice(_selected.Id, deviceId, login, passwordToUpdate, isActive, platform);
            }

            LoadDevices();
            SelectDeviceByKey(deviceId, login);
            MessageBox.Show("Изменения сохранены.", "Аккаунты", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("tsd_device_save_failed", ex);
            MessageBox.Show(ex.Message, "Аккаунты", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearForm()
    {
        DeviceIdBox.Text = string.Empty;
        LoginBox.Text = string.Empty;
        PasswordBox.Text = string.Empty;
        IsActiveCheck.IsChecked = true;
        SetPlatformSelection("TSD");
        UpdateBlockButton();
    }

    private void UpdateBlockButton()
    {
        if (BlockButton == null)
        {
            return;
        }

        if (_selected == null)
        {
            BlockButton.IsEnabled = false;
            BlockButton.Content = "Заблокировать";
            return;
        }

        BlockButton.IsEnabled = true;
        BlockButton.Content = _selected.IsActive ? "Заблокировать" : "Разблокировать";
    }

    private void SelectDevice(long id)
    {
        foreach (var device in _devices)
        {
            if (device.Id == id)
            {
                DevicesGrid.SelectedItem = device;
                DevicesGrid.ScrollIntoView(device);
                return;
            }
        }

        DevicesGrid.SelectedItem = null;
    }

    private void SelectDeviceByKey(string deviceId, string login)
    {
        foreach (var device in _devices)
        {
            if (string.Equals(device.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(device.Login, login, StringComparison.OrdinalIgnoreCase))
            {
                DevicesGrid.SelectedItem = device;
                DevicesGrid.ScrollIntoView(device);
                return;
            }
        }

        DevicesGrid.SelectedItem = null;
    }

    private string GetSelectedPlatform()
    {
        if (PlatformBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            return tag;
        }

        return "TSD";
    }

    private void SetPlatformSelection(string? platform)
    {
        var normalized = string.Equals(platform, "PC", StringComparison.OrdinalIgnoreCase) ? "PC" : "TSD";
        foreach (var entry in PlatformBox.Items)
        {
            if (entry is ComboBoxItem item && item.Tag is string tag
                && string.Equals(tag, normalized, StringComparison.OrdinalIgnoreCase))
            {
                PlatformBox.SelectedItem = item;
                return;
            }
        }

        PlatformBox.SelectedIndex = 0;
    }
}
