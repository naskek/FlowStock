using System.Net;
using System.Text;
using FlowStock.App;
using FlowStock.DesktopUpdate;

namespace FlowStock.Server.Tests.DesktopUpdate;

public sealed class DesktopUpdateEndpointTests
{
    private const string InstalledCommit = "1111111111111111111111111111111111111111";
    private const string TargetCommit = "2222222222222222222222222222222222222222";

    [Fact]
    public void OperationalIpAndAllowInvalidTls_DoNotAffectDefaultUpdateEndpoint()
    {
        var operational = new ServerSettings
        {
            ServerBaseUrl = "https://100.66.142.112:7154",
            AllowInvalidTls = true
        }.Normalize();

        var updateEndpoint = DesktopUpdateEndpointResolver.Resolve(_ => null);

        Assert.Equal("https://100.66.142.112:7154", operational.GetServerBaseUrlOrDefault());
        Assert.True(operational.AllowInvalidTls);
        Assert.Equal("https://flowstock.local:7154/", updateEndpoint.AbsoluteUri);
    }

    [Fact]
    public void UpdateEndpoint_DefaultsToTrustedProductionHost()
    {
        var endpoint = DesktopUpdateEndpointResolver.Resolve(_ => null);

        Assert.Equal("https://flowstock.local:7154/", endpoint.AbsoluteUri);
    }

    [Fact]
    public void DedicatedEnvironmentOverride_IsApplied()
    {
        var endpoint = DesktopUpdateEndpointResolver.Resolve(name =>
            name == DesktopUpdateConstants.UpdateServerBaseUrlEnvironmentVariable
                ? "https://updates.flowstock.example:7443"
                : null);

        Assert.Equal("https://updates.flowstock.example:7443/", endpoint.AbsoluteUri);
    }

    [Fact]
    public void OperationalEnvironmentOverride_DoesNotOverrideUpdateAuthority()
    {
        var requestedKeys = new List<string>();
        var endpoint = DesktopUpdateEndpointResolver.Resolve(name =>
        {
            requestedKeys.Add(name);
            return name == "FLOWSTOCK_SERVER_BASE_URL"
                ? "https://100.66.142.112:7154"
                : null;
        });

        Assert.Equal("https://flowstock.local:7154/", endpoint.AbsoluteUri);
        Assert.Equal(new[] { DesktopUpdateConstants.UpdateServerBaseUrlEnvironmentVariable }, requestedKeys);
    }

    [Theory]
    [InlineData("http://flowstock.local:7154")]
    [InlineData("ftp://flowstock.local:7154")]
    [InlineData("https://flowstock.local:7154/api")]
    [InlineData("https://flowstock.local:7154/?source=other")]
    [InlineData("https://user@flowstock.local:7154")]
    [InlineData("not a uri")]
    public void InvalidOrNonHttpsNonLoopbackEndpoint_IsRejected(string value)
    {
        Assert.Throws<InvalidOperationException>(() =>
            DesktopUpdateEndpointResolver.Resolve(_ => value));
    }

    [Fact]
    public void LoopbackHttp_RemainsAvailableForAutomatedHarnesses()
    {
        var endpoint = DesktopUpdateEndpointResolver.Resolve(_ => "http://127.0.0.1:17154");

        Assert.Equal("http://127.0.0.1:17154/", endpoint.AbsoluteUri);
    }

    [Fact]
    public void UpdateHttpHandler_DoesNotDisableCertificateValidation()
    {
        using var handler = DesktopUpdateHttpClientFactory.CreateHandler();

        Assert.Null(handler.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public async Task InitialCheckEndpoint_IsPersistedInRequestAndUsedForUpdaterRecheck()
    {
        var endpoint = DesktopUpdateEndpointResolver.Resolve(_ => "https://updates.flowstock.example:7443");
        var handler = new RecordingManifestHandler(ManifestJson(TargetCommit));
        using var http = new HttpClient(handler);
        var runner = new SuccessfulGitRunner();
        var checker = new DesktopUpdateChecker(http, new GitRepositoryClient(runner));
        var installed = BuildIdentity.Create("1.0.0", InstalledCommit);

        var initial = await checker.CheckAsync(
            endpoint,
            DesktopUpdateConstants.DefaultRepositoryRoot,
            installed,
            CancellationToken.None);
        Assert.True(initial.CanUpdate);
        Assert.Equal(endpoint, initial.UpdateServerBaseUri);

        var preparedRequest = UpdateBootstrapService.CreateRequest(
            Guid.NewGuid().ToString("N"),
            new string('a', 64),
            initial.UpdateServerBaseUri,
            DesktopUpdateConstants.DefaultRepositoryRoot,
            initial.Installed,
            initial.Target!,
            123,
            sourceRunFallback: true);
        var stateRoot = Path.Combine(Path.GetTempPath(), $"flowstock-update-endpoint-{Guid.NewGuid():N}");
        var requestPath = Path.Combine(stateRoot, "request.json");
        try
        {
            JsonStateStore.WriteAtomic(requestPath, preparedRequest);
            var request = JsonStateStore.Read<UpdateRequest>(requestPath)!;
            var updaterEndpoint = UpdaterEngine.ResolveUpdateServerBaseUri(request);

            var recheck = await checker.CheckAsync(
                updaterEndpoint,
                request.RepositoryRoot,
                request.Current,
                CancellationToken.None);

            Assert.True(recheck.CanUpdate);
            Assert.Equal(endpoint.AbsoluteUri, request.UpdateServerBaseUrl);
            Assert.Equal(endpoint, updaterEndpoint);
            Assert.Equal(2, handler.RequestUris.Count);
            Assert.All(handler.RequestUris, uri =>
                Assert.Equal("https://updates.flowstock.example:7443/api/version", uri.AbsoluteUri));
            Assert.Contains("\"serverBaseUrl\"", File.ReadAllText(requestPath), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(stateRoot))
            {
                Directory.Delete(stateRoot, recursive: true);
            }
        }
    }

    private static string ManifestJson(string commit) => $$"""
        {
          "server_build": { "product_version": "1.1.0", "source_commit": "{{commit}}" },
          "desktop_update": {
            "manifest_version": 1,
            "policy": "server_source_commit",
            "repository_url": "https://github.com/naskek/FlowStock.git",
            "branch": "main",
            "target_product_version": "1.1.0",
            "target_commit": "{{commit}}",
            "minimum_updater_protocol": 1
          }
        }
        """;

    private sealed class RecordingManifestHandler(string json) : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class SuccessfulGitRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            string? workingDirectory,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var args = arguments.ToArray();
            if (args.Contains("--show-toplevel"))
            {
                return Task.FromResult(new ProcessResult(
                    0,
                    DesktopUpdateConstants.DefaultRepositoryRoot,
                    string.Empty));
            }

            if (args.Contains("get-url"))
            {
                return Task.FromResult(new ProcessResult(
                    0,
                    DesktopUpdateConstants.RepositoryUrl,
                    string.Empty));
            }

            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }
}
