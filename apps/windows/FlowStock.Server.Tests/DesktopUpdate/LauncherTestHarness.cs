using System.Diagnostics;
using System.Text.Json;
using FlowStock.DesktopUpdate;

namespace FlowStock.Server.Tests.DesktopUpdate;

internal sealed record ProcessShimInvocation(
    string WorkingDirectory,
    string[] Arguments,
    int ProcessId,
    string? ExitSignalPath);

internal static class LauncherTestHarness
{
    public static string ShimExecutable => Path.Combine(AppContext.BaseDirectory, "FlowStock.ProcessTestShim.exe");

    public static ProcessResult RunLauncher(
        string localAppData,
        string repositoryRoot,
        string dotnetExecutable,
        string? shimOutput = null,
        int shimExitCode = 0,
        bool shimRunsDetached = true)
    {
        var script = Path.Combine(
            DesktopUpdateConstants.DefaultRepositoryRoot,
            "tools",
            "windows",
            "start-flowstock-wpf.ps1");
        var info = new ProcessStartInfo
        {
            FileName = "pwsh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        info.Environment["LOCALAPPDATA"] = localAppData;
        info.Environment["APPDATA"] = localAppData;
        info.Environment["FLOWSTOCK_PROCESS_SHIM_EXIT_CODE"] = shimExitCode.ToString();
        if (shimOutput is not null)
        {
            info.Environment["FLOWSTOCK_PROCESS_SHIM_OUTPUT"] = shimOutput;
            if (shimRunsDetached)
            {
                info.Environment["FLOWSTOCK_PROCESS_SHIM_EXIT_SIGNAL"] = GetExitSignalPath(shimOutput);
            }
        }

        foreach (var argument in new[]
                 {
                     "-NoLogo", "-NoProfile", "-NonInteractive", "-File", script,
                     "-RepositoryRoot", repositoryRoot,
                     "-DotnetExecutable", dotnetExecutable
                 })
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        var emergencyLog = Path.Combine(localAppData, "FlowStock", "Logs", "Updates", "launcher-emergency.log");
        if (File.Exists(emergencyLog))
        {
            stderr += Environment.NewLine + File.ReadAllText(emergencyLog);
        }

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    public static ProcessShimInvocation WaitForInvocation(string path, TimeSpan? timeoutOverride = null)
    {
        var timeoutLimit = timeoutOverride ?? TimeSpan.FromSeconds(10);
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < timeoutLimit)
        {
            try
            {
                if (File.Exists(path))
                {
                    var invocation = JsonSerializer.Deserialize<ProcessShimInvocation>(File.ReadAllText(path))
                                     ?? throw new InvalidOperationException("Process shim output пуст.");
                    ReleaseShim(
                        invocation,
                        path,
                        timeoutLimit - timeout.Elapsed,
                        requireDetachedProcessHandle: true);
                    return invocation;
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                // Child process may still be replacing the output file.
            }

            Thread.Sleep(50);
        }

        SignalTimedOutShimExit(path);
        throw new TimeoutException($"Process shim не записал invocation: {path}");
    }

    private static void SignalShimExit(ProcessShimInvocation invocation, string invocationPath)
    {
        if (invocation.ExitSignalPath is null)
        {
            return;
        }

        var expectedExitSignalPath = Path.GetFullPath(GetExitSignalPath(invocationPath));
        if (!string.Equals(
                Path.GetFullPath(invocation.ExitSignalPath),
                expectedExitSignalPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Process shim вернул неожиданный exit signal path.");
        }

        File.WriteAllText(expectedExitSignalPath, string.Empty);
    }

    private static string GetExitSignalPath(string invocationPath) => $"{invocationPath}.exit-signal";

    private static void ReleaseShim(
        ProcessShimInvocation invocation,
        string invocationPath,
        TimeSpan timeout,
        bool requireDetachedProcessHandle)
    {
        if (invocation.ExitSignalPath is null)
        {
            WaitForProcessExit(invocation.ProcessId, timeout);
            return;
        }

        Process? process = null;
        try
        {
            process = GetProcess(invocation.ProcessId, requireDetachedProcessHandle);
            if (process is null)
            {
                return;
            }

            SignalShimExit(invocation, invocationPath);
            WaitForProcessExit(process, timeout);
        }
        catch
        {
            if (process is not null)
            {
                TerminateProcess(process);
            }

            throw;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static Process? GetProcess(int processId, bool requireRunning)
    {
        if (processId <= 0)
        {
            throw new InvalidOperationException($"Process shim вернул некорректный PID: {processId}");
        }

        try
        {
            return Process.GetProcessById(processId);
        }
        catch (ArgumentException exception) when (requireRunning)
        {
            throw new InvalidOperationException(
                $"Detached process shim PID {processId} завершился до установки lifecycle handshake.",
                exception);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static void WaitForProcessExit(int processId, TimeSpan timeout)
    {
        using var process = GetProcess(processId, requireRunning: false);
        if (process is null)
        {
            return;
        }

        WaitForProcessExit(process, timeout);
    }

    private static void WaitForProcessExit(Process process, TimeSpan timeout)
    {
        var timeoutMilliseconds = Math.Max(0, (int)Math.Ceiling(timeout.TotalMilliseconds));
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            TerminateProcess(process);
            throw new TimeoutException($"Process shim PID {process.Id} не завершился за отведённое время.");
        }
    }

    private static void SignalTimedOutShimExit(string invocationPath)
    {
        try
        {
            var cleanupTimeout = Stopwatch.StartNew();
            while (cleanupTimeout.Elapsed < TimeSpan.FromSeconds(1))
            {
                if (File.Exists(invocationPath))
                {
                    var invocation = JsonSerializer.Deserialize<ProcessShimInvocation>(File.ReadAllText(invocationPath));
                    if (invocation is not null)
                    {
                        try
                        {
                            ReleaseShim(
                                invocation,
                                invocationPath,
                                TimeSpan.FromSeconds(1),
                                requireDetachedProcessHandle: false);
                        }
                        catch (TimeoutException)
                        {
                            // ReleaseShim already terminated and reaped the shim.
                        }
                        catch (InvalidOperationException)
                        {
                            // The shim already exited before emergency cleanup acquired a handle.
                        }

                        return;
                    }
                }

                Thread.Sleep(25);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Preserve the original invocation timeout when emergency cleanup cannot complete.
        }
    }

    private static void TerminateProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            TerminateProcess(process);
        }
        catch (ArgumentException)
        {
            // The process already exited.
        }
    }

    private static void TerminateProcess(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the check and the kill request.
        }

        process.WaitForExit();
    }

    public static void CopyShimRuntime(string destination, string executableName)
    {
        Directory.CreateDirectory(destination);
        var sourceDirectory = Path.GetDirectoryName(ShimExecutable)!;
        foreach (var source in Directory.EnumerateFiles(sourceDirectory, "FlowStock.ProcessTestShim.*"))
        {
            File.Copy(source, Path.Combine(destination, Path.GetFileName(source)), overwrite: true);
        }

        File.Copy(ShimExecutable, Path.Combine(destination, executableName), overwrite: true);
    }
}
