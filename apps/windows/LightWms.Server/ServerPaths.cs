namespace LightWms.Server;

public static class ServerPaths
{
    public static string BaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LightWMS");

    public static string DatabasePath => Path.Combine(BaseDir, "lightwms.db");

    public static string TsdRoot => ResolveTsdRoot();

    private static string ResolveTsdRoot()
    {
        var projectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        var tsdPath = Path.GetFullPath(Path.Combine(projectDir, "..", "..", "android", "tsd"));
        return tsdPath;
    }
}
