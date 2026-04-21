using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FlowStock.Data;
using Npgsql;
using MediaBrushes = System.Windows.Media.Brushes;

namespace FlowStock.App;

public partial class DbConnectionWindow : Window
{
    private readonly AppServices _services;
    private readonly PostgresDiscoveryService _discoveryService = new();
    private readonly CloseDocumentApiClient _closeDocumentApiClient = new();
    private BackupSettings _settings;
    private readonly List<RecentConnectionOption> _recentOptions = new();
    private readonly List<PostgresDiscoveryCandidate> _discoveredConnections = new();
    private readonly bool _requireConnectionOnStartup;
    private bool _manualConnectionMode;
    private CancellationTokenSource? _discoveryCts;
    private static readonly string[] ServerEnvironmentKeys =
    {
        "FLOWSTOCK_SERVER_BASE_URL",
        "FLOWSTOCK_SERVER_DEVICE_ID",
        "FLOWSTOCK_SERVER_CLOSE_TIMEOUT_SECONDS",
        "FLOWSTOCK_SERVER_ALLOW_INVALID_TLS"
    };
    private static readonly string[] PostgresEnvironmentKeys =
    {
        "FLOWSTOCK_PG_HOST",
        "FLOWSTOCK_PG_PORT",
        "FLOWSTOCK_PG_DB",
        "FLOWSTOCK_PG_USER",
        "FLOWSTOCK_PG_PASSWORD"
    };
    private const string LoopbackHost = "127.0.0.1";

    public DbConnectionWindow(AppServices services, bool requireConnectionOnStartup = false)
    {
        _services = services;
        _settings = _services.Settings.Load();
        _requireConnectionOnStartup = requireConnectionOnStartup;

        InitializeComponent();

        var effective = GetEffectiveConfig();
        CurrentTargetText.Text = effective.IsConfigured
            ? $"{effective.Host}:{effective.Port}/{effective.Database} ({effective.Username})"
            : "не настроено";
        RefreshDatabaseOverrideStatus(effective);
        StartupModeText.Visibility = _requireConnectionOnStartup ? Visibility.Visible : Visibility.Collapsed;
        if (_requireConnectionOnStartup)
        {
            StartupModeText.Text = _services.DatabaseStartupError ?? (_services.HasDatabaseConfiguration
                ? "Не удалось подключиться к PostgreSQL. Выберите найденную БД или перейдите в режим ручного подключения."
                : "Подключение к PostgreSQL ещё не настроено. Выберите найденную БД или перейдите в режим ручного подключения.");
        }

        var postgres = _settings.Postgres ?? new PostgresSettings();
        HostBox.Text = NormalizeHost(postgres.Host ?? effective.Host ?? string.Empty);
        PortBox.Text = postgres.Port ?? effective.Port ?? string.Empty;
        DatabaseBox.Text = postgres.Database ?? effective.Database ?? string.Empty;
        UsernameBox.Text = postgres.Username ?? effective.Username ?? string.Empty;
        PasswordBox.Password = postgres.Password ?? string.Empty;

        LoadRecentConnections();
        LoadServerSettingsUi();
        SetManualConnectionMode(false);
        Loaded += DbConnectionWindow_Loaded;
        Closed += DbConnectionWindow_Closed;
    }

