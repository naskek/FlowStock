using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace FlowStock.App;

public partial class MainWindow
{
    static MainWindow()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(InitializeCommercialStatisticsAcceptanceBehavior));
    }

    private static void InitializeCommercialStatisticsAcceptanceBehavior(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not MainWindow window)
        {
            return;
        }

        window.StatisticsVolumeCombo.ClearValue(ItemsControl.DisplayMemberPathProperty);
        window.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() =>
            {
                window.ApplyCommercialStatisticsAcceptanceFixes();
                window.ApplyCommercialStatisticsSelectorBlurFix();
                window.ApplyCommercialStatisticsDrillDownEnhancements();
                window.ApplyCommercialStatisticsContentLayout();
            }));
    }
}
