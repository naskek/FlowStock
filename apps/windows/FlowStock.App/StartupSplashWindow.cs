using System.Windows;
using System.Windows.Controls;
using WpfButton = System.Windows.Controls.Button;
using WpfProgressBar = System.Windows.Controls.ProgressBar;

namespace FlowStock.App;

internal sealed class StartupSplashWindow : Window
{
    private bool _allowClose;
    private readonly TextBlock _statusText;
    private readonly TextBlock _detailsText;
    private readonly WpfProgressBar _progress;
    private readonly WpfButton _closeButton;

    public StartupSplashWindow()
    {
        Title = "FlowStock";
        Width = 440;
        Height = 220;
        MinWidth = 440;
        MinHeight = 220;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;

        var root = new Grid
        {
            Margin = new Thickness(28)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "FlowStock",
            FontSize = 28,
            FontWeight = FontWeights.SemiBold
        };
        Grid.SetRow(title, 0);
        root.Children.Add(title);

        _statusText = new TextBlock
        {
            Text = "Запуск приложения…",
            FontSize = 15,
            Margin = new Thickness(0, 12, 0, 12)
        };
        Grid.SetRow(_statusText, 1);
        root.Children.Add(_statusText);

        _progress = new WpfProgressBar
        {
            IsIndeterminate = true,
            Height = 5,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(_progress, 2);
        root.Children.Add(_progress);

        _detailsText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            MaxHeight = 55
        };
        Grid.SetRow(_detailsText, 3);
        root.Children.Add(_detailsText);

        _closeButton = new WpfButton
        {
            Content = "Закрыть",
            Width = 100,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            Visibility = Visibility.Collapsed
        };
        _closeButton.Click += (_, _) => Close();
        Grid.SetRow(_closeButton, 4);
        root.Children.Add(_closeButton);

        Closing += (_, e) =>
        {
            if (!_allowClose)
            {
                e.Cancel = true;
            }
        };

        Content = root;
    }

    internal string StatusText => _statusText.Text;
    internal string DetailsText => _detailsText.Text;
    internal bool IsProgressVisible => _progress.Visibility == Visibility.Visible;
    internal bool IsCloseVisible => _closeButton.Visibility == Visibility.Visible;

    public void SetStatus(string message)
    {
        _statusText.Text = string.IsNullOrWhiteSpace(message) ? "Идёт запуск…" : message;
    }

    public void ShowFailure(string message, string? logPath)
    {
        _allowClose = true;
        _statusText.Text = "Запуск не завершён";
        _progress.IsIndeterminate = false;
        _progress.Visibility = Visibility.Collapsed;

        var details = string.IsNullOrWhiteSpace(message)
            ? "Не удалось завершить запуск FlowStock."
            : message.Trim();
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            details += $"{Environment.NewLine}Диагностика: {logPath}";
        }

        _detailsText.Text = details;
        _detailsText.Visibility = Visibility.Visible;
        _closeButton.Visibility = Visibility.Visible;
    }
    public void Complete()
    {
        _allowClose = true;
        Close();
    }

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }
}
