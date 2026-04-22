using System.Globalization;
using System.Net.Http;
using FlowStock.Core.Models;
using Npgsql;

namespace FlowStock.App;

public sealed class WpfBatchAddDocLineService
{
    private readonly string _connectionString;
    private readonly SettingsService _settings;
    private readonly FileLogger _logger;
    private readonly AddDocLineApiClient _apiClient;

    public WpfBatchAddDocLineService(string connectionString, SettingsService settings, FileLogger logger)
    {
        _connectionString = connectionString;
        _settings = settings;
        _logger = logger;
        _apiClient = new AddDocLineApiClient();
    }

    public bool IsServerBatchAddDocLineEnabled()
    {
        return true;
    }

    public async Task<WpfBatchAddDocLinesResult> AddLinesBatchAsync(
        Doc doc,
        IReadOnlyList<WpfAddDocLineContext> contexts,
        CancellationToken cancellationToken = default)
    {
        var configuration = LoadConfiguration();
        if (contexts.Count == 0)
        {
            return WpfBatchAddDocLinesResult.Success(0, 0, shouldRefresh: false, string.Empty);
        }

        string docUid;
        try
        {
            docUid = EnsureApiDocMapping(doc, contexts[0], configuration.DeviceId);
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to ensure api_docs mapping for WPF batch add-line doc_id={doc.Id}", ex);
            return WpfBatchAddDocLinesResult.Failure(
                WpfAddDocLineResultKind.LocalMetadataFailure,
                "Не удалось подготовить синхронизацию документа для server batch add-line. Проверьте подключение к БД и повторите.",
                ex);
        }

        var addedCount = 0;
        var replayCount = 0;

        for (var index = 0; index < contexts.Count; index++)
        {
            var lineResult = await AddLineCoreAsync(doc, docUid, contexts[index], index, configuration, cancellationToken);
            if (lineResult.IsSuccess)
            {
                if (lineResult.Kind == WpfAddDocLineResultKind.IdempotentReplay)
                {
                    replayCount++;
                }
                else
                {
                    addedCount++;
                }

                continue;
            }

            var prefix = contexts.Count > 1
                ? $"Ошибка на строке {index + 1} из {contexts.Count}: "
                : string.Empty;

            return WpfBatchAddDocLinesResult.Failure(
                lineResult.Kind,
                prefix + lineResult.Message,
                lineResult.Exception,
                shouldRefresh: addedCount > 0 || replayCount > 0 || lineResult.ShouldRefresh,
                addedCount: addedCount,
                replayCount: replayCount);
        }

        var message = replayCount > 0
            ? $"Обработано строк: {addedCount + replayCount}. Добавлено: {addedCount}. Replay: {replayCount}."
            : string.Empty;

        return WpfBatchAddDocLinesResult.Success(
            addedCount,
            replayCount,
            shouldRefresh: true,
            message: message);
    }

