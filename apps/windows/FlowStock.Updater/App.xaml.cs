using System.IO;
using System.Windows;
using FlowStock.DesktopUpdate;

namespace FlowStock.Updater;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, DesktopUpdateConstants.UpdaterMutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Другой процесс обновления FlowStock уже запущен.", "FlowStock Updater");
            Shutdown(2);
            return;
        }

        if (e.Args.Length != 2 || (e.Args[0] != "--update" && e.Args[0] != "--recover"))
        {
            MessageBox.Show("Updater должен запускаться через FlowStock launcher.", "FlowStock Updater");
            Shutdown(2);
            return;
        }

        var recovery = e.Args[0] == "--recover";
        var outcome = new UpdaterProcessOutcome(recovery);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var window = new UpdaterWindow(recovery, e.Args[1], outcome);
        window.Closed += (_, _) => Shutdown(outcome.ExitCode);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
