using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FlowStock.App;

internal static class ScrollViewerWheelBubble
{
    public static void HandlePreviewMouseWheel(MouseWheelEventArgs e, ScrollViewer? rootScrollViewer)
    {
        if (rootScrollViewer == null || e.Handled)
        {
            return;
        }

        if (e.OriginalSource is not DependencyObject origin)
        {
            return;
        }

        var current = origin;
        while (current != null && !ReferenceEquals(current, rootScrollViewer))
        {
            if (current is ScrollViewer scrollViewer && CanScroll(scrollViewer, e.Delta))
            {
                return;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        e.Handled = true;
        var nextOffset = rootScrollViewer.VerticalOffset - e.Delta;
        if (nextOffset < 0)
        {
            nextOffset = 0;
        }
        else if (nextOffset > rootScrollViewer.ScrollableHeight)
        {
            nextOffset = rootScrollViewer.ScrollableHeight;
        }

        rootScrollViewer.ScrollToVerticalOffset(nextOffset);
    }

    private static bool CanScroll(ScrollViewer scrollViewer, int delta)
    {
        if (delta > 0)
        {
            return scrollViewer.VerticalOffset > 0.5;
        }

        return scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight - 0.5;
    }
}
