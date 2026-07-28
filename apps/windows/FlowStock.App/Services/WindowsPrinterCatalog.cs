using System.Drawing.Printing;

namespace FlowStock.App;

public static class PalletLabelPrinterNameResolver
{
    public const string EnvironmentVariableName = "FLOWSTOCK_PALLET_LABEL_PRINTER_NAME";

    public static string? ResolveEnvironmentOverride(string? environmentValue)
    {
        return NormalizePrinterName(environmentValue);
    }

    public static string? ResolvePrinterName(string? environmentValue, string? settingsValue)
    {
        return ResolveEnvironmentOverride(environmentValue) ?? NormalizePrinterName(settingsValue);
    }

    public static string? NormalizePrinterName(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

public sealed class WindowsPrinterCatalog
{
    public IReadOnlyList<string> GetInstalledPrinterNames()
    {
        return NormalizePrinterNames(PrinterSettings.InstalledPrinters.Cast<string?>());
    }

    public static IReadOnlyList<string> NormalizePrinterNames(IEnumerable<string?> names)
    {
        return names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

public sealed record PalletLabelPrinterSelectionState(
    IReadOnlyList<string> InstalledPrinterNames,
    string PrinterName,
    bool IsPrinterMissing)
{
    public static PalletLabelPrinterSelectionState Build(
        IEnumerable<string?> installedPrinterNames,
        string? savedPrinterName)
    {
        var installed = WindowsPrinterCatalog.NormalizePrinterNames(installedPrinterNames);
        var saved = PalletLabelPrinterNameResolver.NormalizePrinterName(savedPrinterName) ?? string.Empty;
        var isMissing = saved.Length > 0
                        && !installed.Contains(saved, StringComparer.OrdinalIgnoreCase);

        return new PalletLabelPrinterSelectionState(installed, saved, isMissing);
    }
}
