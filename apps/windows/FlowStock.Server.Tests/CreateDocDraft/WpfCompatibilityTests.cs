namespace FlowStock.Server.Tests.CreateDocDraft;

public sealed class WpfCompatibilityTests
{
    [Fact(Skip = "WPF create bridge now exists behind a feature flag, but there is still no lightweight WPF adapter harness in FlowStock.Server.Tests. Verify via docs/architecture/create-doc-draft-wpf-migration.md.")]
    public void WpfCreate_FeatureFlagRoutesToCanonicalPostApiDocs()
    {
    }

    [Fact(Skip = "WPF bridge exists, but server-authored doc_ref replacement is covered only by the manual checklist because there is no WPF automation harness yet.")]
    public void WpfCreate_AcceptsServerAuthoredDocRef()
    {
    }

    [Fact(Skip = "Legacy local create intentionally remains available behind the feature flag at this step. Manual parity coverage is documented; no automated WPF harness exists yet.")]
    public void WpfLegacyCreatePath_RemainsAvailableUnderFeatureFlag()
    {
    }
}
