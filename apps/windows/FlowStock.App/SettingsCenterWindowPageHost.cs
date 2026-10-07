using System.Windows;
using WpfButton = System.Windows.Controls.Button;

namespace FlowStock.App;

internal sealed record HostedSettingsPage(object Controller, FrameworkElement Content);

internal static class SettingsCenterWindowPageHost
{
    public static HostedSettingsPage Detach(Window controller)
    {
        if (controller.Content is not FrameworkElement content)
        {
            throw new InvalidOperationException($"Окно {controller.GetType().Name} не содержит WPF-страницу.");
        }

        controller.Content = null;
        if (content.DataContext is null && controller.DataContext is not null)
        {
            content.DataContext = controller.DataContext;
        }

        HideWindowCloseButtons(content);

        // The legacy Window is only a controller for the embedded settings content.
        // Close the empty shell so it does not remain in Application.Windows and
        // prevent normal OnLastWindowClose application shutdown.
        controller.Close();

        return new HostedSettingsPage(controller, content);
    }

    private static void HideWindowCloseButtons(DependencyObject root)
    {
        if (root is WpfButton button
            && string.Equals(button.Content as string, "Закрыть", StringComparison.OrdinalIgnoreCase))
        {
            button.IsCancel = false;
            button.Visibility = Visibility.Collapsed;
        }

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            HideWindowCloseButtons(child);
        }
    }
}
