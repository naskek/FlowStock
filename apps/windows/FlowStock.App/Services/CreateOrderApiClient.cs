using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowStock.App;

public sealed class CreateOrderApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<CreateOrderApiCallResult> CreateAsync(
        ServerCloseClientOptions options,
        CreateOrderApiRequest request,
        CancellationToken cancellationToken = default)
    {
        Uri baseUri;
        try
        {
            baseUri = new Uri(options.BaseUrl, UriKind.Absolute);
        }
        catch (UriFormatException ex)
        {
            return CreateOrderApiCallResult.TransportFailure(
                CreateOrderTransportFailureKind.InvalidConfiguration,
                $"Некорректный адрес сервера: {options.BaseUrl}",
                ex);
        }

        using var handler = CreateHandler(options);
        using var client = new HttpClient(handler)
        {
            BaseAddress = baseUri
        };

        using var responseMessage = await client.PostAsJsonAsync(
                "/api/orders",
                request,
                cancellationToken)
            .ConfigureAwait(false);

        if (responseMessage.StatusCode == HttpStatusCode.OK)
        {
            var payload = await responseMessage.Content.ReadFromJsonAsync<CreateOrderApiResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (payload == null)
            {
                return CreateOrderApiCallResult.TransportFailure(
                    CreateOrderTransportFailureKind.InvalidResponse,
                    "Сервер вернул пустой ответ при создании заказа.");
            }

            return CreateOrderApiCallResult.Success(payload);
        }

        var error = await responseMessage.Content.ReadFromJsonAsync<ApiErrorResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return CreateOrderApiCallResult.HttpError(responseMessage.StatusCode, error);
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

public sealed class CreateOrderApiRequest
{
    [JsonPropertyName("order_ref")]
    public string? OrderRef { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("partner_id")]
    public long? PartnerId { get; init; }

    [JsonPropertyName("due_date")]
    public string? DueDate { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("comment")]
    public string? Comment { get; init; }

    [JsonPropertyName("bind_reserved_stock")]
    public bool? BindReservedStock { get; init; }

    [JsonPropertyName("lines")]
    public List<CreateOrderApiLineRequest> Lines { get; init; } = new();
}

public sealed class CreateOrderApiLineRequest
{
    [JsonPropertyName("item_id")]
    public long ItemId { get; init; }

    [JsonPropertyName("qty_ordered")]
    public double QtyOrdered { get; init; }
}

public sealed class CreateOrderApiResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("result")]
    public string Result { get; init; } = string.Empty;

    [JsonPropertyName("order_id")]
    public long OrderId { get; init; }

    [JsonPropertyName("order_ref")]
    public string? OrderRef { get; init; }

    [JsonPropertyName("order_ref_changed")]
    public bool OrderRefChanged { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("line_count")]
    public int LineCount { get; init; }
}

public sealed class CreateOrderApiCallResult
{
    public CreateOrderApiResponse? Response { get; init; }
    public ApiErrorResponse? Error { get; init; }
    public HttpStatusCode? StatusCode { get; init; }
    public CreateOrderTransportFailureKind TransportFailureKind { get; init; }
    public string? TransportErrorMessage { get; init; }
    public Exception? TransportException { get; init; }

    public bool IsTransportFailure => TransportFailureKind != CreateOrderTransportFailureKind.None;

    public static CreateOrderApiCallResult Success(CreateOrderApiResponse response)
    {
        return new CreateOrderApiCallResult
        {
            Response = response
        };
    }

    public static CreateOrderApiCallResult HttpError(HttpStatusCode statusCode, ApiErrorResponse? error)
    {
        return new CreateOrderApiCallResult
        {
            StatusCode = statusCode,
            Error = error
        };
    }

    public static CreateOrderApiCallResult TransportFailure(
        CreateOrderTransportFailureKind kind,
        string message,
        Exception? exception = null)
    {
        return new CreateOrderApiCallResult
        {
            TransportFailureKind = kind,
            TransportErrorMessage = message,
            TransportException = exception
        };
    }
}

public enum CreateOrderTransportFailureKind
{
    None,
    InvalidConfiguration,
    Timeout,
    Network,
    InvalidResponse,
    Unexpected
}
