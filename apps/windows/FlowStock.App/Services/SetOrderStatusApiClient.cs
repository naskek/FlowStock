using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowStock.App;

public sealed class SetOrderStatusApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<SetOrderStatusApiCallResult> SetStatusAsync(
        ServerCloseClientOptions options,
        long orderId,
        SetOrderStatusApiRequest request,
        CancellationToken cancellationToken = default)
    {
        Uri baseUri;
        try
        {
            baseUri = new Uri(options.BaseUrl, UriKind.Absolute);
        }
        catch (UriFormatException ex)
        {
            return SetOrderStatusApiCallResult.TransportFailure(
                SetOrderStatusTransportFailureKind.InvalidConfiguration,
                $"Некорректный адрес сервера: {options.BaseUrl}",
                ex);
        }

        using var handler = CreateHandler(options);
        using var client = new HttpClient(handler)
        {
            BaseAddress = baseUri
        };

        using var responseMessage = await client.PostAsJsonAsync(
                $"/api/orders/{orderId}/status",
                request,
                cancellationToken)
            .ConfigureAwait(false);

        if (responseMessage.StatusCode == HttpStatusCode.OK)
        {
            var payload = await responseMessage.Content.ReadFromJsonAsync<SetOrderStatusApiResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (payload == null)
            {
                return SetOrderStatusApiCallResult.TransportFailure(
                    SetOrderStatusTransportFailureKind.InvalidResponse,
                    "Сервер вернул пустой ответ при смене статуса заказа.");
            }

            return SetOrderStatusApiCallResult.Success(payload);
        }

        var rawError = await responseMessage.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(rawError))
        {
            return SetOrderStatusApiCallResult.HttpError(responseMessage.StatusCode, null);
        }

        ApiErrorResponse? error;
        try
        {
            error = JsonSerializer.Deserialize<ApiErrorResponse>(rawError, JsonOptions);
        }
        catch (JsonException)
        {
            error = null;
        }

        return SetOrderStatusApiCallResult.HttpError(responseMessage.StatusCode, error);
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

public sealed class SetOrderStatusApiRequest
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;
}

public sealed class SetOrderStatusApiResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("result")]
    public string Result { get; init; } = string.Empty;

    [JsonPropertyName("order_id")]
    public long OrderId { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

public sealed class SetOrderStatusApiCallResult
{
    public SetOrderStatusApiResponse? Response { get; init; }
    public ApiErrorResponse? Error { get; init; }
    public HttpStatusCode? StatusCode { get; init; }
    public SetOrderStatusTransportFailureKind TransportFailureKind { get; init; }
    public string? TransportErrorMessage { get; init; }
    public Exception? TransportException { get; init; }

    public bool IsTransportFailure => TransportFailureKind != SetOrderStatusTransportFailureKind.None;

    public static SetOrderStatusApiCallResult Success(SetOrderStatusApiResponse response)
    {
        return new SetOrderStatusApiCallResult
        {
            Response = response
        };
    }

    public static SetOrderStatusApiCallResult HttpError(HttpStatusCode statusCode, ApiErrorResponse? error)
    {
        return new SetOrderStatusApiCallResult
        {
            StatusCode = statusCode,
            Error = error
        };
    }

    public static SetOrderStatusApiCallResult TransportFailure(
        SetOrderStatusTransportFailureKind kind,
        string message,
        Exception? exception = null)
    {
        return new SetOrderStatusApiCallResult
        {
            TransportFailureKind = kind,
            TransportErrorMessage = message,
            TransportException = exception
        };
    }
}

public enum SetOrderStatusTransportFailureKind
{
    None,
    InvalidConfiguration,
    Timeout,
    Network,
    InvalidResponse,
    Unexpected
}
