namespace FlowStock.Server.Tests.Commercial;

public sealed class HistoricalFinancialSnapshotProductionBackfillSourceTests
{
    private static readonly string PreflightSource = File.ReadAllText(FindRepoFile(
        "tools",
        "audit",
        "historical-financial-snapshot-backfill-production-preflight.sql"));

    private static readonly string ApplySource = File.ReadAllText(FindRepoFile(
        "tools",
        "audit",
        "historical-financial-snapshot-backfill-production-apply.sql"));

    [Fact]
    public void Production_preflight_is_read_only_and_emits_full_guard_set()
    {
        Assert.Contains("BEGIN READ ONLY;", PreflightSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ROLLBACK;", PreflightSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE order_lines", PreflightSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT INTO order_lines", PreflightSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM order_lines", PreflightSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("candidate_count", PreflightSource, StringComparison.Ordinal);
        Assert.Contains("candidate_quantity", PreflightSource, StringComparison.Ordinal);
        Assert.Contains("reconstructed_gross", PreflightSource, StringComparison.Ordinal);
        Assert.Contains("candidate_fingerprint", PreflightSource, StringComparison.Ordinal);
        Assert.Contains("blocked_count", PreflightSource, StringComparison.Ordinal);
        Assert.Contains("blocked_quantity", PreflightSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_preflight_uses_same_reconstruction_priority()
    {
        Assert.Contains("LEFT JOIN LATERAL", PreflightSource, StringComparison.Ordinal);
        Assert.Contains("o2.partner_id = o.partner_id", PreflightSource, StringComparison.Ordinal);
        Assert.Contains("ol2.item_id = ol.item_id", PreflightSource, StringComparison.Ordinal);
        Assert.Contains(
            "later_price.unit_price_gross,\n            pip.unit_price_gross,\n            i.default_sale_price_gross",
            PreflightSource,
            StringComparison.Ordinal);
        Assert.Contains("FIRST_SUBSEQUENT_SNAPSHOT_PRICE", PreflightSource, StringComparison.Ordinal);
        Assert.Contains("CANDIDATE_STATISTICAL_APPROXIMATION", PreflightSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_apply_requires_backup_confirmation_and_exact_preflight_guards()
    {
        Assert.Contains("CONFIRM_PRODUCTION_APPLY", ApplySource, StringComparison.Ordinal);
        Assert.Contains("APPLY_HISTORICAL_SNAPSHOT_BACKFILL", ApplySource, StringComparison.Ordinal);
        Assert.Contains("CONFIRM_FRESH_BACKUP", ApplySource, StringComparison.Ordinal);
        Assert.Contains("EXPECTED_CANDIDATE_COUNT", ApplySource, StringComparison.Ordinal);
        Assert.Contains("EXPECTED_CANDIDATE_QUANTITY", ApplySource, StringComparison.Ordinal);
        Assert.Contains("EXPECTED_RECONSTRUCTED_GROSS", ApplySource, StringComparison.Ordinal);
        Assert.Contains("EXPECTED_CANDIDATE_FINGERPRINT", ApplySource, StringComparison.Ordinal);
        Assert.Contains("EXPECTED_BLOCKED_COUNT", ApplySource, StringComparison.Ordinal);
        Assert.Contains("EXPECTED_BLOCKED_QUANTITY", ApplySource, StringComparison.Ordinal);
        Assert.Contains("production candidate set differs from reviewed preflight", ApplySource, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_apply_guard_failures_return_nonzero_via_on_error_stop()
    {
        Assert.Contains("\\set ON_ERROR_STOP on", ApplySource, StringComparison.Ordinal);
        Assert.Contains("SELECT 1 / 0 AS fail_closed;", ApplySource, StringComparison.Ordinal);
        Assert.DoesNotContain("\\quit ", ApplySource, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_apply_freezes_reconstruction_sources_during_transaction()
    {
        Assert.Contains("LOCK TABLE orders IN SHARE MODE", ApplySource, StringComparison.Ordinal);
        Assert.Contains("LOCK TABLE order_lines IN SHARE ROW EXCLUSIVE MODE", ApplySource, StringComparison.Ordinal);
        Assert.Contains("LOCK TABLE partners IN SHARE MODE", ApplySource, StringComparison.Ordinal);
        Assert.Contains("LOCK TABLE items IN SHARE MODE", ApplySource, StringComparison.Ordinal);
        Assert.Contains("LOCK TABLE partner_item_sale_prices IN SHARE MODE", ApplySource, StringComparison.Ordinal);
        Assert.Contains("LOCK TABLE vat_rates IN SHARE MODE", ApplySource, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_apply_mutates_only_candidate_null_snapshots()
    {
        Assert.Contains("UPDATE order_lines ol", ApplySource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "plan.decision = 'CANDIDATE_STATISTICAL_APPROXIMATION'",
            ApplySource,
            StringComparison.Ordinal);
        Assert.Contains("plan.order_line_id = ol.id", ApplySource, StringComparison.Ordinal);
        Assert.Contains(
            "(ol.unit_price_gross IS NULL OR ol.vat_rate IS NULL)",
            ApplySource,
            StringComparison.Ordinal);
        Assert.Contains(
            "WHEN ol.unit_price_gross IS NULL THEN plan.proposed_unit_price_gross",
            ApplySource,
            StringComparison.Ordinal);
        Assert.Contains(
            "WHEN ol.vat_rate IS NULL THEN plan.proposed_vat_rate",
            ApplySource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Production_apply_rolls_back_when_post_update_invariants_fail()
    {
        Assert.Contains("actual_updated_count", ApplySource, StringComparison.Ordinal);
        Assert.Contains("actual_candidate_incomplete_count", ApplySource, StringComparison.Ordinal);
        Assert.Contains("actual_remaining_incomplete_count", ApplySource, StringComparison.Ordinal);
        Assert.Contains("actual_remaining_incomplete_quantity", ApplySource, StringComparison.Ordinal);
        Assert.Contains("post-update invariants failed; rolling back transaction", ApplySource, StringComparison.Ordinal);
        Assert.Contains("ROLLBACK;", ApplySource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COMMIT;", ApplySource, StringComparison.OrdinalIgnoreCase);
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
