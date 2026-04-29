using System.Text.Json.Serialization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class MaintenanceBackfillEndpoints
{
    private const string ApplyConfirm = "APPLY";
    private static readonly SemaphoreSlim BackfillLock = new(1, 1);

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/admin/maintenance/backfill-reservations/dry-run", (IDataStore store) =>
            RunBackfill(store, apply: false));

        app.MapPost("/api/admin/maintenance/backfill-reservations/apply", async (HttpRequest request, IDataStore store) =>
        {
            var payload = await request.ReadFromJsonAsync<BackfillApplyRequest>();
            if (!string.Equals(payload?.Confirm, ApplyConfirm, StringComparison.Ordinal))
            {
                return Results.BadRequest(new ApiResult(false, "CONFIRM_REQUIRED"));
            }

            return RunBackfill(store, apply: true);
        });
    }

    private static IResult RunBackfill(IDataStore store, bool apply)
    {
        if (!BackfillLock.Wait(0))
        {
            return Results.Conflict(new ApiResult(false, "BACKFILL_ALREADY_RUNNING"));
        }

        try
        {
            var ledgerRowsBefore = store.CountLedgerEntries();
            var report = new OrderReservationBackfillService(store)
                .Run(new OrderReservationBackfillOptions(apply));
            var ledgerRowsAfter = store.CountLedgerEntries();

            return Results.Ok(MapReport(report, ledgerRowsBefore, ledgerRowsAfter));
        }
        finally
        {
            BackfillLock.Release();
        }
    }

    private static object MapReport(OrderReservationBackfillReport report, long ledgerRowsBefore, long ledgerRowsAfter)
    {
        return new
        {
            mode = report.Applied ? "APPLY" : "DRY_RUN",
            customer_orders = report.CustomerOrderCount,
            active_customer_orders = report.ActiveCustomerOrderCount,
            inactive_skipped_customer_orders = report.InactiveCustomerOrderCount,
            plan_lines_before = report.ExistingPlanLineCount,
            plan_lines_after = report.PlannedPlanLineCount,
            qty_before = report.ExistingPlannedQty,
            qty_after = report.PlannedQty,
            orders_with_changes = report.ChangedOrderCount,
            conflicting_hu = report.Conflicts.Count,
            ledger_rows_before = ledgerRowsBefore,
            ledger_rows_after = ledgerRowsAfter,
            messages = BuildMessages(report, ledgerRowsBefore, ledgerRowsAfter),
            conflicts = report.Conflicts.Select(conflict => new
            {
                hu_code = conflict.HuCode,
                item_id = conflict.ItemId,
                claims = conflict.Claims.Select(claim => new
                {
                    order_id = claim.OrderId,
                    order_ref = claim.OrderRef,
                    qty_planned = claim.QtyPlanned
                }).ToList()
            }).ToList(),
            details = report.Orders.Select(order => new
            {
                order_id = order.OrderId,
                order_ref = order.OrderRef,
                effective_status = order.EffectiveStatus,
                active = order.Active,
                plan_lines_before = order.ExistingPlanLineCount,
                plan_lines_after = order.PlannedPlanLineCount,
                qty_before = order.ExistingPlannedQty,
                qty_after = order.PlannedQty,
                will_change = order.WillChange,
                skip_reason = order.SkipReason,
                lines = order.Lines.Select(line => new
                {
                    order_line_id = line.OrderLineId,
                    item_id = line.ItemId,
                    requested_qty = line.RequestedQty,
                    planned_qty = line.PlannedQty,
                    skip_reason = line.SkipReason
                }).ToList()
            }).ToList()
        };
    }

    private static List<string> BuildMessages(OrderReservationBackfillReport report, long ledgerRowsBefore, long ledgerRowsAfter)
    {
        var messages = new List<string>
        {
            report.Applied
                ? "Apply выполнен: обновлены только строки плана резервов заказов."
                : "Dry-run выполнен: данные не изменялись.",
            $"Заказов с изменениями: {report.ChangedOrderCount}.",
            $"Ledger rows before/after: {ledgerRowsBefore}/{ledgerRowsAfter}."
        };

        if (ledgerRowsBefore != ledgerRowsAfter)
        {
            messages.Add("ВНИМАНИЕ: количество строк ledger изменилось.");
        }

        if (report.Conflicts.Count > 0)
        {
            messages.Add($"Найдены конфликтующие HU: {report.Conflicts.Count}.");
        }

        return messages;
    }

    private sealed class BackfillApplyRequest
    {
        [JsonPropertyName("confirm")]
        public string? Confirm { get; init; }
    }
}
