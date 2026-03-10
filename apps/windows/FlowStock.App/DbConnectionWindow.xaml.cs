using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Npgsql;
using MediaBrushes = System.Windows.Media.Brushes;

namespace FlowStock.App;

public partial class DbConnectionWindow : Window
{
    private readonly AppServices _services;
    private readonly CloseDocumentApiClient _closeDocumentApiClient = new();
    private BackupSettings _settings;
    private readonly List<RecentConnectionOption> _recentOptions = new();
    private bool _useServerCloseDocument;
    private static readonly string[] ServerEnvironmentKeys =
    {
        "FLOWSTOCK_USE_SERVER_CLOSE_DOCUMENT",
        "FLOWSTOCK_SERVER_BASE_URL",
        "FLOWSTOCK_SERVER_DEVICE_ID",
        "FLOWSTOCK_SERVER_CLOSE_TIMEOUT_SECONDS",
        "FLOWSTOCK_SERVER_ALLOW_INVALID_TLS"
    };
    private const string DefaultHost = "127.0.0.1";
    private const string DefaultPort = "15432";
    private const string DefaultDatabase = "flowstock";
    private const string DefaultUsername = "postgres";

    public DbConnectionWindow(AppServices services)
    {
        _services = services;
        _settings = _services.Settings.Load();

        InitializeComponent();

        var effective = GetEffectiveConfig();
        CurrentTargetText.Text = $"{effective.Host}:{effective.Port}/{effective.Database} ({effective.Username})";

        var postgres = _settings.Postgres ?? new PostgresSettings();
        HostBox.Text = NormalizeHost(postgres.Host ?? DefaultHost);
        PortBox.Text = postgres.Port ?? DefaultPort;
        DatabaseBox.Text = postgres.Database ?? DefaultDatabase;
        UsernameBox.Text = postgres.Username ?? DefaultUsername;
        PasswordBox.Password = postgres.Password ?? string.Empty;

        LoadRecentConnections();
        LoadServerSettingsUi();
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

        _settings.Postgres ??= new PostgresSettings();
        _settings.Postgres.Host = input.Host;
        _settings.Postgres.Port = input.Port.ToString(CultureInfo.InvariantCulture);
        _settings.Postgres.Database = input.Database;
        _settings.Postgres.Username = input.Username;
        _settings.Postgres.Password = string.IsNullOrWhiteSpace(input.Password) ? null : input.Password;

        _services.Settings.Save(_settings);

        var message = BuildSuccessMessage(diagnostics)
                      + $"{Environment.NewLine}{Environment.NewLine}Применить сейчас? Приложение будет перезапущено.";
        var applyNow = MessageBox.Show(
            message,
            "Подключение к БД",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (applyNow == MessageBoxResult.Yes)
        {
            RestartApplication();
        }
        else
        {
            MessageBox.Show(
                "Настройки сохранены. Перезапустите приложение, чтобы применить изменения.",
                "Подключение к БД",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
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

    private void LoadServerSettingsUi()
    {
        var server = (_settings.Server ?? new ServerSettings()).Normalize();
        ApplyServerSettingsToInputs(new ServerSettings
        {
            UseServerCloseDocument = server.UseServerCloseDocument,
            BaseUrl = server.BaseUrl ?? WpfCloseDocumentService.DefaultServerBaseUrl,
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
        SetCloseMode(server.UseServerCloseDocument);
        ServerBaseUrlBox.Text = server.BaseUrl ?? WpfCloseDocumentService.DefaultServerBaseUrl;
        ServerDeviceIdBox.Text = server.DeviceId ?? WpfCloseDocumentService.BuildDefaultDeviceId();
        ServerTimeoutBox.Text = server.CloseTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        AllowInvalidTlsCheckBox.IsChecked = server.AllowInvalidTls;
    }

    private void RefreshServerStatus()
    {
        CloseModeText.Text = $"Close mode: {FormatCloseMode(_useServerCloseDocument)}";
        ConfigLoadedText.Text = $"Config loaded: {(File.Exists(_services.SettingsPath) ? "yes" : "no")}";

        var effective = _services.WpfCloseDocuments.GetEffectiveConfiguration();
        ActiveClosePathText.Text = $"Active close path: {FormatCloseMode(effective.UseServerCloseDocument)}";

        var overrides = GetServerEnvironmentOverrides();
        if (overrides.Count == 0)
        {
            EnvironmentOverrideText.Visibility = Visibility.Collapsed;
            EnvironmentOverrideText.Text = string.Empty;
            return;
        }

        EnvironmentOverrideText.Text =
            $"Environment override active: {string.Join(", ", overrides)}. Active close path may differ from saved settings.";
        EnvironmentOverrideText.Visibility = Visibility.Visible;
    }

    private void ToggleCloseMode_Click(object sender, RoutedEventArgs e)
    {
        SetCloseMode(!_useServerCloseDocument);
        RefreshServerStatus();
        SetServerStatus(string.Empty, MediaBrushes.Gray);
    }

    private void SetCloseMode(bool useServerCloseDocument)
    {
        _useServerCloseDocument = useServerCloseDocument;
        ToggleCloseModeButton.Content = useServerCloseDocument
            ? "Switch to Legacy"
            : "Switch to Server API";
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
                BaseUrl = serverSettings.BaseUrl ?? WpfCloseDocumentService.DefaultServerBaseUrl,
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
        MessageBox.Show(message, "CloseDocument settings", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private bool TryReadServerSettingsInput(out ServerSettings serverSettings)
    {
        serverSettings = new ServerSettings();

        var baseUrl = ServerBaseUrlBox.Text?.Trim() ?? string.Empty;
        var deviceId = ServerDeviceIdBox.Text?.Trim();
        var timeoutText = ServerTimeoutBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            MessageBox.Show("Base URL is required.", "CloseDocument settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (!int.TryParse(timeoutText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeoutSeconds) || timeoutSeconds <= 0)
        {
            MessageBox.Show("Timeout (sec) must be a positive integer.", "CloseDocument settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        serverSettings = new ServerSettings
        {
            UseServerCloseDocument = _useServerCloseDocument,
            BaseUrl = NormalizeServerBaseUrl(baseUrl),
            DeviceId = string.IsNullOrWhiteSpace(deviceId) ? WpfCloseDocumentService.BuildDefaultDeviceId() : deviceId,
            CloseTimeoutSeconds = timeoutSeconds,
            AllowInvalidTls = AllowInvalidTlsCheckBox.IsChecked == true
        }.Normalize();

        return true;
    }

    private void SetServerStatus(string message, System.Windows.Media.Brush brush)
    {
        ServerStatusText.Text = message;
        ServerStatusText.Foreground = brush;
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

    private sealed record ConnectionInput(string Host, int Port, string Database, string Username, string Password);

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
               $"Версия: {diagnostics.Version}";
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
        var host = ReadEnvOrSettings("FLOWSTOCK_PG_HOST", settings.Host) ?? DefaultHost;
        var port = ReadEnvOrSettings("FLOWSTOCK_PG_PORT", settings.Port) ?? DefaultPort;
        var database = ReadEnvOrSettings("FLOWSTOCK_PG_DB", settings.Database) ?? DefaultDatabase;
        var user = ReadEnvOrSettings("FLOWSTOCK_PG_USER", settings.Username) ?? DefaultUsername;
        return new EffectiveConfig(NormalizeHost(host), port, database, user);
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
            ? DefaultHost
            : host;
    }

    private static string NormalizeServerBaseUrl(string baseUrl)
    {
        var normalized = baseUrl.Trim();
        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = "https://" + normalized;
        }

        return normalized.TrimEnd('/');
    }

    private static string FormatCloseMode(bool useServerCloseDocument)
    {
        return useServerCloseDocument ? "Server API" : "Legacy";
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

    private sealed record EffectiveConfig(string Host, string Port, string Database, string Username);

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

            var host = NormalizeHost(entry.Host ?? DefaultHost);
            var port = string.IsNullOrWhiteSpace(entry.Port) ? DefaultPort : entry.Port!;
            var database = string.IsNullOrWhiteSpace(entry.Database) ? DefaultDatabase : entry.Database!;
            var username = string.IsNullOrWhiteSpace(entry.Username) ? DefaultUsername : entry.Username!;
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
        HostBox.Text = NormalizeHost(profile.Host ?? DefaultHost);
        PortBox.Text = profile.Port ?? DefaultPort;
        DatabaseBox.Text = profile.Database ?? DefaultDatabase;
        UsernameBox.Text = profile.Username ?? DefaultUsername;
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
