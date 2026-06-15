using System.IO;
using System.Runtime.InteropServices;

namespace FlowStock.App;

public sealed class BarTenderPalletLabelPrintService : IPalletLabelPrintService
{
    private static readonly string[] RequiredFields = ["HuCode", "ItemName", "Qty"];
    private readonly SettingsService _settings;
    private readonly FileLogger _logger;
    private readonly string _baseDir;

    public BarTenderPalletLabelPrintService(SettingsService settings, FileLogger logger, string baseDir)
    {
        _settings = settings;
        _logger = logger;
        _baseDir = baseDir;
    }

    public PalletLabelPrintResult Print(IReadOnlyList<PalletLabelPrintRow> rows, int? copiesOverride = null)
    {
        if (rows.Count == 0)
        {
            return PalletLabelPrintResult.Failure("Сначала сформируйте план паллет");
        }

        var validationError = ValidateRows(rows);
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            return PalletLabelPrintResult.Failure(validationError);
        }

        var config = LoadConfiguration(copiesOverride);
        if (string.IsNullOrWhiteSpace(config.TemplatePath) || !File.Exists(config.TemplatePath))
        {
            return PalletLabelPrintResult.Failure($"Файл шаблона паллетной этикетки не найден: {config.TemplatePath}");
        }

        dynamic? app = null;
        dynamic? format = null;
        try
        {
            var appType = Type.GetTypeFromProgID("BarTender.Application");
            if (appType == null)
            {
                return PalletLabelPrintResult.Failure("BarTender не установлен или COM API недоступен.");
            }

            app = Activator.CreateInstance(appType);
            if (app == null)
            {
                return PalletLabelPrintResult.Failure("BarTender COM API не удалось запустить.");
            }

            app.Visible = false;
            format = app.Formats.Open(config.TemplatePath, false, string.Empty);
            DisableTemplateDatabase(format);
            ApplyPrinter(format, config.PrinterName, config.Copies);

            foreach (var row in rows)
            {
                ApplyNamedSubStrings(format, row);
                format.PrintOut(false, false);
            }

            return PalletLabelPrintResult.Success(rows.Count);
        }
        catch (Exception ex)
        {
            _logger.Error("BarTender pallet label print failed", ex);
            return PalletLabelPrintResult.Failure($"Не удалось напечатать паллетные этикетки: {ex.Message}");
        }
        finally
        {
            CloseFormat(format);
            QuitBarTender(app);
            ReleaseCom(format);
            ReleaseCom(app);
        }
    }

    private PalletLabelPrinterConfiguration LoadConfiguration(int? copiesOverride)
    {
        var labels = _settings.Load().PalletLabels ?? new PalletLabelSettings();
        var templatePath = ReadEnvOrSettings("FLOWSTOCK_PALLET_LABEL_TEMPLATE_PATH", labels.TemplatePath);
        if (string.IsNullOrWhiteSpace(templatePath))
        {
            templatePath = Path.Combine(_baseDir, "templates", "pallet_label.btw");
        }

        if (!Path.IsPathRooted(templatePath))
        {
            templatePath = Path.Combine(_baseDir, templatePath);
        }

        var copies = copiesOverride ?? ReadEnvInt("FLOWSTOCK_PALLET_LABEL_COPIES") ?? labels.Copies;
        copies = Math.Clamp(copies, 1, 100);

        return new PalletLabelPrinterConfiguration(
            templatePath,
            ReadEnvOrSettings("FLOWSTOCK_PALLET_LABEL_PRINTER_NAME", labels.PrinterName),
            copies);
    }

    private static string? ValidateRows(IReadOnlyList<PalletLabelPrintRow> rows)
    {
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.HuCode))
            {
                return "Для паллеты не задан HU.";
            }

            if (string.IsNullOrWhiteSpace(row.ItemName))
            {
                return "Для паллеты не задана номенклатура.";
            }

            if (row.Qty <= 0)
            {
                return "Для паллеты не задано количество.";
            }
        }

        return null;
    }

    private static void DisableTemplateDatabase(dynamic format)
    {
        TryCom(() => format.UseDatabase = false);
        TryCom(() => format.SelectRecordsAtPrint = false);
        TryCom(() => format.RecordRange = "1");
    }

    private static void ApplyPrinter(dynamic format, string? printerName, int copies)
    {
        if (!string.IsNullOrWhiteSpace(printerName))
        {
            TryCom(() => format.PrintSetup.PrinterName = printerName);
            TryCom(() => format.PrintSetup.Printer = printerName);
        }

        TryCom(() => format.PrintSetup.IdenticalCopiesOfLabel = copies);
    }

    private static void ApplyNamedSubStrings(dynamic format, PalletLabelPrintRow row)
    {
        var values = row.ToNamedSubStrings();
        foreach (var field in RequiredFields)
        {
            if (!TrySetNamedSubString(format, field, values[field]))
            {
                throw new InvalidOperationException($"Не удалось заполнить обязательное поле BarTender: {field}");
            }
        }

        foreach (var pair in values)
        {
            if (RequiredFields.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            TrySetNamedSubString(format, pair.Key, pair.Value);
        }
    }

    private static bool TrySetNamedSubString(dynamic format, string name, string value)
    {
        try
        {
            format.SetNamedSubStringValue(name, value ?? string.Empty);
            return true;
        }
        catch
        {
        }

        try
        {
            dynamic subString = format.SubStrings.Item(name);
            subString.Value = value ?? string.Empty;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void CloseFormat(dynamic? format)
    {
        if (format == null)
        {
            return;
        }

        TryCom(() => format.Close(1));
    }

    private static void QuitBarTender(dynamic? app)
    {
        if (app == null)
        {
            return;
        }

        TryCom(() => app.Quit(1));
    }

    private static void ReleaseCom(object? obj)
    {
        if (obj == null)
        {
            return;
        }

        try
        {
            if (Marshal.IsComObject(obj))
            {
                Marshal.FinalReleaseComObject(obj);
            }
        }
        catch
        {
        }
    }

    private static void TryCom(Action action)
    {
        try
        {
            action();
        }
        catch
        {
        }
    }

    private static string? ReadEnvOrSettings(string envKey, string? settingsValue)
    {
        var env = Environment.GetEnvironmentVariable(envKey);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Trim();
        }

        return string.IsNullOrWhiteSpace(settingsValue) ? null : settingsValue.Trim();
    }

    private static int? ReadEnvInt(string envKey)
    {
        var env = Environment.GetEnvironmentVariable(envKey);
        return int.TryParse(env, out var value) ? value : null;
    }

    private sealed record PalletLabelPrinterConfiguration(string TemplatePath, string? PrinterName, int Copies);
}
