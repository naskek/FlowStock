using FlowStock.DesktopUpdate;
using System.IO;

namespace FlowStock.App;

public static class UpdateStartupLifecycle
{
    public static async Task HandleReadyAsync(
        IReadOnlyList<string> args,
        BuildIdentity actual,
        DesktopUpdatePaths paths,
        Func<UpdateResult, Task> showResult,
        TimeSpan pollInterval,
        int pollAttempts,
        CancellationToken cancellationToken = default)
    {
        var session = ReadArgument(args, "--update-session");
        if (session is null)
        {
            return;
        }

        var token = ReadArgument(args, "--startup-token");
        if (token is not null)
        {
            JsonStateStore.WriteAtomic(
                paths.StartupAckFile(session),
                new UpdateStartupAck(
                    DesktopUpdateConstants.ProtocolVersion,
                    session,
                    token,
                    actual,
                    DateTimeOffset.UtcNow));
        }

        for (var attempt = 0; attempt < pollAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resultPath = paths.ResultFile(session);
            var result = JsonStateStore.Read<UpdateResult>(resultPath);
            if (result is not null)
            {
                await showResult(result).ConfigureAwait(true);
                File.Delete(resultPath);
                return;
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(true);
        }
    }

    public static string? ReadArgument(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (args[index] == name)
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
