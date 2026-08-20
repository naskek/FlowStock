using System.Reflection;
using System.IO;
using FlowStock.DesktopUpdate;

namespace FlowStock.App;

public static class AppRuntimeInfo
{
    public static BuildIdentity Current { get; } = BuildIdentity.FromAssembly(Assembly.GetExecutingAssembly());

    public static bool IsDevelopmentCheckout =>
        IsUnder(AppContext.BaseDirectory, DesktopUpdateConstants.DevelopmentRepositoryRoot);

    public static bool IsSourceRun
    {
        get
        {
            var paths = new DesktopUpdatePaths();
            var active = JsonStateStore.Read<RuntimeManifest>(paths.ActiveManifest);
            if (active is null || !BuildIdentity.IsFullCommit(active.Commit))
            {
                return true;
            }

            return !string.Equals(
                Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(paths.AppDirectory(active.Commit)).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    public static string DisplayText =>
        $"FlowStock {Current.ProductVersion} · {Current.ShortCommit}"
        + (IsSourceRun ? " · запуск из исходного checkout" : string.Empty);

    private static bool IsUnder(string candidate, string root)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
