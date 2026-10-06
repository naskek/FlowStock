using System.Windows;
using System.Windows.Controls;

namespace FlowStock.App;

public partial class MainWindow
{
    private bool _commercialStatisticsModeDefaultsApplied;

    private void ApplyCommercialStatisticsModeDefaults()
    {
        if (_commercialStatisticsModeDefaultsApplied || !_commercialStatisticsAdvancedUiInitialized)
        {
            return;
        }

        _commercialStatisticsModeDefaultsApplied = true;

        // The old acceptance layer forced Orders to SHIPPED and then removed the status
        // selector. Keep its unrelated acceptance fixes, but replace only that behavior.
        StatisticsPartnerCombo.SelectionChanged -= StatisticsAcceptancePartner_SelectionChanged;
        StatisticsPartnerCombo.SelectionChanged += StatisticsModeDefaultsPartner_SelectionChanged;

        RestoreCommercialStatisticsStatusFilter();
        if (_commercialStatisticsResetFiltersButton is not null)
        {
            _commercialStatisticsResetFiltersButton.Click += StatisticsModeDefaultsResetStatuses_Click;
        }

        var previousSuppression = _suppressCommercialStatisticsFilterEvents;
        _suppressCommercialStatisticsFilterEvents = true;
        try
        {
            ResetCommercialStatisticsOrderStatusesToDefault();

            var salesItem = StatisticsModeCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag?.ToString(),
                    CommercialStatisticsModeDefaultsPolicy.DefaultMode,
                    StringComparison.OrdinalIgnoreCase));
            if (salesItem is not null)
            {
                StatisticsModeCombo.SelectedItem = salesItem;
            }

            StatisticsStatusesCombo.IsEnabled = false;
        }
        finally
        {
            _suppressCommercialStatisticsFilterEvents = previousSuppression;
        }

        ApplyCommercialStatisticsAdvancedFilters();
        _commercialStatisticsState.CriteriaChanged(periodChanged: false);
        UpdateCommercialStatisticsNavigation();
        ScheduleCommercialStatisticsRefresh();
    }

    private void RestoreCommercialStatisticsStatusFilter()
    {
        if (StatisticsStatusesCombo.Parent is not null)
        {
            StatisticsStatusesCombo.Visibility = Visibility.Visible;
            return;
        }

        if (StatisticsVolumeCombo.Parent is not Grid grid)
        {
            return;
        }

        // Row 2 originally contained the status selector. The old completed-only
        // acceptance override removed it and placed Reset there. Put statuses back
        // and move Reset to its own row.
        if (grid.RowDefinitions.Count < 4)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        if (_commercialStatisticsResetFiltersButton is not null)
        {
            Grid.SetRow(_commercialStatisticsResetFiltersButton, 3);
            Grid.SetColumn(_commercialStatisticsResetFiltersButton, 2);
            Grid.SetColumnSpan(_commercialStatisticsResetFiltersButton, 2);
        }

        StatisticsStatusesCombo.Visibility = Visibility.Visible;
        AddLabeledToolbarControl(grid, 2, "Статусы", StatisticsStatusesCombo, 2, 3);
    }

    private void StatisticsModeDefaultsResetStatuses_Click(object sender, RoutedEventArgs e)
    {
        var previousSuppression = _suppressCommercialStatisticsFilterEvents;
        _suppressCommercialStatisticsFilterEvents = true;
        try
        {
            ResetCommercialStatisticsOrderStatusesToDefault();
        }
        finally
        {
            _suppressCommercialStatisticsFilterEvents = previousSuppression;
        }

        ApplyCommercialStatisticsAdvancedFilters();
        _commercialStatisticsState.CriteriaChanged(periodChanged: false);
        UpdateCommercialStatisticsNavigation();
        ScheduleCommercialStatisticsRefresh();
    }

    private void ResetCommercialStatisticsOrderStatusesToDefault()
    {
        CommercialStatisticsModeDefaultsPolicy.ApplyDefaultOrderStatuses(_statisticsStatusOptions);
        StatisticsStatusesCombo.Text =
            CommercialStatisticsFilterOptions.BuildStatusesLabel(_statisticsStatusOptions);
    }

    private async void StatisticsModeDefaultsPartner_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_suppressCommercialStatisticsFilterEvents
            || !IsLoaded
            || StatisticsPartnerCombo.SelectedItem is not CommercialStatisticsEntityFilterOption partner
            || _commercialStatisticsScopedPartnerId == partner.Id)
        {
            return;
        }

        _commercialStatisticsScopedPartnerId = partner.Id;
        _statisticsPartnerId = partner.Id;
        var requestId = ++_commercialStatisticsPartnerFilterRequestId;

        ResetCommercialStatisticsDependentFiltersToAll();
        _commercialStatisticsState.CriteriaChanged(periodChanged: false);
        UpdateCommercialStatisticsNavigation();
        ScheduleCommercialStatisticsRefresh();

        if (!partner.Id.HasValue)
        {
            ApplyCommercialStatisticsPartnerItemScope(_items.Select(item => item.Id));
            return;
        }

        var from = StatisticsFromDate.SelectedDate;
        var to = StatisticsToDate.SelectedDate;
        if (!from.HasValue || !to.HasValue)
        {
            return;
        }

        var mode = (StatisticsModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()
                   ?? CommercialStatisticsModeDefaultsPolicy.DefaultMode;
        var statuses = CommercialStatisticsModeDefaultsPolicy.BuildStatusesCsv(
            mode,
            _statisticsStatusOptions);

        try
        {
            var result = await _services.WpfCommercialStatisticsApi.GetFilterOptionsAsync(
                new WpfCommercialStatisticsFilterOptionsRequest(
                    mode,
                    from.Value,
                    to.Value,
                    partner.Id.Value,
                    statuses)).ConfigureAwait(true);

            if (requestId != _commercialStatisticsPartnerFilterRequestId
                || _commercialStatisticsScopedPartnerId != partner.Id)
            {
                return;
            }

            ApplyCommercialStatisticsPartnerItemScope(result.ItemIds);
        }
        catch (Exception ex)
        {
            if (requestId != _commercialStatisticsPartnerFilterRequestId)
            {
                return;
            }

            _services.AppLogger.Warn(
                $"Commercial statistics partner filter options failed for partner {partner.Id}: {ex.Message}");
            ApplyCommercialStatisticsPartnerItemScope(_items.Select(item => item.Id));
        }
    }
}

internal static class CommercialStatisticsModeDefaultsPolicy
{
    public const string DefaultMode = "sales";

    private static readonly HashSet<string> DefaultOrderStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "DRAFT",
        "ACCEPTED",
        "IN_PROGRESS",
        "SHIPPED"
    };

    public static void ApplyDefaultOrderStatuses(
        IEnumerable<CommercialStatisticsStatusFilterOption> statuses)
    {
        foreach (var option in statuses)
        {
            option.IsChecked = DefaultOrderStatuses.Contains(option.Code);
        }
    }

    public static string? BuildStatusesCsv(
        string mode,
        IEnumerable<CommercialStatisticsStatusFilterOption> statuses) =>
        CommercialStatisticsFilterOptions.BuildStatusesCsv(mode, statuses);
}
