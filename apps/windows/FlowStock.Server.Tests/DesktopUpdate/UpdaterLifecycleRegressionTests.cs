using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using FlowStock.DesktopUpdate;

namespace FlowStock.Server.Tests.DesktopUpdate;

public sealed class UpdaterLifecycleRegressionTests
{
    [Fact]
    public async Task FirstSourceRunFailureBeforeSwitch_RestartsSourceRunBeforeRemovingPending()
    {
        var repositoryBefore = RepositorySnapshot.Capture(DesktopUpdateConstants.DefaultRepositoryRoot);
        using var fixture = LifecycleFixture.Create(sourceRun: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Engine.RunUpdateAsync(fixture.SessionId, new Progress<UpdateProgress>(), CancellationToken.None));

        Assert.Equal(1, fixture.Launcher.SourceRunStarts);
        Assert.False(File.Exists(fixture.Paths.PendingTransaction));
        Assert.Equal(repositoryBefore, RepositorySnapshot.Capture(DesktopUpdateConstants.DefaultRepositoryRoot));
    }

    [Fact]
    public async Task FirstSourceRunCancellation_RestartsSourceRunBeforeRemovingPending()
    {
        using var fixture = LifecycleFixture.Create(sourceRun: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Engine.RunUpdateAsync(fixture.SessionId, new Progress<UpdateProgress>(), cancellation.Token));

        Assert.Equal(1, fixture.Launcher.SourceRunStarts);
        Assert.False(File.Exists(fixture.Paths.PendingTransaction));
    }

    [Fact]
    public async Task FirstSourceRunRecoveryAfterSwitch_RestartsSourceRunAndClearsCandidate()
    {
        var repositoryBefore = RepositorySnapshot.Capture(DesktopUpdateConstants.DefaultRepositoryRoot);
        using var fixture = LifecycleFixture.Create(sourceRun: true, pendingAfterSwitch: true);
        var shimOutput = Path.Combine(fixture.Root, "source-run-invocation.json");

        await fixture.Engine.RecoverAsync(fixture.SessionId, new Progress<UpdateProgress>());

        Assert.False(File.Exists(fixture.Paths.ActiveManifest));
        Assert.Equal("fallback-ready", JsonStateStore.Read<PendingUpdateTransaction>(fixture.Paths.PendingTransaction)?.Phase);

        var launcherResult = LauncherTestHarness.RunLauncher(
            fixture.Root,
            DesktopUpdateConstants.DefaultRepositoryRoot,
            LauncherTestHarness.ShimExecutable,
            shimOutput);

        Assert.True(
            launcherResult.ExitCode == 0,
            $"Launcher exit={launcherResult.ExitCode}\nstdout={launcherResult.StandardOutput}\nstderr={launcherResult.StandardError}");
        var invocation = LauncherTestHarness.WaitForInvocation(shimOutput);
        Assert.Equal(Path.GetFullPath(DesktopUpdateConstants.DefaultRepositoryRoot), Path.GetFullPath(invocation.WorkingDirectory));
        Assert.Equal(
            new[]
            {
                "run",
                "--project",
                Path.Combine(
                    DesktopUpdateConstants.DefaultRepositoryRoot,
                    "apps",
                    "windows",
                    "FlowStock.App",
                    "FlowStock.App.csproj"),
                "--",
                "--update-session",
                fixture.SessionId
            },
            invocation.Arguments);
        Assert.False(File.Exists(fixture.Paths.PendingTransaction));
        Assert.Equal(repositoryBefore, RepositorySnapshot.Capture(DesktopUpdateConstants.DefaultRepositoryRoot));
    }

    [Fact]
    public async Task SideBySideRecoveryAfterFailure_StartsPreviousLkg()
    {
        using var fixture = LifecycleFixture.Create(sourceRun: false, pendingAfterSwitch: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Engine.RunUpdateAsync(fixture.SessionId, new Progress<UpdateProgress>(), CancellationToken.None));

        Assert.Equal(fixture.Previous, fixture.State.ReadActive()?.GetIdentity());
        Assert.Equal(1, fixture.Launcher.ExecutableStarts);
        Assert.Equal(fixture.Paths.AppExecutable(fixture.Previous.SourceCommit), fixture.Launcher.LastExecutable);
    }

    [Fact]
    public async Task FallbackLaunchFailure_PreservesPendingAndRecordsBothErrors()
    {
        using var fixture = LifecycleFixture.Create(sourceRun: true, failFallbackLaunch: true);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            fixture.Engine.RunUpdateAsync(fixture.SessionId, new Progress<UpdateProgress>(), CancellationToken.None));

