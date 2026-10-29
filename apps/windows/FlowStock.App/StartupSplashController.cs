using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace FlowStock.App;

internal sealed class StartupSplashController
{
    internal const string StatusEnvironmentVariable = "FLOWSTOCK_STARTUP_STATUS_FILE";
    internal const string StatusDirectoryName = "FlowStock-Startup";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly StartupSplashWindow? _window;
    private readonly string? _statusPath;
    private bool _finished;

    private StartupSplashController(StartupSplashWindow? window, string? statusPath)
    {
        _window = window;
        _statusPath = statusPath;
    }

    internal StartupSplashWindow? Window => _window;
    internal string? StatusPath => _statusPath;
    internal bool UsesExternalSplash => _statusPath is not null;

    public static StartupSplashController Start() =>
        Start(Environment.GetEnvironmentVariable(StatusEnvironmentVariable));

    internal static StartupSplashController Start(string? externalStatusCandidate)
    {
        var externalPath = ResolveExternalStatusPath(externalStatusCandidate);
        if (externalPath is not null)
        {
            var external = new StartupSplashController(null, externalPath);
            external.SetStage("Запуск FlowStock…");
            return external;
        }

        // The production WPF startup synchronously initializes PostgreSQL and loads
        // the first window. Keep the splash on its own STA Dispatcher so animation
        // and diagnostics remain responsive while the main Dispatcher is blocked.
        var ready = new TaskCompletionSource<StartupSplashWindow>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                var window = new StartupSplashWindow();
                window.Closed += (_, _) => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                window.Show();
                ready.TrySetResult(window);
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                ready.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "FlowStock startup splash"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var splashWindow = ready.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        return new StartupSplashController(splashWindow, null);
    }

    public void SetStage(string message)
    {
        if (_finished)
        {
            return;
        }

        if (_window is not null)
        {
            UpdateWindow(window => window.SetStatus(message));
            return;
        }

        TryWriteExternal("starting", message, null);
    }

    public void Complete(string message = "Готово")
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        if (_window is not null)
        {
            UpdateWindow(window => window.Complete());
            return;
        }

        TryWriteExternal("ready", message, null);
    }

    public void Fail(string message, string? logPath)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        if (_window is not null)
        {
            UpdateWindow(window => window.ShowFailure(message, logPath));
            return;
        }

        TryWriteExternal("error", message, logPath);
    }

    public void Close()
    {
        if (_window is not null)
        {
            UpdateWindow(window => window.ForceClose());
        }
    }

    private void UpdateWindow(Action<StartupSplashWindow> update)
    {
        var window = _window;
        if (window is null || window.Dispatcher.HasShutdownStarted)
        {
            return;
        }

        try
        {
            if (window.Dispatcher.CheckAccess())
            {
                update(window);
            }
            else
            {
                window.Dispatcher.Invoke(() => update(window));
            }
        }
        catch (TaskCanceledException) when (window.Dispatcher.HasShutdownStarted)
        {
            // The splash thread was already closed during application shutdown.
        }
        catch (InvalidOperationException) when (window.Dispatcher.HasShutdownStarted)
        {
            // The Dispatcher no longer accepts new work.
        }
    }

    internal static string? ResolveExternalStatusPath(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        try
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), StatusDirectoryName))
                       + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(candidate);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(full), ".json", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return full;
        }
        catch
        {
            return null;
        }
    }

    private void TryWriteExternal(string state, string message, string? logPath)
    {
        if (_statusPath is null)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_statusPath)!;
            Directory.CreateDirectory(directory);

            var effectiveLogPath = logPath;
            if (string.IsNullOrWhiteSpace(effectiveLogPath) && File.Exists(_statusPath))
            {
                try
                {
                    using var current = JsonDocument.Parse(File.ReadAllText(_statusPath));
                    if (current.RootElement.TryGetProperty("logPath", out var currentLogPath) &&
                        currentLogPath.ValueKind == JsonValueKind.String)
                    {
                        effectiveLogPath = currentLogPath.GetString();
                    }
                }
                catch
                {
                    // A concurrent atomic replace can make the previous diagnostic unreadable briefly.
                }
            }

            var payload = new StartupSplashStatus(
                state,
                string.IsNullOrWhiteSpace(message) ? "Идёт запуск…" : message.Trim(),
                effectiveLogPath,
                DateTimeOffset.UtcNow);
            var temporary = Path.Combine(
                directory,
                $".{Path.GetFileName(_statusPath)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(payload, JsonOptions));
            File.Move(temporary, _statusPath, true);
        }
        catch
        {
            // Startup feedback must never block the application itself.
        }
    }

    internal sealed record StartupSplashStatus(
        string State,
        string Message,
        string? LogPath,
        DateTimeOffset UpdatedAtUtc);
}
