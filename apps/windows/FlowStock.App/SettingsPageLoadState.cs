namespace FlowStock.App;

// Called from the dispatcher. A cached page keeps its initial load across Loaded/Unloaded.
internal sealed class SettingsPageLoadState
{
    private Task? _initialization;
    private Task? _running;

    public Task InitializeAsync(Func<Task> load) =>
        _initialization is null || _initialization.IsFaulted
            ? _initialization = load()
            : _initialization;

    public Task RunAsync(Func<Task> load)
    {
        if (_running is { IsCompleted: false })
        {
            return _running;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _running = completion.Task;
        _ = CompleteAsync(load, completion);
        return completion.Task;
    }

    private static async Task CompleteAsync(Func<Task> load, TaskCompletionSource completion)
    {
        try
        {
            await load();
            completion.SetResult();
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    }
}
