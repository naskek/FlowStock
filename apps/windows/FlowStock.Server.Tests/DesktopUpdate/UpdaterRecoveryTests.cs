using System.Diagnostics;
using FlowStock.App;
using FlowStock.DesktopUpdate;

namespace FlowStock.Server.Tests.DesktopUpdate;

public sealed class UpdaterRecoveryTests
{
    [Fact]
    public async Task ValidStartupAck_CompletesInterruptedSwitchAsSuccess()
    {
        using var fixture = RecoveryFixture.Create(writeAck: true, corruptUpdater: false);
        var ackBefore = File.ReadAllBytes(fixture.Paths.StartupAckFile(fixture.SessionId));
        var shimOutput = Path.Combine(fixture.Root, "success-ready-invocation.json");

        await fixture.Engine.RecoverAsync(fixture.SessionId, new Progress<UpdateProgress>());

        Assert.Equal(fixture.Target, fixture.State.ReadActive()?.GetIdentity());
        Assert.Equal(fixture.Target, fixture.State.ReadLastKnownGood()?.GetIdentity());
        Assert.True(JsonStateStore.Read<UpdateResult>(fixture.Paths.ResultFile(fixture.SessionId))?.Success);
        Assert.Equal(
            "success-ready",
            JsonStateStore.Read<PendingUpdateTransaction>(fixture.Paths.PendingTransaction)?.Phase);

        var launcherResult = LauncherTestHarness.RunLauncher(
            fixture.Root,
            DesktopUpdateConstants.DefaultRepositoryRoot,
            LauncherTestHarness.ShimExecutable,
            shimOutput);

        Assert.Equal(0, launcherResult.ExitCode);
        var invocation = LauncherTestHarness.WaitForInvocation(shimOutput);
        Assert.Equal(new[] { "--update-session", fixture.SessionId }, invocation.Arguments);
        Assert.Equal(
            Path.GetFullPath(fixture.Paths.AppDirectory(fixture.Target.SourceCommit)),
            Path.GetFullPath(invocation.WorkingDirectory));
        Assert.False(File.Exists(fixture.Paths.PendingTransaction));

        var shown = new List<UpdateResult>();
        await UpdateStartupLifecycle.HandleReadyAsync(
            invocation.Arguments,
            fixture.Target,
            fixture.Paths,
            result =>
            {
                shown.Add(result);
                return Task.CompletedTask;
            },
            TimeSpan.Zero,
            1);

        Assert.Single(shown);
        Assert.True(shown[0].Success);
        Assert.False(File.Exists(fixture.Paths.ResultFile(fixture.SessionId)));
        Assert.Equal(ackBefore, File.ReadAllBytes(fixture.Paths.StartupAckFile(fixture.SessionId)));
    }

    [Fact]
    public async Task MissingStartupAck_ReturnsFirstInstallToSourceRunFallback()
    {
        using var fixture = RecoveryFixture.Create(writeAck: false, corruptUpdater: false);

        await fixture.Engine.RecoverAsync(fixture.SessionId, new Progress<UpdateProgress>());

        Assert.False(File.Exists(fixture.Paths.ActiveManifest));
        Assert.False(JsonStateStore.Read<UpdateResult>(fixture.Paths.ResultFile(fixture.SessionId))?.Success);
        Assert.Equal(
            "fallback-ready",
            JsonStateStore.Read<PendingUpdateTransaction>(fixture.Paths.PendingTransaction)?.Phase);
    }

    [Fact]
    public async Task CorruptedCandidateUpdater_InvalidatesAckAndReturnsToSourceRun()
    {
        using var fixture = RecoveryFixture.Create(writeAck: true, corruptUpdater: true);

        await fixture.Engine.RecoverAsync(fixture.SessionId, new Progress<UpdateProgress>());

        Assert.False(File.Exists(fixture.Paths.ActiveManifest));
        Assert.False(JsonStateStore.Read<UpdateResult>(fixture.Paths.ResultFile(fixture.SessionId))?.Success);
        Assert.Equal(
            "fallback-ready",
            JsonStateStore.Read<PendingUpdateTransaction>(fixture.Paths.PendingTransaction)?.Phase);
    }

