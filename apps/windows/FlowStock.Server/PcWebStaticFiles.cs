using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

[assembly: InternalsVisibleTo("FlowStock.Server.Tests")]

namespace FlowStock.Server;

internal sealed record PcWebFingerprintInput(string CanonicalPath, byte[] Content);

internal sealed record PcWebBundle(
    string Version,
    byte[] RenderedIndex,
    string PcRoot,
    string CompatPath);

internal static class PcWebStaticFiles
{
    internal const string VersionPlaceholder = "__FLOWSTOCK_PC_WEB_VERSION__";
    internal const string HtmlCacheControl = "no-store, max-age=0";
    internal const string RevalidatedCacheControl = "no-cache";
    internal const string ImmutableCacheControl = "public, max-age=31536000, immutable";

    private static readonly string[] RuntimePcPaths =
    [
        "pc/app.js",
        "pc/pc-auth.js",
        "pc/pc-catalog.js",
        "pc/pc-core.js",
        "pc/pc-order-modal.js",
        "pc/pc-stock.js",
        "pc/styles.css",
        "pc/warehouse-board.js"
    ];

    private static readonly HashSet<string> RuntimeRequestPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/app.js",
        "/compat.js",
        "/pc-auth.js",
        "/pc-catalog.js",
        "/pc-core.js",
        "/pc-order-modal.js",
        "/pc-stock.js",
        "/styles.css",
        "/warehouse-board.js"
    };

    internal static PcWebBundle Load(string tsdRoot, string pcRoot)
    {
        var indexPath = Path.Combine(pcRoot, "index.html");
        var compatPath = Path.Combine(tsdRoot, "compat.js");
        var indexBytes = ReadRequiredFile(indexPath, "pc/index.html");
        var rawTemplate = Encoding.UTF8.GetString(indexBytes);
        if (!rawTemplate.Contains(VersionPlaceholder, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"PC index template must contain the {VersionPlaceholder} placeholder.");
        }

        var runtimeInputs = RuntimePcPaths
            .Select(canonicalPath => new PcWebFingerprintInput(
                canonicalPath,
                ReadRequiredFile(
                    Path.Combine(pcRoot, canonicalPath["pc/".Length..].Replace('/', Path.DirectorySeparatorChar)),
                    canonicalPath)))
            .Append(new PcWebFingerprintInput("compat.js", ReadRequiredFile(compatPath, "compat.js")))
            .ToArray();

        var version = ComputeVersion(
            new PcWebFingerprintInput("pc/index.html", indexBytes),
            runtimeInputs);
        var renderedIndex = Encoding.UTF8.GetBytes(
            rawTemplate.Replace(VersionPlaceholder, version, StringComparison.Ordinal));

        return new PcWebBundle(version, renderedIndex, pcRoot, compatPath);
    }

    internal static string ComputeVersion(
        PcWebFingerprintInput indexTemplate,
        IEnumerable<PcWebFingerprintInput> runtimeInputs)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendFingerprintInput(hash, indexTemplate);
        foreach (var input in runtimeInputs.OrderBy(input => input.CanonicalPath, StringComparer.Ordinal))
        {
            AppendFingerprintInput(hash, input);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static void Use(IApplicationBuilder app, PcWebBundle bundle)
    {
        var provider = new PhysicalFileProvider(bundle.PcRoot);
        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".webmanifest"] = "application/manifest+json";

        app.UseWhen(
            context => !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                       && !context.Request.Path.StartsWithSegments("/tsd", StringComparison.OrdinalIgnoreCase),
            pcApp =>
            {
                pcApp.Use(async (context, next) =>
                {
                    if (IsIndexRequest(context.Request.Path))
                    {
                        await WriteIndexAsync(context, bundle.RenderedIndex);
                        return;
                    }

                    if (context.Request.Path.Equals("/compat.js", StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyAssetCacheHeaders(context, bundle.Version, isRuntimeAsset: true);
                        context.Response.ContentType = "text/javascript; charset=utf-8";
                        await context.Response.SendFileAsync(bundle.CompatPath, context.RequestAborted);
                        return;
                    }

                    await next();
                });
                pcApp.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = provider,
                    ContentTypeProvider = contentTypes,
                    OnPrepareResponse = responseContext => ApplyAssetCacheHeaders(
                        responseContext.Context,
                        bundle.Version,
                        RuntimeRequestPaths.Contains(responseContext.Context.Request.Path.Value ?? string.Empty))
                });
                pcApp.Use(async (context, next) =>
                {
                    await next();
                    if (context.Response.StatusCode != StatusCodes.Status404NotFound)
                    {
                        return;
                    }

                    await WriteIndexAsync(context, bundle.RenderedIndex);
                });
            });
    }

    internal static void ApplyVersionCacheHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
    }

    internal static void ApplyAssetCacheHeaders(
        HttpContext context,
        string currentVersion,
        bool isRuntimeAsset)
    {
        if (!isRuntimeAsset)
        {
            context.Response.Headers.CacheControl = RevalidatedCacheControl;
            return;
        }

        var hasCurrentVersion = context.Request.Query.TryGetValue("v", out var values)
                                && values.Count == 1
                                && string.Equals(values[0], currentVersion, StringComparison.Ordinal);
        context.Response.Headers.CacheControl = hasCurrentVersion
            ? ImmutableCacheControl
            : RevalidatedCacheControl;
    }

    private static bool IsIndexRequest(PathString path)
    {
        return path == "/" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteIndexAsync(HttpContext context, byte[] renderedIndex)
    {
        context.Response.Headers.CacheControl = HtmlCacheControl;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength = renderedIndex.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.Body.WriteAsync(renderedIndex, context.RequestAborted);
        }
    }

    private static byte[] ReadRequiredFile(string path, string canonicalPath)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Required PC web runtime file is missing: {canonicalPath}.");
        }

        return File.ReadAllBytes(path);
    }

    private static void AppendFingerprintInput(IncrementalHash hash, PcWebFingerprintInput input)
    {
        var canonicalPath = input.CanonicalPath.Replace('\\', '/');
        var pathBytes = Encoding.UTF8.GetBytes(canonicalPath);
        Span<byte> intBuffer = stackalloc byte[sizeof(int)];
        Span<byte> longBuffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt32BigEndian(intBuffer, pathBytes.Length);
        BinaryPrimitives.WriteInt64BigEndian(longBuffer, input.Content.LongLength);
        hash.AppendData(intBuffer);
        hash.AppendData(pathBytes);
        hash.AppendData(longBuffer);
        hash.AppendData(input.Content);
    }
}
