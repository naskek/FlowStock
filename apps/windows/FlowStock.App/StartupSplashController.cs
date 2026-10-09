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

        var window = new StartupSplashWindow();
        var controller = new StartupSplashController(window, null);
        window.Show();
        window.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
        return controller;
    }

    public void SetStage(string message)
    {
        if (_finished)
        {
            return;
        }

        if (_window is not null)
        {
            _window.SetStatus(message);
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
            _window.Complete();
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
            _window.ShowFailure(message, logPath);
            return;
        }

        TryWriteExternal("error", message, logPath);
    }

    public void Close()
    {
        if (_window?.IsVisible == true)
        {
            _window.ForceClose();
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