        Assert.True(File.Exists(fixture.Paths.PendingTransaction));
        var result = JsonStateStore.Read<UpdateResult>(fixture.Paths.ResultFile(fixture.SessionId));
        Assert.NotNull(result);
        Assert.Contains("Production target", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fallback launch failure", result.FallbackError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecoveryLauncherFallbackFailure_KeepsFallbackReadyPendingForRetry()
    {
        using var fixture = LifecycleFixture.Create(sourceRun: true, pendingAfterSwitch: true);
        await fixture.Engine.RecoverAsync(fixture.SessionId, new Progress<UpdateProgress>());

        var launcherResult = LauncherTestHarness.RunLauncher(
            fixture.Root,
            DesktopUpdateConstants.DefaultRepositoryRoot,
            Path.Combine(fixture.Root, "missing-dotnet.exe"));

        Assert.Equal(1, launcherResult.ExitCode);
        Assert.Equal(
            "fallback-ready",
            JsonStateStore.Read<PendingUpdateTransaction>(fixture.Paths.PendingTransaction)?.Phase);
        Assert.Contains("pending transaction retained", launcherResult.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecoveryProcessFailure_ReturnsNonzeroAndLauncherKeepsPending()
    {
        using var fixture = LifecycleFixture.Create(sourceRun: true, pendingAfterSwitch: true);
        LauncherTestHarness.CopyShimRuntime(fixture.Paths.RecoveryDirectory(fixture.SessionId), "FlowStock.Updater.exe");
        var pending = JsonStateStore.Read<PendingUpdateTransaction>(fixture.Paths.PendingTransaction)! with
        {
            RecoveryBundleSha256 = RuntimeStateManager.Sha256Bundle(fixture.Paths.RecoveryDirectory(fixture.SessionId))
        };
        JsonStateStore.WriteAtomic(fixture.Paths.PendingTransaction, pending);
        var shimOutput = Path.Combine(fixture.Root, "failed-recovery-invocation.json");

        var launcherResult = LauncherTestHarness.RunLauncher(
            fixture.Root,
            DesktopUpdateConstants.DefaultRepositoryRoot,
            LauncherTestHarness.ShimExecutable,
            shimOutput,
            shimExitCode: 17);

        Assert.Equal(1, launcherResult.ExitCode);
        Assert.Equal(
            "active-switched",
            JsonStateStore.Read<PendingUpdateTransaction>(fixture.Paths.PendingTransaction)?.Phase);
        var invocation = LauncherTestHarness.WaitForInvocation(shimOutput);
        Assert.Equal(new[] { "--recover", fixture.SessionId }, invocation.Arguments);
        Assert.Contains("exited with code 17", launcherResult.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class LifecycleFixture : IDisposable
    {
        private LifecycleFixture(
            string root,
            string sessionId,
            DesktopUpdatePaths paths,
            RuntimeStateManager state,
            BuildIdentity previous,
            RecordingRuntimeLauncher launcher,
            UpdaterEngine engine)
        {
            Root = root;
            SessionId = sessionId;
            Paths = paths;
            State = state;
            Previous = previous;
            Launcher = launcher;
            Engine = engine;
        }

        public string Root { get; }
        public string SessionId { get; }
        public DesktopUpdatePaths Paths { get; }
        public RuntimeStateManager State { get; }
        public BuildIdentity Previous { get; }
        public RecordingRuntimeLauncher Launcher { get; }
        public UpdaterEngine Engine { get; }

        public static LifecycleFixture Create(
            bool sourceRun,
            bool pendingAfterSwitch = false,
            bool failFallbackLaunch = false)
        {
            var root = Path.Combine(Path.GetTempPath(), $"flowstock-lifecycle-test-{Guid.NewGuid():N}");
            var paths = new DesktopUpdatePaths(root, root);
            paths.EnsureBaseDirectories();
            var appSource = Path.Combine(AppContext.BaseDirectory, "FlowStock.App.exe");
            var updaterSource = Path.Combine(AppContext.BaseDirectory, "FlowStock.Updater.exe");
            var binaryIdentity = BuildIdentity.FromFile(appSource);
            var previous = binaryIdentity;
            var target = BuildIdentity.Create("1.1.0", "2222222222222222222222222222222222222222");
            var state = new RuntimeStateManager(paths);

            if (!sourceRun)
            {
                InstallRuntime(paths, previous, appSource, updaterSource);
                state.WriteActive(previous);
                state.WriteLastKnownGood(previous);
            }

            var sessionId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(paths.RecoveryDirectory(sessionId));
            File.Copy(updaterSource, paths.RecoveryExecutable(sessionId));
            var request = new UpdateRequest(
                1,
                sessionId,
                new string('a', 64),
                "https://flowstock.example/",
                DesktopUpdateConstants.DefaultRepositoryRoot,
                previous,
                target,
                int.MaxValue,
                sourceRun);
            JsonStateStore.WriteAtomic(paths.RequestFile(sessionId), request);

            if (pendingAfterSwitch)
            {
                JsonStateStore.WriteAtomic(
                    paths.PendingTransaction,
                    new PendingUpdateTransaction(
                        1,
                        sessionId,
                        request.StartupToken,
                        "active-switched",
                        previous,
                        target,
                        sourceRun ? null : previous.SourceCommit,
                        target.SourceCommit,
                        sourceRun,
                        RuntimeStateManager.Sha256Bundle(paths.RecoveryDirectory(sessionId)),
                        DateTimeOffset.UtcNow,
                        $"{sessionId}.log"));
                if (sourceRun)
                {
                    JsonStateStore.WriteAtomic(paths.ActiveManifest, RuntimeStateManager.ToManifest(target));
                }
            }

            var runner = new ToolingRunner();
            var launcher = new RecordingRuntimeLauncher(failFallbackLaunch);
            var engine = new UpdaterEngine(
                paths,
                state,
                runner,
                new GitRepositoryClient(runner),
                new HttpClient(new FailureManifestHandler()),
                _ => { },
                launcher);
            return new LifecycleFixture(root, sessionId, paths, state, previous, launcher, engine);
        }

        private static void InstallRuntime(
            DesktopUpdatePaths paths,
            BuildIdentity identity,
            string appSource,
            string updaterSource)
        {
            Directory.CreateDirectory(paths.AppDirectory(identity.SourceCommit));
            Directory.CreateDirectory(paths.UpdaterDirectory(identity.SourceCommit));
            File.Copy(appSource, paths.AppExecutable(identity.SourceCommit));
            File.Copy(updaterSource, paths.UpdaterExecutable(identity.SourceCommit));
            JsonStateStore.WriteAtomic(paths.InstallManifest(identity.SourceCommit), RuntimeStateManager.ToManifest(identity));
        }

        public void Dispose()
        {
            if (!Directory.Exists(Root))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class RecordingRuntimeLauncher(bool fail) : IRuntimeLauncher
    {
        public int SourceRunStarts { get; private set; }
        public int ExecutableStarts { get; private set; }
        public string? LastExecutable { get; private set; }

        public Process StartExecutable(string executable, IEnumerable<string> arguments, string workingDirectory)
        {
            ExecutableStarts++;
            LastExecutable = executable;
            if (fail)
            {
                throw new InvalidOperationException("fallback launch failure");
            }

            return Process.GetCurrentProcess();
        }

        public Process StartSourceRun(string repositoryRoot, IEnumerable<string> appArguments)
        {
            SourceRunStarts++;
            if (fail)
            {
                throw new InvalidOperationException("fallback launch failure");
            }

            return Process.GetCurrentProcess();
        }
    }

    private sealed class ToolingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            string? workingDirectory,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var args = arguments.ToArray();
            if (fileName == "dotnet")
            {
                return Task.FromResult(new ProcessResult(0, "8.0.424 [test]", string.Empty));
            }

            if (args.Contains("--show-toplevel"))
            {
                return Task.FromResult(new ProcessResult(0, DesktopUpdateConstants.DefaultRepositoryRoot, string.Empty));
            }

            if (args.Contains("get-url"))
            {
                return Task.FromResult(new ProcessResult(0, DesktopUpdateConstants.RepositoryUrl, string.Empty));
            }

            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class FailureManifestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                ReasonPhrase = "production manifest failure"
            });
        }
    }

    private sealed record RepositorySnapshot(
        string Head,
        string Branch,
        string StatusHash,
        string IndexDiffHash,
        string WorkingDiffHash,
        string UntrackedContentHash)
    {
        public static RepositorySnapshot Capture(string repositoryRoot)
        {
            var untracked = RunGitBytes(repositoryRoot, "ls-files", "--others", "--exclude-standard", "-z");
            using var untrackedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var relative in Encoding.UTF8.GetString(untracked)
                         .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                         .Order(StringComparer.Ordinal))
            {
                untrackedHash.AppendData(Encoding.UTF8.GetBytes(relative));
                untrackedHash.AppendData(File.ReadAllBytes(Path.Combine(repositoryRoot, relative)));
            }

            return new RepositorySnapshot(
                Encoding.UTF8.GetString(RunGitBytes(repositoryRoot, "rev-parse", "HEAD")),
                Encoding.UTF8.GetString(RunGitBytes(repositoryRoot, "rev-parse", "--abbrev-ref", "HEAD")),
                Hash(RunGitBytes(repositoryRoot, "status", "--porcelain=v1", "-z")),
                Hash(RunGitBytes(repositoryRoot, "diff", "--cached", "--binary")),
                Hash(RunGitBytes(repositoryRoot, "diff", "--binary")),
                Convert.ToHexString(untrackedHash.GetHashAndReset()));
        }

        private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));

        private static byte[] RunGitBytes(string repositoryRoot, params string[] arguments)
        {
            var info = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repositoryRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.ArgumentList.Add("-C");
            info.ArgumentList.Add(repositoryRoot);
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info)!;
            using var output = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(output);
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
            return output.ToArray();
        }
    }
}
