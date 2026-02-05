using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowStock.App;

public sealed class SettingsService
{
    private readonly string _settingsPath;
    private readonly JsonSerializerOptions _jsonOptions;

    public SettingsService(string settingsPath)
    {
        _settingsPath = settingsPath;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    public BackupSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return BackupSettings.Default();
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<BackupSettings>(json, _jsonOptions);
            return settings?.Normalize() ?? BackupSettings.Default();
        }
        catch
        {
            return BackupSettings.Default();
        }
    }

    public void Save(BackupSettings settings)
    {
        var dir = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(settings.Normalize(), _jsonOptions);
        File.WriteAllText(_settingsPath, json);
    }
}

public enum BackupMode
{
    OnStartIfOlderThanHours,
    OnEveryStart
}

public sealed class BackupSettings
{
    public bool BackupsEnabled { get; set; } = true;
    public BackupMode BackupMode { get; set; } = BackupMode.OnStartIfOlderThanHours;
    public int BackupIfOlderThanHours { get; set; } = 24;
    public int KeepLastNBackups { get; set; } = 30;
    public int HuNextSequence { get; set; } = 1;
    [JsonPropertyName("postgres")]
    public PostgresSettings Postgres { get; set; } = new();
    [JsonPropertyName("recent_postgres")]
    public List<PostgresConnectionProfile> RecentPostgres { get; set; } = new();

    public static BackupSettings Default()
    {
        return new BackupSettings();
    }

    public BackupSettings Normalize()
    {
        if (BackupIfOlderThanHours < 1)
        {
            BackupIfOlderThanHours = 1;
        }

        if (KeepLastNBackups < 1)
        {
            KeepLastNBackups = 1;
        }

        if (HuNextSequence < 1)
        {
            HuNextSequence = 1;
        }

        Postgres = (Postgres ?? new PostgresSettings()).Normalize();
        RecentPostgres = (RecentPostgres ?? new List<PostgresConnectionProfile>())
            .Where(profile => profile != null)
            .Select(profile => profile!.Normalize())
            .Where(profile => !string.IsNullOrWhiteSpace(profile.Host)
                              && !string.IsNullOrWhiteSpace(profile.Port)
                              && !string.IsNullOrWhiteSpace(profile.Database)
                              && !string.IsNullOrWhiteSpace(profile.Username))
            .ToList();

        return this;
    }
}

public sealed class PostgresSettings
{
    public string? Host { get; set; }
    public string? Port { get; set; }
    public string? Database { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }

    public PostgresSettings Normalize()
    {
        Host = NormalizeValue(Host);
        Port = NormalizeValue(Port);
        Database = NormalizeValue(Database);
        Username = NormalizeValue(Username);
        Password = NormalizeValue(Password);

        return this;
    }

    private static string? NormalizeValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

public sealed class PostgresConnectionProfile
{
    public string? Host { get; set; }
    public string? Port { get; set; }
    public string? Database { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }

    public PostgresConnectionProfile Normalize()
    {
        Host = NormalizeValue(Host);
        Port = NormalizeValue(Port);
        Database = NormalizeValue(Database);
        Username = NormalizeValue(Username);
        Password = NormalizeValue(Password);
        return this;
    }

    private static string? NormalizeValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

