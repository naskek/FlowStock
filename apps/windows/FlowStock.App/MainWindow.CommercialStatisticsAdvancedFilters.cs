using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace FlowStock.App;

public partial class MainWindow
{
    private bool _commercialStatisticsAdvancedUiInitialized;
    private bool _applyingCommercialStatisticsMonth;
    private TextBox? _statisticsItemNameContainsText;
    private TextBox? _statisticsGtinsText;
    private DatePicker? _statisticsMonthDate;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        EnsureCommercialStatisticsAdvancedUi();
    }

    private void EnsureCommercialStatisticsAdvancedUi()
    {
        if (_commercialStatisticsAdvancedUiInitialized)
        {
            return;
        }

        _commercialStatisticsAdvancedUiInitialized = true;
        RebuildCommercialStatisticsToolbar();

        StatisticsFromDate.SelectedDateChanged += StatisticsManualPeriodDate_Changed;
        StatisticsToDate.SelectedDateChanged += StatisticsManualPeriodDate_Changed;

        StatisticsExportPdfButton.Click -= StatisticsExportPdf_Click;
        StatisticsExportExcelButton.Click -= StatisticsExportExcel_Click;
        StatisticsExportPdfButton.Click += StatisticsAdvancedExportPdf_Click;
        StatisticsExportExcelButton.Click += StatisticsAdvancedExportExcel_Click;

        ApplyCommercialStatisticsAdvancedFilters();
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

        _statisticsItemNameContainsText = new TextBox
        {
            ToolTip = "Подстрока текущего названия товара, без учёта регистра"
        };
        _statisticsItemNameContainsText.TextChanged += StatisticsAdvancedFilter_TextChanged;

        _statisticsGtinsText = new TextBox
        {
            ToolTip = "Несколько GTIN через запятую, точку с запятой или с новой строки"
        };
        _statisticsGtinsText.TextChanged += StatisticsAdvancedFilter_TextChanged;

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

        var periodGroup = new GroupBox
        {
            Header = "Период",
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(8),
            Content = BuildCommercialStatisticsPeriodPanel()
        };
        Grid.SetColumn(periodGroup, 0);
        layout.Children.Add(periodGroup);

        var filtersGroup = new GroupBox
        {
            Header = "Фильтры",
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(8),
            Content = BuildCommercialStatisticsFiltersPanel()
        };
        Grid.SetColumn(filtersGroup, 1);
        layout.Children.Add(filtersGroup);

        var actionsGroup = new GroupBox
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
        var currentButton = new Button
        {
            Content = "Текущий",
            Padding = new Thickness(10, 3, 10, 3)
        };
        currentButton.Click += StatisticsCurrentMonth_Click;
        quickButtons.Children.Add(currentButton);

        var previousButton = new Button
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
        for (var index = 0; index < 4; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        foreach (var control in new FrameworkElement[]
                 {
                     StatisticsPartnerCombo,
                     StatisticsItemCombo,
                     _statisticsItemNameContainsText!,
                     StatisticsGtinCombo,
                     _statisticsGtinsText!,
                     StatisticsBrandCombo,
                     StatisticsVolumeCombo,
                     StatisticsStatusesCombo
                 })
        {
            PrepareToolbarControl(control, minWidth: 120);
        }

        AddLabeledToolbarControl(grid, 0, "Контрагент", StatisticsPartnerCombo, 0, 1);
        AddLabeledToolbarControl(grid, 0, "Товар", StatisticsItemCombo, 2, 3);
        AddLabeledToolbarControl(grid, 1, "Название содержит", _statisticsItemNameContainsText!, 0, 1);
        AddLabeledToolbarControl(grid, 1, "GTIN", StatisticsGtinCombo, 2, 3);
        AddLabeledToolbarControl(grid, 2, "GTIN набор", _statisticsGtinsText!, 0, 1);
        AddLabeledToolbarControl(grid, 2, "Бренд", StatisticsBrandCombo, 2, 3);
        AddLabeledToolbarControl(grid, 3, "Фасовка", StatisticsVolumeCombo, 0, 1);
        AddLabeledToolbarControl(grid, 3, "Статусы", StatisticsStatusesCombo, 2, 3);
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
            HorizontalAlignment = HorizontalAlignment.Right,
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
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        control.VerticalAlignment = VerticalAlignment.Center;
    }

    private static void DetachFromParent(UIElement element)
    {
        if (element is FrameworkElement { Parent: Panel panel })
        {
            panel.Children.Remove(element);
        }
    }

    private void StatisticsAdvancedFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
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
        _commercialStatisticsState.SetAdvancedFilters(
            CommercialStatisticsAdvancedFilters.NormalizeGtinsCsv(_statisticsGtinsText?.Text),
            CommercialStatisticsAdvancedFilters.NormalizeItemNameContains(
                _statisticsItemNameContainsText?.Text));
    }

    private void StatisticsMonthDate_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _statisticsMonthDate?.SelectedDate is not DateTime selected)
        {
            return;
        }

        ApplyCommercialStatisticsMonth(selected);
    }

    private void StatisticsCurrentMonth_Click(object sender, RoutedEventArgs e)
    {
        SetCommercialStatisticsMonth(DateTime.Today);
    }

    private void StatisticsPreviousMonth_Click(object sender, RoutedEventArgs e)
    {
        SetCommercialStatisticsMonth(DateTime.Today.AddMonths(-1));
    }

    private void SetCommercialStatisticsMonth(DateTime value)
    {
        if (_statisticsMonthDate != null)
        {
            _statisticsMonthDate.SelectedDate = value;
        }
        ApplyCommercialStatisticsMonth(value);
    }

    private void ApplyCommercialStatisticsMonth(DateTime value)
    {
        var period = CommercialStatisticsAdvancedFilters.MonthPeriod(value);
        if (StatisticsFromDate.SelectedDate == period.From
            && StatisticsToDate.SelectedDate == period.To)
        {
            return;
        }

        _applyingCommercialStatisticsMonth = true;
        try
        {
            StatisticsFromDate.SelectedDate = period.From;
            StatisticsToDate.SelectedDate = period.To;
        }
        finally
        {
            _applyingCommercialStatisticsMonth = false;
        }
    }

    private void StatisticsManualPeriodDate_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_applyingCommercialStatisticsMonth || _statisticsMonthDate == null)
        {
            return;
        }

        _statisticsMonthDate.SelectedDate = null;
    }

    private async void StatisticsAdvancedExportPdf_Click(object sender, RoutedEventArgs e)
    {
        await ExportCommercialStatisticsWithAdvancedFiltersAsync(pdf: true).ConfigureAwait(true);
    }

    private async void StatisticsAdvancedExportExcel_Click(object sender, RoutedEventArgs e)
    {
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
            Gtins = _commercialStatisticsState.Gtins,
            ItemNameContains = _commercialStatisticsState.ItemNameContains
        };

        var baseSelection = BuildCommercialStatisticsExportSelection(filters);
        var selection = baseSelection with
        {
            Request = baseSelection.Request with
            {
                Gtins = filters.Gtins,
                ItemNameContains = filters.ItemNameContains
            },
            GtinsLabel = filters.Gtins,
            ItemNameContainsLabel = filters.ItemNameContains
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
