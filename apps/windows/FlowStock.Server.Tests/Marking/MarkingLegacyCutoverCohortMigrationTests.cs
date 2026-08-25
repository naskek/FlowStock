namespace FlowStock.Server.Tests.Marking;

public sealed class MarkingLegacyCutoverCohortMigrationTests
{
    [Fact]
    public void V0040_DefinesFrozenExemptionAndImmutableOutboundAttributionWithoutLegacyCoverage()
    {
        var migration = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "deploy", "postgres", "migrations",
            "V0040__marking_legacy_cutover_exemption_cohort.sql"));

        Assert.Contains("CREATE TABLE marking_legacy_cutover_cohort", migration);
        Assert.Contains("CREATE TABLE marking_legacy_cutover_line_scope", migration);
        Assert.Contains("frozen_unshipped_legacy_quantity", migration);
        Assert.Contains("CREATE TABLE marking_legacy_cutover_subject_exemption", migration);
        Assert.Contains("CREATE TABLE marking_outbound_fulfillment_attribution", migration);
        Assert.Contains("basis IN ('LEGACY_EXEMPT', 'REAL_READY')", migration);
        Assert.Contains("trg_marking_outbound_attribution_immutable", migration);
        Assert.Contains("MARKING_SYNTHETIC_CUTOVER_WORKFLOW_OBSOLETE", migration);
        Assert.Contains("NOT EXISTS (SELECT 1 FROM marking_legacy_cutover_cohort", migration);
        Assert.DoesNotContain("'LEGACY_COHORT'", migration);
        Assert.DoesNotContain("ALTER TABLE marking_operational_coverage", migration);
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
