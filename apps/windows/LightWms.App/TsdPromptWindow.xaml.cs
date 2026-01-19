using System.Windows;

namespace LightWms.App;

public partial class TsdPromptWindow : Window
{
    public TsdPromptChoice Choice { get; private set; } = TsdPromptChoice.Later;

    public TsdPromptWindow()
    {
        InitializeComponent();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        Choice = TsdPromptChoice.Open;
        DialogResult = true;
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        Choice = TsdPromptChoice.Later;
        DialogResult = false;
    }

    private void Disable_Click(object sender, RoutedEventArgs e)
    {
        Choice = TsdPromptChoice.Disable;
        DialogResult = false;
    }
}

public enum TsdPromptChoice
{
    Open,
    Later,
    Disable
}
