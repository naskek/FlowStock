using System.IO;
using System.Windows;
using System.Windows.Threading;
using FlowStock.DesktopUpdate;
using Application = System.Windows.Application;

namespace FlowStock.App;

public partial class App : Application
{
    private FileLogger? _fallbackLogger;
    private FileLogger? _appLogger;
    private string _logPath = Path.Combine(AppPaths.LogsDir, "app.log");
    private AppServices? _services;
    private Mutex? _instanceMutex;
    private StartupSplashController? _startupSplash;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _startupSplash = StartupSplashController.Start();
        }
        catch (Exception ex)
        {
            LogException("StartupSplash", ex);
        }

        try
        {
            if (UiPreviewStartup.TryStart(e.Args, preview =>
                {
                    _logPath = Path.Combine(Path.GetTempPath(), "FlowStock-UiPreview", "app.log");
                    _startupSplash?.SetStage("Загрузка UI Preview / DEV…");
                    DispatcherUnhandledException += OnDispatcherUnhandledException;
                    AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                    ShutdownMode = ShutdownMode.OnMainWindowClose;
                    var window = new MainWindow(preview);
                    MainWindow = window;
                    var readyHandled = false;
                    window.ContentRendered += (_, _) =>
                    {
                        if (readyHandled)
                        {
                            return;
                        }

                        readyHandled = true;
                        _startupSplash?.Complete();
                    };
                    window.Show();
                }))
            {
                return;
            }
        }
        catch (Exception ex)
        {
            LogException("UiPreviewStartup", ex);
            _startupSplash?.Fail(DatabaseErrorFormatter.Format(ex), _logPath);
            MessageBox.Show(
                $"Startup error. See log: {_logPath}\n{DatabaseErrorFormatter.Format(ex)}",
                "FlowStock",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }

        _instanceMutex = new Mutex(true, DesktopUpdateConstants.AppMutexName, out var createdNew);
        if (!createdNew)
        {
            const string message = "FlowStock уже запущен для этого пользователя.";
            _startupSplash?.Fail(message, _logPath);
            MessageBox.Show(message, "FlowStock", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(2);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        try
        {
            _startupSplash?.SetStage("Подготовка runtime…");
            var services = AppServices.CreateDefault();
            _services = services;
            _appLogger = services.AppLogger;
            _logPath = services.AppLogPath;
            if (!services.IsDatabaseAvailable)
            {
                _startupSplash?.SetStage("Открытие настройки подключения…");
                var connectionWindow = new DbConnectionWindow(services, requireConnectionOnStartup: true);
                var updateReadyHandled = false;
                connectionWindow.ContentRendered += (_, _) =>
                {
                    if (updateReadyHandled)
                    {
                        return;
                    }

                    updateReadyHandled = true;
                    _startupSplash?.Complete();
                    _ = HandleUpdateReadyAsync(e.Args);
                };
                connectionWindow.ShowDialog();
                Shutdown(0);
                return;
            }

            TryRunAutoBackup(services);
            _startupSplash?.SetStage("Загрузка интерфейса…");
            var mainWindow = new MainWindow(services);
            MainWindow = mainWindow;
            var mainReadyHandled = false;
            mainWindow.ContentRendered += (_, _) =>
            {
                if (mainReadyHandled)
                {
                    return;
                }

                mainReadyHandled = true;
                _startupSplash?.Complete();
            };
            mainWindow.Show();
            _ = HandleUpdateReadyAsync(e.Args);
            services.LiveRefresh.Start(Dispatcher);
        }
        catch (Exception ex)
        {
            LogException("Startup", ex);
            _startupSplash?.Fail(DatabaseErrorFormatter.Format(ex), _logPath);
            MessageBox.Show($"Startup error. See log: {_logPath}\n{DatabaseErrorFormatter.Format(ex)}", "FlowStock", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _startupSplash?.Close();
        _services?.LiveRefresh.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private Task HandleUpdateReadyAsync(IReadOnlyList<string> args)
    {
        var paths = new DesktopUpdatePaths();
        return UpdateStartupLifecycle.HandleReadyAsync(
            args,
            AppRuntimeInfo.Current,
            paths,
            result =>
            {
                MessageBox.Show(
                    result.Success
                        ? $"FlowStock обновлён: {result.Current.ProductVersion} ({result.Current.ShortCommit}) → {result.Target.ProductVersion} ({result.Target.ShortCommit})."
                        : $"Обновление не завершено: {result.Message}"
                          + (string.IsNullOrWhiteSpace(result.FallbackError)
                              ? string.Empty
                              : $"\nОшибка fallback: {result.FallbackError}")
                          + $"\nЛог: {result.LogPath}",
                    "Обновление FlowStock",
                    MessageBoxButton.OK,
                    result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
                return Task.CompletedTask;
            },
            TimeSpan.FromMilliseconds(250),
            80);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException("Dispatcher", e.Exception);
        _startupSplash?.Fail(DatabaseErrorFormatter.Format(e.Exception), _logPath);
        MessageBox.Show($"Unexpected error. See log: {_logPath}\n{DatabaseErrorFormatter.Format(e.Exception)}", "FlowStock", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogException("AppDomain", ex);
            _startupSplash?.Fail(DatabaseErrorFormatter.Format(ex), _logPath);
            return;
        }

        Log("AppDomain: unknown error");
        _startupSplash?.Fail("Неизвестная ошибка запуска.", _logPath);
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
        var logger = _appLogger ?? (_fallbackLogger ??= new FileLogger(_logPath));
        logger.Error(message);
    }
}
