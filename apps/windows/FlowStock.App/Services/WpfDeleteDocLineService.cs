using System.Globalization;
using System.Net.Http;
using FlowStock.Core.Models;
using Npgsql;

namespace FlowStock.App;

public sealed class WpfDeleteDocLineService
{
    private readonly string _connectionString;
    private readonly SettingsService _settings;
    private readonly FileLogger _logger;
    private readonly DeleteDocLineApiClient _apiClient;

    public WpfDeleteDocLineService(string connectionString, SettingsService settings, FileLogger logger)
    {
        _connectionString = connectionString;
        _settings = settings;
        _logger = logger;
        _apiClient = new DeleteDocLineApiClient();
    }

    public WpfServerDeleteDocLineConfiguration GetEffectiveConfiguration()
    {
        return LoadConfiguration();
    }

    public async Task<WpfDeleteDocLineResult> DeleteLinesAsync(
        Doc doc,
        IReadOnlyCollection<long> lineIds,
        CancellationToken cancellationToken = default)
    {
        var configuration = LoadConfiguration();
        var selectedIds = (lineIds ?? Array.Empty<long>())
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        if (selectedIds.Count == 0)
        {
            return WpfDeleteDocLineResult.Failure(
                WpfDeleteDocLineResultKind.ValidationFailed,
                "Не выбраны строки для удаления.");
        }

        string docUid;
        try
        {
            docUid = EnsureApiDocMapping(doc, configuration.DeviceId);
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to ensure api_docs mapping for WPF delete-line doc_id={doc.Id}", ex);
            return WpfDeleteDocLineResult.Failure(
                WpfDeleteDocLineResultKind.LocalMetadataFailure,
                "Не удалось подготовить синхронизацию документа для server delete-line. Проверьте подключение к БД и повторите.",
                ex);
        }

        var deletedCount = 0;
        var replayCount = 0;
        foreach (var lineId in selectedIds)
        {
            var request = new DeleteDocLineApiRequest
            {
                EventId = BuildEventId(doc.Id, lineId),
                DeviceId = configuration.DeviceId,
                LineId = lineId
            };

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(configuration.RequestTimeoutSeconds));

            DeleteDocLineApiCallResult apiCall;
            try
            {
                apiCall = await _apiClient.DeleteAsync(
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
                _logger.Warn($"WPF server delete-line timed out for doc_id={doc.Id}, doc_uid={docUid}, line_id={lineId}");
                return BuildFailureWithRefreshContext(
                    WpfDeleteDocLineResultKind.Timeout,
                    deletedCount,
                    replayCount,
                    "Сервер не ответил вовремя при удалении строки. Перед повтором обновите документ и проверьте фактическое состояние строк.",
                    ex);
            }
            catch (HttpRequestException ex) when (DeleteDocLineApiClient.IsTlsFailure(ex))
            {
                _logger.Error($"TLS failure during WPF server delete-line for doc_id={doc.Id}, doc_uid={docUid}", ex);
                return BuildFailureWithRefreshContext(
                    WpfDeleteDocLineResultKind.InvalidConfiguration,
                    deletedCount,
                    replayCount,
                    "Ошибка TLS при обращении к серверу. Проверьте сертификат или настройку Allow invalid TLS.",
                    ex);
            }
            catch (HttpRequestException ex)
            {
                _logger.Error($"WPF server delete-line request failed for doc_id={doc.Id}, doc_uid={docUid}", ex);
                return BuildFailureWithRefreshContext(
                    WpfDeleteDocLineResultKind.ServerUnavailable,
                    deletedCount,
                    replayCount,
                    "Не удалось связаться с сервером удаления строк. Проверьте, что FlowStock.Server запущен и доступен.",
                    ex);
            }
            catch (Exception ex)
            {
                _logger.Error($"Unexpected WPF server delete-line failure for doc_id={doc.Id}, doc_uid={docUid}", ex);
                return BuildFailureWithRefreshContext(
                    WpfDeleteDocLineResultKind.UnexpectedError,
                    deletedCount,
                    replayCount,
                    "Не удалось удалить строку через сервер. Подробности записаны в лог.",
                    ex);
            }

            if (apiCall.IsTransportFailure)
            {
                var kind = apiCall.TransportFailureKind switch
                {
                    DeleteDocLineTransportFailureKind.InvalidConfiguration => WpfDeleteDocLineResultKind.InvalidConfiguration,
                    DeleteDocLineTransportFailureKind.Timeout => WpfDeleteDocLineResultKind.Timeout,
                    DeleteDocLineTransportFailureKind.Network => WpfDeleteDocLineResultKind.ServerUnavailable,
                    DeleteDocLineTransportFailureKind.InvalidResponse => WpfDeleteDocLineResultKind.InvalidResponse,
                    _ => WpfDeleteDocLineResultKind.UnexpectedError
                };

                return BuildFailureWithRefreshContext(
                    kind,
                    deletedCount,
                    replayCount,
                    apiCall.TransportErrorMessage ?? "Не удалось выполнить server delete-line.",
                    apiCall.TransportException);
            }

            if (apiCall.Response != null)
            {
                if (!apiCall.Response.Ok)
                {
                    return BuildFailureWithRefreshContext(
                        WpfDeleteDocLineResultKind.ValidationFailed,
                        deletedCount,
                        replayCount,
                        "Сервер отклонил удаление строки.");
                }

                if (apiCall.Response.IdempotentReplay || string.Equals(apiCall.Response.Result, "IDEMPOTENT_REPLAY", StringComparison.OrdinalIgnoreCase))
                {
                    replayCount++;
                    continue;
                }

                deletedCount++;
                continue;
            }

            var httpFailure = MapHttpError(apiCall);
            return BuildFailureWithRefreshContext(
                httpFailure.Kind,
                deletedCount,
                replayCount,
                httpFailure.Message,
                httpFailure.Exception);
        }

        if (deletedCount == 0 && replayCount > 0)
        {
            return WpfDeleteDocLineResult.Success(
                WpfDeleteDocLineResultKind.IdempotentReplay,
                string.Empty,
                shouldRefresh: true);
        }

        var message = string.Empty;
        if (deletedCount > 0 && replayCount > 0)
        {
            message = $"Удалено строк: {deletedCount}. Повторно подтверждено сервером: {replayCount}.";
        }

        return WpfDeleteDocLineResult.Success(
            WpfDeleteDocLineResultKind.Deleted,
            message,
            shouldRefresh: true);
    }

