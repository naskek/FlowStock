namespace FlowStock.Server;

public static class ServerPaths
{
    public static string BaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FlowStock");

    public static string TsdRoot => ResolveTsdRoot();
    public static string PcRoot => ResolvePcRoot();

    private static string ResolveTsdRoot()
    {
        var projectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        var tsdPath = Path.GetFullPath(Path.Combine(projectDir, "..", "..", "android", "tsd"));
        return tsdPath;
    }

    private static string ResolvePcRoot()
    {
        var projectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        var pcPath = Path.GetFullPath(Path.Combine(projectDir, "..", "..", "android", "tsd", "pc"));
        return pcPath;
    }
}

