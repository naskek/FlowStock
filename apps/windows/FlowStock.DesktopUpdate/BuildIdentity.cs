using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace FlowStock.DesktopUpdate;

public sealed record BuildIdentity(string ProductVersion, string SourceCommit)
{
    private static readonly Regex ProductVersionPattern = new(
        @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CommitPattern = new(
        "^[0-9a-f]{40}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public string ShortCommit => SourceCommit.Length >= 8 ? SourceCommit[..8] : SourceCommit;

    public static BuildIdentity FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return ParseInformationalVersion(informationalVersion);
    }

    public static BuildIdentity FromFile(string assemblyPath)
    {
        var informationalVersion = FileVersionInfo.GetVersionInfo(assemblyPath).ProductVersion;
        return ParseInformationalVersion(informationalVersion);
    }

    public static BuildIdentity ParseInformationalVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            throw new InvalidOperationException("InformationalVersion отсутствует.");
        }

        var separator = informationalVersion.LastIndexOf('+');
        if (separator <= 0 || separator == informationalVersion.Length - 1)
        {
            throw new InvalidOperationException("InformationalVersion не содержит полный source commit.");
        }

        var productVersion = informationalVersion[..separator];
        var sourceCommit = informationalVersion[(separator + 1)..];
        return Create(productVersion, sourceCommit);
    }

    public static BuildIdentity Create(string productVersion, string sourceCommit)
    {
        productVersion = productVersion.Trim();
        sourceCommit = sourceCommit.Trim();
        if (!ProductVersionPattern.IsMatch(productVersion))
        {
            throw new InvalidOperationException($"Некорректная product version: {productVersion}");
        }

        if (!CommitPattern.IsMatch(sourceCommit))
        {
            throw new InvalidOperationException("Source commit должен быть полным lowercase SHA-1.");
        }

        return new BuildIdentity(productVersion, sourceCommit);
    }

    public static bool IsFullCommit(string? value) =>
        !string.IsNullOrWhiteSpace(value) && CommitPattern.IsMatch(value);
}
