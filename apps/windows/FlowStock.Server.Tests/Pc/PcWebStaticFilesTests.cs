using System.Text;
using System.Text.Json;
using FlowStock.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.FileProviders;

namespace FlowStock.Server.Tests.Pc;

public sealed class PcWebStaticFilesTests
{
    [Fact]
    public void FingerprintIsDeterministicAndUsesCanonicalPathsAndBytes()
    {
        var template = Input("pc/index.html", "template");
        var first = PcWebStaticFiles.ComputeVersion(
            template,
            [Input("pc/app.js", "app"), Input("pc/styles.css", "styles")]);
        var reordered = PcWebStaticFiles.ComputeVersion(
            template,
            [Input("pc/styles.css", "styles"), Input("pc/app.js", "app")]);
        var changedPath = PcWebStaticFiles.ComputeVersion(
            template,
            [Input("pc/renamed-app.js", "app"), Input("pc/styles.css", "styles")]);
        var changedBytes = PcWebStaticFiles.ComputeVersion(
            template,
            [Input("pc/app.js", "changed"), Input("pc/styles.css", "styles")]);
        var changedTemplate = PcWebStaticFiles.ComputeVersion(
            Input("pc/index.html", "changed-template"),
            [Input("pc/app.js", "app"), Input("pc/styles.css", "styles")]);

        Assert.Equal(first, reordered);
        Assert.Equal(64, first.Length);
        Assert.NotEqual(first, changedPath);
        Assert.NotEqual(first, changedBytes);
        Assert.NotEqual(first, changedTemplate);
    }

    [Fact]
    public void BundleUsesRawTemplateWithoutSelfReferentialRenderedOutput()
    {
        using var files = new PcWebFiles();
        var first = PcWebStaticFiles.Load(files.TsdRoot, files.PcRoot);
        var html = Encoding.UTF8.GetString(first.RenderedIndex);

        Assert.DoesNotContain(PcWebStaticFiles.VersionPlaceholder, html, StringComparison.Ordinal);
        Assert.Equal(9, CountOccurrences(html, $"?v={first.Version}"));
        Assert.Contains(
            $"name=\"flowstock-pc-web-version\" content=\"{first.Version}\"",
            html,
            StringComparison.Ordinal);
        foreach (var runtimeUrl in new[]
                 {
                     "./styles.css",
                     "../compat.js",
                     "./pc-core.js",
                     "./pc-auth.js",
                     "./pc-order-modal.js",
                     "./pc-catalog.js",
                     "./pc-stock.js",
                     "./warehouse-board.js",
                     "./app.js"
                 })
        {
            Assert.Contains($"{runtimeUrl}?v={first.Version}", html, StringComparison.Ordinal);
        }

        first.RenderedIndex[0] ^= 0xff;
        var second = PcWebStaticFiles.Load(files.TsdRoot, files.PcRoot);
        Assert.Equal(first.Version, second.Version);
    }

    [Fact]
    public void DecorativeAssetChangesDoNotInvalidateRuntimeBundle()
    {
        using var files = new PcWebFiles();
        var first = PcWebStaticFiles.Load(files.TsdRoot, files.PcRoot);

        File.WriteAllText(Path.Combine(files.PcRoot, "assets", "FS_logo.png"), "changed-logo");
        File.WriteAllText(Path.Combine(files.PcRoot, "assets", "fs_favicon.png"), "changed-icon");

        var second = PcWebStaticFiles.Load(files.TsdRoot, files.PcRoot);
        Assert.Equal(first.Version, second.Version);
    }

    [Fact]
    public void EveryRequiredRuntimeInputInvalidatesBundle()
    {
        using var files = new PcWebFiles();
        var baseline = PcWebStaticFiles.Load(files.TsdRoot, files.PcRoot).Version;

        foreach (var relativePath in PcWebFiles.RequiredRuntimePaths)
        {
            using var changedFiles = new PcWebFiles();
            File.AppendAllText(Path.Combine(changedFiles.TsdRoot, relativePath), "changed");
            Assert.NotEqual(
                baseline,
                PcWebStaticFiles.Load(changedFiles.TsdRoot, changedFiles.PcRoot).Version);
        }
    }

