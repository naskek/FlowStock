using System.Windows;

namespace FlowStock.App;

internal sealed class SettingsPageLoading(FrameworkElement content, FileLogger logger)
{
    private readonly SettingsPageLoadState _state = new();

    public void InitializeOnLoaded(Func<Task> load)
    {
        content.Loaded += async (_, _) =>
        {
            try
            {
                await _state.InitializeAsync(load);
            }
            catch (Exception exception)
            {
                logger.Error("settings_page load failed", exception);
                MessageBox.Show(exception.Message, "Настройки FlowStock", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
    }

    public Task RunAsync(Func<Task> load) => _state.RunAsync(async () =>
    {
        var wasEnabled = content.IsEnabled;
        var cursor = content.Cursor;
        content.IsEnabled = false;
        content.Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            await load();
        }
        finally
        {
            content.Cursor = cursor;
            content.IsEnabled = wasEnabled;
        }
    });
}
