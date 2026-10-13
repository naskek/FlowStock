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

/// <summary>Modal create/edit account editor; an optional password is part of edit.</summary>
public partial class TsdDeviceEditorWindow : Window
{
    private readonly AccountDialogMode _mode;
    private readonly TsdDeviceInfo? _account;
    private readonly Func<AccountEditSubmission, Task>? _saveAction;
    private bool _saving;

    public bool IsUiPreview => _saveAction is null;
    // Preview never retains or persists the entered password.
    public AccountEditSubmission? PreviewSubmission { get; private set; }

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
        CreateLoginPanel.Visibility = mode == AccountDialogMode.Create
            ? Visibility.Visible : Visibility.Collapsed;
        EditLoginPanel.Visibility = mode == AccountDialogMode.Edit
            ? Visibility.Visible : Visibility.Collapsed;
        LoginDisplayText.Text = account?.Login ?? string.Empty;
        SaveButton.Content = mode == AccountDialogMode.Create ? "Создать" : "Сохранить";
        PasswordHintText.Text = mode == AccountDialogMode.Create
            ? "Укажите пароль для новой учётной записи."
            : "Оставьте пароль и подтверждение пустыми, если менять пароль не нужно.";

        LoginBox.Text = string.Empty;
        IsActiveCheck.IsChecked = account?.IsActive ?? true;
        SetSelectedTag(PlatformBox, account?.Platform ?? "TSD");
        SetSelectedTag(AccessRoleBox, account?.AccessRole ?? "OPERATOR");

        if (IsUiPreview)
        {
            // Preview only returns a sanitized submission to the in-memory demo list.
            // No AppServices, credentials, API or database are involved.
            Title += " [UI Preview / DEV]";
            SaveButton.ToolTip = "Сохранить только в демонстрационных данных.";
        }
        LoginBox.TextChanged += (_, _) => UpdateSaveButton();
        NewLoginBox.TextChanged += (_, _) => UpdateSaveButton();
        PlatformBox.SelectionChanged += (_, _) => UpdateSaveButton();
        AccessRoleBox.SelectionChanged += (_, _) => UpdateSaveButton();
        IsActiveCheck.Checked += (_, _) => UpdateSaveButton();
        IsActiveCheck.Unchecked += (_, _) => UpdateSaveButton();
        NewPasswordBox.PasswordChanged += (_, _) => UpdateSaveButton();
        ConfirmPasswordBox.PasswordChanged += (_, _) => UpdateSaveButton();
        UpdateSaveButton();
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

    private bool IsLoginChangeRequested =>
        _mode == AccountDialogMode.Edit
        && NewLoginPanel.Visibility == Visibility.Visible;

    private void ChangeLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_mode != AccountDialogMode.Edit || _saving) return;
        var opening = !IsLoginChangeRequested;
        NewLoginPanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        ChangeLoginButton.Content = opening ? "Отменить смену логина" : "Изменить логин";
        if (opening)
        {
            NewLoginBox.Focus();
        }
        else
        {
            NewLoginBox.Clear();
        }
        UpdateSaveButton();
    }

    private bool HasChanges()
    {
        var passwordChanged = NewPasswordBox.Password.Length > 0
            || ConfirmPasswordBox.Password.Length > 0;

        if (_mode == AccountDialogMode.Create)
        {
            return LoginBox.Text.Length > 0
                || passwordChanged
                || IsActiveCheck.IsChecked != true
                || SelectedTag(PlatformBox) != "TSD"
                || SelectedTag(AccessRoleBox) != "OPERATOR";
        }

        return passwordChanged
            || (IsLoginChangeRequested
                && !string.IsNullOrWhiteSpace(NewLoginBox.Text)
                && !string.Equals(NewLoginBox.Text.Trim(), _account!.Login, StringComparison.Ordinal))
            || IsActiveCheck.IsChecked != _account!.IsActive
            || !string.Equals(SelectedTag(PlatformBox), _account.Platform, StringComparison.Ordinal)
            || !string.Equals(SelectedTag(AccessRoleBox), _account.AccessRole, StringComparison.Ordinal);
    }

    private void UpdateSaveButton()
    {
        SaveButton.IsEnabled = !_saving && HasChanges();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_saving) DialogResult = false;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || !HasChanges()) return;

        ErrorText.Visibility = Visibility.Collapsed;
        // Rename requires an explicit button click. The old login is never edited in place.
        var login = _mode == AccountDialogMode.Create
            ? LoginBox.Text.Trim()
            : IsLoginChangeRequested ? NewLoginBox.Text.Trim() : _account!.Login;
        if (string.IsNullOrWhiteSpace(login))
        {
            ShowError(IsLoginChangeRequested ? "Введите новый логин." : "Укажите логин.");
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
            if (IsUiPreview)
            {
                // Password is deliberately dropped before returning to the demo list.
                PreviewSubmission = submission with { Password = null };
            }
            else
            {
                await _saveAction!(submission);
            }
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
            UpdateSaveButton();
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
