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
    public HuService Hus { get; }
    public BackupService Backups { get; }
    public AdminAuthService AdminAuth { get; }
    public AdminService Admin { get; }
    public PartnerStatusService PartnerStatuses { get; }
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
        string partnerStatusPath,
        FileLogger appLogger,
        FileLogger adminLogger)
    {
        DataStore = dataStore;
        Catalog = new CatalogService(dataStore);
        Packagings = new ItemPackagingService(dataStore);
        Documents = new DocumentService(dataStore);
        Orders = new OrderService(dataStore);
        Settings = new SettingsService(settingsPath);
        Hus = new HuService(dataStore);
        Import = new ImportService(dataStore);
        Backups = new BackupService(databasePath, backupsDir, appLogger);
        AdminAuth = new AdminAuthService(adminPath, adminLogger);
        Admin = new AdminService(databasePath, backupsDir, dataStore, adminLogger);
        PartnerStatuses = new PartnerStatusService(partnerStatusPath);
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
        var partnerStatusPath = AppPaths.PartnerStatusPath;
        var dbPath = AppPaths.DatabasePath;

        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(backupsDir);
        Directory.CreateDirectory(logsDir);

        var appLogger = new FileLogger(Path.Combine(logsDir, "app.log"));
        var adminLogger = new FileLogger(Path.Combine(logsDir, "admin.log"));
        var config = LoadAppConfig();
        var provider = ResolveDbProvider(config);
        IDataStore dataStore;
        if (provider == DbProvider.Postgres)
        {
            var connectionString = BuildPostgresConnectionString(config);
            dataStore = new PostgresDataStore(connectionString);
            dataStore.Initialize();
            var target = FormatPostgresTarget(connectionString);
            appLogger.Info($"Database provider: postgres {target}");
        }
        else
        {
            MigrateLegacyDatabase(dbPath);
            dataStore = new SqliteDataStore(dbPath);
            dataStore.Initialize();
            appLogger.Info($"Database provider: sqlite {dbPath}");
        }

        return new AppServices(
            dataStore,
            dbPath,
            baseDir,
            backupsDir,
            logsDir,
            settingsPath,
            adminPath,
            partnerStatusPath,
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

    private static AppConfig LoadAppConfig()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            return new AppConfig();
        }

        try
        {
            var json = File.ReadAllText(path);
            return System.Text.Json.JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    private static DbProvider ResolveDbProvider(AppConfig config)
    {
        var env = Environment.GetEnvironmentVariable("LIGHTWMS_DB_PROVIDER");
        var raw = string.IsNullOrWhiteSpace(env) ? config.DbProvider : env;
        if (string.Equals(raw, "postgres", StringComparison.OrdinalIgnoreCase))
        {
            return DbProvider.Postgres;
        }

        return DbProvider.Sqlite;
    }

    private static string BuildPostgresConnectionString(AppConfig config)
    {
        var host = ReadEnvOrConfig("LIGHTWMS_PG_HOST", config.Postgres?.Host) ?? "127.0.0.1";
        var port = ReadEnvOrConfig("LIGHTWMS_PG_PORT", config.Postgres?.Port) ?? "5432";
        var database = ReadEnvOrConfig("LIGHTWMS_PG_DB", config.Postgres?.Database) ?? "lightwms";
        var user = ReadEnvOrConfig("LIGHTWMS_PG_USER", config.Postgres?.Username) ?? "postgres";
        var password = ReadEnvOrConfig("LIGHTWMS_PG_PASSWORD", config.Postgres?.Password) ?? string.Empty;
        return $"Host={host};Port={port};Database={database};Username={user};Password={password};";
    }

    private static string? ReadEnvOrConfig(string envKey, string? fallback)
    {
        var value = Environment.GetEnvironmentVariable(envKey);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string FormatPostgresTarget(string connectionString)
    {
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
        string? host = null;
        string? port = null;
        string? database = null;
        foreach (var part in parts)
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2)
            {
                continue;
            }

            var key = kv[0].Trim();
            var value = kv[1].Trim();
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                host = value;
            }
            else if (key.Equals("Port", StringComparison.OrdinalIgnoreCase))
            {
                port = value;
            }
            else if (key.Equals("Database", StringComparison.OrdinalIgnoreCase))
            {
                database = value;
            }
        }

        if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(database))
        {
            return $"{host}:{port ?? "5432"}/{database}";
        }

        return "unknown";
    }

    private enum DbProvider
    {
        Sqlite,
        Postgres
    }

    private sealed class AppConfig
    {
        public string? DbProvider { get; set; }
        public PostgresConfig? Postgres { get; set; }
    }

    private sealed class PostgresConfig
    {
        public string? Host { get; set; }
        public string? Port { get; set; }
        public string? Database { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
    }
}
