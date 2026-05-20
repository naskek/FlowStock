using FlowStock.App;
using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.CreateDocDraft.Infrastructure;

namespace FlowStock.Server.Tests.CreateDocDraft;

public sealed class WpfCompatibilityTests
{
    [Fact]
    public async Task WpfCreate_FeatureFlagRoutesToCanonicalPostApiDocs()
    {
        var (harness, apiStore) = CreateDocDraftHttpScenario.CreateInboundScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        using var temp = new TempSettingsScope(host.Client.BaseAddress!, useServerCreateDocDraft: true);
        var service = new WpfCreateDocDraftService(new SettingsService(temp.SettingsPath), new FileLogger(temp.LogPath));

        var result = await service.CreateDraftAsync(
            new WpfCreateDocDraftContext(
                "wpf-doc-create-001",
                "wpf-event-create-001",
                DocType.Inbound,
                "IN-2026-000001",
                "Через WPF bridge"));

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Response?.Doc);
        Assert.Equal(WpfCreateDocDraftResultKind.Created, result.Kind);
        Assert.Equal(1, harness.DocCount);
        Assert.Equal(1, apiStore.ApiDocCount);

        var doc = harness.GetDoc(result.Response!.Doc!.Id);
        Assert.Equal("IN-2026-000001", doc.DocRef);
        Assert.Equal(DocType.Inbound, doc.Type);
        Assert.Equal(DocStatus.Draft, doc.Status);
    }

    [Fact]
    public async Task WpfCreate_AcceptsServerAuthoredDocRef()
    {
        var (harness, apiStore) = CreateDocDraftHttpScenario.CreateDocRefCollisionScenario("IN-2026-000001");
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        using var temp = new TempSettingsScope(host.Client.BaseAddress!, useServerCreateDocDraft: true);
        var service = new WpfCreateDocDraftService(new SettingsService(temp.SettingsPath), new FileLogger(temp.LogPath));

        var result = await service.CreateDraftAsync(
            new WpfCreateDocDraftContext(
                "wpf-doc-create-002",
                "wpf-event-create-002",
                DocType.Inbound,
                "IN-2026-000001",
                null));

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Response?.Doc);
        Assert.True(result.Response!.Doc!.DocRefChanged);
        Assert.False(string.IsNullOrWhiteSpace(result.Response.Doc.DocRef));
        Assert.NotEqual("IN-2026-000001", result.Response.Doc.DocRef);
        Assert.Contains("Сервер назначил номер документа", result.Message);

        var doc = harness.GetDoc(result.Response.Doc.Id);
        Assert.Equal(result.Response.Doc.DocRef, doc.DocRef);
    }

    [Fact]
    public async Task WpfCreate_IgnoresLegacyFlagAndStillUsesCanonicalApi()
    {
        var (harness, apiStore) = CreateDocDraftHttpScenario.CreateInboundScenario();
        await using var host = await CloseDocumentHttpHost.StartAsync(harness, apiStore);
        using var temp = new TempSettingsScope(host.Client.BaseAddress!, useServerCreateDocDraft: false);
        var service = new WpfCreateDocDraftService(new SettingsService(temp.SettingsPath), new FileLogger(temp.LogPath));

        var result = await service.CreateDraftAsync(
            new WpfCreateDocDraftContext(
                "wpf-doc-create-003",
                "wpf-event-create-003",
                DocType.Inbound,
                "IN-2026-000003",
                null));

        Assert.True(result.IsSuccess);
        Assert.Equal(WpfCreateDocDraftResultKind.Created, result.Kind);
        Assert.Equal(1, harness.DocCount);
        Assert.Equal(1, apiStore.ApiDocCount);
    }

    private sealed class TempSettingsScope : IDisposable
    {
        private readonly string _dir;

        public TempSettingsScope(Uri baseAddress, bool useServerCreateDocDraft)
        {
            _dir = Path.Combine(Path.GetTempPath(), "FlowStock.Server.Tests", "CreateDocDraftWpf", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            SettingsPath = Path.Combine(_dir, "settings.json");
            LogPath = Path.Combine(_dir, "app.log");

            var settings = new BackupSettings
            {
                Server = new ServerSettings
                {
                    UseServerCreateDocDraft = useServerCreateDocDraft,
                    BaseUrl = baseAddress.ToString().TrimEnd('/'),
                    CloseTimeoutSeconds = 10,
                    AllowInvalidTls = false
                }
            };

            new SettingsService(SettingsPath).Save(settings);
        }

        public string SettingsPath { get; }

        public string LogPath { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_dir))
                {
                    Directory.Delete(_dir, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
