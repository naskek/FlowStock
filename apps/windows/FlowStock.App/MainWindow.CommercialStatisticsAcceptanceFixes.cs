using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WpfTextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace FlowStock.App;

public partial class MainWindow
{
    private bool _commercialStatisticsAcceptanceFixesApplied;
    private string? _commercialStatisticsNavigationSelectedMonth;
    private DependencyPropertyDescriptor? _commercialStatisticsMonthlyItemsSourceDescriptor;

    private void ApplyCommercialStatisticsAcceptanceFixes()
    {
        if (_commercialStatisticsAcceptanceFixesApplied)
        {
            return;
        }

        if (!_commercialStatisticsAdvancedUiInitialized)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(ApplyCommercialStatisticsAcceptanceFixes));
            return;
        }

        _commercialStatisticsAcceptanceFixesApplied = true;
        RewireCommercialStatisticsAcceptanceSelectors();
        RewireCommercialStatisticsVolumeChecklist();
        RewireCommercialStatisticsMonthlyNavigation();
        RewireCommercialStatisticsWholePeriodAction();
        RewireCommercialStatisticsExportActions();
        ConfigureCommercialStatisticsCompletedOnly();
    }

    private void RewireCommercialStatisticsAcceptanceSelectors()
    {
        foreach (var comboBox in new[] { StatisticsItemCombo, StatisticsGtinCombo })
        {
            comboBox.RemoveHandler(
                WpfTextBoxBase.TextChangedEvent,
                new TextChangedEventHandler(StatisticsSearchCombo_TextChanged));
            comboBox.RemoveHandler(
                WpfTextBoxBase.TextChangedEvent,
                new TextChangedEventHandler(StatisticsAdvancedSelector_TextChanged));
            comboBox.DropDownClosed -= StatisticsSearchCombo_DropDownClosed;
            comboBox.PreviewKeyDown -= StatisticsSearchCombo_PreviewKeyDown;
            comboBox.LostKeyboardFocus -= StatisticsSearchCombo_LostKeyboardFocus;
            comboBox.SelectionChanged -= StatisticsCriteria_Changed;
            comboBox.SelectionChanged -= StatisticsAdvancedSelector_SelectionChanged;

            comboBox.GotKeyboardFocus += StatisticsAcceptanceSelector_GotKeyboardFocus;
            comboBox.SelectionChanged += StatisticsAcceptanceSelector_SelectionChanged;
            comboBox.AddHandler(
                WpfTextBoxBase.TextChangedEvent,
                new TextChangedEventHandler(StatisticsAcceptanceSelector_TextChanged),
                handledEventsToo: true);
        }
    }

    private void RewireCommercialStatisticsVolumeChecklist()
    {
        StatisticsVolumeCombo.RemoveHandler(
            WpfTextBoxBase.TextChangedEvent,
            new TextChangedEventHandler(StatisticsSearchCombo_TextChanged));
        StatisticsVolumeCombo.DropDownClosed -= StatisticsSearchCombo_DropDownClosed;
        StatisticsVolumeCombo.PreviewKeyDown -= StatisticsSearchCombo_PreviewKeyDown;
        StatisticsVolumeCombo.LostKeyboardFocus -= StatisticsSearchCombo_LostKeyboardFocus;
        StatisticsVolumeCombo.SelectionChanged -= StatisticsCriteria_Changed;
    }

    private void StatisticsAcceptanceSelector_GotKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        if (_suppressCommercialStatisticsFilterEvents
            || sender is not System.Windows.Controls.ComboBox comboBox)
        {
            return;
        }

        var allLabel = ReferenceEquals(comboBox, StatisticsItemCombo)
            ? "Все товары"
            : ReferenceEquals(comboBox, StatisticsGtinCombo)
                ? "Все GTIN"
                : null;
        if (allLabel is null)
        {
            return;
        }

        if (string.Equals(comboBox.Text?.Trim(), allLabel, StringComparison.OrdinalIgnoreCase))
        {
            var previousSuppression = _suppressCommercialStatisticsFilterEvents;
            _suppressCommercialStatisticsFilterEvents = true;
            try
            {
                comboBox.SelectedItem = null;
                comboBox.Text = string.Empty;
            }
            finally
            {
                _suppressCommercialStatisticsFilterEvents = previousSuppression;
            }
        }

        comboBox.IsDropDownOpen = true;
    }

    private void StatisticsAcceptanceSelector_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressCommercialStatisticsFilterEvents
            || !IsLoaded
            || sender is not System.Windows.Controls.ComboBox comboBox
            || !comboBox.IsKeyboardFocusWithin)
        {
            return;
        }

        var query = comboBox.Text ?? string.Empty;
        var previousSuppression = _suppressCommercialStatisticsFilterEvents;
        _suppressCommercialStatisticsFilterEvents = true;
        try
        {
            if (ReferenceEquals(comboBox, StatisticsItemCombo))
            {
                comboBox.ItemsSource = CommercialStatisticsFilterOptions.SearchEntities(
                    _statisticsItemOptions,
                    query);
            }
            else if (ReferenceEquals(comboBox, StatisticsGtinCombo))
            {
                comboBox.ItemsSource = CommercialStatisticsFilterOptions.SearchText(
                    _statisticsGtinOptions,
                    query);
            }
            else
            {
                return;
            }

            comboBox.SelectedItem = null;
            comboBox.Text = query;
        }
        finally
        {
            _suppressCommercialStatisticsFilterEvents = previousSuppression;
        }

        comboBox.IsDropDownOpen = comboBox.Items.Count > 0;
        RestoreCommercialStatisticsComboText(comboBox, query);
        ApplyCommercialStatisticsAdvancedFilters();
        _commercialStatisticsState.CriteriaChanged(periodChanged: false);
        UpdateCommercialStatisticsNavigation();
        ScheduleCommercialStatisticsRefresh();
    }

    private void StatisticsAcceptanceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCommercialStatisticsFilterEvents
            || !IsLoaded
            || sender is not System.Windows.Controls.ComboBox comboBox
            || comboBox.SelectedItem is null)
        {
            return;
        }

        var selected = comboBox.SelectedItem;
        var selectedLabel = GetCommercialStatisticsOptionLabel(selected) ?? string.Empty;
        var previousSuppression = _suppressCommercialStatisticsFilterEvents;
        _suppressCommercialStatisticsFilterEvents = true;
        try
        {
            if (ReferenceEquals(comboBox, StatisticsItemCombo))
            {
                comboBox.ItemsSource = _statisticsItemOptions;
            }
            else if (ReferenceEquals(comboBox, StatisticsGtinCombo))
            {
                comboBox.ItemsSource = _statisticsGtinOptions;
            }
            else
            {
                return;
            }

            comboBox.SelectedItem = selected;
            comboBox.Text = selectedLabel;
        }
        finally
        {
            _suppressCommercialStatisticsFilterEvents = previousSuppression;
        }

        comboBox.IsDropDownOpen = false;
        ApplyCommercialStatisticsAdvancedFilters();
        _commercialStatisticsState.CriteriaChanged(periodChanged: false);
        UpdateCommercialStatisticsNavigation();
        ScheduleCommercialStatisticsRefresh();
    }

    private void RewireCommercialStatisticsMonthlyNavigation()
    {
        StatisticsMonthlyGrid.SelectionChanged -= StatisticsMonthlyGrid_SelectionChanged;
        StatisticsMonthlyGrid.SelectionChanged -= StatisticsAdvancedMonthlyGrid_SelectionChanged;
        StatisticsMonthlyGrid.SelectionChanged += StatisticsAcceptanceMonthlyGrid_SelectionChanged;

        _commercialStatisticsMonthlyItemsSourceDescriptor =
            DependencyPropertyDescriptor.FromProperty(
                ItemsControl.ItemsSourceProperty,
                typeof(DataGrid));
        _commercialStatisticsMonthlyItemsSourceDescriptor?.AddValueChanged(
            StatisticsMonthlyGrid,
            StatisticsAcceptanceMonthlyItemsSource_Changed);
    }

    private void RewireCommercialStatisticsWholePeriodAction()
    {
        StatisticsAllPeriodButton.Click -= StatisticsAllPeriod_Click;
        StatisticsAllPeriodButton.Click += StatisticsAcceptanceAllPeriod_Click;
    }

    private void RewireCommercialStatisticsExportActions()
    {
        StatisticsExportPdfButton.Click -= StatisticsAdvancedExportPdf_Click;
        StatisticsExportExcelButton.Click -= StatisticsAdvancedExportExcel_Click;
        StatisticsExportPdfButton.Click += StatisticsAcceptanceExportPdf_Click;
        StatisticsExportExcelButton.Click += StatisticsAcceptanceExportExcel_Click;
    }

    private void StatisticsAcceptanceMonthlyItemsSource_Changed(object? sender, EventArgs e)
    {
        if (_restoringCommercialStatisticsMonthSelection
            || string.IsNullOrWhiteSpace(_commercialStatisticsNavigationSelectedMonth)
            || !string.Equals(
                _commercialStatisticsState.DetailMonth,
                _commercialStatisticsNavigationSelectedMonth,
                StringComparison.Ordinal))
        {
            return;
        }

        var selectedMonth = _commercialStatisticsNavigationSelectedMonth;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => RestoreCommercialStatisticsMonthlySelection(selectedMonth)));
    }

    private async void StatisticsAcceptanceMonthlyGrid_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _restoringCommercialStatisticsMonthSelection)
        {
            return;
        }

        if (StatisticsMonthlyGrid.SelectedItem is not WpfCommercialStatisticsMonth month)
        {
            if (string.IsNullOrWhiteSpace(_commercialStatisticsState.DetailMonth))
            {
                _commercialStatisticsNavigationSelectedMonth = null;
            }
            return;
        }

        if (!DateTime.TryParseExact(
                month.Month,
                "yyyy-MM",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var selectedMonth))
        {
            return;
        }

        var navigationFrom = _commercialStatisticsState.DetailPeriodFrom
            ?? StatisticsFromDate.SelectedDate;
        var navigationTo = _commercialStatisticsState.DetailPeriodTo
            ?? StatisticsToDate.SelectedDate;
        if (!navigationFrom.HasValue || !navigationTo.HasValue)
        {
            return;
        }

        _commercialStatisticsNavigationSelectedMonth = month.Month;
        SetCommercialStatisticsPeriodControls(selectedMonth);
        _commercialStatisticsState.CriteriaChanged(periodChanged: false);
        _commercialStatisticsState.SelectDetailMonth(
            month.Month,
            navigationFrom.Value,
            navigationTo.Value);
        UpdateCommercialStatisticsNavigation();
        await LoadCommercialStatisticsImmediatelyAsync().ConfigureAwait(true);
        RestoreCommercialStatisticsMonthlySelection(month.Month);
    }

    private async void StatisticsAcceptanceAllPeriod_Click(object sender, RoutedEventArgs e)
    {
        var navigationFrom = _commercialStatisticsState.DetailPeriodFrom;
        var navigationTo = _commercialStatisticsState.DetailPeriodTo;
        if (!_commercialStatisticsState.ReturnToWholePeriod())
        {
            return;
        }

        _commercialStatisticsNavigationSelectedMonth = null;
        _applyingCommercialStatisticsMonth = true;
        try
        {
            if (_statisticsMonthDate is not null)
            {
                _statisticsMonthDate.SelectedDate = null;
            }
            if (navigationFrom.HasValue)
            {
                StatisticsFromDate.SelectedDate = navigationFrom.Value;
            }
            if (navigationTo.HasValue)
            {
                StatisticsToDate.SelectedDate = navigationTo.Value;
            }
            StatisticsMonthlyGrid.SelectedItem = null;
        }
        finally
        {
            _applyingCommercialStatisticsMonth = false;
        }

        UpdateCommercialStatisticsNavigation();
        await LoadCommercialStatisticsImmediatelyAsync().ConfigureAwait(true);
    }

    private async void StatisticsAcceptanceExportPdf_Click(object sender, RoutedEventArgs e)
    {
        await ExportCommercialStatisticsWithAcceptancePeriodAsync(pdf: true).ConfigureAwait(true);
    }

    private async void StatisticsAcceptanceExportExcel_Click(object sender, RoutedEventArgs e)
    {
        await ExportCommercialStatisticsWithAcceptancePeriodAsync(pdf: false).ConfigureAwait(true);
    }

    private async Task ExportCommercialStatisticsWithAcceptancePeriodAsync(bool pdf)
    {
        var navigationFrom = _commercialStatisticsState.DetailPeriodFrom;
        var navigationTo = _commercialStatisticsState.DetailPeriodTo;
        if (string.IsNullOrWhiteSpace(_commercialStatisticsState.DetailMonth)
            || !navigationFrom.HasValue
            || !navigationTo.HasValue)
        {
            await ExportCommercialStatisticsWithAdvancedFiltersAsync(pdf).ConfigureAwait(true);
            return;
        }

        var visibleFrom = StatisticsFromDate.SelectedDate;
        var visibleTo = StatisticsToDate.SelectedDate;
        Task exportTask;

        _applyingCommercialStatisticsMonth = true;
        try
        {
            StatisticsFromDate.SelectedDate = navigationFrom.Value;
            StatisticsToDate.SelectedDate = navigationTo.Value;
            exportTask = ExportCommercialStatisticsWithAdvancedFiltersAsync(pdf);
        }
        finally
        {
            StatisticsFromDate.SelectedDate = visibleFrom;
            StatisticsToDate.SelectedDate = visibleTo;
            _applyingCommercialStatisticsMonth = false;
        }

        await exportTask.ConfigureAwait(true);
    }

    private void ConfigureCommercialStatisticsCompletedOnly()
    {
        foreach (var option in _statisticsStatusOptions)
        {
            option.IsChecked = string.Equals(
                option.Code,
                "SHIPPED",
                StringComparison.OrdinalIgnoreCase);
        }

        if (StatisticsStatusesCombo.Parent is not Grid grid)
        {
            StatisticsStatusesCombo.Visibility = Visibility.Collapsed;
            return;
        }

        var labels = grid.Children
            .OfType<TextBlock>()
            .Where(text => string.Equals(text.Text, "Статусы", StringComparison.Ordinal))
            .ToArray();
        foreach (var label in labels)
        {
            grid.Children.Remove(label);
        }

        grid.Children.Remove(StatisticsStatusesCombo);
    }
}
