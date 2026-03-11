using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowStock.App;

public sealed class CreateDocDraftApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<CreateDocDraftApiCallResult> CreateAsync(
        ServerCloseClientOptions options,
        CreateDocDraftApiRequest request,
        CancellationToken cancellationToken = default)
    {
        Uri baseUri;
        try
        {
            baseUri = new Uri(options.BaseUrl, UriKind.Absolute);
        }
        catch (UriFormatException ex)
        {
            return CreateDocDraftApiCallResult.TransportFailure(
                CreateDocDraftTransportFailureKind.InvalidConfiguration,
                $"Некорректный адрес сервера: {options.BaseUrl}",
                ex);
        }

        using var handler = CreateHandler(options);
        using var client = new HttpClient(handler)
        {
            BaseAddress = baseUri
        };

        using var responseMessage = await client.PostAsJsonAsync(
            "/api/docs",
            request,
            cancellationToken);

        if (responseMessage.StatusCode == HttpStatusCode.OK)
        {
            var payload = await responseMessage.Content.ReadFromJsonAsync<CreateDocDraftApiResponse>(JsonOptions, cancellationToken);
            if (payload == null)
            {
                return CreateDocDraftApiCallResult.TransportFailure(
                    CreateDocDraftTransportFailureKind.InvalidResponse,
                    "Сервер вернул пустой ответ при создании документа.");
            }

            return CreateDocDraftApiCallResult.Success(payload);
        }

        var error = await responseMessage.Content.ReadFromJsonAsync<ApiErrorResponse>(JsonOptions, cancellationToken);
        return CreateDocDraftApiCallResult.HttpError(responseMessage.StatusCode, error);
    }

    private static HttpMessageHandler CreateHandler(ServerCloseClientOptions options)
    {
        var handler = new HttpClientHandler();
        if (options.AllowInvalidTls)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return handler;
    }

    public static bool IsTlsFailure(HttpRequestException exception)
    {
        if (exception.InnerException is AuthenticationException)
        {
            return true;
        }

        return exception.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase)
               || exception.Message.Contains("TLS", StringComparison.OrdinalIgnoreCase)
               || exception.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class CreateDocDraftApiRequest
{
    [JsonPropertyName("doc_uid")]
    public string DocUid { get; init; } = string.Empty;

    [JsonPropertyName("event_id")]
    public string EventId { get; init; } = string.Empty;

    [JsonPropertyName("device_id")]
    public string? DeviceId { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("doc_ref")]
    public string? DocRef { get; init; }

    [JsonPropertyName("comment")]
    public string? Comment { get; init; }

    [JsonPropertyName("draft_only")]
    public bool DraftOnly { get; init; }
}

public sealed class CreateDocDraftApiResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("result")]
    public string? Result { get; init; }

    [JsonPropertyName("idempotent_replay")]
    public bool IdempotentReplay { get; init; }

    [JsonPropertyName("doc")]
    public CreateDocDraftApiPayload? Doc { get; init; }
}

public sealed class CreateDocDraftApiPayload
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("doc_uid")]
    public string? DocUid { get; init; }

    [JsonPropertyName("doc_ref")]
    public string? DocRef { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("doc_ref_changed")]
    public bool DocRefChanged { get; init; }
}

public sealed class CreateDocDraftApiCallResult
{
    public CreateDocDraftApiResponse? Response { get; init; }
    public ApiErrorResponse? Error { get; init; }
    public HttpStatusCode? StatusCode { get; init; }
    public CreateDocDraftTransportFailureKind TransportFailureKind { get; init; }
    public string? TransportErrorMessage { get; init; }
    public Exception? TransportException { get; init; }

    public bool IsTransportFailure => TransportFailureKind != CreateDocDraftTransportFailureKind.None;

    public static CreateDocDraftApiCallResult Success(CreateDocDraftApiResponse response)
    {
        return new CreateDocDraftApiCallResult
        {
            Response = response
        };
    }

    public static CreateDocDraftApiCallResult HttpError(HttpStatusCode statusCode, ApiErrorResponse? error)
    {
        return new CreateDocDraftApiCallResult
        {
            StatusCode = statusCode,
            Error = error
        };
    }

    public static CreateDocDraftApiCallResult TransportFailure(
        CreateDocDraftTransportFailureKind kind,
        string message,
        Exception? exception = null)
    {
        return new CreateDocDraftApiCallResult
        {
            TransportFailureKind = kind,
            TransportErrorMessage = message,
            TransportException = exception
        };
    }
}

public enum CreateDocDraftTransportFailureKind
{
    None,
    InvalidConfiguration,
    Timeout,
    Network,
    InvalidResponse,
    Unexpected
}
