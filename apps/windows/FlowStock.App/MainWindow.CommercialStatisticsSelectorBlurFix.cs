using System.Windows.Input;

namespace FlowStock.App;

public partial class MainWindow
{
    private bool _commercialStatisticsSelectorBlurFixApplied;

    private void ApplyCommercialStatisticsSelectorBlurFix()
    {
        if (_commercialStatisticsSelectorBlurFixApplied)
        {
            return;
        }

        _commercialStatisticsSelectorBlurFixApplied = true;
        StatisticsItemCombo.LostKeyboardFocus += StatisticsAcceptanceSelector_LostKeyboardFocusFix;
        StatisticsGtinCombo.LostKeyboardFocus += StatisticsAcceptanceSelector_LostKeyboardFocusFix;
    }

    private void StatisticsAcceptanceSelector_LostKeyboardFocusFix(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        if (_suppressCommercialStatisticsFilterEvents
            || sender is not System.Windows.Controls.ComboBox comboBox
            || !CommercialStatisticsSelectorBlurPolicy.ShouldRestoreAll(comboBox.Text))
        {
            return;
        }

        var previousSuppression = _suppressCommercialStatisticsFilterEvents;
        _suppressCommercialStatisticsFilterEvents = true;
        try
        {
            if (ReferenceEquals(comboBox, StatisticsItemCombo))
            {
                comboBox.ItemsSource = _statisticsItemOptions;
                comboBox.SelectedItem = _statisticsItemOptions.Count > 0
                    ? _statisticsItemOptions[0]
                    : null;
                comboBox.Text = _statisticsItemOptions.Count > 0
                    ? _statisticsItemOptions[0].Label
                    : "Все товары";
            }
            else if (ReferenceEquals(comboBox, StatisticsGtinCombo))
            {
                comboBox.ItemsSource = _statisticsGtinOptions;
                comboBox.SelectedItem = _statisticsGtinOptions.Count > 0
                    ? _statisticsGtinOptions[0]
                    : null;
                comboBox.Text = _statisticsGtinOptions.Count > 0
                    ? _statisticsGtinOptions[0].Label
                    : "Все GTIN";
            }
        }
        finally
        {
            _suppressCommercialStatisticsFilterEvents = previousSuppression;
        }
    }
}

internal static class CommercialStatisticsSelectorBlurPolicy
{
    public static bool ShouldRestoreAll(string? text) => string.IsNullOrWhiteSpace(text);
}
