using System.Windows;

namespace LightWms.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = AppServices.CreateDefault();
        var mainWindow = new MainWindow(services);
        mainWindow.Show();
    }
}
