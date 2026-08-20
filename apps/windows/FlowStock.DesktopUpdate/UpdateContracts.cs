using System.Text.Json.Serialization;

namespace FlowStock.DesktopUpdate;

public static class DesktopUpdateConstants
{
    public const int ProtocolVersion = 1;
    public const string Policy = "server_source_commit";
    public const string RemoteName = "origin";
    public const string RemoteBranch = "main";
    public const string RepositoryUrl = "https://github.com/naskek/FlowStock.git";
    public const string DefaultUpdateServerBaseUrl = "https://flowstock.local:7154";
    public const string UpdateServerBaseUrlEnvironmentVariable = "FLOWSTOCK_UPDATE_SERVER_BASE_URL";
    public const string DefaultRepositoryRoot = @"D:\FlowStock";
    public const string DevelopmentRepositoryRoot = @"D:\FlowStock-dev";
    public const string AppMutexName = @"Local\FlowStock.Desktop";
    public const string UpdaterMutexName = @"Local\FlowStock.Updater";
}

public sealed record ServerVersionResponse
{
    [JsonPropertyName("version")]
    public string? LegacyVersion { get; init; }

    [JsonPropertyName("pc_web_version")]
    public string? PcWebVersion { get; init; }

    [JsonPropertyName("server_build")]
    public ServerBuildDto? ServerBuild { get; init; }

    [JsonPropertyName("desktop_update")]
    public DesktopUpdateManifest? DesktopUpdate { get; init; }
}

public sealed record ServerBuildDto
{
    [JsonPropertyName("product_version")]
    public string? ProductVersion { get; init; }

    [JsonPropertyName("source_commit")]
    public string? SourceCommit { get; init; }
}

public sealed record DesktopUpdateManifest
{
    [JsonPropertyName("manifest_version")]
    public int ManifestVersion { get; init; }

    [JsonPropertyName("policy")]
    public string? Policy { get; init; }

    [JsonPropertyName("repository_url")]
    public string? RepositoryUrl { get; init; }

    [JsonPropertyName("branch")]
    public string? Branch { get; init; }

    [JsonPropertyName("target_product_version")]
    public string? TargetProductVersion { get; init; }

    [JsonPropertyName("target_commit")]
    public string? TargetCommit { get; init; }

    [JsonPropertyName("minimum_updater_protocol")]
    public int MinimumUpdaterProtocol { get; init; }

    public BuildIdentity ValidateAndGetTarget()
    {
        if (ManifestVersion != DesktopUpdateConstants.ProtocolVersion
            || MinimumUpdaterProtocol > DesktopUpdateConstants.ProtocolVersion)
        {
            throw new InvalidOperationException("Версия update-протокола не поддерживается.");
        }

        if (!string.Equals(Policy, DesktopUpdateConstants.Policy, StringComparison.Ordinal)
            || !string.Equals(RepositoryUrl, DesktopUpdateConstants.RepositoryUrl, StringComparison.Ordinal)
            || !string.Equals(Branch, DesktopUpdateConstants.RemoteBranch, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Server update manifest не соответствует локальной update policy.");
        }

        return BuildIdentity.Create(TargetProductVersion ?? string.Empty, TargetCommit ?? string.Empty);
    }
}

public enum DesktopUpdateState
{
    Current,
    UpgradeAvailable,
    ClientAheadBlocked,
    DivergedBlocked,
    UnsafeRepository,
    Unavailable
}

public sealed record DesktopUpdateCheckResult(
    DesktopUpdateState State,
    BuildIdentity Installed,
    BuildIdentity? Target,
    string Message,
    Uri UpdateServerBaseUri,
    string? Diagnostics = null)
{
    public bool CanUpdate => State == DesktopUpdateState.UpgradeAvailable && Target != null;
}

public sealed record RuntimeManifest(int SchemaVersion, string ProductVersion, string Commit)
{
    public BuildIdentity GetIdentity() => BuildIdentity.Create(ProductVersion, Commit);
}

public sealed record UpdateRequest(
    int SchemaVersion,
    string SessionId,
    string StartupToken,
    [property: JsonPropertyName("serverBaseUrl")] string UpdateServerBaseUrl,
    string RepositoryRoot,
    BuildIdentity Current,
    BuildIdentity Target,
    int CurrentProcessId,
    bool SourceRunFallback);

public sealed record PendingUpdateTransaction(
    int SchemaVersion,
    string SessionId,
    string StartupToken,
    string Phase,
    BuildIdentity Current,
    BuildIdentity Target,
    string? PreviousRuntimeCommit,
    string? CandidateRuntimeCommit,
    bool SourceRunFallback,
    string RecoveryBundleSha256,
    DateTimeOffset CreatedAt,
    string LogFileName);

public sealed record UpdateStartupAck(
    int SchemaVersion,
    string SessionId,
    string StartupToken,
    BuildIdentity Actual,
    DateTimeOffset StartedAt);

public sealed record UpdateResult(
    int SchemaVersion,
    string SessionId,
    bool Success,
    BuildIdentity Current,
    BuildIdentity Target,
    string Stage,
    string Message,
    string? FallbackError,
    string LogPath,
    DateTimeOffset CompletedAt);
