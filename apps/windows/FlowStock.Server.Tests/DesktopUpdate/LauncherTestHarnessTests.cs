using System.Diagnostics;

namespace FlowStock.Server.Tests.DesktopUpdate;

public sealed class LauncherTestHarnessTests
{
    [Fact]
    public void WaitForInvocation_ReleasesShimAndWaitsForProcessExit()
    {
        var root = Path.Combine(Path.GetTempPath(), $"flowstock-launcher-harness-test-{Guid.NewGuid():N}");
        var output = Path.Combine(root, "invocation.json");
        var exitSignal = $"{output}.exit-signal";
        LauncherTestHarness.CopyShimRuntime(root, "FlowStock.App.exe");

        using var process = StartShim(Path.Combine(root, "FlowStock.App.exe"), output, exitSignal);
        try
        {
            var invocation = LauncherTestHarness.WaitForInvocation(output);

            Assert.Equal(process.Id, invocation.ProcessId);
            Assert.True(process.HasExited);
            Assert.True(File.Exists(exitSignal));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WaitForInvocation_WhenShimIgnoresExitSignal_KillsProcessBeforeReturningError()
    {
        var root = Path.Combine(Path.GetTempPath(), $"flowstock-launcher-harness-timeout-{Guid.NewGuid():N}");
        var output = Path.Combine(root, "invocation.json");
        LauncherTestHarness.CopyShimRuntime(root, "FlowStock.App.exe");

        using var process = StartShim(
            Path.Combine(root, "FlowStock.App.exe"),
            output,
            $"{output}.exit-signal",
            ignoreExitSignal: true);
        try
        {
            Assert.Throws<TimeoutException>(() =>
                LauncherTestHarness.WaitForInvocation(output, TimeSpan.FromMilliseconds(500)));

            Assert.True(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WaitForInvocation_WhenShimReturnsUnexpectedExitSignal_KillsProcessBeforeReturningError()
    {
        var root = Path.Combine(Path.GetTempPath(), $"flowstock-launcher-harness-signal-{Guid.NewGuid():N}");
        var output = Path.Combine(root, "invocation.json");
        LauncherTestHarness.CopyShimRuntime(root, "FlowStock.App.exe");

        using var process = StartShim(
            Path.Combine(root, "FlowStock.App.exe"),
            output,
            Path.Combine(root, "unexpected-exit-signal"));
        try
        {
            Assert.Throws<InvalidOperationException>(() => LauncherTestHarness.WaitForInvocation(output));

            Assert.True(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    private static Process StartShim(
        string executable,
        string output,
        string exitSignal,
        bool ignoreExitSignal = false)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false
        };
        info.Environment["FLOWSTOCK_PROCESS_SHIM_OUTPUT"] = output;
        info.Environment["FLOWSTOCK_PROCESS_SHIM_EXIT_SIGNAL"] = exitSignal;
        if (ignoreExitSignal)
        {
            info.Environment["FLOWSTOCK_PROCESS_SHIM_IGNORE_EXIT_SIGNAL"] = "1";
        }

        return Process.Start(info)!;
    }
}
