using System.Globalization;
using System.Windows;

namespace FlowStock.App;

public enum PrePlanDialogAction
{
    Cancel,
    PlanAll,
    PlanSafeOnly,
    AdoptInternalThenPlan,
    ApplySelectedCoverageThenPlan,
    BindHuFirst
}

public partial class PrePlanCoverageDialog : Window
{
    public PrePlanDialogAction SelectedAction { get; private set; } = PrePlanDialogAction.Cancel;
    public IReadOnlyList<PrePlanWarehouseHuRow> WarehouseRows { get; }
    public IReadOnlyList<PrePlanInternalHuRow> InternalRows { get; }

    public PrePlanCoverageDialog(
        string warningText,
        string questionText,
        bool showBindHuFirst,
        bool showPlanSafeOnly,
        bool planSafeOnlyEnabled,
        bool showAdoptInternal = false)
    {
        InitializeComponent();
        WarehouseRows = Array.Empty<PrePlanWarehouseHuRow>();
        InternalRows = Array.Empty<PrePlanInternalHuRow>();
        DataContext = this;
        WarningTextBox.Text = warningText;
        QuestionTextBlock.Text = questionText;
        PlanSafeOnlyButton.Visibility = showPlanSafeOnly ? Visibility.Visible : Visibility.Collapsed;
        PlanSafeOnlyButton.IsEnabled = planSafeOnlyEnabled;
        ApplySelectedButton.Visibility = showAdoptInternal ? Visibility.Visible : Visibility.Collapsed;
        ApplySelectedButton.IsDefault = showAdoptInternal;
        PlanAllButton.IsDefault = !showAdoptInternal;
    }

    public PrePlanCoverageDialog(
        WpfPrePlanCoveragePreviewApiResult preview,
        string warningText,
        string questionText,
        bool showPlanSafeOnly,
        bool planSafeOnlyEnabled)
    {
        InitializeComponent();
        WarehouseRows = preview.WarehouseHuCandidatesOrEmpty
            .Select(candidate => new PrePlanWarehouseHuRow(candidate))
            .ToArray();
        InternalRows = preview.InternalPlannedHuCandidatesOrEmpty
            .Select(candidate => new PrePlanInternalHuRow(candidate))
            .ToArray();
        DataContext = this;
        WarningTextBox.Text = warningText;
        QuestionTextBlock.Text = questionText;
        UnifiedCoverageGrid.Visibility = WarehouseRows.Count > 0 || InternalRows.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplySelectedButton.Visibility = UnifiedCoverageGrid.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplySelectedButton.IsDefault = ApplySelectedButton.Visibility == Visibility.Visible;
        PlanAllButton.IsDefault = ApplySelectedButton.Visibility != Visibility.Visible;
        PlanSafeOnlyButton.Visibility = showPlanSafeOnly ? Visibility.Visible : Visibility.Collapsed;
        PlanSafeOnlyButton.IsEnabled = planSafeOnlyEnabled;
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

    public WpfSelectedCoveragePlanRequest BuildSelectedCoverageRequest()
    {
        var selectedWarehouse = WarehouseRows
            .Where(row => row.IsSelected && row.CanSelect)
            .Select(row => new WpfSelectedWarehouseHu(row.HuCode, row.ItemId, row.TargetOrderLineId))
            .ToArray();
        var selectedInternal = InternalRows
            .Where(row => row.IsSelected && row.CanSelect)
            .Select(row => row.ProductionPalletId)
            .ToArray();
        return new WpfSelectedCoveragePlanRequest(selectedWarehouse, selectedInternal, PlanRemainder: true);
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

    private void ApplySelected_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = PrePlanDialogAction.ApplySelectedCoverageThenPlan;
        DialogResult = true;
    }
}

public sealed class PrePlanWarehouseHuRow
{
    public PrePlanWarehouseHuRow(WpfWarehouseHuCandidate candidate)
    {
        HuCode = candidate.HuCode;
        ItemId = candidate.ItemId;
        ItemName = candidate.ItemName;
        TargetOrderLineId = candidate.TargetOrderLineId;
        Qty = candidate.Qty;
        Status = candidate.Status;
        DisabledReason = candidate.DisabledReason;
        CanSelect = string.IsNullOrWhiteSpace(candidate.DisabledReason);
        IsSelected = candidate.SelectedByDefault && CanSelect;
    }

    public bool IsSelected { get; set; }
    public bool CanSelect { get; }
    public string DisabledReason { get; }
    public string HuCode { get; }
    public long ItemId { get; }
    public string ItemName { get; }
    public long TargetOrderLineId { get; }
    public double Qty { get; }
    public string QtyDisplay => Qty.ToString("0.###", CultureInfo.CurrentCulture);
    public string Status { get; }
}

public sealed class PrePlanInternalHuRow
{
    public PrePlanInternalHuRow(WpfInternalPlannedHuCandidate candidate)
    {
        ProductionPalletId = candidate.ProductionPalletId;
        HuCode = candidate.HuCode;
        ItemName = candidate.ItemName;
        Qty = candidate.PlannedQty;
        SourceRef = candidate.SourceOrderRef;
        Status = candidate.Status;
        DisabledReason = candidate.DisabledReason;
        CanSelect = string.IsNullOrWhiteSpace(candidate.DisabledReason);
        IsSelected = candidate.SelectedByDefault && CanSelect;
    }

    public bool IsSelected { get; set; }
    public bool CanSelect { get; }
    public string DisabledReason { get; }
    public long ProductionPalletId { get; }
    public string HuCode { get; }
    public string ItemName { get; }
    public double Qty { get; }
    public string QtyDisplay => Qty.ToString("0.###", CultureInfo.CurrentCulture);
    public string SourceRef { get; }
    public string Status { get; }
}
