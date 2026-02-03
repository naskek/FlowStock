using System.IO;

namespace FlowStock.App;

public static class AppPaths
{
    public static string BaseDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlowStock");
    public static string DatabasePath => Path.Combine(BaseDir, "flowstock.db");
    public static string BackupsDir => Path.Combine(BaseDir, "Backups");
    public static string LogsDir => Path.Combine(BaseDir, "Logs");
    public static string SettingsPath => Path.Combine(BaseDir, "settings.json");
    public static string AdminPath => Path.Combine(BaseDir, "admin.json");
    public static string PartnerStatusPath => Path.Combine(BaseDir, "partner_statuses.json");

    public static string LegacyBaseDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlowStock");
    public static string LegacyDatabasePath => Path.Combine(LegacyBaseDir, "flowstock.db");
}

