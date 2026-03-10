using System.Globalization;
using System.Net.Http;
using FlowStock.Core.Models;
using Npgsql;

namespace FlowStock.App;

public sealed class WpfCloseDocumentService
{
    public const string DefaultServerBaseUrl = "https://127.0.0.1:7154";
    public const int DefaultCloseTimeoutSeconds = 15;
    private readonly string _connectionString;
    private readonly SettingsService _settings;
    private readonly FileLogger _logger;
    private readonly CloseDocumentApiClient _apiClient;

    public WpfCloseDocumentService(string connectionString, SettingsService settings, FileLogger logger)
    {
        _connectionString = connectionString;
        _settings = settings;
        _logger = logger;
        _apiClient = new CloseDocumentApiClient();
    }

    public bool IsServerCloseEnabled()
    {
        return LoadConfiguration().UseServerCloseDocument;
    }

    public WpfServerCloseConfiguration GetEffectiveConfiguration()
    {
        return LoadConfiguration();
    }

    public async Task<WpfCloseDocumentResult> CloseAsync(Doc doc, CancellationToken cancellationToken = default)
    {
        var configuration = LoadConfiguration();
        if (!configuration.UseServerCloseDocument)
        {
            return WpfCloseDocumentResult.FeatureDisabled();
        }

        string docUid;
        try
        {
            docUid = EnsureApiDocMapping(doc, configuration.DeviceId);
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to ensure api_docs mapping for WPF close doc_id={doc.Id}", ex);
            return WpfCloseDocumentResult.Failure(
                WpfCloseDocumentResultKind.LocalMetadataFailure,
                "Не удалось подготовить синхронизацию документа для server close. Проверьте подключение к БД и повторите.",
                ex);
        }

        var request = new CloseDocumentApiRequest
        {
            EventId = BuildEventId(doc.Id),
            DeviceId = configuration.DeviceId
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(configuration.CloseTimeoutSeconds));

        CloseDocumentApiCallResult apiCall;
        try
        {
            apiCall = await _apiClient.CloseAsync(
                new ServerCloseClientOptions
                {
                    BaseUrl = configuration.BaseUrl,
                    AllowInvalidTls = configuration.AllowInvalidTls
                },
                docUid,
                request,
                timeoutCts.Token);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Warn($"WPF server close timed out for doc_id={doc.Id}, doc_uid={docUid}");
            return WpfCloseDocumentResult.Failure(
                WpfCloseDocumentResultKind.Timeout,
                "Сервер не ответил вовремя при проведении операции. Проверьте доступность сервера и повторите.",
                ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.Error($"WPF server close request failed for doc_id={doc.Id}, doc_uid={docUid}", ex);
            return WpfCloseDocumentResult.Failure(
                WpfCloseDocumentResultKind.ServerUnavailable,
                "Не удалось связаться с сервером проведения. Проверьте, что FlowStock.Server запущен и доступен.",
                ex);
        }
        catch (Exception ex)
        {
            _logger.Error($"Unexpected WPF server close failure for doc_id={doc.Id}, doc_uid={docUid}", ex);
            return WpfCloseDocumentResult.Failure(
                WpfCloseDocumentResultKind.UnexpectedError,
                "Не удалось провести операцию через сервер. Подробности записаны в лог.",
                ex);
        }

        if (apiCall.IsTransportFailure)
        {
            if (apiCall.TransportException != null)
            {
                _logger.Error($"Transport failure during WPF server close for doc_id={doc.Id}, doc_uid={docUid}", apiCall.TransportException);
            }
            else
            {
                _logger.Warn($"Transport failure during WPF server close for doc_id={doc.Id}, doc_uid={docUid}: {apiCall.TransportErrorMessage}");
            }

            var kind = apiCall.TransportFailureKind switch
            {
                CloseDocumentTransportFailureKind.InvalidConfiguration => WpfCloseDocumentResultKind.InvalidConfiguration,
                CloseDocumentTransportFailureKind.Timeout => WpfCloseDocumentResultKind.Timeout,
                CloseDocumentTransportFailureKind.Network => WpfCloseDocumentResultKind.ServerUnavailable,
                CloseDocumentTransportFailureKind.InvalidResponse => WpfCloseDocumentResultKind.InvalidResponse,
                _ => WpfCloseDocumentResultKind.UnexpectedError
            };

            return WpfCloseDocumentResult.Failure(
                kind,
                apiCall.TransportErrorMessage ?? "Не удалось выполнить server close.",
                apiCall.TransportException);
        }

        if (apiCall.Response != null)
        {
            return MapSuccessResponse(apiCall.Response);
        }

        return MapHttpError(apiCall);
    }

    private WpfCloseDocumentResult MapSuccessResponse(CloseDocumentApiResponse response)
    {
        if (!response.Ok)
        {
            var validationMessage = response.Errors.Count > 0
                ? string.Join(Environment.NewLine, response.Errors)
                : "Сервер отклонил проведение документа.";

            return WpfCloseDocumentResult.FromServerResponse(
                WpfCloseDocumentResultKind.ValidationFailed,
                validationMessage,
                response,
                shouldRefresh: false);
        }

        if (response.AlreadyClosed || string.Equals(response.Result, "ALREADY_CLOSED", StringComparison.OrdinalIgnoreCase))
        {
            return WpfCloseDocumentResult.FromServerResponse(
                WpfCloseDocumentResultKind.AlreadyClosed,
                "Операция уже была проведена на сервере. Состояние обновлено.",
                response,
                shouldRefresh: true);
        }

        if (response.IdempotentReplay)
        {
            return WpfCloseDocumentResult.FromServerResponse(
                WpfCloseDocumentResultKind.IdempotentReplay,
                "Операция уже была проведена ранее. Состояние синхронизировано без повторного проведения.",
                response,
                shouldRefresh: true);
        }

        return WpfCloseDocumentResult.FromServerResponse(
            WpfCloseDocumentResultKind.Closed,
            string.Empty,
            response,
            shouldRefresh: true);
    }

    private static WpfCloseDocumentResult MapHttpError(CloseDocumentApiCallResult apiCall)
    {
        var errorCode = apiCall.Error?.Error;
        var message = errorCode switch
        {
            "DOC_NOT_FOUND" => "Сервер не нашел документ для проведения. Проверьте синхронизацию api_docs и повторите.",
            "EVENT_ID_CONFLICT" => "Сервер отклонил запрос из-за конфликта event_id. Повторите проведение.",
            _ => string.IsNullOrWhiteSpace(errorCode)
                ? $"Сервер вернул ошибку {(int?)apiCall.StatusCode ?? 0}."
                : $"Сервер вернул ошибку: {errorCode}"
        };

        var kind = errorCode switch
        {
            "DOC_NOT_FOUND" => WpfCloseDocumentResultKind.NotFound,
            "EVENT_ID_CONFLICT" => WpfCloseDocumentResultKind.EventConflict,
            _ => WpfCloseDocumentResultKind.ServerRejected
        };

        return WpfCloseDocumentResult.Failure(kind, message);
    }

    private WpfServerCloseConfiguration LoadConfiguration()
    {
        var settings = _settings.Load().Server ?? new ServerSettings();
        var useServerClose = ReadEnvBool("FLOWSTOCK_USE_SERVER_CLOSE_DOCUMENT") ?? settings.UseServerCloseDocument;
        var baseUrl = NormalizeBaseUrl(ReadEnvOrSettings("FLOWSTOCK_SERVER_BASE_URL", settings.BaseUrl) ?? DefaultServerBaseUrl);
        var deviceId = ReadEnvOrSettings("FLOWSTOCK_SERVER_DEVICE_ID", settings.DeviceId);
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            deviceId = BuildDefaultDeviceId();
        }

        var timeoutSeconds = ReadEnvInt("FLOWSTOCK_SERVER_CLOSE_TIMEOUT_SECONDS") ?? settings.CloseTimeoutSeconds;
        if (timeoutSeconds < 1)
        {
            timeoutSeconds = DefaultCloseTimeoutSeconds;
        }

        var allowInvalidTls = ReadEnvBool("FLOWSTOCK_SERVER_ALLOW_INVALID_TLS") ?? settings.AllowInvalidTls;
        return new WpfServerCloseConfiguration(useServerClose, baseUrl, deviceId, timeoutSeconds, allowInvalidTls);
    }

