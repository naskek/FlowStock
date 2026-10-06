using System.Reflection;
using System.IO;
using FlowStock.DesktopUpdate;

namespace FlowStock.App;

public static class AppRuntimeInfo
{
    public static BuildIdentity Current { get; } = BuildIdentity.FromAssembly(Assembly.GetExecutingAssembly());

    public static bool IsDevelopmentCheckout =>
        IsUnder(AppContext.BaseDirectory, DesktopUpdateConstants.DevelopmentRepositoryRoot);

    public static bool IsSourceRun => IsSourceCheckoutDirectory(AppContext.BaseDirectory);

    public static bool IsSourceCheckoutDirectory(string candidate) =>
        IsUnder(candidate, DesktopUpdateConstants.LegacySourceRepositoryRoot)
        || IsUnder(candidate, DesktopUpdateConstants.DevelopmentRepositoryRoot);

    public static string DisplayText =>
        $"FlowStock {Current.ProductVersion} · {Current.ShortCommit}"
        + (IsSourceRun ? " · запуск из исходного checkout" : string.Empty);

    private static bool IsUnder(string candidate, string root)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
