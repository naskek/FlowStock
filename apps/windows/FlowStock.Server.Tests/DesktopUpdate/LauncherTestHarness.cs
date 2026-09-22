using System.Diagnostics;
using System.Text.Json;
using FlowStock.DesktopUpdate;

namespace FlowStock.Server.Tests.DesktopUpdate;

internal sealed record ProcessShimInvocation(string WorkingDirectory, string[] Arguments, int ProcessId);

internal static class LauncherTestHarness
{
    public static string ShimExecutable => Path.Combine(AppContext.BaseDirectory, "FlowStock.ProcessTestShim.exe");

    public static ProcessResult RunLauncher(
        string localAppData,
        string repositoryRoot,
        string dotnetExecutable,
        string? shimOutput = null,
        int shimExitCode = 0)
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

    public static ProcessShimInvocation WaitForInvocation(string path)
    {
        var timeoutLimit = TimeSpan.FromSeconds(10);
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < timeoutLimit)
        {
            try
            {
                if (File.Exists(path))
                {
                    var invocation = JsonSerializer.Deserialize<ProcessShimInvocation>(File.ReadAllText(path))
                                     ?? throw new InvalidOperationException("Process shim output пуст.");
                    WaitForProcessExit(invocation.ProcessId, timeoutLimit - timeout.Elapsed);
                    return invocation;
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                // Child process may still be replacing the output file.
            }

            Thread.Sleep(50);
        }

        throw new TimeoutException($"Process shim не записал invocation: {path}");
    }

    private static void WaitForProcessExit(int processId, TimeSpan timeout)
    {
        if (processId <= 0)
        {
            throw new InvalidOperationException($"Process shim вернул некорректный PID: {processId}");
        }

        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            // The shim may have already exited between writing the invocation file and this lookup.
            return;
        }

        using (process)
        {
            var timeoutMilliseconds = Math.Max(0, (int)Math.Ceiling(timeout.TotalMilliseconds));
            if (!process.WaitForExit(timeoutMilliseconds))
            {
                throw new TimeoutException($"Process shim PID {processId} не завершился за отведённое время.");
            }
        }
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