    public static string BuildDefaultDeviceId()
    {
        return $"WPF-{Environment.MachineName}";
    }

    private string EnsureApiDocMapping(Doc doc, string? deviceId)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();

        using var transaction = connection.BeginTransaction();

        var existingDocUid = FindApiDocUidByDocId(connection, transaction, doc.Id);
        var docUid = !string.IsNullOrWhiteSpace(doc.ApiDocUid)
            ? doc.ApiDocUid!.Trim()
            : !string.IsNullOrWhiteSpace(existingDocUid)
                ? existingDocUid!
                : BuildDerivedDocUid(doc.Id);

        if (string.IsNullOrWhiteSpace(existingDocUid))
        {
            InsertApiDoc(connection, transaction, docUid, doc, deviceId);
        }
        else
        {
            UpdateApiDoc(connection, transaction, docUid, doc, deviceId);
        }

        transaction.Commit();
        return docUid;
    }

    private static string? FindApiDocUidByDocId(NpgsqlConnection connection, NpgsqlTransaction transaction, long docId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
SELECT doc_uid
FROM api_docs
WHERE doc_id = @doc_id
ORDER BY created_at DESC
LIMIT 1;";
        command.Parameters.AddWithValue("@doc_id", docId);
        return command.ExecuteScalar() as string;
    }

    private static void InsertApiDoc(NpgsqlConnection connection, NpgsqlTransaction transaction, string docUid, Doc doc, string? deviceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
INSERT INTO api_docs(doc_uid, doc_id, status, created_at, doc_type, doc_ref, partner_id, device_id)
VALUES(@doc_uid, @doc_id, @status, @created_at, @doc_type, @doc_ref, @partner_id, @device_id);";
        command.Parameters.AddWithValue("@doc_uid", docUid);
        command.Parameters.AddWithValue("@doc_id", doc.Id);
        command.Parameters.AddWithValue("@status", DocTypeMapper.StatusToString(doc.Status));
        command.Parameters.AddWithValue("@created_at", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@doc_type", DocTypeMapper.ToOpString(doc.Type));
        command.Parameters.AddWithValue("@doc_ref", doc.DocRef);
        command.Parameters.AddWithValue("@partner_id", (object?)doc.PartnerId ?? DBNull.Value);
        command.Parameters.AddWithValue("@device_id", (object?)deviceId ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void UpdateApiDoc(NpgsqlConnection connection, NpgsqlTransaction transaction, string docUid, Doc doc, string? deviceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
UPDATE api_docs
SET status = @status,
    doc_type = @doc_type,
    doc_ref = @doc_ref,
    partner_id = @partner_id,
    device_id = COALESCE(@device_id, device_id)
WHERE doc_uid = @doc_uid;";
        command.Parameters.AddWithValue("@doc_uid", docUid);
        command.Parameters.AddWithValue("@status", DocTypeMapper.StatusToString(doc.Status));
        command.Parameters.AddWithValue("@doc_type", DocTypeMapper.ToOpString(doc.Type));
        command.Parameters.AddWithValue("@doc_ref", doc.DocRef);
        command.Parameters.AddWithValue("@partner_id", (object?)doc.PartnerId ?? DBNull.Value);
        command.Parameters.AddWithValue("@device_id", (object?)deviceId ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static string BuildDerivedDocUid(long docId)
    {
        return $"wpf-doc-{docId}";
    }

    private static string BuildEventId(long docId)
    {
        return $"wpf-close-{docId}-{Guid.NewGuid():N}";
    }

    private static string NormalizeBaseUrl(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "https://" + trimmed;
        }

        return trimmed.TrimEnd('/');
    }

    private static string? ReadEnvOrSettings(string envKey, string? settingsValue)
    {
        var env = Environment.GetEnvironmentVariable(envKey);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Trim();
        }

        return string.IsNullOrWhiteSpace(settingsValue) ? null : settingsValue.Trim();
    }

    private static bool? ReadEnvBool(string envKey)
    {
        var env = Environment.GetEnvironmentVariable(envKey);
        if (string.IsNullOrWhiteSpace(env))
        {
            return null;
        }

        return env.Trim().ToLowerInvariant() switch
        {
            "1" => true,
            "true" => true,
            "yes" => true,
            "on" => true,
            "0" => false,
            "false" => false,
            "no" => false,
            "off" => false,
            _ => null
        };
    }

    private static int? ReadEnvInt(string envKey)
    {
        var env = Environment.GetEnvironmentVariable(envKey);
        if (string.IsNullOrWhiteSpace(env))
        {
            return null;
        }

        return int.TryParse(env, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    public sealed record WpfServerCloseConfiguration(
        bool UseServerCloseDocument,
        string BaseUrl,
        string DeviceId,
        int CloseTimeoutSeconds,
        bool AllowInvalidTls);
}

public sealed class WpfCloseDocumentResult
{
    public WpfCloseDocumentResultKind Kind { get; init; }
    public string Message { get; init; } = string.Empty;
    public CloseDocumentApiResponse? Response { get; init; }
    public bool ShouldRefresh { get; init; }
    public Exception? Exception { get; init; }

    public bool IsSuccess => Kind is WpfCloseDocumentResultKind.Closed
        or WpfCloseDocumentResultKind.AlreadyClosed
        or WpfCloseDocumentResultKind.IdempotentReplay;

    public static WpfCloseDocumentResult FeatureDisabled()
    {
        return new WpfCloseDocumentResult
        {
            Kind = WpfCloseDocumentResultKind.FeatureDisabled
        };
    }

    public static WpfCloseDocumentResult FromServerResponse(
        WpfCloseDocumentResultKind kind,
        string message,
        CloseDocumentApiResponse response,
        bool shouldRefresh)
    {
        return new WpfCloseDocumentResult
        {
            Kind = kind,
            Message = message,
            Response = response,
            ShouldRefresh = shouldRefresh
        };
    }

    public static WpfCloseDocumentResult Failure(
        WpfCloseDocumentResultKind kind,
        string message,
        Exception? exception = null)
    {
        return new WpfCloseDocumentResult
        {
            Kind = kind,
            Message = message,
            Exception = exception
        };
    }
}

public enum WpfCloseDocumentResultKind
{
    FeatureDisabled,
    Closed,
    AlreadyClosed,
    IdempotentReplay,
    ValidationFailed,
    NotFound,
    EventConflict,
    ServerRejected,
    LocalMetadataFailure,
    ServerUnavailable,
    Timeout,
    InvalidConfiguration,
    InvalidResponse,
    UnexpectedError
}
