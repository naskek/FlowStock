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

public static class FlowStockEndpointDefaults
{
    public const string ServerBaseUrl = "https://127.0.0.1:7154";
    public const string PcClientUrl = "https://127.0.0.1:7154";
    public const string TsdClientUrl = "https://127.0.0.1:7154/tsd";
}

public static class FlowStockUrlHelper
{
    public static bool TryNormalizeRootUrl(string? value, string defaultScheme, out string normalized, out string error)
    {
        return TryNormalizeUrl(value, defaultScheme, allowPathPrefix: false, defaultPath: "/", out normalized, out error);
    }

    public static bool TryNormalizeTsdUrl(string? value, string defaultScheme, out string normalized, out string error)
    {
        return TryNormalizeUrl(value, defaultScheme, allowPathPrefix: true, defaultPath: "/tsd", out normalized, out error);
    }

    private static bool TryNormalizeUrl(string? value, string defaultScheme, bool allowPathPrefix, string defaultPath, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "URL is required.";
            return false;
        }

        var candidate = value.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = $"{defaultScheme}://{candidate}";
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || !uri.IsAbsoluteUri
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "URL must be absolute.";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "Only http and https are supported.";
            return false;
        }

        if (!allowPathPrefix
            && !string.IsNullOrWhiteSpace(uri.AbsolutePath)
            && !string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal))
        {
            error = "URL must not contain a path prefix.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(uri.Query))
        {
            error = "URL must not contain a query string.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(uri.Fragment))
        {
            error = "URL must not contain a fragment.";
            return false;
        }

        var path = uri.AbsolutePath;
        if (string.IsNullOrWhiteSpace(path) || string.Equals(path, "/", StringComparison.Ordinal))
        {
            path = defaultPath;
        }
        else if (!allowPathPrefix)
        {
            error = "URL must not contain a path prefix.";
            return false;
        }

        var builder = new UriBuilder(uri)
        {
            Path = path,
            Query = string.Empty,
            Fragment = string.Empty
        };

        normalized = builder.Uri.AbsoluteUri.TrimEnd('/');
        return true;
    }

    public static string NormalizeRootUrlOrDefault(string? value, string defaultUrl, string defaultScheme)
    {
        return TryNormalizeRootUrl(value, defaultScheme, out var normalized, out _)
            ? normalized
            : defaultUrl;
    }

    public static string NormalizeTsdUrlOrDefault(string? value, string defaultUrl, string defaultScheme)
    {
        return TryNormalizeTsdUrl(value, defaultScheme, out var normalized, out _)
            ? normalized
            : defaultUrl;
    }
}

public sealed class BackupSettings
{
    public bool BackupsEnabled { get; set; } = true;
    public BackupMode BackupMode { get; set; } = BackupMode.OnStartIfOlderThanHours;
    public int BackupIfOlderThanHours { get; set; } = 24;
    public int KeepLastNBackups { get; set; } = 30;
    public int HuNextSequence { get; set; } = 1;
    [JsonPropertyName("document_numbering")]
    public DocumentNumberingSettings DocumentNumbering { get; set; } = new();
    [JsonPropertyName("postgres")]
    public PostgresSettings Postgres { get; set; } = new();
    [JsonPropertyName("server")]
    public ServerSettings Server { get; set; } = new();
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

        DocumentNumbering = (DocumentNumbering ?? new DocumentNumberingSettings()).Normalize();
        Postgres = (Postgres ?? new PostgresSettings()).Normalize();
        Server = (Server ?? new ServerSettings()).Normalize();
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

public sealed class DocumentNumberingSettings
{
    private static readonly HashSet<string> AllowedStyles = new(StringComparer.OrdinalIgnoreCase)
    {
        "D6",
        "D5",
        "D4",
        "N"
    };

    [JsonPropertyName("template")]
    public string Template { get; set; } = "{PREFIX}-{YYYY}-{SEQ}";

    [JsonPropertyName("year")]
    public string? Year { get; set; }

    [JsonPropertyName("sequence_style")]
    public string SequenceStyle { get; set; } = "D6";

