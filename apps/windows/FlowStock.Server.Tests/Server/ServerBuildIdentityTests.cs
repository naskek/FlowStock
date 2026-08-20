using FlowStock.DesktopUpdate;
using FlowStock.Server;

namespace FlowStock.Server.Tests.Server;

public sealed class ServerBuildIdentityTests
{
    [Fact]
    public void MatchingDeployCommit_PublishesSameExactDesktopTarget()
    {
        var embedded = BuildIdentity.FromAssembly(typeof(ServerBuildIdentity).Assembly);

        var payload = ServerBuildIdentity.CreateVersionPayload("legacy", "web", embedded.SourceCommit);

        Assert.Equal("legacy", payload.Version);
        Assert.Equal("web", payload.PcWebVersion);
        Assert.Equal(embedded.SourceCommit, payload.ServerBuild?.SourceCommit);
        Assert.Equal(embedded.SourceCommit, payload.DesktopUpdate?.TargetCommit);
        Assert.Equal(DesktopUpdateConstants.RepositoryUrl, payload.DesktopUpdate?.RepositoryUrl);
        Assert.Equal(DesktopUpdateConstants.RemoteBranch, payload.DesktopUpdate?.Branch);
    }

    [Fact]
    public void RuntimeDeployCommitMismatch_SuppressesDesktopUpdate()
    {
        string? critical = null;

        var payload = ServerBuildIdentity.CreateVersionPayload(
            "legacy",
            "web",
            "0123456789abcdef0123456789abcdef01234567",
            message => critical = message);

        Assert.NotNull(payload.ServerBuild);
        Assert.Null(payload.DesktopUpdate);
        Assert.NotNull(critical);
    }
}
