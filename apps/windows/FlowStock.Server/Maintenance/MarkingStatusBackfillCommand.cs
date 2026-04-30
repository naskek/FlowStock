using System.Globalization;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Data;

namespace FlowStock.Server.Maintenance;

public static class MarkingStatusBackfillCommand
{
    public static bool TryRun(string[] args, string postgresConnectionString, out int exitCode)
    {
        exitCode = 0;
        if (args.Length < 2
            || !string.Equals(args[0], "maintenance", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(args[1], "backfill-marking-status", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        DateTime? createdBefore = null;
        var apply = false;
        string? confirm = null;

        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--created-before":
                    if (i + 1 >= args.Length
                        || !DateTime.TryParseExact(
                            args[++i],
                            "yyyy-MM-dd",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out var parsed))
                    {
                        Console.Error.WriteLine("Invalid --created-before value. Use YYYY-MM-DD.");
                        PrintUsage();
                        exitCode = 2;
                        return true;
                    }

                    createdBefore = parsed.Date;
                    break;
                case "--apply":
                    apply = true;
                    break;
                case "--dry-run":
                    apply = false;
                    break;
                case "--confirm":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("Missing --confirm value.");
                        PrintUsage();
                        exitCode = 2;
                        return true;
                    }

                    confirm = args[++i];
                    break;
                case "-h":
                case "--help":
                    PrintUsage();
                    return true;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    PrintUsage();
                    exitCode = 2;
                    return true;
            }
        }

        try
        {
            var store = new PostgresDataStore(postgresConnectionString);
            var ledgerBefore = store.CountLedgerEntries();
            var report = new MarkingStatusBackfillService(store).Run(new MarkingStatusBackfillOptions(
                createdBefore,
                apply,
                confirm));
            var ledgerAfter = store.CountLedgerEntries();

            PrintReport(report, ledgerBefore, ledgerAfter);
            if (apply && ledgerBefore != ledgerAfter)
            {
                Console.Error.WriteLine("ERROR: ledger row count changed during marking-status backfill.");
                exitCode = 3;
            }
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUsage();
            exitCode = 2;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUsage();
            exitCode = 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Marking-status backfill failed.");
            Console.Error.WriteLine(ex);
            exitCode = 1;
        }

        return true;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  FlowStock.Server maintenance backfill-marking-status --created-before YYYY-MM-DD [--dry-run]");
        Console.WriteLine("  FlowStock.Server maintenance backfill-marking-status --created-before YYYY-MM-DD --apply --confirm APPLY");
        Console.WriteLine();
        Console.WriteLine("Default mode is dry-run. Apply updates only orders.marking_status and marking timestamps.");
    }

    private static void PrintReport(MarkingStatusBackfillReport report, long ledgerBefore, long ledgerAfter)
    {
        Console.WriteLine($"Marking-status backfill mode: {(report.Applied ? "APPLY" : "DRY-RUN")}");
        Console.WriteLine($"Created before: {report.CreatedBefore:yyyy-MM-dd}");
        Console.WriteLine($"Total scanned: {report.TotalScanned}");
        Console.WriteLine($"Changed to PRINTED: {report.ChangedToPrinted}");
        Console.WriteLine($"Changed to NOT_REQUIRED: {report.ChangedToNotRequired}");
        Console.WriteLine($"Skipped cancelled: {report.SkippedCancelled}");
        Console.WriteLine($"Skipped pending/awaiting confirmation: {report.SkippedPending}");
        Console.WriteLine($"Already PRINTED: {report.AlreadyPrinted}");
        Console.WriteLine($"Ledger rows before/after: {ledgerBefore}/{ledgerAfter}");
    }
}
