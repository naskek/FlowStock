using System.Globalization;
using System.Net.Http;
using FlowStock.Core.Models;
using Npgsql;

namespace FlowStock.App;

public sealed class WpfUpdateDocLineService
{
    private readonly string _connectionString;
    private readonly SettingsService _settings;
    private readonly FileLogger _logger;
    private readonly UpdateDocLineApiClient _apiClient;

    public WpfUpdateDocLineService(string connectionString, SettingsService settings, FileLogger logger)
    {
        _connectionString = connectionString;
        _settings = settings;
        _logger = logger;
        _apiClient = new UpdateDocLineApiClient();
    }

    public WpfServerUpdateDocLineConfiguration GetEffectiveConfiguration()
    {
        return LoadConfiguration();
    }

    public async Task<WpfUpdateDocLineResult> UpdateLineAsync(
        Doc doc,
        WpfUpdateDocLineContext context,
        CancellationToken cancellationToken = default)
    {
        var configuration = LoadConfiguration();
        string docUid;
        try
        {
            docUid = EnsureApiDocMapping(doc, context, configuration.DeviceId);
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to ensure api_docs mapping for WPF update-line doc_id={doc.Id}", ex);
            return WpfUpdateDocLineResult.Failure(
                WpfUpdateDocLineResultKind.LocalMetadataFailure,
                "Не удалось подготовить синхронизацию документа для server update-line. Проверьте подключение к БД и повторите.",
                ex);
        }

        var request = new UpdateDocLineApiRequest
        {
            EventId = BuildEventId(doc.Id, context.LineId),
            DeviceId = configuration.DeviceId,
            LineId = context.LineId,
            Qty = context.QtyBase,
            UomCode = context.UomCode,
            FromLocationId = context.FromLocationId,
            ToLocationId = context.ToLocationId,
            FromHu = NormalizeValue(context.FromHu),
            ToHu = NormalizeValue(context.ToHu)
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(configuration.RequestTimeoutSeconds));

        UpdateDocLineApiCallResult apiCall;
        try
        {
            apiCall = await _apiClient.UpdateAsync(
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
            _logger.Warn($"WPF server update-line timed out for doc_id={doc.Id}, doc_uid={docUid}, line_id={context.LineId}");
            return WpfUpdateDocLineResult.Failure(
                WpfUpdateDocLineResultKind.Timeout,
                "Сервер не ответил вовремя при обновлении строки. Перед повтором обновите документ и проверьте фактическое состояние строк.",
                ex,
                shouldRefresh: true);
        }
        catch (HttpRequestException ex) when (UpdateDocLineApiClient.IsTlsFailure(ex))
        {
            _logger.Error($"TLS failure during WPF server update-line for doc_id={doc.Id}, doc_uid={docUid}", ex);
            return WpfUpdateDocLineResult.Failure(
                WpfUpdateDocLineResultKind.InvalidConfiguration,
                "Ошибка TLS при обращении к серверу. Проверьте сертификат или настройку Allow invalid TLS.",
                ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.Error($"WPF server update-line request failed for doc_id={doc.Id}, doc_uid={docUid}", ex);
            return WpfUpdateDocLineResult.Failure(
                WpfUpdateDocLineResultKind.ServerUnavailable,
                "Не удалось связаться с сервером обновления строк. Проверьте, что FlowStock.Server запущен и доступен.",
                ex);
        }
        catch (Exception ex)
        {
            _logger.Error($"Unexpected WPF server update-line failure for doc_id={doc.Id}, doc_uid={docUid}", ex);
            return WpfUpdateDocLineResult.Failure(
                WpfUpdateDocLineResultKind.UnexpectedError,
                "Не удалось обновить строку через сервер. Подробности записаны в лог.",
                ex);
        }

        if (apiCall.IsTransportFailure)
        {
            var kind = apiCall.TransportFailureKind switch
            {
                UpdateDocLineTransportFailureKind.InvalidConfiguration => WpfUpdateDocLineResultKind.InvalidConfiguration,
                UpdateDocLineTransportFailureKind.Timeout => WpfUpdateDocLineResultKind.Timeout,
                UpdateDocLineTransportFailureKind.Network => WpfUpdateDocLineResultKind.ServerUnavailable,
                UpdateDocLineTransportFailureKind.InvalidResponse => WpfUpdateDocLineResultKind.InvalidResponse,
                _ => WpfUpdateDocLineResultKind.UnexpectedError
            };

            return WpfUpdateDocLineResult.Failure(
                kind,
                apiCall.TransportErrorMessage ?? "Не удалось выполнить server update-line.",
                apiCall.TransportException);
        }

        if (apiCall.Response != null)
        {
            return MapSuccessResponse(apiCall.Response);
        }

        return MapHttpError(apiCall);
    }

    private static WpfUpdateDocLineResult MapSuccessResponse(UpdateDocLineApiResponse response)
    {
        if (!response.Ok)
        {
            return WpfUpdateDocLineResult.Failure(
                WpfUpdateDocLineResultKind.ValidationFailed,
                "Сервер отклонил обновление строки.");
        }

        if (response.IdempotentReplay || string.Equals(response.Result, "IDEMPOTENT_REPLAY", StringComparison.OrdinalIgnoreCase))
        {
            return WpfUpdateDocLineResult.Success(
                WpfUpdateDocLineResultKind.IdempotentReplay,
                string.Empty,
                response,
                shouldRefresh: true);
        }

        return WpfUpdateDocLineResult.Success(
            WpfUpdateDocLineResultKind.Updated,
            string.Empty,
            response,
            shouldRefresh: true);
    }

    private static WpfUpdateDocLineResult MapHttpError(UpdateDocLineApiCallResult apiCall)
    {
        var errorCode = apiCall.Error?.Error;
        var message = errorCode switch
        {
            "DOC_NOT_FOUND" => "Сервер не нашел документ для обновления строки. Проверьте синхронизацию api_docs и повторите.",
            "DOC_NOT_DRAFT" => "Сервер не разрешает менять строки в документе, который уже не является черновиком.",
            "EVENT_ID_CONFLICT" => "Сервер отклонил запрос из-за конфликта event_id. Обновите документ и повторите действие.",
            "UNKNOWN_LINE" => "Сервер не нашел выбранную строку документа.",
            "INVALID_QTY" => "Количество должно быть больше нуля.",
            "MISSING_LOCATION" => "Серверу не хватает обязательной локации для обновления строки.",
            "UNKNOWN_HU" => "Указанный HU не найден или недоступен.",
            "UNKNOWN_LOCATION" => "Сервер не нашел указанную локацию.",
            _ => string.IsNullOrWhiteSpace(errorCode)
                ? $"Сервер вернул ошибку {(int?)apiCall.StatusCode ?? 0}."
                : $"Сервер вернул ошибку: {errorCode}"
        };

        var kind = errorCode switch
        {
            "DOC_NOT_FOUND" => WpfUpdateDocLineResultKind.NotFound,
            "DOC_NOT_DRAFT" => WpfUpdateDocLineResultKind.ValidationFailed,
            "EVENT_ID_CONFLICT" => WpfUpdateDocLineResultKind.EventConflict,
            "UNKNOWN_LINE" => WpfUpdateDocLineResultKind.ValidationFailed,
            "INVALID_QTY" => WpfUpdateDocLineResultKind.ValidationFailed,
            "MISSING_LOCATION" => WpfUpdateDocLineResultKind.ValidationFailed,
            "UNKNOWN_HU" => WpfUpdateDocLineResultKind.ValidationFailed,
            "UNKNOWN_LOCATION" => WpfUpdateDocLineResultKind.ValidationFailed,
            _ => WpfUpdateDocLineResultKind.ServerRejected
        };

        return WpfUpdateDocLineResult.Failure(kind, message);
    }

    private WpfServerUpdateDocLineConfiguration LoadConfiguration()
    {
        var settings = _settings.Load().Server ?? new ServerSettings();
        var baseUrl = NormalizeBaseUrl(ReadEnvOrSettings("FLOWSTOCK_SERVER_BASE_URL", settings.BaseUrl) ?? WpfCloseDocumentService.DefaultServerBaseUrl);
        var deviceId = ReadEnvOrSettings("FLOWSTOCK_SERVER_DEVICE_ID", settings.DeviceId);
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            deviceId = WpfCloseDocumentService.BuildDefaultDeviceId();
        }

        var timeoutSeconds = ReadEnvInt("FLOWSTOCK_SERVER_CLOSE_TIMEOUT_SECONDS") ?? settings.CloseTimeoutSeconds;
        if (timeoutSeconds < 1)
        {
            timeoutSeconds = WpfCloseDocumentService.DefaultCloseTimeoutSeconds;
        }

        return new WpfServerUpdateDocLineConfiguration(
            baseUrl,
            deviceId,
            timeoutSeconds,
            settings.AllowInvalidTls);
    }

    private string EnsureApiDocMapping(Doc doc, WpfUpdateDocLineContext context, string? deviceId)
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
            InsertApiDoc(connection, transaction, docUid, doc, context, deviceId);
        }
        else
        {
            UpdateApiDoc(connection, transaction, docUid, doc, context, deviceId);
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

    private static void InsertApiDoc(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string docUid,
        Doc doc,
        WpfUpdateDocLineContext context,
        string? deviceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
INSERT INTO api_docs(
    doc_uid,
    doc_id,
    status,
    created_at,
    doc_type,
    doc_ref,
    partner_id,
    from_location_id,
    to_location_id,
    from_hu,
    to_hu,
    device_id)
VALUES(
    @doc_uid,
    @doc_id,
    @status,
    @created_at,
    @doc_type,
    @doc_ref,
    @partner_id,
    @from_location_id,
    @to_location_id,
    @from_hu,
    @to_hu,
    @device_id);";
        command.Parameters.AddWithValue("@doc_uid", docUid);
        command.Parameters.AddWithValue("@doc_id", doc.Id);
        command.Parameters.AddWithValue("@status", DocTypeMapper.StatusToString(doc.Status));
        command.Parameters.AddWithValue("@created_at", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@doc_type", DocTypeMapper.ToOpString(doc.Type));
        command.Parameters.AddWithValue("@doc_ref", doc.DocRef);
        command.Parameters.AddWithValue("@partner_id", (object?)doc.PartnerId ?? DBNull.Value);
        command.Parameters.AddWithValue("@from_location_id", (object?)context.FromLocationId ?? DBNull.Value);
        command.Parameters.AddWithValue("@to_location_id", (object?)context.ToLocationId ?? DBNull.Value);
        command.Parameters.AddWithValue("@from_hu", (object?)NormalizeValue(context.FromHu) ?? DBNull.Value);
        command.Parameters.AddWithValue("@to_hu", (object?)NormalizeValue(context.ToHu) ?? DBNull.Value);
        command.Parameters.AddWithValue("@device_id", (object?)deviceId ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void UpdateApiDoc(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string docUid,
        Doc doc,
        WpfUpdateDocLineContext context,
        string? deviceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
UPDATE api_docs
SET status = @status,
    doc_type = @doc_type,
    doc_ref = @doc_ref,
    partner_id = @partner_id,
    from_location_id = @from_location_id,
    to_location_id = @to_location_id,
    from_hu = @from_hu,
    to_hu = @to_hu,
    device_id = COALESCE(@device_id, device_id)
WHERE doc_uid = @doc_uid;";
        command.Parameters.AddWithValue("@doc_uid", docUid);
        command.Parameters.AddWithValue("@status", DocTypeMapper.StatusToString(doc.Status));
        command.Parameters.AddWithValue("@doc_type", DocTypeMapper.ToOpString(doc.Type));
        command.Parameters.AddWithValue("@doc_ref", doc.DocRef);
        command.Parameters.AddWithValue("@partner_id", (object?)doc.PartnerId ?? DBNull.Value);
        command.Parameters.AddWithValue("@from_location_id", (object?)context.FromLocationId ?? DBNull.Value);
        command.Parameters.AddWithValue("@to_location_id", (object?)context.ToLocationId ?? DBNull.Value);
        command.Parameters.AddWithValue("@from_hu", (object?)NormalizeValue(context.FromHu) ?? DBNull.Value);
        command.Parameters.AddWithValue("@to_hu", (object?)NormalizeValue(context.ToHu) ?? DBNull.Value);
        command.Parameters.AddWithValue("@device_id", (object?)deviceId ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static string BuildDerivedDocUid(long docId)
    {
        return $"wpf-doc-{docId}";
    }

    private static string BuildEventId(long docId, long lineId)
    {
        return $"wpf-line-update-{docId}-{lineId}-{Guid.NewGuid():N}";
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

    private static string? NormalizeValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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
}

public sealed record WpfUpdateDocLineContext(
    long LineId,
    double QtyBase,
    string? UomCode,
    long? FromLocationId,
    long? ToLocationId,
    string? FromHu,
    string? ToHu);

public sealed record WpfServerUpdateDocLineConfiguration(
    string BaseUrl,
    string DeviceId,
    int RequestTimeoutSeconds,
    bool AllowInvalidTls);

public sealed class WpfUpdateDocLineResult
{
    public WpfUpdateDocLineResultKind Kind { get; init; }
    public string Message { get; init; } = string.Empty;
    public UpdateDocLineApiResponse? Response { get; init; }
    public bool ShouldRefresh { get; init; }
    public Exception? Exception { get; init; }

    public bool IsSuccess => Kind is WpfUpdateDocLineResultKind.Updated
        or WpfUpdateDocLineResultKind.IdempotentReplay;

    public static WpfUpdateDocLineResult Success(
        WpfUpdateDocLineResultKind kind,
        string message,
        UpdateDocLineApiResponse response,
        bool shouldRefresh)
    {
        return new WpfUpdateDocLineResult
        {
            Kind = kind,
            Message = message,
            Response = response,
            ShouldRefresh = shouldRefresh
        };
    }

    public static WpfUpdateDocLineResult Failure(
        WpfUpdateDocLineResultKind kind,
        string message,
        Exception? exception = null,
        bool shouldRefresh = false)
    {
        return new WpfUpdateDocLineResult
        {
            Kind = kind,
            Message = message,
            Exception = exception,
            ShouldRefresh = shouldRefresh
        };
    }
}

public enum WpfUpdateDocLineResultKind
{
    Updated,
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
