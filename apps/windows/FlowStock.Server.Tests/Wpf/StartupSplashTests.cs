using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
using FlowStock.App;
using FlowStock.DesktopUpdate;

namespace FlowStock.Server.Tests.Wpf;

public sealed class StartupSplashTests
{
    [Fact]
    public async Task Internal_splash_tracks_stage_and_closes_when_ready()
    {
        await OnUiThread(() =>
        {
            var controller = StartupSplashController.Start(null);
            var window = Assert.IsType<StartupSplashWindow>(controller.Window);
            try
            {
                Assert.False(controller.UsesExternalSplash);
                Assert.True(window.IsVisible);
                Assert.True(window.IsProgressVisible);

                controller.SetStage("Загрузка интерфейса…");
                Assert.Equal("Загрузка интерфейса…", window.StatusText);

                controller.Complete();
                Assert.False(window.IsVisible);
            }
            finally
            {
                controller.Close();
            }
        });
    }

    [Fact]
    public async Task Internal_splash_turns_failure_into_diagnostic_state()
    {
        await OnUiThread(() =>
        {
            var controller = StartupSplashController.Start(null);
            var window = Assert.IsType<StartupSplashWindow>(controller.Window);
            try
            {
                controller.Fail("boom", @"C:\temp\app.log");

                Assert.Equal("Запуск не завершён", window.StatusText);
                Assert.Contains("boom", window.DetailsText);
                Assert.Contains(@"C:\temp\app.log", window.DetailsText);
                Assert.False(window.IsProgressVisible);
                Assert.True(window.IsCloseVisible);
            }
            finally
            {
                controller.Close();
            }
        });
    }

    [Fact]
    public void External_splash_status_is_written_and_finished_without_creating_window()
    {
        var directory = Path.Combine(Path.GetTempPath(), StartupSplashController.StatusDirectoryName);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.json");

        try
        {
            var controller = StartupSplashController.Start(path);
            Assert.True(controller.UsesExternalSplash);
            Assert.Null(controller.Window);

            AssertStatus(path, "starting", "Запуск FlowStock…");

            controller.SetStage("Подготовка runtime…");
            AssertStatus(path, "starting", "Подготовка runtime…");

            controller.Complete();
            AssertStatus(path, "ready", "Готово");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task External_status_path_outside_dedicated_temp_directory_is_rejected()
    {
        var invalid = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        Assert.Null(StartupSplashController.ResolveExternalStatusPath(invalid));

        await OnUiThread(() =>
        {
            var controller = StartupSplashController.Start(invalid);
            try
            {
                Assert.False(controller.UsesExternalSplash);
                Assert.NotNull(controller.Window);
            }
            finally
            {
                controller.Close();
            }
        });
    }

    [Theory]
    [InlineData("""{"state":"ready","message":"Готово"}""", 0)]
    [InlineData("""{"state":"error","message":"Ошибка запуска"}""", 2)]
    public void Headless_launcher_splash_terminates_on_terminal_status(string json, int expectedExitCode)
    {
        var result = RunHeadlessSplash(json, timeoutSeconds: 2);

        Assert.Equal(expectedExitCode, result.ExitCode);
    }

    [Fact]
    public void Headless_launcher_splash_times_out_instead_of_loading_forever()
    {
        var result = RunHeadlessSplash(
            """{"state":"starting","message":"Идёт запуск"}""",
            timeoutSeconds: 1);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("FLOWSTOCK_STARTUP_TIMEOUT:", result.StandardError, StringComparison.Ordinal);
    }

    private static void AssertStatus(string path, string expectedState, string expectedMessage)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.Equal(expectedState, root.GetProperty("state").GetString());
        Assert.Equal(expectedMessage, root.GetProperty("message").GetString());
    }

    private static ProcessResult RunHeadlessSplash(string initialJson, int timeoutSeconds)
    {
        var directory = Path.Combine(Path.GetTempPath(), StartupSplashController.StatusDirectoryName);
        Directory.CreateDirectory(directory);
        var statusPath = Path.Combine(directory, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(statusPath, initialJson);

        try
        {
            var script = Path.Combine(
                DesktopUpdateConstants.DefaultRepositoryRoot,
                "tools",
                "windows",
                "show-flowstock-startup-splash.ps1");
            var info = new ProcessStartInfo
            {
                FileName = "pwsh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in new[]
                     {
                         "-NoLogo", "-NoProfile", "-NonInteractive", "-File", script,
                         "-StatusPath", statusPath,
                         "-TimeoutSeconds", timeoutSeconds.ToString(),
                         "-Headless"
                     })
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10_000), "startup splash helper did not exit");
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }
        finally
        {
            File.Delete(statusPath);
        }
    }

    private static async Task OnUiThread(Action action)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                action();
                result.SetResult();
            }
            catch (Exception exception)
            {
                result.SetException(exception);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            await result.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        }
    }
}
