using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FlowStock.App;
using FlowStock.DesktopUpdate;

namespace FlowStock.Server.Tests.Wpf;

public sealed class UiPreviewProcessTests
{
    [Fact]
    public async Task Explicit_preview_ignores_backend_environment_and_production_mutex_and_exits_on_close()
    {
        var root = FindRepositoryRoot();
        var configuration = AppContext.BaseDirectory.Contains(Path.DirectorySeparatorChar + "Release" + Path.DirectorySeparatorChar)
            ? "Release" : "Debug";
        var executable = Path.Combine(root, "apps", "windows", "FlowStock.App", "bin", configuration, "net8.0-windows", "FlowStock.App.exe");
        Assert.True(File.Exists(executable), executable);

        using var database = new TcpListener(IPAddress.Loopback, 0);
        using var server = new TcpListener(IPAddress.Loopback, 0);
        database.Start();
        server.Start();
        // Preview must work while the ordinary production single-instance mutex is held.
        using var productionMutex = new Mutex(false, DesktopUpdateConstants.AppMutexName);
        var files = new[] { AppPaths.SettingsPath, AppPaths.AdminPath, AppPaths.PartnerStatusPath };
        var before = files.Select(ReadOrMissing).ToArray();
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = root
        };
        info.ArgumentList.Add("--ui-preview");
        info.ArgumentList.Add("--update-session");
        info.ArgumentList.Add(new string('a', 32));
        info.Environment["FLOWSTOCK_PG_HOST"] = "127.0.0.1";
        info.Environment["FLOWSTOCK_PG_PORT"] = ((IPEndPoint)database.LocalEndpoint).Port.ToString();
        info.Environment["FLOWSTOCK_PG_DB"] = "preview-must-not-connect";
        info.Environment["FLOWSTOCK_PG_USER"] = "preview-must-not-connect";
        info.Environment["FLOWSTOCK_PG_PASSWORD"] = "preview-must-not-connect";
        info.Environment["FLOWSTOCK_SERVER_BASE_URL"] = $"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}";
        info.Environment["FLOWSTOCK_UPDATE_SERVER_BASE_URL"] = info.Environment["FLOWSTOCK_SERVER_BASE_URL"];
        using var process = Process.Start(info)!;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!process.HasExited && DateTime.UtcNow < deadline)
            {
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero &&
                    process.MainWindowTitle.Contains("UI Preview / DEV", StringComparison.Ordinal))
                {
                    break;
                }

                await Task.Delay(100);
            }
            Assert.False(process.HasExited);
            process.Refresh();
            Assert.NotEqual(IntPtr.Zero, process.MainWindowHandle);
            Assert.Contains("UI Preview / DEV", process.MainWindowTitle);
            await Task.Delay(500); // include ContentRendered and deferred dispatcher work
            Assert.False(database.Pending(), "Preview attempted a DB connection.");
            Assert.False(server.Pending(), "Preview attempted a Server/updater connection.");
            Assert.True(process.CloseMainWindow());
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(before, files.Select(ReadOrMissing).ToArray());
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static string ReadOrMissing(string path) => File.Exists(path) ? File.ReadAllText(path) : "<missing>";

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "FLOWSTOCK.cmd"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}
