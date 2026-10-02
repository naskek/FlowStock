using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using WpfButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using WpfTextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace FlowStock.App;

public partial class MainWindow
{
    private bool _commercialStatisticsAcceptanceFixesApplied;
    private bool _preserveCommercialStatisticsMonthlyNavigation;
    private string? _commercialStatisticsNavigationSelectedMonth;
    private IReadOnlyList<WpfCommercialStatisticsMonth>? _commercialStatisticsNavigationMonths;
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
        RewireCommercialStatisticsMonthlyNavigation();
        ConfigureCommercialStatisticsCompletedOnly();

        StatisticsVolumeCombo.AddHandler(
            WpfButtonBase.ClickEvent,
            new RoutedEventHandler(StatisticsAcceptanceVolumeOption_Click),
            handledEventsToo: true);
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

        CaptureCommercialStatisticsNavigationMonths();
    }

    private void StatisticsAcceptanceMonthlyItemsSource_Changed(object? sender, EventArgs e)
    {
        if (_restoringCommercialStatisticsMonthSelection)
        {
            return;
        }

        var current = StatisticsMonthlyGrid.Items
            .OfType<WpfCommercialStatisticsMonth>()
            .ToArray();
        if (!_preserveCommercialStatisticsMonthlyNavigation)
        {
            if (current.Length > 1)
            {
                _commercialStatisticsNavigationMonths = current;
            }
            return;
        }

        if (current.Length > 1)
        {
            _commercialStatisticsNavigationMonths = current;
            return;
        }

        if (_commercialStatisticsNavigationMonths is { Count: > 1 })
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(RestoreCommercialStatisticsNavigationMonths));
        }
    }

    private void CaptureCommercialStatisticsNavigationMonths()
    {
        var current = StatisticsMonthlyGrid.Items
            .OfType<WpfCommercialStatisticsMonth>()
            .ToArray();
        if (current.Length > 1)
        {
            _commercialStatisticsNavigationMonths = current;
        }
    }

    private async void StatisticsAcceptanceMonthlyGrid_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!IsLoaded
            || _restoringCommercialStatisticsMonthSelection
            || StatisticsMonthlyGrid.SelectedItem is not WpfCommercialStatisticsMonth month
            || !DateTime.TryParseExact(
                month.Month,
                "yyyy-MM",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var selectedMonth))
        {
            return;
        }

        CaptureCommercialStatisticsNavigationMonths();
        _preserveCommercialStatisticsMonthlyNavigation = true;
        _commercialStatisticsNavigationSelectedMonth = month.Month;

        SetCommercialStatisticsPeriodControls(selectedMonth);
        _commercialStatisticsState.CriteriaChanged(periodChanged: true);
        _commercialStatisticsState.SelectDetailMonth(month.Month);
        UpdateCommercialStatisticsNavigation();
        await LoadCommercialStatisticsImmediatelyAsync().ConfigureAwait(true);
        RestoreCommercialStatisticsNavigationMonths();
    }

    private void RestoreCommercialStatisticsNavigationMonths()
    {
        if (_commercialStatisticsNavigationMonths is not { Count: > 1 } months)
        {
            return;
        }

        _restoringCommercialStatisticsMonthSelection = true;
        try
        {
            StatisticsMonthlyGrid.ItemsSource = months;
            var selected = months.FirstOrDefault(item => string.Equals(
                item.Month,
                _commercialStatisticsNavigationSelectedMonth,
                StringComparison.Ordinal));
            StatisticsMonthlyGrid.SelectedItem = selected;
            if (selected is not null)
            {
                StatisticsMonthlyGrid.ScrollIntoView(selected);
            }
        }
        finally
        {
            _restoringCommercialStatisticsMonthSelection = false;
        }
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

    private void StatisticsAcceptanceVolumeOption_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressCommercialStatisticsFilterEvents
            || e.OriginalSource is not System.Windows.Controls.CheckBox checkBox
            || checkBox.DataContext is not CommercialStatisticsVolumeFilterOption option)
        {
            return;
        }

        var previousSuppression = _suppressCommercialStatisticsFilterEvents;
        _suppressCommercialStatisticsFilterEvents = true;
        try
        {
            var specificOptions = _statisticsVolumeMultiOptions
                .Where(candidate => !candidate.IsAll)
                .ToArray();
            var allOption = _statisticsVolumeMultiOptions.FirstOrDefault(candidate => candidate.IsAll);

            if (option.IsAll)
            {
                foreach (var candidate in specificOptions)
                {
                    candidate.IsChecked = true;
                }
                if (allOption is not null)
                {
                    allOption.IsChecked = true;
                }
            }
            else if (allOption is not null)
            {
                allOption.IsChecked = specificOptions.All(candidate => candidate.IsChecked);
            }
        }
        finally
        {
            _suppressCommercialStatisticsFilterEvents = previousSuppression;
        }

        UpdateStatisticsVolumeFilterText();
        ApplyCommercialStatisticsAdvancedFilters();
        _commercialStatisticsState.CriteriaChanged(periodChanged: false);
        UpdateCommercialStatisticsNavigation();
        ScheduleCommercialStatisticsRefresh();
        Dispatcher.BeginInvoke(() => StatisticsVolumeCombo.IsDropDownOpen = true);
    }
}
