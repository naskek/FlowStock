using System.Globalization;
using FlowStock.Core.Services;
using FlowStock.Data;

namespace FlowStock.Server.Maintenance;

public static class OrderReservationBackfillCommand
{
    public static bool TryRun(string[] args, string postgresConnectionString, out int exitCode)
    {
        exitCode = 0;
        if (args.Length < 2
            || !string.Equals(args[0], "maintenance", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(args[1], "backfill-reservations", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var apply = false;
        foreach (var arg in args.Skip(2))
        {
            switch (arg)
            {
                case "--apply":
                    apply = true;
                    break;
                case "--dry-run":
                    apply = false;
                    break;
                case "-h":
                case "--help":
                    PrintUsage();
                    return true;
                default:
                    Console.Error.WriteLine($"Unknown argument: {arg}");
                    PrintUsage();
                    exitCode = 2;
                    return true;
            }
        }

        try
        {
            var store = new PostgresDataStore(postgresConnectionString);
            var ledgerBefore = store.CountLedgerEntries();
            var report = new OrderReservationBackfillService(store).Run(new OrderReservationBackfillOptions(apply));
            var ledgerAfter = store.CountLedgerEntries();

            PrintReport(report, ledgerBefore, ledgerAfter);
            if (ledgerBefore != ledgerAfter)
            {
                Console.Error.WriteLine("ERROR: ledger row count changed during reservation backfill.");
                exitCode = 3;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Reservation backfill failed.");
            Console.Error.WriteLine(ex);
            exitCode = 1;
        }

        return true;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  FlowStock.Server maintenance backfill-reservations [--dry-run]");
        Console.WriteLine("  FlowStock.Server maintenance backfill-reservations --apply");
        Console.WriteLine();
        Console.WriteLine("Default mode is dry-run. --apply updates only order_receipt_plan_lines.");
    }

    private static void PrintReport(OrderReservationBackfillReport report, long ledgerBefore, long ledgerAfter)
    {
        Console.WriteLine($"Reservation backfill mode: {(report.Applied ? "APPLY" : "DRY-RUN")}");
        Console.WriteLine($"Customer orders: {report.CustomerOrderCount}");
        Console.WriteLine($"Active customer orders: {report.ActiveCustomerOrderCount}");
        Console.WriteLine($"Inactive/skipped customer orders: {report.InactiveCustomerOrderCount}");
        Console.WriteLine($"Plan lines before: {report.ExistingPlanLineCount}, qty: {FormatQty(report.ExistingPlannedQty)}");
        Console.WriteLine($"Plan lines after: {report.PlannedPlanLineCount}, qty: {FormatQty(report.PlannedQty)}");
        Console.WriteLine($"Orders with changes: {report.ChangedOrderCount}");
        Console.WriteLine($"Conflicting HU: {report.Conflicts.Count}");
        Console.WriteLine($"Ledger rows before/after: {ledgerBefore}/{ledgerAfter}");

        foreach (var conflict in report.Conflicts)
        {
            Console.WriteLine($"CONFLICT HU {conflict.HuCode}, item {conflict.ItemId}");
            foreach (var claim in conflict.Claims)
            {
                Console.WriteLine($"  order {claim.OrderRef} ({claim.OrderId}), qty {FormatQty(claim.QtyPlanned)}");
            }
        }

        foreach (var order in report.Orders.Where(order => order.WillChange))
        {
            var action = order.Active ? "rebuild" : $"clear ({order.SkipReason})";
            Console.WriteLine(
                $"ORDER {order.OrderRef} ({order.OrderId}) {action}: " +
                $"{order.ExistingPlanLineCount}/{FormatQty(order.ExistingPlannedQty)} -> " +
                $"{order.PlannedPlanLineCount}/{FormatQty(order.PlannedQty)}");
        }

        foreach (var order in report.Orders)
        {
            foreach (var line in order.Lines.Where(line => !string.IsNullOrWhiteSpace(line.SkipReason)))
            {
                Console.WriteLine(
                    $"SKIP order {order.OrderRef} ({order.OrderId}), line {line.OrderLineId}, item {line.ItemId}: " +
                    $"{line.SkipReason}, requested {FormatQty(line.RequestedQty)}, planned {FormatQty(line.PlannedQty)}");
            }
        }
    }

    private static string FormatQty(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
