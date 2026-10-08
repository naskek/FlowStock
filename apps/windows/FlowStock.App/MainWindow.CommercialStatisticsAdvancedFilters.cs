using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Orientation = System.Windows.Controls.Orientation;
using WpfBinding = System.Windows.Data.Binding;
using WpfButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using WpfTextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace FlowStock.App;

public partial class MainWindow
{
    private bool _commercialStatisticsAdvancedUiInitialized;
    private bool _applyingCommercialStatisticsMonth;
    private bool _restoringCommercialStatisticsMonthSelection;
    private readonly ObservableCollection<CommercialStatisticsVolumeFilterOption> _statisticsVolumeMultiOptions = [];
    private DatePicker? _statisticsMonthDate;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        EnsureCommercialStatisticsAdvancedUi();
        if (IsUiPreview)
        {
            // Use exactly the production presentation pipeline, without its data loading.
            ApplyCommercialStatisticsAcceptanceFixes();
            ApplyCommercialStatisticsModeDefaults();
            ApplyCommercialStatisticsSelectorBlurFix();
            ApplyCommercialStatisticsDrillDownEnhancements();
            ApplyCommercialStatisticsContentLayout();
            UiPreviewContext.Prepare(this);
        }
    }

    private void EnsureCommercialStatisticsAdvancedUi()
    {
        if (_commercialStatisticsAdvancedUiInitialized)
        {
            return;
        }

        _commercialStatisticsAdvancedUiInitialized = true;
        RebuildCommercialStatisticsToolbar();
        RewireCommercialStatisticsAdvancedInteractions();

        StatisticsExportPdfButton.Click -= StatisticsExportPdf_Click;
        StatisticsExportExcelButton.Click -= StatisticsExportExcel_Click;
        StatisticsExportPdfButton.Click += StatisticsAdvancedExportPdf_Click;
        StatisticsExportExcelButton.Click += StatisticsAdvancedExportExcel_Click;

        ApplyCommercialStatisticsAdvancedFilters();
    }

    private void RewireCommercialStatisticsAdvancedInteractions()
    {
#pragma warning disable CS8622 // Legacy XAML handler uses non-null sender; WPF supplies the DatePicker instance.
        StatisticsFromDate.SelectedDateChanged -= StatisticsPeriod_Changed;
        StatisticsToDate.SelectedDateChanged -= StatisticsPeriod_Changed;
#pragma warning restore CS8622
        StatisticsFromDate.SelectedDateChanged += StatisticsAdvancedPeriod_Changed;
        StatisticsToDate.SelectedDateChanged += StatisticsAdvancedPeriod_Changed;

        StatisticsMonthlyGrid.SelectionChanged -= StatisticsMonthlyGrid_SelectionChanged;
        StatisticsMonthlyGrid.SelectionChanged += StatisticsAdvancedMonthlyGrid_SelectionChanged;

        StatisticsItemCombo.LostKeyboardFocus -= StatisticsSearchCombo_LostKeyboardFocus;
        StatisticsGtinCombo.LostKeyboardFocus -= StatisticsSearchCombo_LostKeyboardFocus;
        StatisticsItemCombo.SelectionChanged += StatisticsAdvancedSelector_SelectionChanged;
        StatisticsGtinCombo.SelectionChanged += StatisticsAdvancedSelector_SelectionChanged;
        StatisticsItemCombo.AddHandler(
            WpfTextBoxBase.TextChangedEvent,
            new TextChangedEventHandler(StatisticsAdvancedSelector_TextChanged),
            handledEventsToo: true);
        StatisticsGtinCombo.AddHandler(
            WpfTextBoxBase.TextChangedEvent,
            new TextChangedEventHandler(StatisticsAdvancedSelector_TextChanged),
            handledEventsToo: true);

        StatisticsVolumeCombo.PreviewKeyDown -= StatisticsSearchCombo_PreviewKeyDown;
        StatisticsVolumeCombo.LostKeyboardFocus -= StatisticsSearchCombo_LostKeyboardFocus;
        StatisticsVolumeCombo.SelectionChanged -= StatisticsCriteria_Changed;
        StatisticsVolumeCombo.RemoveHandler(
            WpfTextBoxBase.TextChangedEvent,
            new TextChangedEventHandler(StatisticsSearchCombo_TextChanged));
    }

    private void RebuildCommercialStatisticsToolbar()
    {
        if (StatisticsFromDate.Parent is not WrapPanel periodSource
            || StatisticsPartnerCombo.Parent is not WrapPanel filtersSource
            || StatisticsExportPdfButton.Parent is not WrapPanel actionsSource
            || periodSource.Parent is not StackPanel toolbarHost
            || filtersSource.Parent != toolbarHost
            || actionsSource.Parent != toolbarHost)
        {
            return;
        }

        _statisticsMonthDate = new DatePicker
        {
            ToolTip = "Выберите любую дату нужного месяца"
        };
        _statisticsMonthDate.SelectedDateChanged += StatisticsMonthDate_Changed;

        ConfigureStatisticsVolumeMultiSelect();

        var existingControls = new FrameworkElement[]
        {
            StatisticsFromDate,
            StatisticsToDate,
            StatisticsModeCombo,
            StatisticsGroupCombo,
            StatisticsPartnerCombo,
            StatisticsItemCombo,
            StatisticsGtinCombo,
            StatisticsBrandCombo,
            StatisticsVolumeCombo,
            StatisticsStatusesCombo,
            StatisticsExportPdfButton,
            StatisticsExportExcelButton,
            StatisticsExportStatusText
        };
        foreach (var control in existingControls)
        {
            DetachFromParent(control);
        }

        toolbarHost.Children.Clear();

        var layout = new Grid
        {
            Margin = new Thickness(0, 0, 0, 4)
        };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });

        var periodGroup = new System.Windows.Controls.GroupBox
        {
            Header = "Период",
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(8),
            Content = BuildCommercialStatisticsPeriodPanel()
        };
        Grid.SetColumn(periodGroup, 0);
        layout.Children.Add(periodGroup);

        var filtersGroup = new System.Windows.Controls.GroupBox
        {
            Header = "Фильтры",
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(8),
            Content = BuildCommercialStatisticsFiltersPanel()
        };
        Grid.SetColumn(filtersGroup, 1);
        layout.Children.Add(filtersGroup);

        var actionsGroup = new System.Windows.Controls.GroupBox
        {
            Header = "Отображение и действия",
            Padding = new Thickness(8),
            Content = BuildCommercialStatisticsActionsPanel()
        };
        Grid.SetColumn(actionsGroup, 2);
        layout.Children.Add(actionsGroup);

        toolbarHost.Children.Add(layout);
    }

    private UIElement BuildCommercialStatisticsPeriodPanel()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var index = 0; index < 4; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        PrepareToolbarControl(StatisticsFromDate, minWidth: 150);
        PrepareToolbarControl(StatisticsToDate, minWidth: 150);
        PrepareToolbarControl(_statisticsMonthDate!, minWidth: 150);

        AddLabeledToolbarControl(grid, 0, "С", StatisticsFromDate);
        AddLabeledToolbarControl(grid, 1, "По", StatisticsToDate);
        AddLabeledToolbarControl(grid, 2, "Месяц", _statisticsMonthDate!);

        var quickButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 0)
        };
        var currentButton = new System.Windows.Controls.Button
        {
            Content = "Текущий",
            Padding = new Thickness(10, 3, 10, 3)
        };
        currentButton.Click += StatisticsCurrentMonth_Click;
        quickButtons.Children.Add(currentButton);

        var previousButton = new System.Windows.Controls.Button
        {
            Content = "Предыдущий",
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(10, 3, 10, 3)
        };
        previousButton.Click += StatisticsPreviousMonth_Click;
        quickButtons.Children.Add(previousButton);

        Grid.SetRow(quickButtons, 3);
        Grid.SetColumn(quickButtons, 1);
        grid.Children.Add(quickButtons);
        return grid;
    }

    private UIElement BuildCommercialStatisticsFiltersPanel()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var index = 0; index < 3; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        foreach (var control in new FrameworkElement[]
                 {
                     StatisticsPartnerCombo,
                     StatisticsItemCombo,
                     StatisticsGtinCombo,
                     StatisticsBrandCombo,
                     StatisticsVolumeCombo,
                     StatisticsStatusesCombo
                 })
        {
            PrepareToolbarControl(control, minWidth: 120);
        }

        AddLabeledToolbarControl(grid, 0, "Контрагент", StatisticsPartnerCombo, 0, 1);
        AddLabeledToolbarControl(grid, 0, "Товар / название", StatisticsItemCombo, 2, 3);
        AddLabeledToolbarControl(grid, 1, "GTIN", StatisticsGtinCombo, 0, 1);
        AddLabeledToolbarControl(grid, 1, "Бренд", StatisticsBrandCombo, 2, 3);
        AddLabeledToolbarControl(grid, 2, "Фасовка", StatisticsVolumeCombo, 0, 1);
        AddLabeledToolbarControl(grid, 2, "Статусы", StatisticsStatusesCombo, 2, 3);
        return grid;
    }

    private UIElement BuildCommercialStatisticsActionsPanel()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        PrepareToolbarControl(StatisticsModeCombo, minWidth: 135);
        PrepareToolbarControl(StatisticsGroupCombo, minWidth: 135);
        AddLabeledToolbarControl(grid, 0, "Режим", StatisticsModeCombo);
        AddLabeledToolbarControl(grid, 1, "Группировка", StatisticsGroupCombo);

        var exportButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0)
        };
        StatisticsExportPdfButton.Margin = new Thickness(0);
        StatisticsExportPdfButton.Padding = new Thickness(10, 4, 10, 4);
        exportButtons.Children.Add(StatisticsExportPdfButton);

        StatisticsExportExcelButton.Margin = new Thickness(6, 0, 0, 0);
        StatisticsExportExcelButton.Padding = new Thickness(10, 4, 10, 4);
        exportButtons.Children.Add(StatisticsExportExcelButton);

        Grid.SetRow(exportButtons, 2);
        Grid.SetColumnSpan(exportButtons, 2);
        grid.Children.Add(exportButtons);

        StatisticsExportStatusText.Margin = new Thickness(0, 6, 0, 0);
        StatisticsExportStatusText.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(StatisticsExportStatusText, 3);
        Grid.SetColumnSpan(StatisticsExportStatusText, 2);
        grid.Children.Add(StatisticsExportStatusText);
        return grid;
    }

    private void ConfigureStatisticsVolumeMultiSelect()
    {
        _statisticsVolumeMultiOptions.Clear();
        foreach (var option in CommercialStatisticsVolumeFilterOptions.Build(_statisticsVolumeOptions))
        {
            _statisticsVolumeMultiOptions.Add(option);
        }

        _statisticsVolume = null;
        StatisticsVolumeCombo.ItemsSource = _statisticsVolumeMultiOptions;
        StatisticsVolumeCombo.SelectedIndex = -1;
        StatisticsVolumeCombo.IsEditable = true;
        StatisticsVolumeCombo.IsReadOnly = true;
        StatisticsVolumeCombo.IsTextSearchEnabled = false;
        StatisticsVolumeCombo.StaysOpenOnEdit = true;

        var checkBoxFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.CheckBox));
        checkBoxFactory.SetBinding(
            ContentControl.ContentProperty,
            new WpfBinding(nameof(CommercialStatisticsVolumeFilterOption.Label)));
        checkBoxFactory.SetBinding(
            ToggleButton.IsCheckedProperty,
            new WpfBinding(nameof(CommercialStatisticsVolumeFilterOption.IsChecked))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
        checkBoxFactory.AddHandler(
            WpfButtonBase.ClickEvent,
            new RoutedEventHandler(StatisticsVolumeOption_Click));
        StatisticsVolumeCombo.ItemTemplate = new DataTemplate
        {
            VisualTree = checkBoxFactory
        };
        UpdateStatisticsVolumeFilterText();
    }

    private void StatisticsVolumeOption_Click(object sender, RoutedEventArgs e)
    {
        if (IsUiPreview) return;

        if (_suppressCommercialStatisticsFilterEvents)
        {
            return;
        }

        UpdateStatisticsVolumeFilterText();
        ApplyCommercialStatisticsAdvancedFilters();
        _commercialStatisticsState.CriteriaChanged(periodChanged: false);
        UpdateCommercialStatisticsNavigation();
        ScheduleCommercialStatisticsRefresh();
        Dispatcher.BeginInvoke(() => StatisticsVolumeCombo.IsDropDownOpen = true);
    }

    private void UpdateStatisticsVolumeFilterText()
    {
        StatisticsVolumeCombo.Text =
            CommercialStatisticsVolumeFilterOptions.BuildLabel(_statisticsVolumeMultiOptions);
    }

    private static void AddLabeledToolbarControl(
        Grid grid,
        int row,
        string label,
        FrameworkElement control,
        int labelColumn = 0,
        int controlColumn = 1)
    {
        var text = new TextBlock
        {
            Text = label,
            Margin = new Thickness(0, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(text, row);
        Grid.SetColumn(text, labelColumn);
        grid.Children.Add(text);

        Grid.SetRow(control, row);
        Grid.SetColumn(control, controlColumn);
        grid.Children.Add(control);
    }

    private static void PrepareToolbarControl(FrameworkElement control, double minWidth)
    {
        control.Width = double.NaN;
        control.MinWidth = minWidth;
        control.Margin = new Thickness(0, 2, 12, 2);
        control.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        control.VerticalAlignment = VerticalAlignment.Center;
    }

    private static void DetachFromParent(UIElement element)
    {
        if (element is FrameworkElement { Parent: System.Windows.Controls.Panel panel })
        {
            panel.Children.Remove(element);
        }
    }

    private void StatisticsAdvancedSelector_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsUiPreview) return;

        if (_suppressCommercialStatisticsFilterEvents || !IsLoaded)
        {
            return;
        }

        ApplyCommercialStatisticsAdvancedFilters();
        _commercialStatisticsState.CriteriaChanged(periodChanged: false);
        UpdateCommercialStatisticsNavigation();
        ScheduleCommercialStatisticsRefresh();
    }

    private void StatisticsAdvancedSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsUiPreview) return;

        if (_suppressCommercialStatisticsFilterEvents || !IsLoaded)
        {
            return;
        }

        ApplyCommercialStatisticsAdvancedFilters();
        _commercialStatisticsState.CriteriaChanged(periodChanged: false);
        UpdateCommercialStatisticsNavigation();
        ScheduleCommercialStatisticsRefresh();
    }

    private void ApplyCommercialStatisticsAdvancedFilters()
    {
        var itemNameContains = ResolveCommercialStatisticsItemFilter();
        ResolveCommercialStatisticsGtinFilter();
        var volumes = CommercialStatisticsVolumeFilterOptions.BuildCsv(_statisticsVolumeMultiOptions);
        _commercialStatisticsState.SetAdvancedFilters(
            gtins: null,
            itemNameContains,
            volumes);
    }

    private string? ResolveCommercialStatisticsItemFilter()
    {
        var entered = StatisticsItemCombo.Text?.Trim();
        var exact = _statisticsItemOptions.FirstOrDefault(option =>
            option.Id.HasValue
            && string.Equals(option.Label.Trim(), entered, StringComparison.OrdinalIgnoreCase));
        _statisticsItemId = exact?.Id;
        if (exact is not null || string.IsNullOrWhiteSpace(entered))
        {
            return null;
        }

        var allOption = _statisticsItemOptions.FirstOrDefault(option => !option.Id.HasValue);
        if (allOption is not null
            && string.Equals(allOption.Label.Trim(), entered, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return CommercialStatisticsAdvancedFilters.NormalizeItemNameContains(entered);
    }

    private void ResolveCommercialStatisticsGtinFilter()
    {
        var entered = RemoveCommercialStatisticsWhitespace(StatisticsGtinCombo.Text);
        _statisticsGtin = _statisticsGtinOptions
            .FirstOrDefault(option =>
                option.Value is not null
                && string.Equals(
                    RemoveCommercialStatisticsWhitespace(option.Value),
                    entered,
                    StringComparison.OrdinalIgnoreCase))
            ?.Value;
    }

    private static string RemoveCommercialStatisticsWhitespace(string? value) =>
        string.Concat((value ?? string.Empty).Where(character => !char.IsWhiteSpace(character)));

    private async void StatisticsAdvancedMonthlyGrid_SelectionChanged(
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

        SetCommercialStatisticsPeriodControls(selectedMonth);
        _commercialStatisticsState.CriteriaChanged(periodChanged: true);
        _commercialStatisticsState.SelectDetailMonth(month.Month);
        UpdateCommercialStatisticsNavigation();
        await LoadCommercialStatisticsImmediatelyAsync().ConfigureAwait(true);
        RestoreCommercialStatisticsMonthlySelection(month.Month);
    }

    private void RestoreCommercialStatisticsMonthlySelection(string month)
    {
        var row = StatisticsMonthlyGrid.Items
            .OfType<WpfCommercialStatisticsMonth>()
            .FirstOrDefault(item => string.Equals(item.Month, month, StringComparison.Ordinal));
        if (row is null)
        {
            return;
        }

        _restoringCommercialStatisticsMonthSelection = true;
        try
        {
            StatisticsMonthlyGrid.SelectedItem = row;
            StatisticsMonthlyGrid.ScrollIntoView(row);
        }
        finally
        {
            _restoringCommercialStatisticsMonthSelection = false;
        }
    }

    private void StatisticsAdvancedPeriod_Changed(object? sender, EventArgs e)
    {
        if (IsUiPreview) return;

        if (!IsLoaded || _applyingCommercialStatisticsMonth)
        {
            return;
        }

        _applyingCommercialStatisticsMonth = true;
        try
        {
            if (_statisticsMonthDate != null)
            {
                _statisticsMonthDate.SelectedDate = null;
            }
        }
        finally
        {
            _applyingCommercialStatisticsMonth = false;
        }

        StatisticsMonthlyGrid.SelectedItem = null;
        _commercialStatisticsState.CriteriaChanged(periodChanged: true);
        UpdateCommercialStatisticsNavigation();
        ScheduleCommercialStatisticsRefresh();
    }

    private void StatisticsMonthDate_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (IsUiPreview) return;

        if (!IsLoaded
            || _applyingCommercialStatisticsMonth
            || _statisticsMonthDate?.SelectedDate is not DateTime selected)
        {
            return;
        }

        ApplyCommercialStatisticsMonth(selected);
    }

    private void StatisticsCurrentMonth_Click(object sender, RoutedEventArgs e)
    {
        if (IsUiPreview) return;

        ApplyCommercialStatisticsMonth(DateTime.Today);
    }

    private void StatisticsPreviousMonth_Click(object sender, RoutedEventArgs e)
    {
        if (IsUiPreview) return;

        ApplyCommercialStatisticsMonth(DateTime.Today.AddMonths(-1));
    }

    private void ApplyCommercialStatisticsMonth(DateTime value)
    {
        SetCommercialStatisticsPeriodControls(value);
        StatisticsMonthlyGrid.SelectedItem = null;
        _commercialStatisticsState.CriteriaChanged(periodChanged: true);
        UpdateCommercialStatisticsNavigation();
        ScheduleCommercialStatisticsRefresh();
    }

    private void SetCommercialStatisticsPeriodControls(DateTime value)
    {
        var period = CommercialStatisticsAdvancedFilters.MonthPeriod(value);
        _applyingCommercialStatisticsMonth = true;
        try
        {
            if (_statisticsMonthDate != null)
            {
                _statisticsMonthDate.SelectedDate = new DateTime(value.Year, value.Month, 1);
            }
            StatisticsFromDate.SelectedDate = period.From;
            StatisticsToDate.SelectedDate = period.To;
        }
        finally
        {
            _applyingCommercialStatisticsMonth = false;
        }
    }

    private async void StatisticsAdvancedExportPdf_Click(object sender, RoutedEventArgs e)
    {
        if (IsUiPreview) return;

        await ExportCommercialStatisticsWithAdvancedFiltersAsync(pdf: true).ConfigureAwait(true);
    }

    private async void StatisticsAdvancedExportExcel_Click(object sender, RoutedEventArgs e)
    {
        if (IsUiPreview) return;

        await ExportCommercialStatisticsWithAdvancedFiltersAsync(pdf: false).ConfigureAwait(true);
    }

    private async Task ExportCommercialStatisticsWithAdvancedFiltersAsync(bool pdf)
    {
        if (_commercialStatisticsExportInProgress)
        {
            return;
        }

        ApplyCommercialStatisticsAdvancedFilters();
        var filters = BuildCurrentCommercialStatisticsFilters(out var validationError);
        if (filters is null)
        {
            StatisticsExportStatusText.Text = validationError;
            return;
        }

        filters = filters with
        {
            Volume = null,
            Gtins = null,
            ItemNameContains = _commercialStatisticsState.ItemNameContains
        };

        var baseSelection = BuildCommercialStatisticsExportSelection(filters);
        var selection = baseSelection with
        {
            Request = baseSelection.Request with
            {
                Gtins = null,
                ItemNameContains = _commercialStatisticsState.ItemNameContains,
                Volume = null,
                Volumes = _commercialStatisticsState.Volumes
            },
            GtinsLabel = null,
            ItemNameContainsLabel = _commercialStatisticsState.ItemNameContains,
            VolumeLabel = CommercialStatisticsVolumeFilterOptions.BuildLabel(_statisticsVolumeMultiOptions)
        };

        var extension = pdf ? ".pdf" : ".xlsx";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = pdf ? "Сохранить отчёт статистики PDF" : "Сохранить отчёт статистики Excel",
            Filter = pdf ? "PDF (*.pdf)|*.pdf" : "Excel (*.xlsx)|*.xlsx",
            DefaultExt = extension,
            AddExtension = true,
            OverwritePrompt = true,
            FileName = BuildCommercialStatisticsExportFileName(filters, extension)
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _commercialStatisticsExportInProgress = true;
        UpdateCommercialStatisticsExportButtons();
        StatisticsExportStatusText.Text = "Загрузка полного отчёта...";
        try
        {
            var report = await CommercialStatisticsExportLoader.LoadAsync(
                selection,
                _services.WpfCommercialStatisticsApi.GetAsync).ConfigureAwait(true);

            StatisticsExportStatusText.Text = pdf
                ? "Формирование PDF..."
                : "Формирование Excel...";
            var content = pdf
                ? CommercialStatisticsPdfExporter.Create(report)
                : CommercialStatisticsExcelExporter.Create(report);
            CommercialStatisticsExportFileWriter.WriteAtomically(dialog.FileName, content);
            StatisticsExportStatusText.Text = $"Сохранено: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("commercial statistics advanced export failed", ex);
            StatisticsExportStatusText.Text = "Не удалось сформировать отчёт.";
            System.Windows.MessageBox.Show(
                ex.Message,
                "Экспорт статистики",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _commercialStatisticsExportInProgress = false;
            UpdateCommercialStatisticsExportButtons();
        }
    }
}