    public DocumentNumberingSettings Normalize()
    {
        Template = string.IsNullOrWhiteSpace(Template)
            ? "{PREFIX}-{YYYY}-{SEQ}"
            : Template.Trim();

        if (string.IsNullOrWhiteSpace(Year))
        {
            Year = null;
        }
        else
        {
            var trimmed = Year.Trim();
            Year = trimmed.Length == 4 && int.TryParse(trimmed, out _) ? trimmed : null;
        }

        SequenceStyle = string.IsNullOrWhiteSpace(SequenceStyle)
            ? "D6"
            : SequenceStyle.Trim().ToUpperInvariant();
        if (!AllowedStyles.Contains(SequenceStyle))
        {
            SequenceStyle = "D6";
        }

        return this;
    }
}

public sealed class ServerSettings
{
    [JsonPropertyName("use_server_create_order")]
    public bool UseServerCreateOrder { get; set; }

    [JsonPropertyName("use_server_update_order")]
    public bool UseServerUpdateOrder { get; set; }

    [JsonPropertyName("use_server_delete_order")]
    public bool UseServerDeleteOrder { get; set; }

    [JsonPropertyName("use_server_set_order_status")]
    public bool UseServerSetOrderStatus { get; set; }

    [JsonPropertyName("use_server_incoming_request_order_approval")]
    public bool UseServerIncomingRequestOrderApproval { get; set; }

    [JsonPropertyName("use_server_create_doc_draft")]
    public bool UseServerCreateDocDraft { get; set; }

    [JsonPropertyName("use_server_close_document")]
    public bool UseServerCloseDocument { get; set; }

    [JsonPropertyName("use_server_add_doc_line")]
    public bool UseServerAddDocLine { get; set; }

    [JsonPropertyName("use_server_update_doc_line")]
    public bool UseServerUpdateDocLine { get; set; }

    [JsonPropertyName("use_server_delete_doc_line")]
    public bool UseServerDeleteDocLine { get; set; }

    [JsonPropertyName("base_url")]
    public string? ServerBaseUrl { get; set; }

    [JsonPropertyName("pc_client_url")]
    public string? PcClientUrl { get; set; }

    [JsonPropertyName("tsd_client_url")]
    public string? TsdClientUrl { get; set; }

    [JsonIgnore]
    public string? BaseUrl
    {
        get => ServerBaseUrl;
        set => ServerBaseUrl = value;
    }

    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }

    [JsonPropertyName("close_timeout_seconds")]
    public int CloseTimeoutSeconds { get; set; } = 15;

    [JsonPropertyName("allow_invalid_tls")]
    public bool AllowInvalidTls { get; set; }

    public ServerSettings Normalize()
    {
        UseServerCreateOrder = true;
        UseServerUpdateOrder = true;
        UseServerDeleteOrder = true;
        UseServerSetOrderStatus = true;
        UseServerIncomingRequestOrderApproval = true;
        UseServerCreateDocDraft = true;
        UseServerCloseDocument = true;
        UseServerAddDocLine = true;
        UseServerUpdateDocLine = true;
        UseServerDeleteDocLine = true;
        ServerBaseUrl = NormalizeValue(ServerBaseUrl);
        PcClientUrl = NormalizeValue(PcClientUrl);
        TsdClientUrl = NormalizeValue(TsdClientUrl);
        DeviceId = NormalizeValue(DeviceId);

        if (CloseTimeoutSeconds < 1)
        {
            CloseTimeoutSeconds = 1;
        }
        else if (CloseTimeoutSeconds > 120)
        {
            CloseTimeoutSeconds = 120;
        }

        return this;
    }

    public string GetServerBaseUrlOrDefault()
    {
        return FlowStockUrlHelper.NormalizeRootUrlOrDefault(ServerBaseUrl, FlowStockEndpointDefaults.ServerBaseUrl, Uri.UriSchemeHttps);
    }

    public string GetPcClientUrlOrDefault()
    {
        return FlowStockUrlHelper.NormalizeRootUrlOrDefault(PcClientUrl, FlowStockEndpointDefaults.PcClientUrl, Uri.UriSchemeHttps);
    }

    public string GetTsdClientUrlOrDefault()
    {
        return FlowStockUrlHelper.NormalizeTsdUrlOrDefault(TsdClientUrl, FlowStockEndpointDefaults.TsdClientUrl, Uri.UriSchemeHttps);
    }

    private static string? NormalizeValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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

