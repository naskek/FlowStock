using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using FlowStock.Core.Models;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfPanel = System.Windows.Controls.Panel;

namespace FlowStock.App;

public partial class AdminWindow : Window
{
    private readonly AppServices _services;
    private readonly Action? _onOperationsCleared;
    private readonly Dictionary<string, WpfCheckBox> _clientBlockBoxes = new(StringComparer.OrdinalIgnoreCase);

    public AdminWindow(AppServices services, Action? onOperationsCleared = null)
    {
        _services = services;
        _onOperationsCleared = onOperationsCleared;

        InitializeComponent();
        LoadClientBlocksUi();
    }

    private void OpenDbConnection_Click(object sender, RoutedEventArgs e)
    {
        var window = new DbConnectionWindow(_services)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void OpenTsdDevices_Click(object sender, RoutedEventArgs e)
    {
        var window = new TsdDeviceWindow(_services)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void OpenMaintenance_Click(object sender, RoutedEventArgs e)
    {
        var window = new MaintenanceWindow(_services)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void ChangeAdminPassword_Click(object sender, RoutedEventArgs e)
    {
        if (_services.AdminAuth.EnsureAdminPasswordExists())
        {
            var prompt = new PasswordPromptWindow(_services.AdminAuth) { Owner = this };
            if (prompt.ShowDialog() != true)
            {
                _services.AdminLogger.Info("admin_change_password aborted: current password prompt cancelled");
                return;
            }
        }

        var setPassword = new SetAdminPasswordWindow(_services.AdminAuth) { Owner = this };
        if (setPassword.ShowDialog() != true)
        {
            _services.AdminLogger.Info("admin_change_password aborted: new password dialog cancelled");
            return;
        }

        _services.AdminLogger.Info("admin_password changed from ui");
        MessageBox.Show("Пароль администратора изменён.", "Администрирование", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ClearOperations_Click(object sender, RoutedEventArgs e)
    {
        // Опасное действие: требуем пароль администратора, чтобы исключить случайное/ошибочное нажатие.
        if (!_services.AdminAuth.EnsureAdminPasswordExists())
        {
            var setPassword = new SetAdminPasswordWindow(_services.AdminAuth) { Owner = this };
            if (setPassword.ShowDialog() != true)
            {
                _services.AdminLogger.Info("admin_reset_movements aborted: admin password not set");
                return;
            }
        }

        var prompt = new PasswordPromptWindow(_services.AdminAuth) { Owner = this };
        if (prompt.ShowDialog() != true)
        {
            _services.AdminLogger.Info("admin_reset_movements aborted: password prompt cancelled");
            return;
        }

        var confirm = MessageBox.Show(
            "Очистить все операции и заказы? Это действие удалит тестовые движения.",
            "Администрирование",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _services.Admin.ResetMovements();
            _services.AdminLogger.Info("admin_reset_movements from ui");
            _onOperationsCleared?.Invoke();
            MessageBox.Show("Операции очищены.", "Администрирование", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _services.AdminLogger.Error("admin_reset_movements failed", ex);
            MessageBox.Show(ex.Message, "Администрирование", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SaveClientBlocks_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = _clientBlockBoxes
                .Select(entry => new ClientBlockSetting(entry.Key, entry.Value.IsChecked == true))
                .ToList();
            var saved = await _services.WpfAdminApi
                .TrySaveClientBlocksAsync(settings)
                .ConfigureAwait(true);
            if (!saved)
            {
                throw new InvalidOperationException("Не удалось сохранить доступ к веб-блокам через сервер.");
            }

            ClientBlocksStatusText.Text = "Доступ к веб-блокам сохранен. Изменения применятся после обновления страницы у пользователей.";
            _services.AdminLogger.Info("admin_client_blocks saved");
        }
        catch (Exception ex)
        {
            _services.AdminLogger.Error("admin_client_blocks save failed", ex);
            MessageBox.Show(ex.Message, "Администрирование", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadClientBlocksUi()
    {
        try
        {
            var settings = _services.WpfAdminApi.TryGetClientBlocks(out var apiSettings)
                ? apiSettings
                : Array.Empty<ClientBlockSetting>();
            var states = ClientBlockCatalog.MergeWithDefaults(settings);
            _clientBlockBoxes.Clear();
            PopulateClientBlockPanel(
                PcBlocksPanel,
                ClientBlockCatalog.All.Where(definition => definition.Client == "PC"),
                states);
            PopulateClientBlockPanel(
                TsdMainBlocksPanel,
                ClientBlockCatalog.All.Where(definition => definition.Client == "TSD" && definition.Section == "Основные"),
                states);
            PopulateClientBlockPanel(
                TsdOperationBlocksPanel,
                ClientBlockCatalog.All.Where(definition => definition.Client == "TSD" && definition.Section == "Операции"),
                states);
            ClientBlocksStatusText.Text = "Отключенные блоки скрываются у всех пользователей веб-клиентов.";
        }
        catch (Exception ex)
        {
            _services.AdminLogger.Error("admin_client_blocks load failed", ex);
            ClientBlocksStatusText.Text = "Не удалось загрузить доступ к веб-блокам.";
            SaveClientBlocksButton.IsEnabled = false;
        }
    }

    private void PopulateClientBlockPanel(
        WpfPanel panel,
        IEnumerable<ClientBlockDefinition> definitions,
        IReadOnlyDictionary<string, bool> states)
    {
        panel.Children.Clear();
        foreach (var definition in definitions)
        {
            var isEnabled = states.TryGetValue(definition.Key, out var value) ? value : true;
            var checkBox = new WpfCheckBox
            {
                Content = definition.Label,
                IsChecked = isEnabled,
                Margin = new Thickness(0, 0, 0, 6)
            };
            panel.Children.Add(checkBox);
            _clientBlockBoxes[definition.Key] = checkBox;
        }
    }
}
