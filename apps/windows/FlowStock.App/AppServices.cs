using System.IO;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Services;
using FlowStock.Data;

namespace FlowStock.App;

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
    public string ConnectionString { get; }
    public string BaseDir { get; }
    public string BackupsDir { get; }
    public string LogsDir { get; }
    public string SettingsPath { get; }
    public string AdminPath { get; }
    public string AppLogPath { get; }
    public string AdminLogPath { get; }

    private AppServices(
        IDataStore dataStore,
        string connectionString,
        string databaseTarget,
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
        Backups = new BackupService(connectionString, backupsDir, appLogger);
        AdminAuth = new AdminAuthService(adminPath, adminLogger);
        Admin = new AdminService(connectionString, dataStore, Backups, adminLogger);
        PartnerStatuses = new PartnerStatusService(partnerStatusPath);
        DatabasePath = databaseTarget;
        ConnectionString = connectionString;
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

        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(backupsDir);
        Directory.CreateDirectory(logsDir);

        var appLogger = new FileLogger(Path.Combine(logsDir, "app.log"));
        var adminLogger = new FileLogger(Path.Combine(logsDir, "admin.log"));
        var userSettings = new SettingsService(settingsPath).Load();
        var connectionString = BuildPostgresConnectionString(userSettings);
        IDataStore dataStore = new PostgresDataStore(connectionString);
        dataStore.Initialize();
        var target = FormatPostgresTarget(connectionString);
        appLogger.Info($"Database provider: postgres {target}");

        return new AppServices(
            dataStore,
            connectionString,
            target,
            baseDir,
            backupsDir,
            logsDir,
            settingsPath,
            adminPath,
            partnerStatusPath,
            appLogger,
            adminLogger);
    }

    private static string BuildPostgresConnectionString(BackupSettings? userSettings)
    {
        var userPostgres = userSettings?.Postgres;
        var host = ReadEnvOrSettings("FLOWSTOCK_PG_HOST", userPostgres?.Host) ?? "127.0.0.1";
        var port = ReadEnvOrSettings("FLOWSTOCK_PG_PORT", userPostgres?.Port) ?? "15432";
        var database = ReadEnvOrSettings("FLOWSTOCK_PG_DB", userPostgres?.Database) ?? "flowstock";
        var user = ReadEnvOrSettings("FLOWSTOCK_PG_USER", userPostgres?.Username) ?? "postgres";
        var password = ReadEnvOrSettings("FLOWSTOCK_PG_PASSWORD", userPostgres?.Password) ?? string.Empty;
        host = NormalizeHost(host);
        return $"Host={host};Port={port};Database={database};Username={user};Password={password};";
    }

    private static string? ReadEnvOrSettings(string envKey, string? settingsValue)
    {
        var value = Environment.GetEnvironmentVariable(envKey);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (!string.IsNullOrWhiteSpace(settingsValue))
        {
            return settingsValue;
        }

        return null;
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

    private static string NormalizeHost(string host)
    {
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? "127.0.0.1"
            : host;
    }
}

