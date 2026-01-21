using System.IO;
using LightWms.Core.Abstractions;
using LightWms.Core.Services;
using LightWms.Data;

namespace LightWms.App;

public sealed class AppServices
{
    public IDataStore DataStore { get; }
    public CatalogService Catalog { get; }
    public ItemPackagingService Packagings { get; }
    public DocumentService Documents { get; }
    public OrderService Orders { get; }
    public ImportService Import { get; }
    public SettingsService Settings { get; }
    public BackupService Backups { get; }
    public AdminAuthService AdminAuth { get; }
    public AdminService Admin { get; }
    public FileLogger AppLogger { get; }
    public FileLogger AdminLogger { get; }
    public string DatabasePath { get; }
    public string BaseDir { get; }
    public string BackupsDir { get; }
    public string LogsDir { get; }
    public string SettingsPath { get; }
    public string AdminPath { get; }
    public string AppLogPath { get; }
    public string AdminLogPath { get; }

    private AppServices(
        IDataStore dataStore,
        string databasePath,
        string baseDir,
        string backupsDir,
        string logsDir,
        string settingsPath,
        string adminPath,
        FileLogger appLogger,
        FileLogger adminLogger)
    {
        DataStore = dataStore;
        Catalog = new CatalogService(dataStore);
        Packagings = new ItemPackagingService(dataStore);
        Documents = new DocumentService(dataStore);
        Orders = new OrderService(dataStore);
        Import = new ImportService(dataStore);
        Settings = new SettingsService(settingsPath);
        Backups = new BackupService(databasePath, backupsDir, appLogger);
        AdminAuth = new AdminAuthService(adminPath, adminLogger);
        Admin = new AdminService(databasePath, backupsDir, dataStore, adminLogger);
        DatabasePath = databasePath;
        BaseDir = baseDir;
        BackupsDir = backupsDir;
        LogsDir = logsDir;
        SettingsPath = settingsPath;
        AdminPath = adminPath;
        AppLogger = appLogger;
        AdminLogger = adminLogger;
        AppLogPath = Path.Combine(logsDir, "app.log");
        AdminLogPath = Path.Combine(logsDir, "admin.log");
    }

    public static AppServices CreateDefault()
    {
        var baseDir = AppPaths.BaseDir;
        var backupsDir = AppPaths.BackupsDir;
        var logsDir = AppPaths.LogsDir;
        var settingsPath = AppPaths.SettingsPath;
        var adminPath = AppPaths.AdminPath;
        var dbPath = AppPaths.DatabasePath;

        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(backupsDir);
        Directory.CreateDirectory(logsDir);

        MigrateLegacyDatabase(dbPath);

        var appLogger = new FileLogger(Path.Combine(logsDir, "app.log"));
        var adminLogger = new FileLogger(Path.Combine(logsDir, "admin.log"));
        var dataStore = new SqliteDataStore(dbPath);
        dataStore.Initialize();

        return new AppServices(
            dataStore,
            dbPath,
            baseDir,
            backupsDir,
            logsDir,
            settingsPath,
            adminPath,
            appLogger,
            adminLogger);
    }

    private static void MigrateLegacyDatabase(string dbPath)
    {
        if (File.Exists(dbPath))
        {
            return;
        }

        var legacyPath = AppPaths.LegacyDatabasePath;
        if (!File.Exists(legacyPath))
        {
            return;
        }

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.Copy(legacyPath, dbPath, overwrite: false);
        CopyIfExists(legacyPath + "-wal", dbPath + "-wal");
        CopyIfExists(legacyPath + "-shm", dbPath + "-shm");
    }

    private static void CopyIfExists(string source, string target)
    {
        if (!File.Exists(source))
        {
            return;
        }

        File.Copy(source, target, overwrite: true);
    }
}
