using System.Windows;
using System.Windows.Controls;

namespace FlowStock.App;

public partial class MainWindow
{
    static MainWindow()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(ClearCommercialStatisticsVolumeDisplayMemberPath));
    }

    private static void ClearCommercialStatisticsVolumeDisplayMemberPath(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not MainWindow window)
        {
            return;
        }

        window.StatisticsVolumeCombo.ClearValue(ItemsControl.DisplayMemberPathProperty);
    }
}
