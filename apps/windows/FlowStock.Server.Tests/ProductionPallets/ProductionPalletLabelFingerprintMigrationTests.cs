namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class ProductionPalletLabelFingerprintMigrationTests
{
    [Fact]
    public void V0042_IsAdditiveAndDoesNotInventOrNormalizePhysicalPrintEvidence()
    {
        var migration = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "deploy",
            "postgres",
            "migrations",
            "V0042__production_pallet_label_fingerprint.sql"));

        Assert.Contains("printed_label_fingerprint TEXT NULL", migration);
        Assert.Contains("printed_label_fingerprint_version SMALLINT NULL", migration);
        Assert.Contains("ck_production_pallets_label_fingerprint_pair", migration);
        Assert.Contains("ck_production_pallets_label_fingerprint_format", migration);
        Assert.Contains("printed_label_fingerprint_version = 1", migration);
        Assert.DoesNotContain("UPDATE production_pallets\nSET printed_label_fingerprint", migration);
        Assert.DoesNotContain("SET printed_at = created_at", migration);
        Assert.DoesNotContain("SET status = 'PRINTED'", migration);
        Assert.DoesNotContain("ck_production_pallets_planned_has_no_print_evidence", migration);
        Assert.DoesNotContain("ck_production_pallets_printed_has_timestamp", migration);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "deploy", "postgres", "migrations")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
