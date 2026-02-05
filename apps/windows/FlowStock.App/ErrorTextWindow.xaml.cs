using System.Windows;

namespace FlowStock.App;

public partial class ErrorTextWindow : Window
{
    public ErrorTextWindow(string title, string message)
    {
        InitializeComponent();
        Title = title;
        ErrorTextBox.Text = message;
        ErrorTextBox.CaretIndex = 0;
        ErrorTextBox.Select(0, 0);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ErrorTextBox.Text))
        {
            return;
        }

        System.Windows.Clipboard.SetText(ErrorTextBox.Text);
    }
}
