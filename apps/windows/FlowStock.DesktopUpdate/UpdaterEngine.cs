using System.Diagnostics;

namespace FlowStock.DesktopUpdate;

public sealed record UpdateProgress(string Stage, string Message, bool IsIndeterminate = true);

public sealed record UpdaterTimingOptions(TimeSpan StartupAckTimeout, TimeSpan StabilityWindow)
{
    public static UpdaterTimingOptions Default { get; } = new(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5));
}

public sealed class UpdaterEngine
{
    private readonly DesktopUpdatePaths _paths;
    private readonly RuntimeStateManager _state;
    private readonly IProcessRunner _runner;
    private readonly GitRepositoryClient _git;
    private readonly HttpClient _httpClient;
    private readonly Action<string> _log;
    private readonly IRuntimeLauncher _runtimeLauncher;
    private readonly UpdaterTimingOptions _timing;

    public UpdaterEngine(
        DesktopUpdatePaths paths,
        RuntimeStateManager state,
        IProcessRunner runner,
        GitRepositoryClient git,
        HttpClient httpClient,
        Action<string> log,
        IRuntimeLauncher? runtimeLauncher = null,
        UpdaterTimingOptions? timing = null)
    {
        _paths = paths;
        _state = state;
        _runner = runner;
        _git = git;
        _httpClient = httpClient;
        _log = log;
        _runtimeLauncher = runtimeLauncher ?? new RuntimeLauncher();
        _timing = timing ?? UpdaterTimingOptions.Default;
    }

