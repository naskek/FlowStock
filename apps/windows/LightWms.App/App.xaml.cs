using System.IO;
using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace LightWms.App;

public partial class App : Application
{
    private readonly FileLogger _fallbackLogger = new(Path.Combine(AppPaths.LogsDir, "app.log"));
    private FileLogger? _appLogger;
    private string _logPath = Path.Combine(AppPaths.LogsDir, "app.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        try
        {
            var services = AppServices.CreateDefault();
            _appLogger = services.AppLogger;
            _logPath = services.AppLogPath;
            TryRunAutoBackup(services);
            var mainWindow = new MainWindow(services);
            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            LogException("Startup", ex);
            MessageBox.Show($"Startup error. See log: {_logPath}\n{ex.Message}", "LightWMS Local", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException("Dispatcher", e.Exception);
        MessageBox.Show($"Unexpected error. See log: {_logPath}\n{e.Exception.Message}", "LightWMS Local", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogException("AppDomain", ex);
            return;
        }

        Log("AppDomain: unknown error");
    }

    private void TryRunAutoBackup(AppServices services)
    {
        try
        {
            var settings = services.Settings.Load();
            if (!settings.BackupsEnabled)
            {
                return;
            }

            var shouldBackup = settings.BackupMode == BackupMode.OnEveryStart;
            if (settings.BackupMode == BackupMode.OnStartIfOlderThanHours)
            {
                var last = services.Backups.GetLastBackupTime();
                if (!last.HasValue || DateTime.Now - last.Value > TimeSpan.FromHours(settings.BackupIfOlderThanHours))
                {
                    shouldBackup = true;
                }
            }

            if (!shouldBackup)
            {
                return;
            }

            services.Backups.CreateBackup(settings.BackupMode == BackupMode.OnEveryStart
                ? "auto_on_start"
                : "auto_on_start_if_older");
            services.Backups.ApplyRetention(settings.KeepLastNBackups);
        }
        catch (Exception ex)
        {
            services.AppLogger.Error("Auto backup failed", ex);
        }
    }

    private void LogException(string scope, Exception ex)
    {
        Log($"{scope}: {ex}");
    }

    private void Log(string message)
    {
        var logger = _appLogger ?? _fallbackLogger;
        logger.Error(message);
    }
}
