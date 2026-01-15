using System.Windows;

namespace LightWms.App;

public partial class SetAdminPasswordWindow : Window
{
    private const int MinLength = 6;
    private readonly AdminAuthService _auth;

    public SetAdminPasswordWindow(AdminAuthService auth)
    {
        _auth = auth;
        InitializeComponent();
        Loaded += (_, _) => PasswordBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        var password = PasswordBox.Password;
        var confirm = ConfirmBox.Password;

        if (string.IsNullOrWhiteSpace(password) || password.Length < MinLength)
        {
            ShowError($"Пароль должен быть не короче {MinLength} символов.");
            return;
        }

        if (!string.Equals(password, confirm, StringComparison.Ordinal))
        {
            ShowError("Пароли не совпадают.");
            return;
        }

        try
        {
            _auth.SetPassword(password);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Администрирование", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
