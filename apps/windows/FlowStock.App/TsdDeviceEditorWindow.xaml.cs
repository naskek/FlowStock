using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace FlowStock.App;

public enum AccountDialogMode
{
    Create,
    Edit,
    ChangePassword
}

public sealed record AccountEditSubmission(
    string Login,
    string? Password,
    bool IsActive,
    string Platform,
    string AccessRole);

/// <summary>One modal presentation for the three existing account operations.</summary>
public partial class TsdDeviceEditorWindow : Window
{
    private readonly AccountDialogMode _mode;
    private readonly TsdDeviceInfo? _account;
    private readonly Func<AccountEditSubmission, Task>? _saveAction;
    private bool _saving;

    public bool IsUiPreview => _saveAction is null;

    public TsdDeviceEditorWindow(
        AccountDialogMode mode,
        TsdDeviceInfo? account,
        Func<AccountEditSubmission, Task>? saveAction)
    {
        if (mode != AccountDialogMode.Create && account is null)
            throw new ArgumentNullException(nameof(account));
        _mode = mode;
        _account = account;
        _saveAction = saveAction;
        InitializeComponent();

        Title = mode switch
        {
            AccountDialogMode.Create => "Создать аккаунт",
            AccountDialogMode.Edit => "Редактировать аккаунт",
            _ => "Сменить пароль"
        };
        ModeHeader.Text = Title;
        AccountNameText.Text = mode switch
        {
            AccountDialogMode.Create => "Новая учётная запись ПК Web / ТСД",
            AccountDialogMode.Edit => "Изменение данных существующей учётной записи",
            _ => $"Аккаунт: {account!.Login}"
        };

        ProfileFieldsPanel.Visibility = mode == AccountDialogMode.ChangePassword
            ? Visibility.Collapsed : Visibility.Visible;
        PasswordFieldsPanel.Visibility = mode == AccountDialogMode.Edit
            ? Visibility.Collapsed : Visibility.Visible;
        SaveButton.Content = mode switch
        {
            AccountDialogMode.Create => "Создать",
            AccountDialogMode.Edit => "Сохранить",
            _ => "Сменить пароль"
        };

        LoginBox.Text = account?.Login ?? string.Empty;
        IsActiveCheck.IsChecked = account?.IsActive ?? true;
        SetSelectedTag(PlatformBox, account?.Platform ?? "TSD");
        SetSelectedTag(AccessRoleBox, account?.AccessRole ?? "OPERATOR");

        if (IsUiPreview)
        {
            // Presentation-only mode: all input fields may be inspected, but
            // no persistence callback, credentials, services or backend exist.
            Title += " [UI Preview / DEV]";
            SaveButton.IsEnabled = false;
            SaveButton.ToolTip = UiPreviewContext.OperationUnavailable;
        }
        Loaded += (_, _) =>
        {
            if (mode == AccountDialogMode.ChangePassword || mode == AccountDialogMode.Create)
                NewPasswordBox.Focus();
            else
                LoginBox.Focus();
        };
    }

    private static void SetSelectedTag(ComboBox box, string value)
    {
        foreach (var entry in box.Items)
        {
            if (entry is ComboBoxItem item && string.Equals(item.Tag as string, value,
                    StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        box.SelectedIndex = 0;
    }

    private static string SelectedTag(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_saving) DialogResult = false;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || IsUiPreview) return;

        ErrorText.Visibility = Visibility.Collapsed;
        var login = _mode == AccountDialogMode.Create ? LoginBox.Text.Trim()
            : _mode == AccountDialogMode.ChangePassword ? _account!.Login : LoginBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(login))
        {
            ShowError("Укажите логин.");
            return;
        }

        var platform = _mode == AccountDialogMode.ChangePassword
            ? _account!.Platform : SelectedTag(PlatformBox);
        var role = _mode == AccountDialogMode.ChangePassword
            ? _account!.AccessRole : SelectedTag(AccessRoleBox);
        if (platform is not ("TSD" or "PC" or "BOTH")
            || role is not ("OPERATOR" or "ADMIN"))
        {
            ShowError("Выберите допустимую платформу и роль ПК Web.");
            return;
        }

        string? password = null;
        if (_mode != AccountDialogMode.Edit)
        {
            password = NewPasswordBox.Password;
            if (string.IsNullOrWhiteSpace(password))
            {
                ShowError("Введите новый пароль.");
                return;
            }
            if (!string.Equals(password, ConfirmPasswordBox.Password, StringComparison.Ordinal))
            {
                ShowError("Подтверждение пароля не совпадает.");
                return;
            }
        }

        var submission = new AccountEditSubmission(
            login, password,
            _mode == AccountDialogMode.ChangePassword ? _account!.IsActive : IsActiveCheck.IsChecked == true,
            platform, role);

        _saving = true;
        SaveButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        try
        {
            await _saveAction!(submission);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            // Never include password values in error UI or logging.
            ShowError(ex.Message);
        }
        finally
        {
            _saving = false;
            SaveButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
