using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LightWms.App;

public enum PartnerStatus
{
    Supplier,
    Client,
    Both
}

public sealed class PartnerStatusService
{
    private readonly string _path;
    private readonly JsonSerializerOptions _jsonOptions;
    private Dictionary<long, PartnerStatus> _statuses = new();

    public PartnerStatusService(string path)
    {
        _path = path;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        Load();
    }

    public PartnerStatus GetStatus(long partnerId)
    {
        return _statuses.TryGetValue(partnerId, out var status) ? status : PartnerStatus.Both;
    }

    public void SetStatus(long partnerId, PartnerStatus status)
    {
        _statuses[partnerId] = status;
        Save();
    }

    public void RemoveStatus(long partnerId)
    {
        if (_statuses.Remove(partnerId))
        {
            Save();
        }
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            _statuses = new Dictionary<long, PartnerStatus>();
            return;
        }

        try
        {
            var json = File.ReadAllText(_path);
            var data = JsonSerializer.Deserialize<Dictionary<long, PartnerStatus>>(json, _jsonOptions);
            _statuses = data ?? new Dictionary<long, PartnerStatus>();
        }
        catch
        {
            _statuses = new Dictionary<long, PartnerStatus>();
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(_statuses, _jsonOptions);
        File.WriteAllText(_path, json);
    }
}
