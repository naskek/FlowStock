using System.Globalization;
using System.Net.Http;
using FlowStock.Core.Models;

namespace FlowStock.App;

public sealed class WpfCreateDocDraftService
{
    private readonly SettingsService _settings;
    private readonly FileLogger _logger;
    private readonly CreateDocDraftApiClient _apiClient;

    public WpfCreateDocDraftService(SettingsService settings, FileLogger logger)
    {
        _settings = settings;
        _logger = logger;
        _apiClient = new CreateDocDraftApiClient();
    }

    public WpfServerCreateDocDraftConfiguration GetEffectiveConfiguration()
    {
        return LoadConfiguration();
    }

    public async Task<WpfCreateDocDraftResult> CreateDraftAsync(
        WpfCreateDocDraftContext context,
        CancellationToken cancellationToken = default)
    {
        var configuration = LoadConfiguration();
        var request = new CreateDocDraftApiRequest
        {
            DocUid = context.DocUid,
            EventId = context.EventId,
            DeviceId = configuration.DeviceId,
            Type = DocTypeMapper.ToOpString(context.DocType),
            DocRef = NormalizeValue(context.RequestedDocRef),
            Comment = NormalizeValue(context.Comment),
            DraftOnly = true
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(configuration.RequestTimeoutSeconds));

        CreateDocDraftApiCallResult apiCall;
        try
        {
            apiCall = await _apiClient.CreateAsync(
                new ServerCloseClientOptions
                {
                    BaseUrl = configuration.BaseUrl,
                    AllowInvalidTls = configuration.AllowInvalidTls
                },
                request,
                timeoutCts.Token);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Warn($"WPF server create timed out for doc_uid={context.DocUid}, type={context.DocType}");
            return WpfCreateDocDraftResult.Failure(
                WpfCreateDocDraftResultKind.Timeout,
                "Сервер не ответил вовремя при создании черновика. Повторите создание в этом же окне: будет использован тот же request id.",
                ex);
        }
        catch (HttpRequestException ex) when (CreateDocDraftApiClient.IsTlsFailure(ex))
        {
            _logger.Error($"TLS failure during WPF server draft create for doc_uid={context.DocUid}", ex);
            return WpfCreateDocDraftResult.Failure(
                WpfCreateDocDraftResultKind.InvalidConfiguration,
                "Ошибка TLS при обращении к серверу. Проверьте сертификат или настройку Allow invalid TLS.",
                ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.Error($"WPF server draft create request failed for doc_uid={context.DocUid}", ex);
            return WpfCreateDocDraftResult.Failure(
                WpfCreateDocDraftResultKind.ServerUnavailable,
                "Не удалось связаться с сервером создания документов. Проверьте, что FlowStock.Server запущен и доступен.",
                ex);
        }
        catch (Exception ex)
        {
            _logger.Error($"Unexpected WPF server draft create failure for doc_uid={context.DocUid}", ex);
            return WpfCreateDocDraftResult.Failure(
                WpfCreateDocDraftResultKind.UnexpectedError,
                "Не удалось создать документ через сервер. Подробности записаны в лог.",
                ex);
        }

        if (apiCall.IsTransportFailure)
        {
            if (apiCall.TransportException != null)
            {
                _logger.Error($"Transport failure during WPF server draft create for doc_uid={context.DocUid}", apiCall.TransportException);
            }
            else
            {
                _logger.Warn($"Transport failure during WPF server draft create for doc_uid={context.DocUid}: {apiCall.TransportErrorMessage}");
            }

            var kind = apiCall.TransportFailureKind switch
            {
                CreateDocDraftTransportFailureKind.InvalidConfiguration => WpfCreateDocDraftResultKind.InvalidConfiguration,
                CreateDocDraftTransportFailureKind.Timeout => WpfCreateDocDraftResultKind.Timeout,
                CreateDocDraftTransportFailureKind.Network => WpfCreateDocDraftResultKind.ServerUnavailable,
                CreateDocDraftTransportFailureKind.InvalidResponse => WpfCreateDocDraftResultKind.InvalidResponse,
                _ => WpfCreateDocDraftResultKind.UnexpectedError
            };

            return WpfCreateDocDraftResult.Failure(
                kind,
                apiCall.TransportErrorMessage ?? "Не удалось выполнить server create.",
                apiCall.TransportException);
        }

        if (apiCall.Response != null)
        {
            return MapSuccessResponse(context, apiCall.Response);
        }

        return MapHttpError(apiCall);
    }

    private static WpfCreateDocDraftResult MapSuccessResponse(
        WpfCreateDocDraftContext context,
        CreateDocDraftApiResponse response)
    {
        if (!response.Ok)
        {
            return WpfCreateDocDraftResult.Failure(
                WpfCreateDocDraftResultKind.ValidationFailed,
                "Сервер отклонил создание документа.");
        }

        if (response.Doc == null
            || response.Doc.Id <= 0
            || string.IsNullOrWhiteSpace(response.Doc.DocUid)
            || string.IsNullOrWhiteSpace(response.Doc.DocRef))
        {
            return WpfCreateDocDraftResult.Failure(
                WpfCreateDocDraftResultKind.InvalidResponse,
                "Сервер вернул неполный ответ при создании документа.");
        }

        var docRefChanged = response.Doc.DocRefChanged;
        if (!docRefChanged && !string.IsNullOrWhiteSpace(context.RequestedDocRef))
        {
            docRefChanged = !string.Equals(
                NormalizeValue(context.RequestedDocRef),
                NormalizeValue(response.Doc.DocRef),
                StringComparison.OrdinalIgnoreCase);
        }

        var message = docRefChanged || string.IsNullOrWhiteSpace(context.RequestedDocRef)
            ? $"Сервер назначил номер документа: {response.Doc.DocRef}"
            : string.Empty;

        if (response.IdempotentReplay
            || string.Equals(response.Result, "IDEMPOTENT_REPLAY", StringComparison.OrdinalIgnoreCase))
        {
            return WpfCreateDocDraftResult.Success(
                WpfCreateDocDraftResultKind.IdempotentReplay,
                message,
                response);
        }

        return WpfCreateDocDraftResult.Success(
            WpfCreateDocDraftResultKind.Created,
            message,
            response);
    }

    private static WpfCreateDocDraftResult MapHttpError(CreateDocDraftApiCallResult apiCall)
    {
        var errorCode = apiCall.Error?.Error;
        var message = errorCode switch
        {
            "EVENT_ID_CONFLICT" => "Сервер отклонил запрос из-за конфликта event_id. Повторите создание в этом окне или начните заново.",
            "DUPLICATE_DOC_UID" => "Сервер обнаружил конфликт по doc_uid. Закройте окно создания и откройте его заново.",
            "DOC_REF_EXISTS" => "Сервер не смог подобрать свободный номер документа. Обновите номер и повторите.",
            "INVALID_TYPE" => "Сервер отклонил тип документа.",
            "UNKNOWN_ORDER" => "Сервер не нашел указанный заказ.",
            "UNKNOWN_PARTNER" => "Сервер не нашел указанного контрагента.",
            "UNKNOWN_LOCATION" => "Сервер не нашел указанную локацию.",
            "UNKNOWN_HU" => "Указанный HU не найден или недоступен.",
            "ORDER_PARTNER_MISMATCH" => "Контрагент не совпадает с выбранным заказом.",
            "INTERNAL_ORDER_NOT_ALLOWED_FOR_OUTBOUND" => "Внутренний заказ нельзя использовать для клиентской отгрузки.",
            "MISSING_PARTNER" => "Серверу не хватает обязательного контрагента для создания документа.",
            "MISSING_LOCATION" => "Серверу не хватает обязательной локации для создания документа.",
            _ => string.IsNullOrWhiteSpace(errorCode)
                ? $"Сервер вернул ошибку {(int?)apiCall.StatusCode ?? 0}."
                : $"Сервер вернул ошибку: {errorCode}"
        };

        var kind = errorCode switch
        {
            "EVENT_ID_CONFLICT" => WpfCreateDocDraftResultKind.EventConflict,
            "DUPLICATE_DOC_UID" => WpfCreateDocDraftResultKind.DuplicateDocUid,
            "DOC_REF_EXISTS" => WpfCreateDocDraftResultKind.ValidationFailed,
            "INVALID_TYPE" => WpfCreateDocDraftResultKind.ValidationFailed,
            "UNKNOWN_ORDER" => WpfCreateDocDraftResultKind.ValidationFailed,
            "UNKNOWN_PARTNER" => WpfCreateDocDraftResultKind.ValidationFailed,
            "UNKNOWN_LOCATION" => WpfCreateDocDraftResultKind.ValidationFailed,
            "UNKNOWN_HU" => WpfCreateDocDraftResultKind.ValidationFailed,
            "ORDER_PARTNER_MISMATCH" => WpfCreateDocDraftResultKind.ValidationFailed,
            "INTERNAL_ORDER_NOT_ALLOWED_FOR_OUTBOUND" => WpfCreateDocDraftResultKind.ValidationFailed,
            "MISSING_PARTNER" => WpfCreateDocDraftResultKind.ValidationFailed,
            "MISSING_LOCATION" => WpfCreateDocDraftResultKind.ValidationFailed,
            _ => WpfCreateDocDraftResultKind.ServerRejected
        };

        return WpfCreateDocDraftResult.Failure(kind, message);
    }

    private WpfServerCreateDocDraftConfiguration LoadConfiguration()
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

        return new WpfServerCreateDocDraftConfiguration(
            baseUrl,
            deviceId,
            timeoutSeconds,
            settings.AllowInvalidTls);
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

public sealed record WpfCreateDocDraftContext(
    string DocUid,
    string EventId,
    DocType DocType,
    string? RequestedDocRef,
    string? Comment);

public sealed record WpfServerCreateDocDraftConfiguration(
    string BaseUrl,
    string DeviceId,
    int RequestTimeoutSeconds,
    bool AllowInvalidTls);

public sealed class WpfCreateDocDraftResult
{
    public WpfCreateDocDraftResultKind Kind { get; init; }
    public string Message { get; init; } = string.Empty;
    public CreateDocDraftApiResponse? Response { get; init; }
    public Exception? Exception { get; init; }

    public bool IsSuccess => Kind is WpfCreateDocDraftResultKind.Created
        or WpfCreateDocDraftResultKind.IdempotentReplay;

    public static WpfCreateDocDraftResult Success(
        WpfCreateDocDraftResultKind kind,
        string message,
        CreateDocDraftApiResponse response)
    {
        return new WpfCreateDocDraftResult
        {
            Kind = kind,
            Message = message,
            Response = response
        };
    }

    public static WpfCreateDocDraftResult Failure(
        WpfCreateDocDraftResultKind kind,
        string message,
        Exception? exception = null)
    {
        return new WpfCreateDocDraftResult
        {
            Kind = kind,
            Message = message,
            Exception = exception
        };
    }
}

public enum WpfCreateDocDraftResultKind
{
    Created,
    IdempotentReplay,
    ValidationFailed,
    EventConflict,
    DuplicateDocUid,
    ServerRejected,
    ServerUnavailable,
    Timeout,
    InvalidConfiguration,
    InvalidResponse,
    UnexpectedError
}