    public async Task RunUpdateAsync(
        string sessionId,
        IProgress<UpdateProgress> progress,
        CancellationToken cancellationToken)
    {
        _paths.EnsureBaseDirectories();
        var request = ReadRequest(sessionId);
        var updateServerBaseUri = ValidateRequest(request);
        await ValidateToolingAsync(request, cancellationToken).ConfigureAwait(false);
        var recoveryExecutable = _paths.RecoveryExecutable(sessionId);
        var recoveryHash = RuntimeStateManager.Sha256Bundle(_paths.RecoveryDirectory(sessionId));
        var previousActive = _state.ReadActive();
        var pending = new PendingUpdateTransaction(
            DesktopUpdateConstants.ProtocolVersion,
            sessionId,
            request.StartupToken,
            "prepared",
            request.Current,
            request.Target,
            previousActive?.Commit,
            null,
            request.SourceRunFallback,
            recoveryHash,
            DateTimeOffset.UtcNow,
            Path.GetFileName(_paths.LogFile(sessionId)));
        JsonStateStore.WriteAtomic(_paths.PendingTransaction, pending);
        File.WriteAllText(_paths.ReadyFile(sessionId), string.Empty);
        Process? candidateProcess = null;

        try
        {
            progress.Report(new UpdateProgress("подготовка", "Ожидание завершения FlowStock"));
            await WaitForWpfExitAsync(request.CurrentProcessId, cancellationToken).ConfigureAwait(false);

            pending = WritePhase(pending, "preflight");
            progress.Report(new UpdateProgress("повторная проверка", "Проверка production target и Git"));
            EnsureDiskSpace(_paths.Root, 2L * 1024 * 1024 * 1024);
            var checker = new DesktopUpdateChecker(_httpClient, _git);
            var check = await checker.CheckAsync(
                updateServerBaseUri,
                request.RepositoryRoot,
                request.Current,
                cancellationToken).ConfigureAwait(false);
            if (!check.CanUpdate || check.Target != request.Target)
            {
                throw new InvalidOperationException("Production target изменился или больше не разрешён.");
            }

            var transactionRoot = _paths.TransactionRoot(sessionId);
            var source = Path.Combine(transactionRoot, "source");
            var staging = Path.Combine(transactionRoot, "staging");
            pending = WritePhase(pending, "worktree");
            progress.Report(new UpdateProgress("подготовка worktree", "Создание detached worktree exact target"));
            await _git.CreateDetachedWorktreeAsync(
                request.RepositoryRoot,
                source,
                request.Target.SourceCommit,
                cancellationToken).ConfigureAwait(false);
            try
            {
                pending = WritePhase(pending, "publish");
                progress.Report(new UpdateProgress("publish и проверка", "Сборка App и Updater"));
                await PublishAndValidateAsync(source, staging, request.Target, cancellationToken).ConfigureAwait(false);

                var secondCheck = await checker.CheckAsync(
                    updateServerBaseUri,
                    request.RepositoryRoot,
                    request.Current,
                    cancellationToken).ConfigureAwait(false);
                if (!secondCheck.CanUpdate || secondCheck.Target != request.Target)
                {
                    throw new InvalidOperationException("Production target изменился перед active switch.");
                }

                pending = WritePhase(pending, "switching");
                progress.Report(new UpdateProgress("переключение", "Установка side-by-side runtime"));
                InstallStaging(staging, request.Target);
                if (previousActive is not null)
                {
                    _state.WriteLastKnownGood(previousActive.GetIdentity());
                }

                _state.WriteActive(request.Target);
                pending = pending with { Phase = "active-switched", CandidateRuntimeCommit = request.Target.SourceCommit };
                JsonStateStore.WriteAtomic(_paths.PendingTransaction, pending);

                progress.Report(new UpdateProgress("запуск", "Запуск нового FlowStock"));
                candidateProcess = _runtimeLauncher.StartExecutable(
                    _paths.AppExecutable(request.Target.SourceCommit),
                    ["--update-session", sessionId, "--startup-token", request.StartupToken],
                    _paths.AppDirectory(request.Target.SourceCommit));

                progress.Report(new UpdateProgress("проверка старта", "Ожидание startup handshake"));
                await WaitForStartupAckAsync(request, candidateProcess, cancellationToken).ConfigureAwait(false);
                await Task.Delay(_timing.StabilityWindow, cancellationToken).ConfigureAwait(false);
                if (candidateProcess.HasExited)
                {
                    throw new InvalidOperationException("Новый FlowStock завершился в stability window.");
                }

                _state.WriteLastKnownGood(request.Target);
                CompleteTerminal(request, true, "завершено", "Обновление установлено и проверено.");
                candidateProcess.Dispose();
                candidateProcess = null;
            }
            finally
            {
                try
                {
                    await _git.RemoveWorktreeAsync(request.RepositoryRoot, source, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _log($"Cleanup worktree: {exception.Message}");
                }
            }
        }
        catch (Exception exception)
        {
            _log(exception.ToString());
            try
            {
                throw HandleFailedUpdate(request, pending, previousActive, candidateProcess, exception);
            }
            finally
            {
                candidateProcess?.Dispose();
            }
        }
    }

    public Task RecoverAsync(string sessionId, IProgress<UpdateProgress> progress)
    {
        var pending = JsonStateStore.Read<PendingUpdateTransaction>(_paths.PendingTransaction)
            ?? throw new InvalidOperationException("Pending transaction отсутствует.");
        if (!string.Equals(pending.SessionId, sessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Pending transaction session не совпадает.");
        }

        var request = ReadRequest(sessionId);
        progress.Report(new UpdateProgress("восстановление", "Проверка незавершённой транзакции"));
        var ack = JsonStateStore.Read<UpdateStartupAck>(_paths.StartupAckFile(sessionId));
        if (ack is not null
            && ack.SessionId == sessionId
            && ack.StartupToken == pending.StartupToken
            && ack.Actual == pending.Target
            && IsInstalledRuntimeValid(pending.Target))
        {
            _state.WriteActive(pending.Target);
            _state.WriteLastKnownGood(pending.Target);
            WriteResult(request, true, "recovered", "Запуск новой версии был подтверждён до сбоя updater.");
            WritePhase(pending, "success-ready");
            return Task.CompletedTask;
        }

        RuntimeManifest? previous = null;
        if (pending.PreviousRuntimeCommit is not null)
        {
            previous = _state.ReadLastKnownGood();
            if (previous?.Commit != pending.PreviousRuntimeCommit)
            {
                throw new InvalidOperationException("LKG runtime не совпадает с pending transaction.");
            }
        }

        RollBackActive(previous, pending.SourceRunFallback);
        var existingResult = JsonStateStore.Read<UpdateResult>(_paths.ResultFile(sessionId));
        WriteResult(
            request,
            false,
            "recovered",
            existingResult?.Message ?? "Неподтверждённый candidate отменён; восстановлен LKG/bootstrap mode.",
            existingResult?.FallbackError);
        WritePhase(pending, "fallback-ready");
        return Task.CompletedTask;
    }

    private UpdateRequest ReadRequest(string sessionId) =>
        JsonStateStore.Read<UpdateRequest>(_paths.RequestFile(sessionId))
        ?? throw new InvalidOperationException("Update request отсутствует.");

    private static Uri ValidateRequest(UpdateRequest request)
    {
        if (request.SchemaVersion != DesktopUpdateConstants.ProtocolVersion
            || !Guid.TryParseExact(request.SessionId, "N", out _)
            || request.StartupToken.Length != 64
            || request.Current == request.Target
            || !string.Equals(
                Path.GetFullPath(request.RepositoryRoot).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(DesktopUpdateConstants.DefaultRepositoryRoot).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Update request не прошёл локальную validation.");
        }

        return ResolveUpdateServerBaseUri(request);
    }

    public static Uri ResolveUpdateServerBaseUri(UpdateRequest request) =>
        DesktopUpdateEndpointResolver.Validate(request.UpdateServerBaseUrl);

    private async Task PublishAndValidateAsync(
        string source,
        string staging,
        BuildIdentity target,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(staging);
        var appDirectory = Path.Combine(staging, "app");
        var updaterDirectory = Path.Combine(staging, "updater");
        await PublishAsync(
            source,
            Path.Combine(source, "apps", "windows", "FlowStock.App", "FlowStock.App.csproj"),
            appDirectory,
            target.SourceCommit,
            cancellationToken).ConfigureAwait(false);
        await PublishAsync(
            source,
            Path.Combine(source, "apps", "windows", "FlowStock.Updater", "FlowStock.Updater.csproj"),
            updaterDirectory,
            target.SourceCommit,
            cancellationToken).ConfigureAwait(false);

        if (BuildIdentity.FromFile(Path.Combine(appDirectory, "FlowStock.App.exe")) != target
            || BuildIdentity.FromFile(Path.Combine(updaterDirectory, "FlowStock.Updater.exe")) != target)
        {
            throw new InvalidOperationException("Published binaries не совпадают с target BuildIdentity.");
        }

        JsonStateStore.WriteAtomic(
            Path.Combine(staging, "install-manifest.json"),
            RuntimeStateManager.ToManifest(target));
    }

    private async Task PublishAsync(
        string source,
        string project,
        string output,
        string commit,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            "dotnet",
            ["publish", project, "-c", "Release", "-o", output, $"/p:SourceRevisionId={commit}"],
            source,
            TimeSpan.FromMinutes(15),
            cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"dotnet publish failed: {result.StandardError.Trim()}");
        }
    }

    private void InstallStaging(string staging, BuildIdentity target)
    {
        var versionRoot = _paths.VersionRoot(target.SourceCommit);
        if (Directory.Exists(versionRoot))
        {
            var manifest = JsonStateStore.Read<RuntimeManifest>(_paths.InstallManifest(target.SourceCommit));
            if (manifest?.GetIdentity() != target || !IsInstalledRuntimeValid(target))
            {
                throw new InvalidOperationException("Существующий target runtime повреждён.");
            }

            return;
        }

        Directory.Move(staging, versionRoot);
    }

    private async Task ValidateToolingAsync(UpdateRequest request, CancellationToken cancellationToken)
    {
        EnsureDiskSpace(_paths.Root, 2L * 1024 * 1024 * 1024);
        await _git.ValidateRepositoryAndRemoteAsync(request.RepositoryRoot, cancellationToken).ConfigureAwait(false);
        var sdks = await _runner.RunAsync(
            "dotnet",
            ["--list-sdks"],
            request.RepositoryRoot,
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        if (!sdks.Success || !sdks.StandardOutput.Split('\n').Any(line => line.TrimStart().StartsWith("8.", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Совместимый .NET 8 SDK не найден.");
        }
    }

    private bool IsInstalledRuntimeValid(BuildIdentity identity)
    {
        try
        {
            var manifest = JsonStateStore.Read<RuntimeManifest>(_paths.InstallManifest(identity.SourceCommit));
            return manifest?.GetIdentity() == identity
                   && File.Exists(_paths.AppExecutable(identity.SourceCommit))
                   && File.Exists(_paths.UpdaterExecutable(identity.SourceCommit))
                   && BuildIdentity.FromFile(_paths.AppExecutable(identity.SourceCommit)) == identity
                   && BuildIdentity.FromFile(_paths.UpdaterExecutable(identity.SourceCommit)) == identity;
        }
        catch
        {
            return false;
        }
    }

    private async Task WaitForStartupAckAsync(
        UpdateRequest request,
        Process process,
        CancellationToken cancellationToken)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < _timing.StartupAckTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException("Новый FlowStock завершился до startup handshake.");
            }

            var ack = JsonStateStore.Read<UpdateStartupAck>(_paths.StartupAckFile(request.SessionId));
            if (ack is not null)
            {
                if (ack.SessionId != request.SessionId
                    || ack.StartupToken != request.StartupToken
                    || ack.Actual != request.Target)
                {
                    throw new InvalidOperationException("Startup ACK не соответствует update transaction.");
                }

                return;
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Startup handshake не получен за {_timing.StartupAckTimeout.TotalSeconds:0} секунд.");
    }

    private static async Task WaitForWpfExitAsync(int processId, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // Process already exited between request creation and updater startup.
        }

        using var probe = new Mutex(false, DesktopUpdateConstants.AppMutexName);
        if (!probe.WaitOne(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("WPF mutex не освободился после завершения процесса.");
        }

        probe.ReleaseMutex();
    }

    private static void EnsureDiskSpace(string path, long requiredBytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path))
            ?? throw new InvalidOperationException("Не удалось определить диск runtime root.");
        if (new DriveInfo(root).AvailableFreeSpace < requiredBytes)
        {
            throw new InvalidOperationException("Недостаточно свободного места для side-by-side update.");
        }
    }

    private void RollBackActive(RuntimeManifest? previous, bool sourceRunFallback)
    {
        if (previous is not null)
        {
            _state.WriteActive(previous.GetIdentity());
        }
        else if (sourceRunFallback)
        {
            _state.ClearActive();
        }
    }

    private void LaunchPrevious(RuntimeManifest? previous, UpdateRequest request)
    {
        var resultArguments = new[] { "--update-session", request.SessionId };
        if (previous is not null)
        {
            var executable = _paths.AppExecutable(previous.Commit);
            if (!File.Exists(executable))
            {
                throw new FileNotFoundException("Previous LKG executable не найден.", executable);
            }

            _runtimeLauncher.StartExecutable(executable, resultArguments, _paths.AppDirectory(previous.Commit)).Dispose();
            return;
        }

        if (request.SourceRunFallback)
        {
            _runtimeLauncher.StartSourceRun(request.RepositoryRoot, resultArguments).Dispose();
            return;
        }

        throw new InvalidOperationException("Previous runtime или source-run fallback отсутствует.");
    }

    private Exception HandleFailedUpdate(
        UpdateRequest request,
        PendingUpdateTransaction pending,
        RuntimeManifest? previousActive,
        Process? unverifiedCandidate,
        Exception updateFailure)
    {
        try
        {
            StopUnverifiedCandidate(unverifiedCandidate);
            RollBackActive(previousActive, request.SourceRunFallback);
            LaunchPrevious(previousActive, request);
            CompleteTerminal(request, false, pending.Phase, updateFailure.Message);
            return updateFailure;
        }
        catch (Exception fallbackFailure)
        {
            _log($"Fallback launch failed: {fallbackFailure}");
            WritePhase(pending, "fallback-failed");
            WriteResult(request, false, pending.Phase, updateFailure.Message, fallbackFailure.Message);
            return new AggregateException(
                "Обновление завершилось ошибкой, и fallback runtime не удалось запустить.",
                updateFailure,
                fallbackFailure);
        }
    }

    private static void StopUnverifiedCandidate(Process? candidate)
    {
        if (candidate is null || candidate.HasExited)
        {
            return;
        }

        candidate.CloseMainWindow();
        if (!candidate.WaitForExit(2000))
        {
            candidate.Kill(entireProcessTree: true);
            if (!candidate.WaitForExit(5000))
            {
                throw new InvalidOperationException("Неподтверждённый candidate process не удалось завершить перед fallback.");
            }
        }
    }

    private void CompleteTerminal(UpdateRequest request, bool success, string stage, string message)
    {
        WriteResult(request, success, stage, message);
        if (File.Exists(_paths.PendingTransaction))
        {
            File.Delete(_paths.PendingTransaction);
        }
    }

    private void WriteResult(
        UpdateRequest request,
        bool success,
        string stage,
        string message,
        string? fallbackError = null)
    {
        JsonStateStore.WriteAtomic(
            _paths.ResultFile(request.SessionId),
            new UpdateResult(
                DesktopUpdateConstants.ProtocolVersion,
                request.SessionId,
                success,
                request.Current,
                request.Target,
                stage,
                message,
                fallbackError,
                _paths.LogFile(request.SessionId),
                DateTimeOffset.UtcNow));
    }

    private PendingUpdateTransaction WritePhase(PendingUpdateTransaction pending, string phase)
    {
        pending = pending with { Phase = phase };
        JsonStateStore.WriteAtomic(_paths.PendingTransaction, pending);
        return pending;
    }
}
