using FlowStock.App;
using FlowStock.DesktopUpdate;
using FlowStock.Updater;

namespace FlowStock.Server.Tests.DesktopUpdate;

public sealed class UpdateStartupLifecycleTests
{
    [Fact]
    public async Task DbConnectionWindowReady_WritesAckAndConsumesResultOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), $"flowstock-db-startup-test-{Guid.NewGuid():N}");
        var paths = new DesktopUpdatePaths(root, root);
        paths.EnsureBaseDirectories();
        var session = Guid.NewGuid().ToString("N");
        var token = new string('a', 64);
        var actual = BuildIdentity.Create("1.1.0", "2222222222222222222222222222222222222222");
        var previous = BuildIdentity.Create("1.0.0", "1111111111111111111111111111111111111111");
        var result = new UpdateResult(
            1,
            session,
            true,
            previous,
            actual,
            "завершено",
            "ok",
            null,
            paths.LogFile(session),
            DateTimeOffset.UtcNow);
        JsonStateStore.WriteAtomic(paths.ResultFile(session), result);
        var shown = new List<UpdateResult>();
        try
        {
            await UpdateStartupLifecycle.HandleReadyAsync(
                ["--update-session", session, "--startup-token", token],
                actual,
                paths,
                value =>
                {
                    shown.Add(value);
                    return Task.CompletedTask;
                },
                TimeSpan.Zero,
                1);

            var ack = JsonStateStore.Read<UpdateStartupAck>(paths.StartupAckFile(session));
            Assert.Equal(actual, ack?.Actual);
            Assert.Equal(token, ack?.StartupToken);
            Assert.Equal([result], shown);
            Assert.False(File.Exists(paths.ResultFile(session)));

            await UpdateStartupLifecycle.HandleReadyAsync(
                ["--update-session", session, "--startup-token", token],
                actual,
                paths,
                value =>
                {
                    shown.Add(value);
                    return Task.CompletedTask;
                },
                TimeSpan.Zero,
                1);
            Assert.Single(shown);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void SuccessfulRecovery_ClosesUpdaterAutomatically()
    {
        Assert.True(UpdaterWindow.ShouldCloseAutomaticallyAfterSuccess(recovery: true));
        Assert.False(UpdaterWindow.ShouldCloseAutomaticallyAfterSuccess(recovery: false));
    }

    [Fact]
    public void RecoveryException_ProducesNonzeroProcessOutcomeUntilRecoverySucceeds()
    {
        var recovery = new UpdaterProcessOutcome(recovery: true);
        recovery.MarkFailure();

        Assert.Equal(UpdaterProcessOutcome.RecoveryFailureExitCode, recovery.ExitCode);
        Assert.NotEqual(0, recovery.ExitCode);

        recovery.MarkSuccess();
        Assert.Equal(0, recovery.ExitCode);

        var normalUpdate = new UpdaterProcessOutcome(recovery: false);
        normalUpdate.MarkFailure();
        Assert.Equal(0, normalUpdate.ExitCode);
    }
}
