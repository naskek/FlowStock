using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfComboBox = System.Windows.Controls.ComboBox;

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
    private bool _inlineBusy;
    private bool _suppressInlineEvents;

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
                AccountsStatusText.Text = "Ошибка загрузки аккаунтов. Нажмите F5 для повтора.";
                MessageBox.Show("Не удалось загрузить аккаунты через server API.",
                    "Аккаунты", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _suppressInlineEvents = true;
            try
            {
                _devices.Clear();
                foreach (var device in result.devices)
                    _devices.Add(device);
                SelectDevice(selectedId);
            }
            finally { _suppressInlineEvents = false; }
            AccountsStatusText.Text = $"Всего аккаунтов: {_devices.Count} · F5 — обновить список";
            loaded = true;
        });
        return loaded;
    }

    private void DevicesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = DevicesGrid.SelectedItem as TsdDeviceInfo;
        UpdateActions();
    }

    private static bool IsInteractiveCellElement(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is WpfComboBox or WpfCheckBox)
                return true;
            element = element is Visual
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    private async void DevicesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_dialogOpen || _inlineBusy || _selected is null
            || IsInteractiveCellElement(e.OriginalSource as DependencyObject))
            return;
        if (e.OriginalSource is FrameworkElement element
            && element.DataContext is TsdDeviceInfo)
        {
            e.Handled = true;
            await ShowEditorAsync(AccountDialogMode.Edit);
        }
    }

    private async void DevicesGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F5 || IsUiPreview || _dialogOpen || _inlineBusy) return;
        e.Handled = true;
        await LoadDevicesAsync();
    }

    private void UpdateActions()
    {
        CreateAccountButton.IsEnabled = !_dialogOpen && !_inlineBusy;
        EditAccountButton.IsEnabled = !_dialogOpen && !_inlineBusy && _selected != null;
        DeleteAccountButton.IsEnabled = !_dialogOpen && !_inlineBusy && _selected != null;
        DevicesGrid.IsEnabled = !_dialogOpen && !_inlineBusy;
    }

    private async void Create_Click(object sender, RoutedEventArgs e) =>
        await ShowEditorAsync(AccountDialogMode.Create);

    private async void Edit_Click(object sender, RoutedEventArgs e) =>
        await ShowEditorAsync(AccountDialogMode.Edit);

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || _inlineBusy || _selected is null) return;
        var account = _selected;
        var confirm = MessageBox.Show(
            $"Удалить аккаунт «{account.Login}»?\n\n" +
            "Это действие нельзя отменить. История операций сохранится. " +
            "Активные сессии ПК Web будут закрыты.",
            "Удаление аккаунта", MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;
        await DeleteAccountAsync(account);
    }

    private async Task DeleteAccountAsync(TsdDeviceInfo account)
    {
        if (_dialogOpen || _inlineBusy || !_devices.Contains(account)) return;
        _inlineBusy = true;
        UpdateActions();
        try
        {
            if (IsUiPreview)
            {
                _devices.Remove(account);
                SelectDevice(null);
                AccountsStatusText.Text =
                    "UI Preview / DEV · Аккаунт удалён только из демоданных.";
                return;
            }

            // Abort on stale selection before the destructive server request.
            await EnsureCurrentAccountAsync(account);

            // A failed/unknown DELETE response must never be treated as success.
            var deleted = false;
            Exception? error = null;
            try
            {
                deleted = await _services.WpfAdminApi.TryDeleteTsdDeviceAsync(account.Id);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            var refreshed = await LoadDevicesAsync();
            if (!refreshed)
            {
                AccountsStatusText.Text = "Исход удаления неизвестен. Нажмите F5.";
                var explanation = error is null
                    ? "Не удалось подтвердить состояние аккаунта на сервере."
                    : "Сервер отклонил удаление: " + error.Message
                      + " Состояние списка проверить не удалось.";
                MessageBox.Show(explanation + " Проверьте список перед повторным удалением.",
                    "Удаление аккаунта", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var remains = _devices.Any(row => row.Id == account.Id);
            if (!remains)
            {
                AccountsStatusText.Text = "Аккаунт удалён, история операций сохранена.";
                return;
            }

            throw error ?? new InvalidOperationException(deleted
                ? "Сервер подтвердил удаление, но аккаунт всё ещё присутствует. Нажмите F5."
                : "Сервер не подтвердил удаление аккаунта.");
        }
        catch (Exception ex)
        {
            if (!IsUiPreview)
                _services.AppLogger.Error("account_delete_failed", ex);
            MessageBox.Show("Не удалось удалить аккаунт: " + ex.Message,
                "Удаление аккаунта", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _inlineBusy = false;
            UpdateActions();
        }
    }

    private async Task ShowEditorAsync(AccountDialogMode mode)
    {
        if (_dialogOpen || _inlineBusy || (mode == AccountDialogMode.Edit && _selected is null))
            return;

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
                    await EnsureCurrentAccountAsync(account!);
                    // Only an explicit rename action may change the account login.
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
            if (editor.ShowDialog() == true)
            {
                if (IsUiPreview)
                {
                    // The modal returns no password; updates stay in this synthetic list.
                    var submitted = editor.PreviewSubmission
                        ?? throw new InvalidOperationException("Демо-форма не вернула изменения.");
                    if (mode == AccountDialogMode.Create)
                    {
                        var nextId = _devices.Count == 0 ? 1 : _devices.Max(row => row.Id) + 1;
                        var created = new TsdDeviceInfo
                        {
                            Id = nextId,
                            DeviceId = $"DEMO-DEVICE-{nextId:000}",
                            Login = submitted.Login,
                            IsActive = submitted.IsActive,
                            Platform = submitted.Platform,
                            AccessRole = submitted.AccessRole
                        };
                        _devices.Add(created);
                        SelectDevice(nextId);
                    }
                    else
                    {
                        if (_devices.Any(row => row.Id != account!.Id
                            && string.Equals(row.Login, submitted.Login, StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidOperationException("Логин уже используется.");
                        ReplaceDevice(account!, CopyAccount(
                            account!, submitted.Platform, submitted.IsActive, submitted.AccessRole,
                            submitted.Login));
                    }
                    AccountsStatusText.Text =
                        "UI Preview / DEV · Изменения сохранены только в демоданных.";
                }
                else if (await LoadDevicesAsync())
                {
                    if (mode == AccountDialogMode.Create)
                        SelectDeviceByLogin(savedLogin!);
                    else
                        SelectDevice(account!.Id);
                    AccountsStatusText.Text = "Изменения аккаунта сохранены.";
                }
                else
                {
                    AccountsStatusText.Text =
                        "Данные сохранены, но список не обновился. Нажмите F5.";
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

    private async void InlinePlatform_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not WpfComboBox box || !box.IsLoaded
            || box.DataContext is not TsdDeviceInfo original)
            return;
        var next = (box.SelectedItem as ComboBoxItem)?.Tag as string;
        if (next == null || string.Equals(next, original.Platform, StringComparison.Ordinal))
            return;
        await SaveInlineAsync(original, next, original.IsActive, original.AccessRole,
            () => box.SelectedValue = original.Platform);
    }

    private async void InlineRole_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not WpfComboBox box || !box.IsLoaded
            || box.DataContext is not TsdDeviceInfo original)
            return;
        var next = (box.SelectedItem as ComboBoxItem)?.Tag as string;
        if (next == null || string.Equals(next, original.AccessRole, StringComparison.Ordinal))
            return;
        await SaveInlineAsync(original, original.Platform, original.IsActive, next,
            () => box.SelectedValue = original.AccessRole);
    }

    private async void InlineActive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfCheckBox check || check.DataContext is not TsdDeviceInfo original)
            return;
        var next = check.IsChecked == true;
        if (next == original.IsActive) return;
        await SaveInlineAsync(original, original.Platform, next, original.AccessRole,
            () => check.IsChecked = original.IsActive);
    }

    private async Task SaveInlineAsync(
        TsdDeviceInfo original, string platform, bool active, string role, Action revert)
    {
        if (_suppressInlineEvents || _dialogOpen || _inlineBusy)
        {
            RevertInline(revert);
            return;
        }
        if (IsUiPreview)
        {
            // Demo changes exist only in memory: never resolve AppServices.
            ReplaceDevice(original, CopyAccount(original, platform, active, role));
            AccountsStatusText.Text = "UI Preview / DEV · Изменения только в демоданных.";
            return;
        }

        _inlineBusy = true;
        UpdateActions();
        try
        {
            await EnsureCurrentAccountAsync(original);
            var success = await _services.WpfAdminApi.TryUpdateTsdDeviceAsync(
                original.Id, original.Login, null, active, platform, role);
            if (!success)
                throw new InvalidOperationException("Server API отклонил изменение аккаунта.");

            ReplaceDevice(original, CopyAccount(original, platform, active, role));
            AccountsStatusText.Text = "Аккаунт обновлён.";
            if (!await LoadDevicesAsync())
                AccountsStatusText.Text = "Изменение сохранено, но список не обновился. Нажмите F5.";
        }
        catch (Exception ex)
        {
            RevertInline(revert);
            _services.AppLogger.Error("account_inline_update_failed", ex);
            AccountsStatusText.Text = "Изменение отклонено, значение восстановлено.";
            MessageBox.Show("Не удалось сохранить изменение: " + ex.Message,
                "Аккаунты", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _inlineBusy = false;
            UpdateActions();
        }
    }

    private void RevertInline(Action revert)
    {
        _suppressInlineEvents = true;
        try { revert(); }
        finally { _suppressInlineEvents = false; }
    }

    private static TsdDeviceInfo CopyAccount(
        TsdDeviceInfo account, string platform, bool active, string role, string? login = null) =>
        new()
        {
            Id = account.Id,
            DeviceId = account.DeviceId,
            Login = login ?? account.Login,
            Platform = platform,
            IsActive = active,
            AccessRole = role,
            CreatedAt = account.CreatedAt,
            LastSeen = account.LastSeen
        };

    private void ReplaceDevice(TsdDeviceInfo original, TsdDeviceInfo updated)
    {
        var index = _devices.IndexOf(original);
        if (index < 0) return;
        _suppressInlineEvents = true;
        try
        {
            _devices[index] = updated;
            SelectDevice(updated.Id);
        }
        finally { _suppressInlineEvents = false; }
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
            throw new InvalidOperationException(
                "Аккаунт был изменён другим оператором. Нажмите F5 и повторите.");
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
        DevicesGrid.IsReadOnly = false;
        DevicesGrid.SelectedIndex = 0;
        AccountsStatusText.Text = "UI Preview / DEV · Правки только в демоданных; F5 не нужен.";
        UpdateActions();
    }
}
