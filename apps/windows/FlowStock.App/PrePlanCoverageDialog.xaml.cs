using System.Windows;

namespace FlowStock.App;

public enum PrePlanDialogAction
{
    Cancel,
    PlanAll,
    PlanSafeOnly,
    AdoptInternalThenPlan,
    BindHuFirst
}

public partial class PrePlanCoverageDialog : Window
{
    public PrePlanDialogAction SelectedAction { get; private set; } = PrePlanDialogAction.Cancel;

    public PrePlanCoverageDialog(
        string warningText,
        string questionText,
        bool showBindHuFirst,
        bool showPlanSafeOnly,
        bool planSafeOnlyEnabled,
        bool showAdoptInternal = false)
    {
        InitializeComponent();
        WarningTextBox.Text = warningText;
        QuestionTextBlock.Text = questionText;
        BindHuFirstButton.Visibility = showBindHuFirst ? Visibility.Visible : Visibility.Collapsed;
        PlanSafeOnlyButton.Visibility = showPlanSafeOnly ? Visibility.Visible : Visibility.Collapsed;
        PlanSafeOnlyButton.IsEnabled = planSafeOnlyEnabled;
        AdoptInternalButton.Visibility = showAdoptInternal ? Visibility.Visible : Visibility.Collapsed;
        AdoptInternalButton.IsDefault = showAdoptInternal;
        PlanAllButton.IsDefault = !showAdoptInternal;
    }

    public static PrePlanCoverageDialog CreateNeutral(string warningText, string questionText)
    {
        return new PrePlanCoverageDialog(
            warningText,
            questionText,
            showBindHuFirst: false,
            showPlanSafeOnly: false,
            planSafeOnlyEnabled: false);
    }

    private void PlanAll_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = PrePlanDialogAction.PlanAll;
        DialogResult = true;
    }

    private void PlanSafeOnly_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = PrePlanDialogAction.PlanSafeOnly;
        DialogResult = true;
    }

    private void AdoptInternal_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = PrePlanDialogAction.AdoptInternalThenPlan;
        DialogResult = true;
    }

    private void BindHuFirst_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = PrePlanDialogAction.BindHuFirst;
        DialogResult = true;
    }
}
