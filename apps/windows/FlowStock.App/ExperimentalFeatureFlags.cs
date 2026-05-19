namespace FlowStock.App;

/// <summary>
/// Runtime switches for experimental features hidden from default operator UI.
/// Set <see cref="WarehouseTasksEnabled"/> to true during development to show WPF «Задания» tab.
/// </summary>
public static class ExperimentalFeatureFlags
{
    public static bool WarehouseTasksEnabled { get; set; } = false;
}
