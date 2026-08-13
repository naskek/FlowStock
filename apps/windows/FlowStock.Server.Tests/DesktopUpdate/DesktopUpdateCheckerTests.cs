using System.Net;
using System.Text;
using FlowStock.DesktopUpdate;

namespace FlowStock.Server.Tests.DesktopUpdate;

public sealed class DesktopUpdateCheckerTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task EqualCommit_ReturnsCurrentWithoutAnyGitProcess()
    {
        var runner = new RejectingProcessRunner();
        using var http = new HttpClient(new JsonHandler(ManifestJson(Commit)));
        var checker = new DesktopUpdateChecker(http, new GitRepositoryClient(runner));

        var result = await checker.CheckAsync(
            new Uri("https://flowstock.example/"),
            @"D:\FlowStock",
            BuildIdentity.Create("1.0.0", Commit),
            CancellationToken.None);

        Assert.Equal(DesktopUpdateState.Current, result.State);
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public async Task ServerRepositoryMetadataMismatch_FailsClosed()
    {
        using var http = new HttpClient(new JsonHandler(
            ManifestJson(Commit).Replace(DesktopUpdateConstants.RemoteBranch, "release", StringComparison.Ordinal)));
        var checker = new DesktopUpdateChecker(http, new GitRepositoryClient(new RejectingProcessRunner()));

        var result = await checker.CheckAsync(
            new Uri("https://flowstock.example/"),
            @"D:\FlowStock",
            BuildIdentity.Create("1.0.0", Commit),
            CancellationToken.None);

        Assert.Equal(DesktopUpdateState.Unavailable, result.State);
    }

    [Fact]
    public async Task NonHttpsNonLoopbackEndpoint_IsRejectedBeforeHttpRequest()
    {
        var handler = new RejectingHttpHandler();
        using var http = new HttpClient(handler);
        var checker = new DesktopUpdateChecker(http, new GitRepositoryClient(new RejectingProcessRunner()));

        var result = await checker.CheckAsync(
            new Uri("http://flowstock.local:7154/"),
            @"D:\FlowStock",
            BuildIdentity.Create("1.0.0", Commit),
            CancellationToken.None);

        Assert.Equal(DesktopUpdateState.Unavailable, result.State);
        Assert.Equal(0, handler.CallCount);
        Assert.Contains("http://flowstock.local:7154", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrustedEndpointTlsFailure_IncludesEndpointWithoutAllowInvalidTlsSuggestion()
    {
        using var http = new HttpClient(new ThrowingHttpHandler());
        var checker = new DesktopUpdateChecker(http, new GitRepositoryClient(new RejectingProcessRunner()));

        var result = await checker.CheckAsync(
            new Uri("https://flowstock.local:7154/"),
            @"D:\FlowStock",
            BuildIdentity.Create("1.0.0", Commit),
            CancellationToken.None);

        Assert.Equal(DesktopUpdateState.Unavailable, result.State);
        Assert.Contains("Не удалось связаться с доверенным сервером обновлений", result.Message, StringComparison.Ordinal);
        Assert.Contains("https://flowstock.local:7154", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("allow_invalid_tls", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string ManifestJson(string commit) => $$"""
        {
          "server_build": { "product_version": "1.0.0", "source_commit": "{{commit}}" },
          "desktop_update": {
            "manifest_version": 1,
            "policy": "server_source_commit",
            "repository_url": "https://github.com/naskek/FlowStock.git",
            "branch": "main",
            "target_product_version": "1.0.0",
            "target_commit": "{{commit}}",
            "minimum_updater_protocol": 1
          }
        }
        """;

    private sealed class RejectingProcessRunner : IProcessRunner
    {
        public int CallCount { get; private set; }

        public Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            string? workingDirectory,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new Xunit.Sdk.XunitException("Git/process invocation was not expected.");
        }
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class RejectingHttpHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new Xunit.Sdk.XunitException("HTTP request не должен выполняться для invalid update endpoint.");
        }
    }

    private sealed class ThrowingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException(
                "TLS validation failed",
                new System.Security.Authentication.AuthenticationException("RemoteCertificateNameMismatch"));
    }
}
