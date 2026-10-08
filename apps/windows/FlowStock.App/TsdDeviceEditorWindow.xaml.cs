using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using WpfComboBox = System.Windows.Controls.ComboBox;

namespace FlowStock.App;

public enum AccountDialogMode
{
    Create,
    Edit
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
        if (mode == AccountDialogMode.Edit && account is null)
            throw new ArgumentNullException(nameof(account));
        _mode = mode;
        _account = account;
        _saveAction = saveAction;
        InitializeComponent();

        Title = mode == AccountDialogMode.Create ? "Создать аккаунт" : "Редактировать аккаунт";
        ModeHeader.Text = Title;
        AccountNameText.Text = mode == AccountDialogMode.Create
            ? "Новая учётная запись ПК Web / ТСД"
            : $"Аккаунт: {account!.Login}. Логин изменить нельзя.";
        SaveButton.Content = mode == AccountDialogMode.Create ? "Создать" : "Сохранить";
        LoginBox.IsReadOnly = mode == AccountDialogMode.Edit;
        PasswordHintText.Text = mode == AccountDialogMode.Create
            ? "Укажите пароль для новой учётной записи."
            : "Оставьте пароль и подтверждение пустыми, если менять пароль не нужно.";

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
        Closing += (_, args) =>
        {
            // A request that has reached the server must not be abandoned
            // via the title-bar close button while its result is unknown.
            if (_saving) args.Cancel = true;
        };
        Loaded += (_, _) =>
        {
            if (mode == AccountDialogMode.Create)
                LoginBox.Focus();
            else
                PlatformBox.Focus();
        };
    }

    private static void SetSelectedTag(WpfComboBox box, string value)
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

    private static string SelectedTag(WpfComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_saving) DialogResult = false;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || IsUiPreview) return;

        ErrorText.Visibility = Visibility.Collapsed;
        // Existing login is immutable in WPF, regardless of editor text state.
        var login = _mode == AccountDialogMode.Create ? LoginBox.Text.Trim() : _account!.Login;
        if (string.IsNullOrWhiteSpace(login))
        {
            ShowError("Укажите логин.");
            return;
        }

        var platform = SelectedTag(PlatformBox);
        var role = SelectedTag(AccessRoleBox);
        if (platform is not ("TSD" or "PC" or "BOTH")
            || role is not ("OPERATOR" or "ADMIN"))
        {
            ShowError("Выберите допустимую платформу и роль ПК Web.");
            return;
        }

        var enteredPassword = NewPasswordBox.Password;
        var confirm = ConfirmPasswordBox.Password;
        if (_mode == AccountDialogMode.Create && string.IsNullOrWhiteSpace(enteredPassword))
        {
            ShowError("Введите новый пароль.");
            return;
        }
        if ((enteredPassword.Length > 0 || confirm.Length > 0)
            && !string.Equals(enteredPassword, confirm, StringComparison.Ordinal))
        {
            ShowError("Подтверждение пароля не совпадает.");
            return;
        }
        if (_mode == AccountDialogMode.Edit
            && enteredPassword.Length > 0 && string.IsNullOrWhiteSpace(enteredPassword))
        {
            ShowError("Пароль не может состоять только из пробелов.");
            return;
        }
        string? password = string.IsNullOrEmpty(enteredPassword) ? null : enteredPassword;
        var submission = new AccountEditSubmission(
            login, password, IsActiveCheck.IsChecked == true, platform, role);

        _saving = true;
        SaveButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        var saved = false;
        try
        {
            await _saveAction!(submission);
            saved = true;
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

        // Setting DialogResult closes a modal synchronously. Closing must see
        // _saving = false, otherwise it cancels a successful save.
        if (saved)
            DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
