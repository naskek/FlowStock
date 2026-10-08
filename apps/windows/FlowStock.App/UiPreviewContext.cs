using System.Windows;
using System.Windows.Controls;
using WpfButton = System.Windows.Controls.Button;

namespace FlowStock.App;

/// <summary>A presentation-only composition root: no production services or settings.</summary>
public sealed class UiPreviewContext
{
    public const string OperationUnavailable = "UI Preview / DEV: операция отключена. БД и Server не используются.";

    internal static void Prepare(FrameworkElement root)
    {
        if (root is Window window) window.Title += " [UI Preview / DEV]";
        foreach (var element in Descendants(root))
        {
            if (element is WpfButton button && button.Content as string != "Закрыть")
            {
                button.IsEnabled = false;
                button.ToolTip = OperationUnavailable;
            }
            if (element is MenuItem menu && !menu.HasItems && menu.Name != "OpenSettingsMenuItem" && menu.Name != "OpenHuRegistryMenuItem")
            {
                menu.IsEnabled = false;
                menu.ToolTip = OperationUnavailable;
            }
            if (element is DataGrid grid)
            {
                grid.IsReadOnly = true;
                grid.ItemsSource = UiPreviewDemoData.ForGrid(grid);
            }
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        foreach (var element in Descendants(child))
            yield return element;
    }
}

public static class UiPreviewStartup
{
    public static bool TryStart(IReadOnlyList<string> args, Action<UiPreviewContext> start)
    {
        if (!args.Contains("--ui-preview", StringComparer.Ordinal)) return false;
        start(new UiPreviewContext());
        return true;
    }
}
