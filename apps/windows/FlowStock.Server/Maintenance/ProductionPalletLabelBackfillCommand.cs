using System.Text.Json;
using FlowStock.Core.Services;
using FlowStock.Data;

namespace FlowStock.Server.Maintenance;

public static class ProductionPalletLabelBackfillCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static bool TryRun(string[] args, string postgresConnectionString, out int exitCode)
    {
        exitCode = 0;
        if (args.Length < 2
            || !string.Equals(args[0], "maintenance", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(args[1], "production-label-fingerprint-backfill", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var apply = false;
        string? confirm = null;
        for (var index = 2; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--dry-run":
                    apply = false;
                    break;
                case "--apply":
                    apply = true;
                    break;
                case "--confirm" when index + 1 < args.Length:
                    confirm = args[++index];
                    break;
                case "-h":
                case "--help":
                    PrintUsage();
                    return true;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[index]}");
                    PrintUsage();
                    exitCode = 2;
                    return true;
            }
        }

        if (apply && !string.Equals(confirm, "APPLY", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Apply requires --confirm APPLY and a fresh backup.");
            exitCode = 2;
            return true;
        }

        try
        {
            var report = new ProductionPalletLabelBackfillService(new PostgresDataStore(postgresConnectionString))
                .Run(apply);
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            exitCode = report.BlockerCount == 0 ? 0 : 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Production label fingerprint backfill failed.");
            Console.Error.WriteLine(ex);
            exitCode = 1;
        }

        return true;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  FlowStock.Server maintenance production-label-fingerprint-backfill --dry-run");
        Console.WriteLine("  FlowStock.Server maintenance production-label-fingerprint-backfill --apply --confirm APPLY");
    }
}
