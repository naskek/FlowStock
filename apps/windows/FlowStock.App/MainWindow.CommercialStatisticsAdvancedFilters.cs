using System.Windows;
using System.Windows.Controls;

namespace FlowStock.App;

public partial class MainWindow
{
    private void StatisticsAdvancedFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressCommercialStatisticsFilterEvents || !IsLoaded)
        {
            return;
        }

        _commercialStatisticsState.CriteriaChanged(periodChanged: false);
        UpdateCommercialStatisticsNavigation();
        ScheduleCommercialStatisticsRefresh();
    }

    private void StatisticsMonthDate_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || StatisticsMonthDate.SelectedDate is not DateTime selected)
        {
            return;
        }

        ApplyCommercialStatisticsMonth(selected);
    }

    private void StatisticsCurrentMonth_Click(object sender, RoutedEventArgs e)
    {
        StatisticsMonthDate.SelectedDate = DateTime.Today;
        ApplyCommercialStatisticsMonth(DateTime.Today);
    }

    private void StatisticsPreviousMonth_Click(object sender, RoutedEventArgs e)
    {
        var previousMonth = DateTime.Today.AddMonths(-1);
        StatisticsMonthDate.SelectedDate = previousMonth;
        ApplyCommercialStatisticsMonth(previousMonth);
    }

    private void ApplyCommercialStatisticsMonth(DateTime value)
    {
        var period = CommercialStatisticsAdvancedFilters.MonthPeriod(value);
        if (StatisticsFromDate.SelectedDate == period.From
            && StatisticsToDate.SelectedDate == period.To)
        {
            return;
        }

        StatisticsFromDate.SelectedDate = period.From;
        StatisticsToDate.SelectedDate = period.To;
    }
}
