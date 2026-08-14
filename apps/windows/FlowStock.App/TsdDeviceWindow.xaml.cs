using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace FlowStock.App;

public partial class TsdDeviceWindow : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<TsdDeviceInfo> _devices = new();
    private TsdDeviceInfo? _selected;

    public TsdDeviceWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        DevicesGrid.ItemsSource = _devices;
        LoadDevices();
        ClearForm();
    }

    private void LoadDevices()
    {
        _devices.Clear();
        if (!_services.WpfAdminApi.TryGetTsdDevices(out var devices))
        {
            MessageBox.Show(
                "Не удалось загрузить аккаунты через защищённый server API.",
                "Аккаунты",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }
        foreach (var device in devices)
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

        LoginBox.Text = device.Login;
        PasswordBox.Text = string.Empty;
        IsActiveCheck.IsChecked = device.IsActive;
        IsAdminCheck.IsChecked = string.Equals(device.AccessRole, "ADMIN", StringComparison.OrdinalIgnoreCase);
        SetPlatformSelection(device.Platform);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadDevices();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        DevicesGrid.SelectedItem = null;
        _selected = null;
        ClearForm();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var login = LoginBox.Text?.Trim() ?? string.Empty;
        var password = PasswordBox.Text ?? string.Empty;
        var isActive = IsActiveCheck.IsChecked == true;
        var platform = GetSelectedPlatform();
        var accessRole = IsAdminCheck.IsChecked == true ? "ADMIN" : "OPERATOR";
        var selectedId = _selected?.Id ?? 0;

        try
        {
            if (_selected == null)
            {
                var saved = await _services.WpfAdminApi
                    .TryAddTsdDeviceAsync(login, password, isActive, platform, accessRole)
                    .ConfigureAwait(true);
                if (!saved)
                {
                    throw new InvalidOperationException("Server API отклонил создание аккаунта.");
                }
            }
            else
            {
                var passwordToUpdate = string.IsNullOrWhiteSpace(password) ? null : password;
                var saved = await _services.WpfAdminApi
                    .TryUpdateTsdDeviceAsync(selectedId, login, passwordToUpdate, isActive, platform, accessRole)
                    .ConfigureAwait(true);
                if (!saved)
                {
                    throw new InvalidOperationException("Server API отклонил изменение аккаунта.");
                }
            }

            LoadDevices();
            if (selectedId > 0)
            {
                SelectDevice(selectedId);
            }
            else
            {
                SelectDeviceByLogin(login);
            }
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
        LoginBox.Text = string.Empty;
        PasswordBox.Text = string.Empty;
        IsActiveCheck.IsChecked = true;
        IsAdminCheck.IsChecked = false;
        SetPlatformSelection("TSD");
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

    private void SelectDeviceByLogin(string login)
    {
        foreach (var device in _devices)
        {
            if (string.Equals(device.Login, login, StringComparison.OrdinalIgnoreCase))
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
        var normalized = string.Equals(platform, "PC", StringComparison.OrdinalIgnoreCase)
            ? "PC"
            : string.Equals(platform, "BOTH", StringComparison.OrdinalIgnoreCase)
                ? "BOTH"
                : "TSD";
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