    [Fact]
    public void MissingRequiredRuntimeInputFailsBundleLoading()
    {
        using var files = new PcWebFiles();
        File.Delete(Path.Combine(files.PcRoot, "pc-stock.js"));

        var error = Assert.Throws<InvalidOperationException>(() =>
            PcWebStaticFiles.Load(files.TsdRoot, files.PcRoot));

        Assert.Contains("pc/pc-stock.js", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingVersionPlaceholderFailsBundleLoading()
    {
        using var files = new PcWebFiles();
        var indexPath = Path.Combine(files.PcRoot, "index.html");
        File.WriteAllText(
            indexPath,
            File.ReadAllText(indexPath).Replace(
                PcWebStaticFiles.VersionPlaceholder,
                "fixed-version",
                StringComparison.Ordinal));

        var error = Assert.Throws<InvalidOperationException>(() =>
            PcWebStaticFiles.Load(files.TsdRoot, files.PcRoot));

        Assert.Contains(PcWebStaticFiles.VersionPlaceholder, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpPipelineAppliesPcCachePolicyAndKeepsTsdCompatSeparate()
    {
        using var files = new PcWebFiles();
        var bundle = PcWebStaticFiles.Load(files.TsdRoot, files.PcRoot);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();

        app.MapGet("/api/version", (HttpContext context) =>
        {
            PcWebStaticFiles.ApplyVersionCacheHeaders(context.Response);
            return Results.Ok(new { version = "server-version", pc_web_version = bundle.Version });
        });
        app.Map("/tsd", tsdApp =>
        {
            tsdApp.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(files.TsdRoot),
                OnPrepareResponse = response => response.Context.Response.Headers.CacheControl = "no-cache"
            });
        });
        PcWebStaticFiles.Use(app, bundle);
        await app.StartAsync();

        var client = app.GetTestClient();
        await AssertCacheControl(client, "/", PcWebStaticFiles.HtmlCacheControl);
        await AssertCacheControl(client, "/index.html", PcWebStaticFiles.HtmlCacheControl);
        await AssertCacheControl(
            client,
            $"/app.js?v={bundle.Version}",
            PcWebStaticFiles.ImmutableCacheControl);
        await AssertCacheControl(client, "/app.js", PcWebStaticFiles.RevalidatedCacheControl);
        await AssertCacheControl(client, "/app.js?v=stale", PcWebStaticFiles.RevalidatedCacheControl);
        await AssertCacheControl(
            client,
            $"/compat.js?v={bundle.Version}",
            PcWebStaticFiles.ImmutableCacheControl);
        await AssertCacheControl(client, "/compat.js?v=unknown", PcWebStaticFiles.RevalidatedCacheControl);
        await AssertCacheControl(client, "/assets/FS_logo.png", PcWebStaticFiles.RevalidatedCacheControl);
        await AssertCacheControl(client, "/assets/fs_favicon.png", PcWebStaticFiles.RevalidatedCacheControl);
        await AssertCacheControl(client, "/tsd/compat.js", "no-cache");

        var versionResponse = await client.GetAsync("/api/version");
        Assert.Equal("no-store", versionResponse.Headers.CacheControl?.ToString());
        using var versionJson = JsonDocument.Parse(await versionResponse.Content.ReadAsStringAsync());
        Assert.Equal("server-version", versionJson.RootElement.GetProperty("version").GetString());
        Assert.Equal(bundle.Version, versionJson.RootElement.GetProperty("pc_web_version").GetString());
    }

    private static PcWebFingerprintInput Input(string canonicalPath, string content)
    {
        return new PcWebFingerprintInput(canonicalPath, Encoding.UTF8.GetBytes(content));
    }

    private static async Task AssertCacheControl(HttpClient client, string path, string expected)
    {
        var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        Assert.Equal(expected, response.Headers.CacheControl?.ToString());
    }

    private static int CountOccurrences(string value, string expected)
    {
        return value.Split(expected, StringSplitOptions.None).Length - 1;
    }

    private sealed class PcWebFiles : IDisposable
    {
        internal static readonly string[] RequiredRuntimePaths =
        [
            "compat.js",
            Path.Combine("pc", "app.js"),
            Path.Combine("pc", "pc-auth.js"),
            Path.Combine("pc", "pc-catalog.js"),
            Path.Combine("pc", "pc-core.js"),
            Path.Combine("pc", "pc-order-modal.js"),
            Path.Combine("pc", "pc-stock.js"),
            Path.Combine("pc", "styles.css"),
            Path.Combine("pc", "warehouse-board.js")
        ];

        internal PcWebFiles()
        {
            TsdRoot = Directory.CreateTempSubdirectory("flowstock-pc-web-").FullName;
            PcRoot = Directory.CreateDirectory(Path.Combine(TsdRoot, "pc")).FullName;
            Directory.CreateDirectory(Path.Combine(PcRoot, "assets"));
            File.WriteAllText(
                Path.Combine(PcRoot, "index.html"),
                """
                <html>
                <head>
                  <meta name="flowstock-pc-web-version" content="__FLOWSTOCK_PC_WEB_VERSION__">
                  <link rel="stylesheet" href="./styles.css?v=__FLOWSTOCK_PC_WEB_VERSION__">
                </head>
                <body>
                  <script src="../compat.js?v=__FLOWSTOCK_PC_WEB_VERSION__"></script>
                  <script src="./pc-core.js?v=__FLOWSTOCK_PC_WEB_VERSION__"></script>
                  <script src="./pc-auth.js?v=__FLOWSTOCK_PC_WEB_VERSION__"></script>
                  <script src="./pc-order-modal.js?v=__FLOWSTOCK_PC_WEB_VERSION__"></script>
                  <script src="./pc-catalog.js?v=__FLOWSTOCK_PC_WEB_VERSION__"></script>
                  <script src="./pc-stock.js?v=__FLOWSTOCK_PC_WEB_VERSION__"></script>
                  <script src="./warehouse-board.js?v=__FLOWSTOCK_PC_WEB_VERSION__"></script>
                  <script src="./app.js?v=__FLOWSTOCK_PC_WEB_VERSION__"></script>
                </body>
                </html>
                """);
            foreach (var relativePath in RequiredRuntimePaths)
            {
                File.WriteAllText(Path.Combine(TsdRoot, relativePath), relativePath);
            }

            File.WriteAllText(Path.Combine(PcRoot, "assets", "FS_logo.png"), "logo");
            File.WriteAllText(Path.Combine(PcRoot, "assets", "fs_favicon.png"), "icon");
        }

        internal string TsdRoot { get; }

        internal string PcRoot { get; }

        public void Dispose()
        {
            Directory.Delete(TsdRoot, recursive: true);
        }
    }
}
