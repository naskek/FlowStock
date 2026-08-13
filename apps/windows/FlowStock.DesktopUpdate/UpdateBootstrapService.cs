using System.Diagnostics;
using System.Security.Cryptography;

namespace FlowStock.DesktopUpdate;

public sealed class UpdateBootstrapService
{
    private readonly DesktopUpdatePaths _paths;
    private readonly RuntimeStateManager _state;
    private readonly GitRepositoryClient _git;
    private readonly IProcessRunner _runner;

    public UpdateBootstrapService(
        DesktopUpdatePaths paths,
        RuntimeStateManager state,
        GitRepositoryClient git,
        IProcessRunner runner)
    {
        _paths = paths;
        _state = state;
        _git = git;
        _runner = runner;
    }

    public async Task<string> PrepareAndLaunchAsync(
        Uri updateServerBaseUri,
        string repositoryRoot,
        BuildIdentity current,
        BuildIdentity target,
        CancellationToken cancellationToken)
    {
        _paths.EnsureBaseDirectories();
        var sessionId = Guid.NewGuid().ToString("N");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var transactionRoot = _paths.TransactionRoot(sessionId);
        var recoveryDirectory = _paths.RecoveryDirectory(sessionId);
        Directory.CreateDirectory(transactionRoot);

        var active = _state.ReadActive();
        var sourceRun = active is null;
        if (sourceRun)
        {
            await PublishBootstrapUpdaterAsync(
                repositoryRoot,
                current,
                transactionRoot,
                recoveryDirectory,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (active!.GetIdentity() != current)
            {
                throw new InvalidOperationException("Запущенная assembly не совпадает с active runtime manifest.");
            }

            CopyDirectory(_paths.UpdaterDirectory(current.SourceCommit), recoveryDirectory);
        }

        var recoveryExecutable = _paths.RecoveryExecutable(sessionId);
        if (!File.Exists(recoveryExecutable) || BuildIdentity.FromFile(recoveryExecutable) != current)
        {
            throw new InvalidOperationException("Recovery updater не прошёл проверку embedded BuildIdentity.");
        }

        var request = CreateRequest(
            sessionId,
            token,
            updateServerBaseUri,
            repositoryRoot,
            current,
            target,
            Environment.ProcessId,
            sourceRun);
        JsonStateStore.WriteAtomic(_paths.RequestFile(sessionId), request);

        RuntimeStateManager.Start(
            recoveryExecutable,
            ["--update", sessionId],
            recoveryDirectory).Dispose();
        return sessionId;
    }

    public static UpdateRequest CreateRequest(
        string sessionId,
        string startupToken,
        Uri updateServerBaseUri,
        string repositoryRoot,
        BuildIdentity current,
        BuildIdentity target,
        int currentProcessId,
        bool sourceRunFallback)
    {
        var validatedEndpoint = DesktopUpdateEndpointResolver.Validate(updateServerBaseUri);
        return new UpdateRequest(
            DesktopUpdateConstants.ProtocolVersion,
            sessionId,
            startupToken,
            validatedEndpoint.AbsoluteUri,
            Path.GetFullPath(repositoryRoot),
            current,
            target,
            currentProcessId,
            sourceRunFallback);
    }

    public async Task WaitUntilReadyAsync(string sessionId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        while (!File.Exists(_paths.ReadyFile(sessionId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (started.Elapsed >= timeout)
            {
                throw new TimeoutException("Updater не подтвердил готовность к завершению WPF.");
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishBootstrapUpdaterAsync(
        string repositoryRoot,
        BuildIdentity current,
        string transactionRoot,
        string recoveryDirectory,
        CancellationToken cancellationToken)
    {
        await _git.ValidateRepositoryAndRemoteAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        var source = Path.Combine(transactionRoot, "source");
        await _git.CreateDetachedWorktreeAsync(repositoryRoot, source, current.SourceCommit, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var project = Path.Combine(source, "apps", "windows", "FlowStock.Updater", "FlowStock.Updater.csproj");
            var result = await _runner.RunAsync(
                "dotnet",
                ["publish", project, "-c", "Release", "-o", recoveryDirectory,
                    $"/p:SourceRevisionId={current.SourceCommit}"],
                source,
                TimeSpan.FromMinutes(10),
                cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                throw new InvalidOperationException($"Не удалось собрать bootstrap updater: {result.StandardError.Trim()}");
            }
        }
        finally
        {
            try
            {
                await _git.RemoveWorktreeAsync(repositoryRoot, source, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Cleanup failure is diagnostic-only; the updater-owned path remains isolated.
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Updater runtime не найден: {source}");
        }

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
