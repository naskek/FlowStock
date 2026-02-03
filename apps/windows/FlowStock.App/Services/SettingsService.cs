using System.IO;
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
    public string? TsdFolderPath { get; set; }
    public bool TsdAutoPromptEnabled { get; set; } = true;
    public int HuNextSequence { get; set; } = 1;
    [JsonPropertyName("tsd")]
    public TsdSettings Tsd { get; set; } = new();

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

        if (string.IsNullOrWhiteSpace(TsdFolderPath))
        {
            TsdFolderPath = null;
        }

        if (HuNextSequence < 1)
        {
            HuNextSequence = 1;
        }

        Tsd = (Tsd ?? new TsdSettings()).Normalize();

        return this;
    }
}

public sealed class TsdSettings
{
    [JsonPropertyName("devices")]
    public List<TsdDevice> Devices { get; set; } = new();

    [JsonPropertyName("last_device_id")]
    public string? LastDeviceId { get; set; }

    public TsdSettings Normalize()
    {
        Devices = Devices ?? new List<TsdDevice>();
        Devices = Devices
            .Where(device => device != null && !string.IsNullOrWhiteSpace(device.Id))
            .Select(device => new TsdDevice
            {
                Id = device.Id.Trim(),
                Name = string.IsNullOrWhiteSpace(device.Name) ? device.Id.Trim() : device.Name.Trim()
            })
            .GroupBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (string.IsNullOrWhiteSpace(LastDeviceId))
        {
            LastDeviceId = null;
        }
        else
        {
            LastDeviceId = LastDeviceId.Trim();
        }

        return this;
    }
}

public sealed class TsdDevice
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

