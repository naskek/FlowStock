using System.Reflection;
using System.Text.Json.Serialization;
using FlowStock.DesktopUpdate;

namespace FlowStock.Server;

public static class ServerBuildIdentity
{
    public static ServerVersionPayload CreateVersionPayload(
        string legacyVersion,
        string pcWebVersion,
        string? expectedDeployCommit,
        Action<string>? reportCritical = null)
    {
        BuildIdentity? embedded = null;
        try
        {
            embedded = BuildIdentity.FromAssembly(Assembly.GetExecutingAssembly());
        }
        catch (Exception exception)
        {
            reportCritical?.Invoke($"Server assembly build identity некорректна: {exception.Message}");
        }

        DesktopUpdateManifest? update = null;
        if (embedded is not null)
        {
            if (!BuildIdentity.IsFullCommit(expectedDeployCommit)
                || !string.Equals(embedded.SourceCommit, expectedDeployCommit, StringComparison.Ordinal))
            {
                reportCritical?.Invoke(
                    "FLOWSTOCK_SOURCE_COMMIT отсутствует, некорректен или не совпадает со встроенным source commit. Desktop update отключён.");
            }
            else
            {
                update = new DesktopUpdateManifest
                {
                    ManifestVersion = DesktopUpdateConstants.ProtocolVersion,
                    Policy = DesktopUpdateConstants.Policy,
                    RepositoryUrl = DesktopUpdateConstants.RepositoryUrl,
                    Branch = DesktopUpdateConstants.RemoteBranch,
                    TargetProductVersion = embedded.ProductVersion,
                    TargetCommit = embedded.SourceCommit,
                    MinimumUpdaterProtocol = DesktopUpdateConstants.ProtocolVersion
                };
            }
        }

        return new ServerVersionPayload(
            legacyVersion,
            pcWebVersion,
            embedded is null ? null : new ServerBuildDto
            {
                ProductVersion = embedded.ProductVersion,
                SourceCommit = embedded.SourceCommit
            },
            update);
    }
}

public sealed record ServerVersionPayload(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("pc_web_version")] string PcWebVersion,
    [property: JsonPropertyName("server_build")] ServerBuildDto? ServerBuild,
    [property: JsonPropertyName("desktop_update")] DesktopUpdateManifest? DesktopUpdate);
