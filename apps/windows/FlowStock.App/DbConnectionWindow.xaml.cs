using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using Npgsql;

namespace FlowStock.App;

public partial class DbConnectionWindow : Window
{
    private readonly AppServices _services;
    private BackupSettings _settings;
    private readonly List<RecentConnectionOption> _recentOptions = new();
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
