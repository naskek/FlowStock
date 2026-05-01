using System.Globalization;
using System.IO;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Services;
using FlowStock.Data;
using Npgsql;

namespace FlowStock.App;

public sealed class AppServices
{
    public IDataStore DataStore { get; }
    public CatalogService Catalog { get; }
    public ItemPackagingService Packagings { get; }
    public DocumentService Documents { get; }
    public OrderService Orders { get; }
    public ImportService Import { get; }
    public KmService Km { get; }
    public SettingsService Settings { get; }
    public HuService Hus { get; }
    public BackupService Backups { get; }
    public AdminAuthService AdminAuth { get; }
    public AdminService Admin { get; }
    public PartnerStatusService PartnerStatuses { get; }
    public WpfAdminApiService WpfAdminApi { get; }
    public WpfCatalogApiService WpfCatalogApi { get; }
    public WpfPartnerApiService WpfPartnerApi { get; }
    public WpfHuApiService WpfHuApi { get; }
    public WpfImportApiService WpfImportApi { get; }
    public WpfPackagingApiService WpfPackagingApi { get; }
    public WpfMarkingApiService WpfMarkingApi { get; }
    public WpfReadApiService WpfReadApi { get; }
    public WpfDocumentRuntimeApiService WpfDocumentRuntimeApi { get; }
    public WpfIncomingRequestsApiService WpfIncomingRequestsApi { get; }
    public WpfCreateOrderService WpfCreateOrders { get; }
    public WpfUpdateOrderService WpfUpdateOrders { get; }
    public WpfDeleteOrderService WpfDeleteOrders { get; }
    public WpfSetOrderStatusService WpfSetOrderStatuses { get; }
    public IncomingRequestOrderApiBridgeService IncomingRequestOrderApprovals { get; }
    public WpfCreateDocDraftService WpfCreateDocDrafts { get; }
    public WpfCloseDocumentService WpfCloseDocuments { get; }
    public WpfAddDocLineService WpfAddDocLines { get; }
    public WpfBatchAddDocLineService WpfBatchAddDocLines { get; }
    public WpfUpdateDocLineService WpfUpdateDocLines { get; }
    public WpfDeleteDocLineService WpfDeleteDocLines { get; }
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
    public bool HasDatabaseConfiguration { get; }
    public bool IsDatabaseAvailable { get; }
    public string? DatabaseStartupError { get; }

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
        FileLogger adminLogger,
        bool hasDatabaseConfiguration,
        bool isDatabaseAvailable,
        string? databaseStartupError)
    {
        DataStore = dataStore;
        Catalog = new CatalogService(dataStore);
        Packagings = new ItemPackagingService(dataStore);
        Documents = new DocumentService(dataStore);
        Orders = new OrderService(dataStore);
        Settings = new SettingsService(settingsPath);
        Hus = new HuService(dataStore);
        Import = new ImportService(dataStore);
        Km = new KmService(dataStore);
        Backups = new BackupService(connectionString, backupsDir, appLogger);
        AdminAuth = new AdminAuthService(adminPath, adminLogger);
        Admin = new AdminService(connectionString, dataStore, Backups, adminLogger);
        PartnerStatuses = new PartnerStatusService(partnerStatusPath);
        WpfAdminApi = new WpfAdminApiService(Settings, appLogger);
        WpfCatalogApi = new WpfCatalogApiService(Settings, appLogger);
        WpfPartnerApi = new WpfPartnerApiService(Settings, appLogger);
        WpfHuApi = new WpfHuApiService(Settings, appLogger);
        WpfImportApi = new WpfImportApiService(Settings, appLogger);
        WpfPackagingApi = new WpfPackagingApiService(Settings, appLogger);
        WpfMarkingApi = new WpfMarkingApiService(Settings, appLogger);
        WpfReadApi = new WpfReadApiService(Settings, appLogger);
        WpfDocumentRuntimeApi = new WpfDocumentRuntimeApiService(Settings, appLogger);
        WpfIncomingRequestsApi = new WpfIncomingRequestsApiService(Settings, appLogger);
        WpfCreateOrders = new WpfCreateOrderService(Settings, appLogger);
        WpfUpdateOrders = new WpfUpdateOrderService(Settings, appLogger);
        WpfDeleteOrders = new WpfDeleteOrderService(Settings, appLogger);
        WpfSetOrderStatuses = new WpfSetOrderStatusService(Settings, appLogger);
        IncomingRequestOrderApprovals = new IncomingRequestOrderApiBridgeService(Settings, appLogger, WpfIncomingRequestsApi);
        WpfCreateDocDrafts = new WpfCreateDocDraftService(Settings, appLogger);
        WpfCloseDocuments = new WpfCloseDocumentService(connectionString, Settings, appLogger);
        WpfAddDocLines = new WpfAddDocLineService(connectionString, Settings, appLogger);
        WpfBatchAddDocLines = new WpfBatchAddDocLineService(connectionString, Settings, appLogger);
        WpfUpdateDocLines = new WpfUpdateDocLineService(connectionString, Settings, appLogger);
        WpfDeleteDocLines = new WpfDeleteDocLineService(connectionString, Settings, appLogger);
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
        HasDatabaseConfiguration = hasDatabaseConfiguration;
        IsDatabaseAvailable = isDatabaseAvailable;
        DatabaseStartupError = databaseStartupError;
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
        var connectionConfig = BuildPostgresConnectionConfiguration(userSettings);
        var connectionString = connectionConfig.ConnectionString;
        IDataStore dataStore = new PostgresDataStore(connectionString);
        var target = connectionConfig.Target;
        var isDatabaseAvailable = false;
        string? databaseStartupError = null;

        if (!connectionConfig.IsConfigured)
        {
            appLogger.Info("Database configuration is missing. WPF starts in connection setup mode.");
            databaseStartupError = "Подключение к БД не настроено.";
        }
        else
        {
            try
            {
                dataStore.Initialize();
                isDatabaseAvailable = true;
                appLogger.Info($"Database provider: postgres {target}");
            }
            catch (Exception ex)
            {
                appLogger.Error($"Database init failed for postgres {target}. App starts in connection setup mode.", ex);
                databaseStartupError = DatabaseErrorFormatter.Format(ex);
            }
        }

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
            adminLogger,
            connectionConfig.IsConfigured,
            isDatabaseAvailable,
            databaseStartupError);
    }

    private static PostgresConnectionConfig BuildPostgresConnectionConfiguration(BackupSettings? userSettings)
    {
        var userPostgres = userSettings?.Postgres;
        var host = ReadEnvOrSettings("FLOWSTOCK_PG_HOST", userPostgres?.Host);
        var port = ReadEnvOrSettings("FLOWSTOCK_PG_PORT", userPostgres?.Port);
        var database = ReadEnvOrSettings("FLOWSTOCK_PG_DB", userPostgres?.Database);
        var user = ReadEnvOrSettings("FLOWSTOCK_PG_USER", userPostgres?.Username);
        var password = ReadEnvOrSettings("FLOWSTOCK_PG_PASSWORD", userPostgres?.Password) ?? string.Empty;
        var normalizedHost = string.IsNullOrWhiteSpace(host) ? null : NormalizeHost(host.Trim());
        var normalizedDatabase = NormalizeValue(database);
        var normalizedUser = NormalizeValue(user);
        var normalizedPortText = NormalizeValue(port);
        var hasValidPort = int.TryParse(normalizedPortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort)
                           && parsedPort > 0;
        var isConfigured = !string.IsNullOrWhiteSpace(normalizedHost)
                           && !string.IsNullOrWhiteSpace(normalizedDatabase)
                           && !string.IsNullOrWhiteSpace(normalizedUser)
                           && hasValidPort;

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = normalizedHost ?? string.Empty,
            Database = normalizedDatabase ?? string.Empty,
            Username = normalizedUser ?? string.Empty,
            Password = password
        };

        builder.Port = hasValidPort ? parsedPort : 5432;

        return new PostgresConnectionConfig(
            builder.ConnectionString,
            FormatPostgresTarget(normalizedHost, normalizedPortText, normalizedDatabase),
            isConfigured);
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

    private static string FormatPostgresTarget(string? host, string? port, string? database)
    {
        if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(database))
        {
            return $"{host}:{port ?? "5432"}/{database}";
        }

        return "not configured";
    }

    private static string NormalizeHost(string host)
    {
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? "127.0.0.1"
            : host;
    }

    private static string? NormalizeValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed record PostgresConnectionConfig(string ConnectionString, string Target, bool IsConfigured);
}

