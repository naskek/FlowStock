namespace FlowStock.Server.Tests.Marking;

public sealed class MarkingRequestExportBatchMigrationTests
{
    [Fact]
    public void V0041_DefinesImmutableRequestMembershipWithoutWorkbookOrCodes()
    {
        var migration = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "deploy", "postgres", "migrations",
            "V0041__marking_request_export_batches.sql"));

        Assert.Contains("CREATE TABLE marking_request_export_batch", migration);
        Assert.Contains("CREATE TABLE marking_request_export_batch_request", migration);
        Assert.Contains("UNIQUE (order_id, expected_snapshot_hash)", migration);
        Assert.Contains("UNIQUE (marking_order_id)", migration);
        Assert.Contains("post_export_snapshot_hash", migration);
        Assert.Contains("MARKING_REQUEST_EXPORT_BATCH_IMMUTABLE", migration);
        Assert.DoesNotContain("marking_code", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bytea", migration, StringComparison.OrdinalIgnoreCase);
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
