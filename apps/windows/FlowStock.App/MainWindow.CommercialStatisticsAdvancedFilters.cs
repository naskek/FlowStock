using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace FlowStock.App;

public partial class MainWindow
{
    private bool _commercialStatisticsAdvancedUiInitialized;
    private bool _applyingCommercialStatisticsMonth;
    private System.Windows.Controls.TextBox? _statisticsItemNameContainsText;
    private System.Windows.Controls.TextBox? _statisticsGtinsText;
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
        AddCommercialStatisticsMonthControls();
        AddCommercialStatisticsProductFilterControls();

        StatisticsFromDate.SelectedDateChanged += StatisticsManualPeriodDate_Changed;
        StatisticsToDate.SelectedDateChanged += StatisticsManualPeriodDate_Changed;

        StatisticsExportPdfButton.Click -= StatisticsExportPdf_Click;
        StatisticsExportExcelButton.Click -= StatisticsExportExcel_Click;
        StatisticsExportPdfButton.Click += StatisticsAdvancedExportPdf_Click;
        StatisticsExportExcelButton.Click += StatisticsAdvancedExportExcel_Click;

        ApplyCommercialStatisticsAdvancedFilters();
    }

    private void AddCommercialStatisticsMonthControls()
    {
        if (StatisticsToDate.Parent is not WrapPanel panel)
        {
            return;
        }

        var insertAt = panel.Children.IndexOf(StatisticsToDate) + 1;
        panel.Children.Insert(insertAt++, new TextBlock
        {
            Text = "Месяц",
            VerticalAlignment = VerticalAlignment.Center
        });

        _statisticsMonthDate = new DatePicker
        {
            Width = 125,
            Margin = new Thickness(6, 0, 6, 0),
            ToolTip = "Выберите любую дату нужного месяца"
        };
        _statisticsMonthDate.SelectedDateChanged += StatisticsMonthDate_Changed;
        panel.Children.Insert(insertAt++, _statisticsMonthDate);

        var currentButton = new System.Windows.Controls.Button
        {
            Content = "Текущий",
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(7, 2, 7, 2)
        };
        currentButton.Click += StatisticsCurrentMonth_Click;
        panel.Children.Insert(insertAt++, currentButton);

        var previousButton = new System.Windows.Controls.Button
        {
            Content = "Предыдущий",
            Margin = new Thickness(0, 0, 12, 0),
            Padding = new Thickness(7, 2, 7, 2)
        };
        previousButton.Click += StatisticsPreviousMonth_Click;
        panel.Children.Insert(insertAt, previousButton);
    }

    private void AddCommercialStatisticsProductFilterControls()
    {
        if (StatisticsItemCombo.Parent is not WrapPanel panel)
        {
            return;
        }

        var itemInsertAt = panel.Children.IndexOf(StatisticsItemCombo) + 1;
        panel.Children.Insert(itemInsertAt++, new TextBlock
        {
            Text = "Название содержит",
            VerticalAlignment = VerticalAlignment.Center
        });
        _statisticsItemNameContainsText = new System.Windows.Controls.TextBox
        {
            Width = 170,
            Margin = new Thickness(6, 0, 12, 0),
            ToolTip = "Подстрока текущего названия товара, без учёта регистра"
        };
        _statisticsItemNameContainsText.TextChanged += StatisticsAdvancedFilter_TextChanged;
        panel.Children.Insert(itemInsertAt, _statisticsItemNameContainsText);

        var gtinInsertAt = panel.Children.IndexOf(StatisticsGtinCombo) + 1;
        panel.Children.Insert(gtinInsertAt++, new TextBlock
        {
            Text = "GTIN набор",
            VerticalAlignment = VerticalAlignment.Center
        });
        _statisticsGtinsText = new System.Windows.Controls.TextBox
        {
            Width = 210,
            Margin = new Thickness(6, 0, 12, 0),
            ToolTip = "Несколько GTIN через запятую, точку с запятой или с новой строки"
        };
        _statisticsGtinsText.TextChanged += StatisticsAdvancedFilter_TextChanged;
        panel.Children.Insert(gtinInsertAt, _statisticsGtinsText);
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