    private async Task<WpfAddDocLineResult> AddLineCoreAsync(
        Doc doc,
        string docUid,
        WpfAddDocLineContext context,
        int lineIndex,
        WpfServerAddDocLineConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var request = new AddDocLineApiRequest
        {
            EventId = BuildBatchEventId(doc.Id, lineIndex),
            DeviceId = configuration.DeviceId,
            ItemId = context.ItemId,
            Barcode = context.Barcode,
            OrderLineId = context.OrderLineId,
            Qty = context.QtyBase,
            UomCode = context.UomCode,
            FromLocationId = context.FromLocationId,
            ToLocationId = context.ToLocationId,
            FromHu = NormalizeValue(context.FromHu),
            ToHu = NormalizeValue(context.ToHu)
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(configuration.RequestTimeoutSeconds));

        AddDocLineApiCallResult apiCall;
        try
        {
            apiCall = await _apiClient.AddAsync(
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
            _logger.Warn($"WPF server batch add-line timed out for doc_id={doc.Id}, doc_uid={docUid}, line_index={lineIndex}, item_id={context.ItemId}");
            return WpfAddDocLineResult.Failure(
                WpfAddDocLineResultKind.Timeout,
                "Сервер не ответил вовремя при пакетном добавлении строк. Обновите документ и проверьте, какие строки уже появились.",
                ex,
                shouldRefresh: true);
        }
        catch (HttpRequestException ex) when (AddDocLineApiClient.IsTlsFailure(ex))
        {
            _logger.Error($"TLS failure during WPF server batch add-line for doc_id={doc.Id}, doc_uid={docUid}", ex);
            return WpfAddDocLineResult.Failure(
                WpfAddDocLineResultKind.InvalidConfiguration,
                "Ошибка TLS при обращении к серверу. Проверьте сертификат или настройку Allow invalid TLS.",
                ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.Error($"WPF server batch add-line request failed for doc_id={doc.Id}, doc_uid={docUid}", ex);
            return WpfAddDocLineResult.Failure(
                WpfAddDocLineResultKind.ServerUnavailable,
                "Не удалось связаться с сервером пакетного добавления строк. Проверьте, что FlowStock.Server запущен и доступен.",
                ex);
        }
        catch (Exception ex)
        {
            _logger.Error($"Unexpected WPF server batch add-line failure for doc_id={doc.Id}, doc_uid={docUid}", ex);
            return WpfAddDocLineResult.Failure(
                WpfAddDocLineResultKind.UnexpectedError,
                "Не удалось добавить строки через сервер. Подробности записаны в лог.",
                ex);
        }

        if (apiCall.IsTransportFailure)
        {
            if (apiCall.TransportException != null)
            {
                _logger.Error($"Transport failure during WPF server batch add-line for doc_id={doc.Id}, doc_uid={docUid}", apiCall.TransportException);
            }
            else
            {
                _logger.Warn($"Transport failure during WPF server batch add-line for doc_id={doc.Id}, doc_uid={docUid}: {apiCall.TransportErrorMessage}");
            }

            var kind = apiCall.TransportFailureKind switch
            {
                AddDocLineTransportFailureKind.InvalidConfiguration => WpfAddDocLineResultKind.InvalidConfiguration,
                AddDocLineTransportFailureKind.Timeout => WpfAddDocLineResultKind.Timeout,
                AddDocLineTransportFailureKind.Network => WpfAddDocLineResultKind.ServerUnavailable,
                AddDocLineTransportFailureKind.InvalidResponse => WpfAddDocLineResultKind.InvalidResponse,
                _ => WpfAddDocLineResultKind.UnexpectedError
            };

            return WpfAddDocLineResult.Failure(
                kind,
                apiCall.TransportErrorMessage ?? "Не удалось выполнить batch add-line.",
                apiCall.TransportException);
        }

        if (apiCall.Response != null)
        {
            return MapSuccessResponse(apiCall.Response);
        }

        return MapHttpError(apiCall);
    }

    private static WpfAddDocLineResult MapSuccessResponse(AddDocLineApiResponse response)
    {
        if (!response.Ok)
        {
            return WpfAddDocLineResult.Failure(
                WpfAddDocLineResultKind.ValidationFailed,
                "Сервер отклонил добавление строки.");
        }

        if (response.IdempotentReplay || string.Equals(response.Result, "IDEMPOTENT_REPLAY", StringComparison.OrdinalIgnoreCase))
        {
            return WpfAddDocLineResult.Success(
                WpfAddDocLineResultKind.IdempotentReplay,
                string.Empty,
                response,
                shouldRefresh: true);
        }

        return WpfAddDocLineResult.Success(
            WpfAddDocLineResultKind.Added,
            string.Empty,
            response,
            shouldRefresh: true);
    }

    private static WpfAddDocLineResult MapHttpError(AddDocLineApiCallResult apiCall)
    {
        var errorCode = apiCall.Error?.Error;
        var message = errorCode switch
        {
            "DOC_NOT_FOUND" => "Сервер не нашел документ для добавления строки. Проверьте синхронизацию api_docs и повторите.",
            "DOC_NOT_DRAFT" => "Сервер не разрешает добавлять строки в документ, который уже не является черновиком.",
            "EVENT_ID_CONFLICT" => "Сервер отклонил запрос из-за конфликта event_id. Обновите документ и повторите пакетную операцию.",
            "UNKNOWN_ITEM" => "Сервер не нашел выбранный товар.",
            "INVALID_QTY" => "Количество должно быть больше нуля.",
            "MISSING_LOCATION" => "Серверу не хватает обязательной локации для добавления строки.",
            "UNKNOWN_HU" => "Указанный HU не найден или недоступен.",
            _ => string.IsNullOrWhiteSpace(errorCode)
                ? $"Сервер вернул ошибку {(int?)apiCall.StatusCode ?? 0}."
                : $"Сервер вернул ошибку: {errorCode}"
        };

        var kind = errorCode switch
        {
            "DOC_NOT_FOUND" => WpfAddDocLineResultKind.NotFound,
            "DOC_NOT_DRAFT" => WpfAddDocLineResultKind.ValidationFailed,
            "EVENT_ID_CONFLICT" => WpfAddDocLineResultKind.EventConflict,
            "UNKNOWN_ITEM" => WpfAddDocLineResultKind.ValidationFailed,
            "INVALID_QTY" => WpfAddDocLineResultKind.ValidationFailed,
            "MISSING_LOCATION" => WpfAddDocLineResultKind.ValidationFailed,
            "UNKNOWN_HU" => WpfAddDocLineResultKind.ValidationFailed,
            _ => WpfAddDocLineResultKind.ServerRejected
        };

        return WpfAddDocLineResult.Failure(kind, message);
    }

    private WpfServerAddDocLineConfiguration LoadConfiguration()
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

        return new WpfServerAddDocLineConfiguration(
            baseUrl,
            deviceId,
            timeoutSeconds,
            settings.AllowInvalidTls);
    }

    private string EnsureApiDocMapping(Doc doc, WpfAddDocLineContext context, string? deviceId)
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
        WpfAddDocLineContext context,
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
        WpfAddDocLineContext context,
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

    private static string BuildBatchEventId(long docId, int lineIndex)
    {
        return $"wpf-batchline-{docId}-{lineIndex}-{Guid.NewGuid():N}";
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

public sealed class WpfBatchAddDocLinesResult
{
    public WpfAddDocLineResultKind Kind { get; init; }
    public string Message { get; init; } = string.Empty;
    public int AddedCount { get; init; }
    public int ReplayCount { get; init; }
    public bool ShouldRefresh { get; init; }
    public Exception? Exception { get; init; }

    public bool IsSuccess => Kind is WpfAddDocLineResultKind.Added
        or WpfAddDocLineResultKind.IdempotentReplay;

    public static WpfBatchAddDocLinesResult Success(
        int addedCount,
        int replayCount,
        bool shouldRefresh,
        string message)
    {
        return new WpfBatchAddDocLinesResult
        {
            Kind = addedCount > 0 ? WpfAddDocLineResultKind.Added : WpfAddDocLineResultKind.IdempotentReplay,
            AddedCount = addedCount,
            ReplayCount = replayCount,
            ShouldRefresh = shouldRefresh,
            Message = message
        };
    }

    public static WpfBatchAddDocLinesResult Failure(
        WpfAddDocLineResultKind kind,
        string message,
        Exception? exception = null,
        bool shouldRefresh = false,
        int addedCount = 0,
        int replayCount = 0)
    {
        return new WpfBatchAddDocLinesResult
        {
            Kind = kind,
            Message = message,
            Exception = exception,
            ShouldRefresh = shouldRefresh,
            AddedCount = addedCount,
            ReplayCount = replayCount
        };
    }
}
