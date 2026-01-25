using System.IO;

namespace LightWms.App;

public static class AppPaths
{
    public static string BaseDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LightWMS");
    public static string DatabasePath => Path.Combine(BaseDir, "lightwms.db");
    public static string BackupsDir => Path.Combine(BaseDir, "Backups");
    public static string LogsDir => Path.Combine(BaseDir, "Logs");
    public static string SettingsPath => Path.Combine(BaseDir, "settings.json");
    public static string AdminPath => Path.Combine(BaseDir, "admin.json");
    public static string PartnerStatusPath => Path.Combine(BaseDir, "partner_statuses.json");

    public static string LegacyBaseDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightWms.Local");
    public static string LegacyDatabasePath => Path.Combine(LegacyBaseDir, "lightwms.db");
}
