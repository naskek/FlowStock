using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FlowStock.App;

public partial class TsdDeviceWindow : Window
{
    private readonly AppServices? _productionServices;
    public bool IsUiPreview => _productionServices is null;
    private AppServices _services => _productionServices
        ?? throw new InvalidOperationException(UiPreviewContext.OperationUnavailable);
    private readonly SettingsPageLoading _loading = null!;
    private readonly ObservableCollection<TsdDeviceInfo> _devices = new();
    private TsdDeviceInfo? _selected;
    private bool _dialogOpen;

    public TsdDeviceWindow(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _productionServices = services;
        InitializeComponent();
        DevicesGrid.ItemsSource = _devices;
        _loading = new SettingsPageLoading((FrameworkElement)Content, _services.AppLogger);
        _loading.InitializeOnLoaded(async () => { await LoadDevicesAsync(); });
        UpdateActions();
    }

    private async Task<bool> LoadDevicesAsync()
    {
        var loaded = false;
        await _loading.RunAsync(async () =>
        {
        var selectedId = _selected?.Id;
        var result = await Task.Run(() =>
        {
            var success = _services.WpfAdminApi.TryGetTsdDevices(out var devices);
            return (success, devices);
        });
        if (!result.success)
        {
            AccountsStatusText.Text = "Ошибка загрузки аккаунтов.";
            MessageBox.Show("Не удалось загрузить аккаунты через server API.",
                "Аккаунты", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Never discard a previously loaded list on a failed refresh.
        _devices.Clear();
        foreach (var device in result.devices)
            _devices.Add(device);
        SelectDevice(selectedId);
        AccountsStatusText.Text = $"Всего аккаунтов: {_devices.Count}";
        loaded = true;
        });
        return loaded;
    }

    private void DevicesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = DevicesGrid.SelectedItem as TsdDeviceInfo;
        UpdateActions();
    }

    private void DevicesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_dialogOpen || _selected == null) return;
        // A header/scrollbar double click must not open an editor.
        if (e.OriginalSource is FrameworkElement element
            && element.DataContext is TsdDeviceInfo)
        {
            e.Handled = true;
            _ = ShowEditorAsync(AccountDialogMode.Edit);
        }
    }

    private void UpdateActions()
    {
        CreateAccountButton.IsEnabled = !_dialogOpen;
        EditAccountButton.IsEnabled = !_dialogOpen && _selected != null;
        ChangePasswordButton.IsEnabled = !_dialogOpen && _selected != null;
        RefreshAccountsButton.IsEnabled = !_dialogOpen && !IsUiPreview;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (IsUiPreview || _dialogOpen) return;
        await LoadDevicesAsync();
    }

    private async void Create_Click(object sender, RoutedEventArgs e) =>
        await ShowEditorAsync(AccountDialogMode.Create);

    private async void Edit_Click(object sender, RoutedEventArgs e) =>
        await ShowEditorAsync(AccountDialogMode.Edit);

    private async void ChangePassword_Click(object sender, RoutedEventArgs e) =>
        await ShowEditorAsync(AccountDialogMode.ChangePassword);

    private async Task ShowEditorAsync(AccountDialogMode mode)
    {
        if (_dialogOpen || (mode != AccountDialogMode.Create && _selected is null)) return;

        var account = mode == AccountDialogMode.Create ? null : _selected;
        var savedLogin = account?.Login;
        _dialogOpen = true;
        UpdateActions();
        try
        {
            Func<AccountEditSubmission, Task>? save = IsUiPreview ? null : async submission =>
            {
                bool succeeded;
                if (mode == AccountDialogMode.Create)
                {
                    succeeded = await _services.WpfAdminApi.TryAddTsdDeviceAsync(
                        submission.Login, submission.Password!, submission.IsActive,
                        submission.Platform, submission.AccessRole);
                }
                else
                {
                    // The legacy update endpoint also writes platform/role/activity
                    // during a password-only update. Check current server state first
                    // so a stale dialog cannot silently overwrite known changes.
                    await EnsureCurrentAccountAsync(account!);
                    succeeded = await _services.WpfAdminApi.TryUpdateTsdDeviceAsync(
                        account!.Id, submission.Login, submission.Password,
                        submission.IsActive, submission.Platform, submission.AccessRole);
                }

                if (!succeeded)
                    throw new InvalidOperationException("Server API отклонил сохранение аккаунта.");
                savedLogin = submission.Login;
            };

            var editor = new TsdDeviceEditorWindow(mode, account, save)
            {
                Owner = Window.GetWindow(DevicesGrid)
            };
            if (editor.ShowDialog() == true && !IsUiPreview)
            {
                if (await LoadDevicesAsync())
                {
                    if (mode == AccountDialogMode.Create)
                        SelectDeviceByLogin(savedLogin!);
                    else
                        SelectDevice(account!.Id);
                    AccountsStatusText.Text = mode == AccountDialogMode.ChangePassword
                        ? "Пароль успешно изменён." : "Изменения аккаунта сохранены.";
                }
                else
                {
                    AccountsStatusText.Text =
                        "Данные сохранены, но список не обновился. Нажмите «Обновить».";
                }
            }
        }
        catch (Exception ex)
        {
            if (!IsUiPreview)
                _services.AppLogger.Error("account_dialog_failed", ex);
            MessageBox.Show("Не удалось завершить операцию с аккаунтом: " + ex.Message,
                "Аккаунты", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _dialogOpen = false;
            UpdateActions();
        }
    }

    private async Task EnsureCurrentAccountAsync(TsdDeviceInfo original)
    {
        var result = await Task.Run(() =>
        {
            var success = _services.WpfAdminApi.TryGetTsdDevices(out var devices);
            return (success, devices);
        });
        if (!result.success)
            throw new InvalidOperationException("Не удалось проверить актуальные данные аккаунта.");

        var current = result.devices.FirstOrDefault(item => item.Id == original.Id);
        if (current == null
            || !string.Equals(current.Login, original.Login, StringComparison.Ordinal)
            || !string.Equals(current.Platform, original.Platform, StringComparison.Ordinal)
            || !string.Equals(current.AccessRole, original.AccessRole, StringComparison.Ordinal)
            || current.IsActive != original.IsActive)
        {
            throw new InvalidOperationException("Аккаунт был изменён другим оператором. Обновите список и повторите.");
        }
    }

    private void SelectDevice(long? id)
    {
        var selected = _devices.FirstOrDefault(device => device.Id == id);
        DevicesGrid.SelectedItem = selected;
        if (selected != null) DevicesGrid.ScrollIntoView(selected);
    }

    private void SelectDeviceByLogin(string login)
    {
        var selected = _devices.FirstOrDefault(device =>
            string.Equals(device.Login, login, StringComparison.OrdinalIgnoreCase));
        DevicesGrid.SelectedItem = selected;
        if (selected != null) DevicesGrid.ScrollIntoView(selected);
    }

    private void InitializeUiPreviewAccounts()
    {
        // In-memory synthetic accounts. No secrets, AppServices or requests.
        DevicesGrid.ItemsSource = _devices;
        var data = new[]
        {
            ("operator_demo", "PC", true, "OPERATOR"),
            ("manager_demo", "BOTH", true, "ADMIN"),
            ("warehouse_demo", "TSD", true, "OPERATOR"),
            ("shift_demo", "BOTH", false, "OPERATOR"),
            ("quality_demo", "PC", true, "OPERATOR"),
            ("office_demo", "PC", true, "ADMIN")
        };
        for (var i = 0; i < data.Length; i++)
        {
            var (login, platform, active, role) = data[i];
            _devices.Add(new TsdDeviceInfo
            {
                Id = i + 1,
                DeviceId = $"DEMO-DEVICE-{i + 1:000}",
                Login = login,
                Platform = platform,
                IsActive = active,
                AccessRole = role
            });
        }
        DevicesGrid.SelectedIndex = 0;
        AccountsStatusText.Text = "UI Preview / DEV · Демонстрационные аккаунты, сервер отключён.";
        foreach (var button in new[] { CreateAccountButton, EditAccountButton, ChangePasswordButton })
            button.ToolTip = "Открыть демонстрационный диалог (без сохранения).";
        UpdateActions();
    }
}