    private sealed class RecoveryFixture : IDisposable
    {
        private RecoveryFixture(
            string root,
            string sessionId,
            DesktopUpdatePaths paths,
            RuntimeStateManager state,
            BuildIdentity target,
            UpdaterEngine engine)
        {
            Root = root;
            SessionId = sessionId;
            Paths = paths;
            State = state;
            Target = target;
            Engine = engine;
        }

        public string Root { get; }
        public string SessionId { get; }
        public DesktopUpdatePaths Paths { get; }
        public RuntimeStateManager State { get; }
        public BuildIdentity Target { get; }
        public UpdaterEngine Engine { get; }

        public static RecoveryFixture Create(bool writeAck, bool corruptUpdater)
        {
            var root = Path.Combine(Path.GetTempPath(), $"flowstock-recovery-test-{Guid.NewGuid():N}");
            var paths = new DesktopUpdatePaths(root, root);
            paths.EnsureBaseDirectories();
            var shimSource = LauncherTestHarness.ShimExecutable;
            var target = BuildIdentity.FromFile(shimSource);

            LauncherTestHarness.CopyShimRuntime(paths.AppDirectory(target.SourceCommit), "FlowStock.App.exe");
            LauncherTestHarness.CopyShimRuntime(paths.UpdaterDirectory(target.SourceCommit), "FlowStock.Updater.exe");
            JsonStateStore.WriteAtomic(paths.InstallManifest(target.SourceCommit), RuntimeStateManager.ToManifest(target));
            if (corruptUpdater)
            {
                File.WriteAllBytes(paths.UpdaterExecutable(target.SourceCommit), [1, 2, 3, 4]);
            }

            var state = new RuntimeStateManager(paths);
            state.WriteActive(target);
            var sessionId = Guid.NewGuid().ToString("N");
            var token = new string('a', 64);
            var current = BuildIdentity.Create(
                "0.9.0",
                "1111111111111111111111111111111111111111");
            var request = new UpdateRequest(
                1,
                sessionId,
                token,
                "https://flowstock.example/",
                DesktopUpdateConstants.DefaultRepositoryRoot,
                current,
                target,
                1,
                true);
            JsonStateStore.WriteAtomic(paths.RequestFile(sessionId), request);
            JsonStateStore.WriteAtomic(
                paths.PendingTransaction,
                new PendingUpdateTransaction(
                    1,
                    sessionId,
                    token,
                    "active-switched",
                    current,
                    target,
                    null,
                    target.SourceCommit,
                    true,
                    new string('b', 64),
                    DateTimeOffset.UtcNow,
                    $"{sessionId}.log"));
            if (writeAck)
            {
                JsonStateStore.WriteAtomic(
                    paths.StartupAckFile(sessionId),
                    new UpdateStartupAck(1, sessionId, token, target, DateTimeOffset.UtcNow));
            }

            var runner = new RejectingRunner();
            var engine = new UpdaterEngine(
                paths,
                state,
                runner,
                new GitRepositoryClient(runner),
                new HttpClient(),
                _ => { },
                new RejectingRuntimeLauncher());
            return new RecoveryFixture(root, sessionId, paths, state, target, engine);
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

    private sealed class RejectingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            string? workingDirectory,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("Recovery must not invoke an external process in this scenario.");
    }

    private sealed class RejectingRuntimeLauncher : IRuntimeLauncher
    {
        public Process StartExecutable(string executable, IEnumerable<string> arguments, string workingDirectory) =>
            throw new Xunit.Sdk.XunitException("Recovery updater must not launch a runtime itself.");

        public Process StartSourceRun(string repositoryRoot, IEnumerable<string> appArguments) =>
            throw new Xunit.Sdk.XunitException("Recovery updater must not launch source-run itself.");
    }
}
