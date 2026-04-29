using System.Globalization;
using System.Text;
using System.Windows;

namespace FlowStock.App;

public partial class MaintenanceWindow : Window
{
    private readonly AppServices _services;
    private bool _dryRunSucceeded;

    public MaintenanceWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
    }

    private async void DryRun_Click(object sender, RoutedEventArgs e)
    {
        await RunBackfillAsync(apply: false);
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        await RunBackfillAsync(apply: true);
    }

    private async Task RunBackfillAsync(bool apply)
    {
        SetBusy(true);
        MaintenanceStatusText.Text = apply ? "Выполняется apply..." : "Выполняется dry-run...";

        try
        {
            WpfMaintenanceBackfillReportResult result = apply
                ? await _services.WpfAdminApi.RunReservationBackfillApplyAsync(ApplyConfirmBox.Text).ConfigureAwait(true)
                : await _services.WpfAdminApi.RunReservationBackfillDryRunAsync().ConfigureAwait(true);

            if (!result.IsSuccess || result.Report == null)
            {
                MaintenanceStatusText.Text = result.Error ?? "Не удалось выполнить backfill.";
                MessageBox.Show(
                    result.Error ?? "Не удалось выполнить backfill резервов заказов.",
                    "Обслуживание",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            ReportBox.Text = FormatReport(result.Report);
            MaintenanceStatusText.Text = apply ? "Apply завершен." : "Dry-run завершен.";
            _dryRunSucceeded = !apply || _dryRunSucceeded;
            if (!apply)
            {
                _dryRunSucceeded = true;
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        DryRunButton.IsEnabled = !busy;
        ApplyButton.IsEnabled = !busy && _dryRunSucceeded;
        ApplyConfirmBox.IsEnabled = !busy;
    }

    private static string FormatReport(WpfMaintenanceBackfillReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Mode: {report.Mode}");
        sb.AppendLine($"Customer orders: {report.CustomerOrders}");
        sb.AppendLine($"Active customer orders: {report.ActiveCustomerOrders}");
        sb.AppendLine($"Inactive/skipped customer orders: {report.InactiveSkippedCustomerOrders}");
        sb.AppendLine($"Plan lines before/after: {report.PlanLinesBefore}/{report.PlanLinesAfter}");
        sb.AppendLine($"Qty before/after: {FormatQty(report.QtyBefore)}/{FormatQty(report.QtyAfter)}");
        sb.AppendLine($"Orders with changes: {report.OrdersWithChanges}");
        sb.AppendLine($"Conflicting HU: {report.ConflictingHu}");
        sb.AppendLine($"Ledger rows before/after: {report.LedgerRowsBefore}/{report.LedgerRowsAfter}");

        if (report.Messages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Messages:");
            foreach (var message in report.Messages)
            {
                sb.AppendLine($"- {message}");
            }
        }

        if (report.Conflicts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Conflicts:");
            foreach (var conflict in report.Conflicts)
            {
                sb.AppendLine($"- HU {conflict.HuCode}, item {conflict.ItemId}");
                foreach (var claim in conflict.Claims)
                {
                    sb.AppendLine($"  order {claim.OrderRef} ({claim.OrderId}), qty {FormatQty(claim.QtyPlanned)}");
                }
            }
        }

        var changedOrders = report.Details.Where(detail => detail.WillChange).ToList();
        if (changedOrders.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Orders with changes:");
            foreach (var detail in changedOrders)
            {
                sb.AppendLine(
                    $"- {detail.OrderRef} ({detail.OrderId}), status={detail.EffectiveStatus}, " +
                    $"lines {detail.PlanLinesBefore}->{detail.PlanLinesAfter}, qty {FormatQty(detail.QtyBefore)}->{FormatQty(detail.QtyAfter)}");
                if (!string.IsNullOrWhiteSpace(detail.SkipReason))
                {
                    sb.AppendLine($"  skip: {detail.SkipReason}");
                }
            }
        }

        var skippedLines = report.Details
            .SelectMany(detail => detail.Lines.Select(line => new { detail.OrderRef, detail.OrderId, Line = line }))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Line.SkipReason))
            .ToList();
        if (skippedLines.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Skipped lines:");
            foreach (var entry in skippedLines)
            {
                sb.AppendLine(
                    $"- order {entry.OrderRef} ({entry.OrderId}), line {entry.Line.OrderLineId}, item {entry.Line.ItemId}: " +
                    $"{entry.Line.SkipReason}, requested {FormatQty(entry.Line.RequestedQty)}, planned {FormatQty(entry.Line.PlannedQty)}");
            }
        }

        return sb.ToString();
    }

    private static string FormatQty(double value)
    {
        return value.ToString("0.###", CultureInfo.CurrentCulture);
    }
}