    private static WpfDeleteDocLineResult MapHttpError(DeleteDocLineApiCallResult apiCall)
    {
        var errorCode = apiCall.Error?.Error;
        var message = errorCode switch
        {
            "DOC_NOT_FOUND" => "Сервер не нашел документ для удаления строки. Проверьте синхронизацию api_docs и повторите.",
            "DOC_NOT_DRAFT" => "Сервер не разрешает удалять строки в документе, который уже не является черновиком.",
            "EVENT_ID_CONFLICT" => "Сервер отклонил запрос из-за конфликта event_id. Обновите документ и повторите действие.",
            "UNKNOWN_LINE" => "Сервер не нашел выбранную строку документа.",
            _ => string.IsNullOrWhiteSpace(errorCode)
                ? $"Сервер вернул ошибку {(int?)apiCall.StatusCode ?? 0}."
                : $"Сервер вернул ошибку: {errorCode}"
        };

        var kind = errorCode switch
        {
            "DOC_NOT_FOUND" => WpfDeleteDocLineResultKind.NotFound,
            "DOC_NOT_DRAFT" => WpfDeleteDocLineResultKind.ValidationFailed,
            "EVENT_ID_CONFLICT" => WpfDeleteDocLineResultKind.EventConflict,
            "UNKNOWN_LINE" => WpfDeleteDocLineResultKind.ValidationFailed,
            _ => WpfDeleteDocLineResultKind.ServerRejected
        };

        return WpfDeleteDocLineResult.Failure(kind, message);
    }

    private static WpfDeleteDocLineResult BuildFailureWithRefreshContext(
        WpfDeleteDocLineResultKind kind,
        int deletedCount,
        int replayCount,
        string message,
        Exception? exception = null)
    {
        if (deletedCount > 0 || replayCount > 0)
        {
            var prefix = $"Часть строк уже была обработана сервером (deleted={deletedCount}, replay={replayCount}). Обновите документ и проверьте фактическое состояние строк.";
            return WpfDeleteDocLineResult.Failure(
                kind,
                $"{prefix} {message}",
                exception,
                shouldRefresh: true);
        }

        return WpfDeleteDocLineResult.Failure(kind, message, exception);
    }

    private WpfServerDeleteDocLineConfiguration LoadConfiguration()
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

        return new WpfServerDeleteDocLineConfiguration(
            baseUrl,
            deviceId,
            timeoutSeconds,
            settings.AllowInvalidTls);
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

    private static void InsertApiDoc(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string docUid,
        Doc doc,
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
        command.Parameters.AddWithValue("@from_location_id", DBNull.Value);
        command.Parameters.AddWithValue("@to_location_id", DBNull.Value);
        command.Parameters.AddWithValue("@from_hu", DBNull.Value);
        command.Parameters.AddWithValue("@to_hu", DBNull.Value);
        command.Parameters.AddWithValue("@device_id", (object?)deviceId ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void UpdateApiDoc(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string docUid,
        Doc doc,
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

    private static string BuildEventId(long docId, long lineId)
    {
        return $"wpf-line-delete-{docId}-{lineId}-{Guid.NewGuid():N}";
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
}

public sealed record WpfServerDeleteDocLineConfiguration(
    string BaseUrl,
    string DeviceId,
    int RequestTimeoutSeconds,
    bool AllowInvalidTls);

public sealed class WpfDeleteDocLineResult
{
    public WpfDeleteDocLineResultKind Kind { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool ShouldRefresh { get; init; }
    public Exception? Exception { get; init; }

    public bool IsSuccess => Kind is WpfDeleteDocLineResultKind.Deleted
        or WpfDeleteDocLineResultKind.IdempotentReplay;

    public static WpfDeleteDocLineResult Success(
        WpfDeleteDocLineResultKind kind,
        string message,
        bool shouldRefresh)
    {
        return new WpfDeleteDocLineResult
        {
            Kind = kind,
            Message = message,
            ShouldRefresh = shouldRefresh
        };
    }

    public static WpfDeleteDocLineResult Failure(
        WpfDeleteDocLineResultKind kind,
        string message,
        Exception? exception = null,
        bool shouldRefresh = false)
    {
        return new WpfDeleteDocLineResult
        {
            Kind = kind,
            Message = message,
            Exception = exception,
            ShouldRefresh = shouldRefresh
        };
    }
}

public enum WpfDeleteDocLineResultKind
{
    Deleted,
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
