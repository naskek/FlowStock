using FlowStock.DesktopUpdate;

namespace FlowStock.Server.Tests.DesktopUpdate;

public sealed class GitTargetPolicyTests
{
    private static readonly BuildIdentity Installed = BuildIdentity.Create(
        "1.0.0",
        "1111111111111111111111111111111111111111");
    private static readonly BuildIdentity Target = BuildIdentity.Create(
        "1.1.0",
        "2222222222222222222222222222222222222222");

    [Fact]
    public async Task InstalledAncestorTarget_AllowsUpgradeEvenWhenRemoteMainCanBeAhead()
    {
        var client = new GitRepositoryClient(new RelationRunner(Relation.Upgrade));

        await client.ValidateTargetAsync(@"D:\FlowStock", Installed, Target, CancellationToken.None);
    }

    [Fact]
    public async Task InstalledAheadTarget_BlocksDowngrade()
    {
        var client = new GitRepositoryClient(new RelationRunner(Relation.ClientAhead));

        await Assert.ThrowsAsync<ClientAheadException>(() =>
            client.ValidateTargetAsync(@"D:\FlowStock", Installed, Target, CancellationToken.None));
    }

    [Fact]
    public async Task DivergedInstalledAndTarget_BlocksUpdate()
    {
        var client = new GitRepositoryClient(new RelationRunner(Relation.Diverged));

        await Assert.ThrowsAsync<DivergedClientException>(() =>
            client.ValidateTargetAsync(@"D:\FlowStock", Installed, Target, CancellationToken.None));
    }

    [Fact]
    public async Task TargetOutsideFetchedRemoteMain_IsRejected()
    {
        var client = new GitRepositoryClient(new RelationRunner(Relation.TargetOutsideRemote));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ValidateTargetAsync(@"D:\FlowStock", Installed, Target, CancellationToken.None));
    }

    private enum Relation
    {
        Upgrade,
        ClientAhead,
        Diverged,
        TargetOutsideRemote
    }

    private sealed class RelationRunner(Relation relation) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            string? workingDirectory,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var args = arguments.ToArray();
            var commandIndex = Array.IndexOf(args, "merge-base");
            if (commandIndex < 0)
            {
                return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            }

            var left = args[commandIndex + 2];
            var right = args[commandIndex + 3];
            if (right == $"refs/remotes/{DesktopUpdateConstants.RemoteName}/{DesktopUpdateConstants.RemoteBranch}")
            {
                return Result(relation == Relation.TargetOutsideRemote ? 1 : 0);
            }

            if (left == Installed.SourceCommit && right == Target.SourceCommit)
            {
                return Result(relation == Relation.Upgrade ? 0 : 1);
            }

            if (left == Target.SourceCommit && right == Installed.SourceCommit)
            {
                return Result(relation == Relation.ClientAhead ? 0 : 1);
            }

            return Result(1);
        }

        private static Task<ProcessResult> Result(int exitCode) =>
            Task.FromResult(new ProcessResult(exitCode, string.Empty, exitCode == 0 ? string.Empty : "not ancestor"));
    }
}