    private async void DbConnectionWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await DiscoverConnectionsAsync();
    }

    private void DbConnectionWindow_Closed(object? sender, EventArgs e)
    {
        _discoveryCts?.Cancel();
        _discoveryCts?.Dispose();
        _discoveryCts = null;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadInput(out var input))
        {
            return;
        }

        if (!TryRunConnectionTest(input, out var diagnostics))
        {
            return;
        }

        if (HasBlockingPostgresEnvironmentOverride(input, out var overrideMessage))
        {
            MessageBox.Show(
                overrideMessage,
                "Подключение к БД",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!ConfirmApiTargetMatchesDatabase(input))
        {
            return;
        }

        _settings.Postgres ??= new PostgresSettings();
        _settings.Postgres.Host = input.Host;
        _settings.Postgres.Port = input.Port.ToString(CultureInfo.InvariantCulture);
        _settings.Postgres.Database = input.Database;
        _settings.Postgres.Username = input.Username;
        _settings.Postgres.Password = string.IsNullOrWhiteSpace(input.Password) ? null : input.Password;

        _services.Settings.Save(_settings);

        MessageBox.Show(
            BuildSuccessMessage(diagnostics)
            + $"{Environment.NewLine}{Environment.NewLine}Настройки сохранены. Приложение будет перезапущено.",
            "Подключение к БД",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        RestartApplication();
    }

    private void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadInput(out var input))
        {
            return;
        }

        if (!TryRunConnectionTest(input, out var diagnostics))
        {
            return;
        }

            MessageBox.Show(BuildSuccessMessage(diagnostics),
            "Подключение к БД",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void ApplyMigrations_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadInput(out var input))
        {
            return;
        }

        var confirm = MessageBox.Show(
            "Будут применены недостающие SQL-миграции FlowStock к выбранной базе.\n"
            + "Существующие данные не удаляются, но перед production-миграцией рекомендуется сделать backup.\n\n"
            + "Продолжить?",
            "Инициализация / миграции БД",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        if (!TryBuildConnectionString(input, out var connectionString))
        {
            return;
        }

        ApplyMigrationsButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => ApplySchemaMigrations(connectionString));
            ValidateFlowStockSchema(connectionString);
            AddRecentConnection(input);

            var summary = new StringBuilder();
            summary.AppendLine("Миграции применены успешно.");
            summary.AppendLine($"Каталог миграций: {result.MigrationsDirectory}");
            summary.AppendLine($"Применено: {result.AppliedVersions.Count}");
            summary.AppendLine($"Пропущено (уже в БД): {result.SkippedVersions.Count}");
            if (result.AppliedVersions.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine("Новые версии:");
                summary.AppendLine(string.Join(", ", result.AppliedVersions));
            }

            summary.AppendLine();
            summary.AppendLine("Схема FlowStock: OK.");
            summary.AppendLine("Перезапустите WPF/Server, чтобы подхватить изменения.");

            MessageBox.Show(
                summary.ToString(),
                "Инициализация / миграции БД",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("db_migration_failed", ex);
            ShowConnectionError($"Не удалось применить миграции: {ex.Message}", ex.ToString());
        }
        finally
        {
            ApplyMigrationsButton.IsEnabled = true;
        }
    }

    private async void CheckSchemaVersion_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadInput(out var input))
        {
            return;
        }

        if (!TryBuildConnectionString(input, out var connectionString))
        {
            return;
        }

        CheckSchemaVersionButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => CheckSchemaVersion(connectionString));

            var summary = new StringBuilder();
            summary.AppendLine("Проверка версии схемы завершена.");
            summary.AppendLine(result.SchemaMigrationsExists
                ? "Таблица schema_migrations: есть"
                : "Таблица schema_migrations: отсутствует");
            summary.AppendLine($"Применено версий: {result.AppliedVersions.Count}");

            if (!string.IsNullOrWhiteSpace(result.MigrationsDirectory))
            {
                summary.AppendLine($"Каталог миграций: {result.MigrationsDirectory}");
                summary.AppendLine($"Доступно файлов миграций: {result.AvailableVersions.Count}");
                summary.AppendLine($"Ожидают применения: {result.PendingVersions.Count}");
            }
            else
            {
                summary.AppendLine("Каталог миграций не найден рядом с репозиторием.");
            }

            if (result.AppliedVersions.Count > 0)
            {
                summary.AppendLine($"Последняя примененная: {result.AppliedVersions[^1]}");
            }

            if (result.AvailableVersions.Count > 0)
            {
                summary.AppendLine($"Последняя доступная: {result.AvailableVersions[^1]}");
            }

            if (result.PendingVersions.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine("Не применены:");
                summary.AppendLine(string.Join(", ", result.PendingVersions));
            }

            if (result.UnknownAppliedVersions.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine("В БД есть версии, которых нет в папке миграций:");
                summary.AppendLine(string.Join(", ", result.UnknownAppliedVersions));
            }

            if (!result.IsFlowStockSchemaValid && !string.IsNullOrWhiteSpace(result.ValidationError))
            {
                summary.AppendLine();
                summary.AppendLine("Проверка целостности схемы:");
                summary.AppendLine(result.ValidationError);
            }
            else
            {
                summary.AppendLine();
                summary.AppendLine("Проверка целостности схемы: OK");
            }

            MessageBox.Show(
                summary.ToString(),
                "Проверка схемы БД",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("db_schema_version_check_failed", ex);
            ShowConnectionError($"Не удалось проверить схему: {ex.Message}", ex.ToString());
        }
        finally
        {
            CheckSchemaVersionButton.IsEnabled = true;
        }
    }

    private async void ScanConnections_Click(object sender, RoutedEventArgs e)
    {
        await DiscoverConnectionsAsync();
    }

    private void DiscoveredConnectionsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DiscoveredConnectionsCombo.SelectedItem is not PostgresDiscoveryCandidate candidate)
        {
            return;
        }

        ApplyDiscoveredCandidate(candidate);
        if (!_manualConnectionMode)
        {
            SetDiscoveryStatus($"Выбран найденный адрес {candidate.Host}:{candidate.Port}. Database / Username / Password задаются вручную.", MediaBrushes.DarkGreen);
        }
    }

    private void ManualModeButton_Click(object sender, RoutedEventArgs e)
    {
        SetManualConnectionMode(true);
    }

    private void AutoModeButton_Click(object sender, RoutedEventArgs e)
    {
        SetManualConnectionMode(false);
        if (DiscoveredConnectionsCombo.SelectedItem is PostgresDiscoveryCandidate candidate)
        {
            ApplyDiscoveredCandidate(candidate);
        }
    }

    private void LoadServerSettingsUi()
    {
        var server = (_settings.Server ?? new ServerSettings()).Normalize();
        ApplyServerSettingsToInputs(new ServerSettings
        {
            ServerBaseUrl = server.GetServerBaseUrlOrDefault(),
            PcClientUrl = server.GetPcClientUrlOrDefault(),
            TsdClientUrl = server.GetTsdClientUrlOrDefault(),
            DeviceId = server.DeviceId ?? WpfCloseDocumentService.BuildDefaultDeviceId(),
            CloseTimeoutSeconds = server.CloseTimeoutSeconds < 1
                ? WpfCloseDocumentService.DefaultCloseTimeoutSeconds
                : server.CloseTimeoutSeconds,
            AllowInvalidTls = server.AllowInvalidTls
        });
        RefreshServerStatus();
        SetServerStatus(string.Empty, MediaBrushes.Gray);
    }

    private void ApplyServerSettingsToInputs(ServerSettings server)
    {
        ServerBaseUrlBox.Text = server.GetServerBaseUrlOrDefault();
        PcClientUrlBox.Text = server.GetPcClientUrlOrDefault();
        TsdClientUrlBox.Text = server.GetTsdClientUrlOrDefault();
        ServerDeviceIdBox.Text = server.DeviceId ?? WpfCloseDocumentService.BuildDefaultDeviceId();
        ServerTimeoutBox.Text = server.CloseTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        AllowInvalidTlsCheckBox.IsChecked = server.AllowInvalidTls;
    }

    private void RefreshServerStatus()
    {
        ApiWriteModeText.Text = "API-only write-path: заказы, подтверждение входящих веб-заявок, создание/проведение документов и строки документов.";
        var effective = _services.WpfCloseDocuments.GetEffectiveConfiguration();
        ApiWriteCoverageText.Text =
            $"Effective DB: {_services.DatabasePath} | Effective API target: {effective.BaseUrl} | device: {effective.DeviceId} | timeout: {effective.CloseTimeoutSeconds}s | TLS override: {(effective.AllowInvalidTls ? "dev-only enabled" : "strict")}";
        ConfigLoadedText.Text = $"Config loaded: {(File.Exists(_services.SettingsPath) ? "yes" : "no")}";

        var overrides = GetServerEnvironmentOverrides();
        if (overrides.Count == 0)
        {
            EnvironmentOverrideText.Visibility = Visibility.Collapsed;
            EnvironmentOverrideText.Text = string.Empty;
            return;
        }

        EnvironmentOverrideText.Text =
            $"Environment override active: {string.Join(", ", overrides)}. Active server paths may differ from saved settings.";
        EnvironmentOverrideText.Visibility = Visibility.Visible;
    }

    private async void CheckServer_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadServerSettingsInput(out var serverSettings))
        {
            return;
        }

        SetServerStatus("Checking server...", MediaBrushes.DimGray);

        var result = await _closeDocumentApiClient.PingAsync(
            new ServerCloseClientOptions
            {
                BaseUrl = serverSettings.GetServerBaseUrlOrDefault(),
                AllowInvalidTls = serverSettings.AllowInvalidTls
            },
            serverSettings.CloseTimeoutSeconds);

        var brush = result.IsSuccess ? MediaBrushes.DarkGreen : MediaBrushes.DarkRed;
        SetServerStatus(result.Message, brush);

        var caption = result.IsSuccess ? "Check server" : "Check server failed";
        var icon = result.IsSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning;
        MessageBox.Show(result.Message, caption, MessageBoxButton.OK, icon);
    }

    private void SaveServerSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadServerSettingsInput(out var serverSettings))
        {
            return;
        }

        _settings.Server = serverSettings;
        _services.Settings.Save(_settings);
        ApplyServerSettingsToInputs(serverSettings);
        RefreshServerStatus();

        const string message = "Settings saved to %APPDATA%\\FlowStock\\settings.json";
        SetServerStatus(message, MediaBrushes.DarkGreen);
        MessageBox.Show(message, "Server API settings", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private bool TryReadServerSettingsInput(out ServerSettings serverSettings)
    {
        serverSettings = new ServerSettings();

        var serverBaseUrlInput = ServerBaseUrlBox.Text?.Trim() ?? string.Empty;
        var pcClientUrlInput = PcClientUrlBox.Text?.Trim() ?? string.Empty;
        var tsdClientUrlInput = TsdClientUrlBox.Text?.Trim() ?? string.Empty;
        var deviceId = ServerDeviceIdBox.Text?.Trim();
        var timeoutText = ServerTimeoutBox.Text?.Trim() ?? string.Empty;

        if (!TryNormalizeRootUrl(serverBaseUrlInput, Uri.UriSchemeHttps, "Адрес сервера", out var serverBaseUrl))
        {
            return false;
        }

        if (!TryNormalizeRootUrl(pcClientUrlInput, Uri.UriSchemeHttps, "Адрес ПК-интерфейса", out var pcClientUrl))
        {
            return false;
        }

        if (!TryNormalizeRootUrl(tsdClientUrlInput, Uri.UriSchemeHttp, "Адрес ТСД-интерфейса", out var tsdClientUrl))
        {
            return false;
        }

        if (!int.TryParse(timeoutText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeoutSeconds) || timeoutSeconds <= 0)
        {
            MessageBox.Show("Timeout (sec) must be a positive integer.", "Server API settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        serverSettings = new ServerSettings
        {
            ServerBaseUrl = serverBaseUrl,
            PcClientUrl = pcClientUrl,
            TsdClientUrl = tsdClientUrl,
            DeviceId = string.IsNullOrWhiteSpace(deviceId) ? WpfCloseDocumentService.BuildDefaultDeviceId() : deviceId,
            CloseTimeoutSeconds = timeoutSeconds,
            AllowInvalidTls = AllowInvalidTlsCheckBox.IsChecked == true
        }.Normalize();

        return true;
    }

    private void OpenPcClient_Click(object sender, RoutedEventArgs e)
    {
        OpenConfiguredUrl(PcClientUrlBox.Text, Uri.UriSchemeHttps, "PC URL");
    }

    private void OpenTsdClient_Click(object sender, RoutedEventArgs e)
    {
        OpenConfiguredUrl(TsdClientUrlBox.Text, Uri.UriSchemeHttp, "TSD URL");
    }

    private void SetServerStatus(string message, System.Windows.Media.Brush brush)
    {
        ServerStatusText.Text = message;
        ServerStatusText.Foreground = brush;
    }

    private async Task DiscoverConnectionsAsync()
    {
        _discoveryCts?.Cancel();
        _discoveryCts?.Dispose();
        _discoveryCts = new CancellationTokenSource();

        ScanConnectionsButton.IsEnabled = false;
        SetDiscoveryStatus("Идет поиск PostgreSQL на этом ПК и в локальной сети...", MediaBrushes.DimGray);

        try
        {
            var currentHost = NormalizeHost(HostBox.Text?.Trim() ?? string.Empty);
            var currentPort = PortBox.Text?.Trim() ?? string.Empty;
            var hasCurrentTarget = !string.IsNullOrWhiteSpace(currentHost) && !string.IsNullOrWhiteSpace(currentPort);
            var discovered = await _discoveryService.DiscoverAsync(_discoveryCts.Token);

            _discoveredConnections.Clear();
            _discoveredConnections.AddRange(discovered);
            DiscoveredConnectionsCombo.ItemsSource = null;
            DiscoveredConnectionsCombo.ItemsSource = _discoveredConnections;

            if (_discoveredConnections.Count == 0)
            {
                DiscoveredConnectionsCombo.SelectedItem = null;
                SetDiscoveryStatus(
                    "Автопоиск не нашёл открытый PostgreSQL на этом ПК или в локальной сети. Переключитесь в режим ручного подключения.",
                    MediaBrushes.DarkOrange);
                return;
            }

            var selected = _discoveredConnections.FirstOrDefault(candidate =>
                string.Equals(candidate.Host, currentHost, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Port.ToString(CultureInfo.InvariantCulture), currentPort, StringComparison.OrdinalIgnoreCase))
                ?? _discoveredConnections[0];

            DiscoveredConnectionsCombo.SelectedItem = selected;
            if (!hasCurrentTarget && !_manualConnectionMode)
            {
                ApplyDiscoveredCandidate(selected);
            }
            else if (!string.Equals(selected.Host, currentHost, StringComparison.OrdinalIgnoreCase)
                     || !string.Equals(selected.Port.ToString(CultureInfo.InvariantCulture), currentPort, StringComparison.OrdinalIgnoreCase))
            {
                SetManualConnectionMode(true);
                SetDiscoveryStatus(
                    $"Автопоиск нашёл {_discoveredConnections.Count} кандидатов, но текущее подключение {currentHost}:{currentPort} среди них не найдено. Оставлен ручной режим.",
                    MediaBrushes.DarkOrange);
                return;
            }
            else if (!_manualConnectionMode)
            {
                ApplyDiscoveredCandidate(selected);
            }

            SetDiscoveryStatus(
                $"Найдено подключений: {_discoveredConnections.Count}. По умолчанию используется автопоиск host/port, остальные поля задаются вручную.",
                MediaBrushes.DarkGreen);
        }
        catch (OperationCanceledException)
        {
            SetDiscoveryStatus("Автопоиск отменен.", MediaBrushes.Gray);
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("postgres_discovery_failed", ex);
            SetDiscoveryStatus($"Автопоиск завершился с ошибкой: {ex.Message}", MediaBrushes.DarkRed);
        }
        finally
        {
            ScanConnectionsButton.IsEnabled = true;
        }
    }

    private void ApplyDiscoveredCandidate(PostgresDiscoveryCandidate candidate)
    {
        HostBox.Text = candidate.Host;
        PortBox.Text = candidate.Port.ToString(CultureInfo.InvariantCulture);
    }

    private void SetManualConnectionMode(bool isManual)
    {
        _manualConnectionMode = isManual;
        HostBox.IsEnabled = isManual;
        PortBox.IsEnabled = isManual;
        ManualModeButton.Visibility = isManual ? Visibility.Collapsed : Visibility.Visible;
        AutoModeButton.Visibility = isManual ? Visibility.Visible : Visibility.Collapsed;

        if (!_manualConnectionMode && DiscoveredConnectionsCombo.SelectedItem is PostgresDiscoveryCandidate candidate)
        {
            ApplyDiscoveredCandidate(candidate);
        }
    }

    private void SetDiscoveryStatus(string message, System.Windows.Media.Brush brush)
    {
        DiscoveryStatusText.Text = message;
        DiscoveryStatusText.Foreground = brush;
    }

    private bool TryReadInput(out ConnectionInput input)
    {
        input = new ConnectionInput(string.Empty, 0, string.Empty, string.Empty, string.Empty);
        var host = HostBox.Text?.Trim() ?? string.Empty;
        var port = PortBox.Text?.Trim() ?? string.Empty;
        var database = DatabaseBox.Text?.Trim() ?? string.Empty;
        var username = UsernameBox.Text?.Trim() ?? string.Empty;
        var password = PasswordBox.Password?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(host)
            || string.IsNullOrWhiteSpace(port)
            || string.IsNullOrWhiteSpace(database)
            || string.IsNullOrWhiteSpace(username))
        {
            MessageBox.Show("Заполните host, port, database и username.", "Подключение к БД", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (!int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort) || parsedPort <= 0)
        {
            MessageBox.Show("Port должен быть целым числом.", "Подключение к БД", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        input = new ConnectionInput(NormalizeHost(host), parsedPort, database, username, password);
        return true;
    }

    private static bool TryBuildConnectionString(ConnectionInput input, out string connectionString)
    {
        connectionString = string.Empty;
        try
        {
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = NormalizeHost(input.Host),
                Port = input.Port,
                Database = input.Database,
                Username = input.Username,
                Password = input.Password,
                Timeout = 10
            };
            connectionString = builder.ConnectionString;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static MigrationApplyResult ApplySchemaMigrations(string connectionString)
    {
        if (!TryResolveMigrationsDirectory(out var migrationsDirectory))
        {
            throw new InvalidOperationException(
                "Каталог миграций не найден. Ожидался путь deploy/postgres/migrations рядом с репозиторием FlowStock.");
        }

        var migrationFiles = Directory.GetFiles(migrationsDirectory, "V*.sql", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (migrationFiles.Count == 0)
        {
            throw new InvalidOperationException($"В каталоге {migrationsDirectory} не найдено файлов миграций V*.sql.");
        }

        var appliedVersions = new List<string>();
        var skippedVersions = new List<string>();

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using (var bootstrap = connection.CreateCommand())
        {
            bootstrap.CommandText = @"
CREATE TABLE IF NOT EXISTS schema_migrations (
    version TEXT PRIMARY KEY,
    filename TEXT NOT NULL UNIQUE,
    applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);";
            bootstrap.ExecuteNonQuery();
        }

        foreach (var migrationPath in migrationFiles)
        {
            var filename = Path.GetFileName(migrationPath);
            var version = GetMigrationVersion(filename);

            using (var exists = connection.CreateCommand())
            {
                exists.CommandText = "SELECT 1 FROM schema_migrations WHERE version = @version LIMIT 1;";
                exists.Parameters.AddWithValue("@version", version);
                var alreadyApplied = exists.ExecuteScalar() != null;
                if (alreadyApplied)
                {
                    skippedVersions.Add(version);
                    continue;
                }
            }

            var sql = File.ReadAllText(migrationPath);
            using var apply = connection.CreateCommand();
            apply.CommandText = $@"
BEGIN;
{sql}
INSERT INTO schema_migrations(version, filename, applied_at)
VALUES (@version, @filename, NOW());
COMMIT;";
            apply.Parameters.AddWithValue("@version", version);
            apply.Parameters.AddWithValue("@filename", filename);
            apply.ExecuteNonQuery();
            appliedVersions.Add(version);
        }

        return new MigrationApplyResult(migrationsDirectory, appliedVersions, skippedVersions);
    }

    private static SchemaVersionCheckResult CheckSchemaVersion(string connectionString)
    {
        var availableVersions = new List<string>();
        var appliedVersions = new List<string>();
        var pendingVersions = new List<string>();
        var unknownAppliedVersions = new List<string>();

        var hasMigrationsDirectory = TryResolveMigrationsDirectory(out var migrationsDirectory);
        if (!hasMigrationsDirectory)
        {
            migrationsDirectory = string.Empty;
        }
        else
        {
            availableVersions = Directory.GetFiles(migrationsDirectory, "V*.sql", SearchOption.TopDirectoryOnly)
                .Select(path => GetMigrationVersion(Path.GetFileName(path)))
                .OrderBy(version => version, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var schemaMigrationsExists = false;
        string? validationError = null;
        var isFlowStockSchemaValid = false;

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        schemaMigrationsExists = TableExistsInConnection(connection, "schema_migrations");
        if (schemaMigrationsExists)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT version FROM schema_migrations ORDER BY version;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0))
                {
                    appliedVersions.Add(reader.GetString(0));
                }
            }
        }

        if (availableVersions.Count > 0)
        {
            pendingVersions = availableVersions
                .Where(version => !appliedVersions.Contains(version, StringComparer.OrdinalIgnoreCase))
                .ToList();
            unknownAppliedVersions = appliedVersions
                .Where(version => !availableVersions.Contains(version, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        try
        {
            ValidateFlowStockSchema(connectionString);
            isFlowStockSchemaValid = true;
        }
        catch (Exception ex)
        {
            validationError = ex.Message;
        }

        return new SchemaVersionCheckResult(
            migrationsDirectory,
            schemaMigrationsExists,
            availableVersions,
            appliedVersions,
            pendingVersions,
            unknownAppliedVersions,
            isFlowStockSchemaValid,
            validationError);
    }

    private static string GetMigrationVersion(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            throw new InvalidOperationException("Пустое имя файла миграции.");
        }

        var markerIndex = filename.IndexOf("__", StringComparison.Ordinal);
        if (markerIndex <= 0)
        {
            throw new InvalidOperationException($"Некорректное имя файла миграции: {filename}");
        }

        return filename.Substring(0, markerIndex);
    }

    private static bool TryResolveMigrationsDirectory(out string migrationsDirectory)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory))
        {
            roots.Add(AppContext.BaseDirectory);
        }

        if (!string.IsNullOrWhiteSpace(Environment.CurrentDirectory))
        {
            roots.Add(Environment.CurrentDirectory);
        }

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var directory = new DirectoryInfo(root);
            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName, "deploy", "postgres", "migrations");
                if (Directory.Exists(candidate))
                {
                    migrationsDirectory = candidate;
                    return true;
                }

                directory = directory.Parent;
            }
        }

        migrationsDirectory = string.Empty;
        return false;
    }

    private static bool TableExistsInConnection(NpgsqlConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT 1
FROM information_schema.tables
WHERE table_schema = current_schema()
  AND table_name = @table_name
LIMIT 1;";
        command.Parameters.AddWithValue("@table_name", tableName.ToLowerInvariant());
        return command.ExecuteScalar() != null;
    }

    private sealed record ConnectionInput(string Host, int Port, string Database, string Username, string Password);

    private sealed record MigrationApplyResult(
        string MigrationsDirectory,
        IReadOnlyList<string> AppliedVersions,
        IReadOnlyList<string> SkippedVersions);

    private sealed record SchemaVersionCheckResult(
        string MigrationsDirectory,
        bool SchemaMigrationsExists,
        IReadOnlyList<string> AvailableVersions,
        IReadOnlyList<string> AppliedVersions,
        IReadOnlyList<string> PendingVersions,
        IReadOnlyList<string> UnknownAppliedVersions,
        bool IsFlowStockSchemaValid,
        string? ValidationError);

    private static string BuildPostgresSummary(PostgresException ex, ConnectionInput input)
    {
        var target = $"{input.Host}:{input.Port}/{input.Database}";
        var code = string.IsNullOrWhiteSpace(ex.SqlState) ? "unknown" : ex.SqlState;
        var hint = code switch
        {
            PostgresErrorCodes.InvalidPassword => "Неверный логин или пароль.",
            PostgresErrorCodes.InvalidCatalogName => "База данных не найдена.",
            PostgresErrorCodes.InvalidAuthorizationSpecification => "Недостаточно прав для подключения.",
            PostgresErrorCodes.CannotConnectNow => "Сервер не принимает соединения.",
            PostgresErrorCodes.ConnectionException => "Проблема сетевого соединения.",
            PostgresErrorCodes.ConnectionDoesNotExist => "Соединение разорвано.",
            PostgresErrorCodes.ConnectionFailure => "Не удалось установить соединение.",
            _ => "Ошибка подключения."
        };

        return $"Ошибка подключения ({code}). {hint} Host: {target}, user: {input.Username}.";
    }

    private bool TryRunConnectionTest(ConnectionInput input, out ConnectionDiagnostics diagnostics)
    {
        diagnostics = new ConnectionDiagnostics("unknown", "unknown", "unknown", "unknown", "unknown");

        try
        {
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = NormalizeHost(input.Host),
                Port = input.Port,
                Database = input.Database,
                Username = input.Username,
                Password = input.Password,
                Timeout = 5
            };
            using var connection = new NpgsqlConnection(builder.ConnectionString);
            connection.Open();
            ValidateFlowStockSchema(builder.ConnectionString);
            using var command = new NpgsqlCommand(
                "select current_database(), current_user, inet_server_addr(), inet_server_port(), version();",
                connection);
            command.CommandTimeout = 5;
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException("Пустой ответ от сервера.");
            }

            diagnostics = ReadDiagnostics(reader);
            _services.AppLogger.Info(
                $"db_connection_test ok db={diagnostics.Database} user={diagnostics.User} server_addr={diagnostics.ServerAddr} server_port={diagnostics.ServerPort} version={diagnostics.Version}");
            AddRecentConnection(input);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            _services.AppLogger.Error("db_connection_test failed", ex);
            ShowConnectionError(DatabaseErrorFormatter.Format(ex), ex.ToString());
            return false;
        }
        catch (PostgresException ex)
        {
            _services.AppLogger.Error("db_connection_test failed", ex);
            ShowConnectionError(BuildPostgresSummary(ex, input), ex.ToString());
            return false;
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("db_connection_test failed", ex);
            ShowConnectionError($"Ошибка подключения: {ex.Message}", ex.ToString());
            return false;
        }
    }

    private static ConnectionDiagnostics ReadDiagnostics(NpgsqlDataReader reader)
    {
        var database = reader.IsDBNull(0) ? "unknown" : reader.GetString(0);
        var user = reader.IsDBNull(1) ? "unknown" : reader.GetString(1);
        var serverAddr = reader.IsDBNull(2) ? "unknown" : reader.GetFieldValue<IPAddress>(2).ToString();
        var serverPort = reader.IsDBNull(3) ? "unknown" : reader.GetInt32(3).ToString(CultureInfo.InvariantCulture);
        var version = reader.IsDBNull(4) ? "unknown" : reader.GetString(4);
        return new ConnectionDiagnostics(database, user, serverAddr, serverPort, version);
    }

    private static string BuildSuccessMessage(ConnectionDiagnostics diagnostics)
    {
        return $"Подключение успешно.{Environment.NewLine}" +
               $"База: {diagnostics.Database}{Environment.NewLine}" +
               $"Пользователь: {diagnostics.User}{Environment.NewLine}" +
               $"Сервер: {diagnostics.ServerAddr}:{diagnostics.ServerPort}{Environment.NewLine}" +
               $"Версия: {diagnostics.Version}{Environment.NewLine}" +
               $"Схема FlowStock: OK";
    }

    private void ShowConnectionError(string summary, string details)
    {
        var window = new ErrorTextWindow("Подключение к БД", $"{summary}{Environment.NewLine}{Environment.NewLine}{details}")
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private EffectiveConfig GetEffectiveConfig()
    {
        var settings = _settings.Postgres ?? new PostgresSettings();
        var host = ReadEnvOrSettings("FLOWSTOCK_PG_HOST", settings.Host);
        var port = ReadEnvOrSettings("FLOWSTOCK_PG_PORT", settings.Port);
        var database = ReadEnvOrSettings("FLOWSTOCK_PG_DB", settings.Database);
        var user = ReadEnvOrSettings("FLOWSTOCK_PG_USER", settings.Username);
        var normalizedHost = string.IsNullOrWhiteSpace(host) ? null : NormalizeHost(host);
        var isConfigured = !string.IsNullOrWhiteSpace(normalizedHost)
                           && !string.IsNullOrWhiteSpace(port)
                           && !string.IsNullOrWhiteSpace(database)
                           && !string.IsNullOrWhiteSpace(user);
        return new EffectiveConfig(normalizedHost, port, database, user, isConfigured);
    }

    private void RefreshDatabaseOverrideStatus(EffectiveConfig effective)
    {
        var overrides = GetPostgresEnvironmentOverrides();
        if (overrides.Count == 0)
        {
            DatabaseEnvironmentOverrideText.Visibility = Visibility.Collapsed;
            DatabaseEnvironmentOverrideText.Text = string.Empty;
            return;
        }

        var target = effective.IsConfigured
            ? $"{effective.Host}:{effective.Port}/{effective.Database} ({effective.Username})"
            : "не настроено";
        DatabaseEnvironmentOverrideText.Text =
            $"Environment override active: {string.Join(", ", overrides)}. Активное подключение к БД берется из переменных окружения, а не из settings.json. Effective DB: {target}.";
        DatabaseEnvironmentOverrideText.Visibility = Visibility.Visible;
    }

    private bool HasBlockingPostgresEnvironmentOverride(ConnectionInput input, out string message)
    {
        message = string.Empty;
        var overrides = GetPostgresEnvironmentOverrides();
        if (overrides.Count == 0)
        {
            return false;
        }

        var effective = GetEffectiveConfig();
        var sameTarget =
            string.Equals(NormalizeHost(effective.Host ?? string.Empty), NormalizeHost(input.Host), StringComparison.OrdinalIgnoreCase)
            && string.Equals(effective.Port ?? string.Empty, input.Port.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            && string.Equals(effective.Database ?? string.Empty, input.Database, StringComparison.OrdinalIgnoreCase)
            && string.Equals(effective.Username ?? string.Empty, input.Username, StringComparison.OrdinalIgnoreCase);

        var envPassword = Environment.GetEnvironmentVariable("FLOWSTOCK_PG_PASSWORD");
        var samePassword = string.IsNullOrWhiteSpace(envPassword) || string.Equals(envPassword, input.Password, StringComparison.Ordinal);

        if (sameTarget && samePassword)
        {
            return false;
        }

        message =
            "Переключение БД не будет применено, потому что активны переменные окружения PostgreSQL: "
            + string.Join(", ", overrides)
            + Environment.NewLine
            + "Они имеют приоритет над %APPDATA%\\FlowStock\\settings.json. Уберите эти переменные из процесса/ярлыка/терминала и запустите WPF заново.";
        return true;
    }

    private bool ConfirmApiTargetMatchesDatabase(ConnectionInput input)
    {
        var rawServerUrl = ServerBaseUrlBox.Text?.Trim();
        if (!FlowStockUrlHelper.TryNormalizeRootUrl(rawServerUrl, Uri.UriSchemeHttps, out var normalizedServerUrl, out _)
            || !Uri.TryCreate(normalizedServerUrl, UriKind.Absolute, out var serverUri))
        {
            return true;
        }

        if (AreEquivalentHosts(input.Host, serverUri.Host))
        {
            return true;
        }

        var result = MessageBox.Show(
            $"БД выбрана: {NormalizeHost(input.Host)}:{input.Port}/{input.Database}{Environment.NewLine}"
            + $"Server API URL: {normalizedServerUrl}{Environment.NewLine}{Environment.NewLine}"
            + "Часть WPF уже работает через Server API. Эти операции записи пойдут в указанный Server API, а не напрямую в выбранную БД. "
            + "Если это разные стенды, данные будут расходиться. Продолжить?",
            "Проверка стенда FlowStock",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return result == MessageBoxResult.Yes;
    }

    private static string? ReadEnvOrSettings(string envKey, string? settingsValue)
    {
        var value = Environment.GetEnvironmentVariable(envKey);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        return string.IsNullOrWhiteSpace(settingsValue) ? null : settingsValue.Trim();
    }

    private static string NormalizeHost(string host)
    {
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? LoopbackHost
            : host;
    }

    private static void ValidateFlowStockSchema(string connectionString)
    {
        var store = new PostgresDataStore(connectionString);
        store.Initialize();
    }

    private void OpenConfiguredUrl(string? rawUrl, string defaultScheme, string fieldName)
    {
        if (!TryNormalizeRootUrl(rawUrl, defaultScheme, fieldName, out var normalized))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = normalized,
            UseShellExecute = true
        });
    }

    private static bool TryNormalizeRootUrl(string? rawUrl, string defaultScheme, string fieldName, out string normalized)
    {
        if (FlowStockUrlHelper.TryNormalizeRootUrl(rawUrl, defaultScheme, out normalized, out var error))
        {
            return true;
        }

        MessageBox.Show($"{fieldName}: {error}", "Server API settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private static List<string> GetServerEnvironmentOverrides()
    {
        var overrides = new List<string>();
        foreach (var key in ServerEnvironmentKeys)
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
            {
                overrides.Add(key);
            }
        }

        return overrides;
    }

    private static List<string> GetPostgresEnvironmentOverrides()
    {
        var overrides = new List<string>();
        foreach (var key in PostgresEnvironmentKeys)
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
            {
                overrides.Add(key);
            }
        }

        return overrides;
    }

    private static bool AreEquivalentHosts(string left, string right)
    {
        var normalizedLeft = NormalizeHost(left.Trim());
        var normalizedRight = NormalizeHost(right.Trim());

        if (IsLoopback(normalizedLeft) && IsLoopback(normalizedRight))
        {
            return true;
        }

        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLoopback(string host)
    {
        return string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record EffectiveConfig(string? Host, string? Port, string? Database, string? Username, bool IsConfigured);

    private sealed record ConnectionDiagnostics(string Database, string User, string ServerAddr, string ServerPort, string Version);

    private sealed record RecentConnectionOption(string Display, PostgresConnectionProfile Profile);

    private static void RestartApplication()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            exePath = Process.GetCurrentProcess().MainModule?.FileName;
        }

        if (string.IsNullOrWhiteSpace(exePath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = true
        });
        System.Windows.Application.Current.Shutdown();
    }

    private void LoadRecentConnections()
    {
        _recentOptions.Clear();
        var recent = _settings.RecentPostgres ?? new List<PostgresConnectionProfile>();
        foreach (var entry in recent)
        {
            if (entry == null)
            {
                continue;
            }

            var host = NormalizeHost(entry.Host ?? string.Empty);
            var port = entry.Port ?? string.Empty;
            var database = entry.Database ?? string.Empty;
            var username = entry.Username ?? string.Empty;
            var display = $"{host}:{port}/{database} ({username})";
            _recentOptions.Add(new RecentConnectionOption(display, new PostgresConnectionProfile
            {
                Host = host,
                Port = port,
                Database = database,
                Username = username,
                Password = entry.Password
            }));
        }

        RecentConnectionsCombo.ItemsSource = _recentOptions;
    }

    private void RecentConnectionsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecentConnectionsCombo.SelectedItem is not RecentConnectionOption option)
        {
            return;
        }

        var profile = option.Profile;
        SetManualConnectionMode(true);
        HostBox.Text = NormalizeHost(profile.Host ?? string.Empty);
        PortBox.Text = profile.Port ?? string.Empty;
        DatabaseBox.Text = profile.Database ?? string.Empty;
        UsernameBox.Text = profile.Username ?? string.Empty;
        PasswordBox.Password = profile.Password ?? string.Empty;
    }

    private void AddRecentConnection(ConnectionInput input)
    {
        _settings.RecentPostgres ??= new List<PostgresConnectionProfile>();
        var host = NormalizeHost(input.Host);
        var port = input.Port.ToString(CultureInfo.InvariantCulture);
        var database = input.Database;
        var username = input.Username;

        _settings.RecentPostgres.RemoveAll(profile =>
            profile != null
            && string.Equals(NormalizeHost(profile.Host ?? string.Empty), host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(profile.Port ?? string.Empty, port, StringComparison.OrdinalIgnoreCase)
            && string.Equals(profile.Database ?? string.Empty, database, StringComparison.OrdinalIgnoreCase)
            && string.Equals(profile.Username ?? string.Empty, username, StringComparison.OrdinalIgnoreCase));

        _settings.RecentPostgres.Insert(0, new PostgresConnectionProfile
        {
            Host = host,
            Port = port,
            Database = database,
            Username = username,
            Password = string.IsNullOrWhiteSpace(input.Password) ? null : input.Password
        });

        const int maxRecent = 8;
        if (_settings.RecentPostgres.Count > maxRecent)
        {
            _settings.RecentPostgres.RemoveRange(maxRecent, _settings.RecentPostgres.Count - maxRecent);
        }

        _services.Settings.Save(_settings);
        LoadRecentConnections();
    }
}
