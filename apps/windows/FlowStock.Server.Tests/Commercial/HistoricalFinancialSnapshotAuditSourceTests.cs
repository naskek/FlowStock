namespace FlowStock.Server.Tests.Commercial;

public sealed class HistoricalFinancialSnapshotAuditSourceTests
{
    private static readonly string AuditSource = File.ReadAllText(FindRepoFile(
        "tools",
        "audit",
        "historical-financial-snapshot-backfill-dry-run.sql"));

    [Fact]
    public void Audit_is_explicitly_read_only_and_has_no_order_line_mutation()
    {
        Assert.Contains("BEGIN READ ONLY;", AuditSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ROLLBACK;", AuditSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE order_lines", AuditSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT INTO order_lines", AuditSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM order_lines", AuditSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Audit_targets_only_active_shipped_customer_lines_with_missing_snapshots()
    {
        Assert.Contains("UPPER(o.order_type) = 'CUSTOMER'", AuditSource, StringComparison.Ordinal);
        Assert.Contains("UPPER(o.status) = 'SHIPPED'", AuditSource, StringComparison.Ordinal);
        Assert.Contains("ol.cancelled_at IS NULL", AuditSource, StringComparison.Ordinal);
        Assert.Contains(
            "(ol.unit_price_gross IS NULL OR ol.vat_rate IS NULL)",
            AuditSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_matches_current_commercial_terms_resolution_and_marks_approximation()
    {
        Assert.Contains("pip.is_active = TRUE", AuditSource, StringComparison.Ordinal);
        Assert.Contains(
            "COALESCE(pip.unit_price_gross, i.default_sale_price_gross)",
            AuditSource,
            StringComparison.Ordinal);
        Assert.Contains("vr.is_active", AuditSource, StringComparison.Ordinal);
        Assert.Contains("CANDIDATE_CURRENT_APPROXIMATION", AuditSource, StringComparison.Ordinal);
        Assert.Contains("BLOCKED_CURRENT_PRICE_MISSING", AuditSource, StringComparison.Ordinal);
        Assert.Contains("BLOCKED_CURRENT_VAT_REQUIRED", AuditSource, StringComparison.Ordinal);
        Assert.Contains("BLOCKED_CURRENT_VAT_INACTIVE", AuditSource, StringComparison.Ordinal);
    }

    private static string FindRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
