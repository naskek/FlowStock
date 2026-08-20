namespace FlowStock.DesktopUpdate;

public sealed class GitRepositoryClient
{
    private readonly IProcessRunner _runner;

    public GitRepositoryClient(IProcessRunner runner)
    {
        _runner = runner;
    }

    public async Task ValidateRepositoryAndRemoteAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var resolved = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar);
        var dev = Path.GetFullPath(DesktopUpdateConstants.DevelopmentRepositoryRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(resolved, dev, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Self-update из development repository запрещён.");
        }

        var root = await GitAsync(repositoryRoot, ["rev-parse", "--show-toplevel"], cancellationToken);
        if (!string.Equals(
                Path.GetFullPath(root.StandardOutput.Trim()).TrimEnd(Path.DirectorySeparatorChar),
                resolved,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Configured repository root не совпадает с Git repository root.");
        }

        var remote = await GitAsync(
            repositoryRoot,
            ["remote", "get-url", DesktopUpdateConstants.RemoteName],
            cancellationToken);
        if (!string.Equals(remote.StandardOutput.Trim(), DesktopUpdateConstants.RepositoryUrl, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Fetch URL remote origin не соответствует canonical FlowStock repository.");
        }
    }

    public Task FetchExpectedBranchAsync(string repositoryRoot, CancellationToken cancellationToken) =>
        GitAsync(
            repositoryRoot,
            ["fetch", "--no-tags", DesktopUpdateConstants.RemoteName,
                $"refs/heads/{DesktopUpdateConstants.RemoteBranch}:refs/remotes/{DesktopUpdateConstants.RemoteName}/{DesktopUpdateConstants.RemoteBranch}"],
            cancellationToken,
            TimeSpan.FromMinutes(2));

    public async Task ValidateTargetAsync(
        string repositoryRoot,
        BuildIdentity installed,
        BuildIdentity target,
        CancellationToken cancellationToken)
    {
        await GitAsync(repositoryRoot, ["cat-file", "-e", $"{target.SourceCommit}^{{commit}}"], cancellationToken);
        await GitAsync(repositoryRoot, ["cat-file", "-e", $"{installed.SourceCommit}^{{commit}}"], cancellationToken);
        await GitAsync(
            repositoryRoot,
            ["merge-base", "--is-ancestor", target.SourceCommit,
                $"refs/remotes/{DesktopUpdateConstants.RemoteName}/{DesktopUpdateConstants.RemoteBranch}"],
            cancellationToken);

        var relation = await GitRawAsync(
            repositoryRoot,
            ["merge-base", "--is-ancestor", installed.SourceCommit, target.SourceCommit],
            cancellationToken);
        if (relation.ExitCode != 0)
        {
            var reverse = await GitRawAsync(
                repositoryRoot,
                ["merge-base", "--is-ancestor", target.SourceCommit, installed.SourceCommit],
                cancellationToken);
            throw reverse.ExitCode == 0
                ? new ClientAheadException("Установленный WPF новее production server target; downgrade заблокирован.")
                : new DivergedClientException("Installed runtime и server target принадлежат расходящимся историям.");
        }
    }

    public Task CreateDetachedWorktreeAsync(
        string repositoryRoot,
        string worktreePath,
        string commit,
        CancellationToken cancellationToken) =>
        GitAsync(repositoryRoot, ["worktree", "add", "--detach", worktreePath, commit], cancellationToken, TimeSpan.FromMinutes(2));

    public async Task RemoveWorktreeAsync(
        string repositoryRoot,
        string worktreePath,
        CancellationToken cancellationToken)
    {
        var result = await GitRawAsync(
            repositoryRoot,
            ["worktree", "remove", worktreePath],
            cancellationToken,
            TimeSpan.FromMinutes(2));
        if (!result.Success)
        {
            throw new InvalidOperationException($"Не удалось удалить временный worktree: {result.StandardError.Trim()}");
        }
    }

    public async Task<string> ReadDiagnosticsAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var branch = await GitRawAsync(repositoryRoot, ["status", "--short", "--branch"], cancellationToken);
        return branch.Success ? branch.StandardOutput.Trim() : branch.StandardError.Trim();
    }

    private async Task<ProcessResult> GitAsync(
        string repositoryRoot,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var result = await GitRawAsync(repositoryRoot, args, cancellationToken, timeout);
        if (!result.Success)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)}: {result.StandardError.Trim()}");
        }

        return result;
    }

    private Task<ProcessResult> GitRawAsync(
        string repositoryRoot,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var arguments = new List<string> { "-C", repositoryRoot };
        arguments.AddRange(args);
        return _runner.RunAsync("git", arguments, repositoryRoot, timeout ?? TimeSpan.FromSeconds(30), cancellationToken);
    }
}

public sealed class ClientAheadException(string message) : InvalidOperationException(message);
public sealed class DivergedClientException(string message) : InvalidOperationException(message);
