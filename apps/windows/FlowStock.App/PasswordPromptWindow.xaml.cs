using System.Windows;

namespace FlowStock.App;

public partial class PasswordPromptWindow : Window
{
    private readonly AdminAuthService _auth;

    public PasswordPromptWindow(AdminAuthService auth)
    {
        _auth = auth;
        InitializeComponent();
        Loaded += (_, _) => PasswordBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        if (_auth.VerifyPassword(PasswordBox.Password))
        {
            DialogResult = true;
            return;
        }

        ErrorText.Visibility = Visibility.Visible;
        PasswordBox.SelectAll();
        PasswordBox.Focus();
    }
}

