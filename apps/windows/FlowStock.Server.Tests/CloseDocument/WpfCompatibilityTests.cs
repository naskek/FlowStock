namespace FlowStock.Server.Tests.CloseDocument;

public sealed class WpfCompatibilityTests
{
    [Fact(Skip = "WPF server-close feature flag is implemented, but adapter/UI automation is still absent; use docs/architecture/close-document-wpf-migration.md manual checklist.")]
    public void DetailsWindow_SavesHeaderBeforeCanonicalClose()
    {
    }

    [Fact(Skip = "WPF server-close feature flag is implemented, but adapter/UI automation is still absent; use docs/architecture/close-document-wpf-migration.md manual checklist.")]
    public void MainWindowAndDetailsWindow_ConvergeToSameCloseOutcome()
    {
    }

    [Fact(Skip = "WPF server-close feature flag is implemented, but adapter/UI automation is still absent; use docs/architecture/close-document-wpf-migration.md manual checklist.")]
    public void OutboundPreview_RemainsUiOnly()
    {
    }
}
