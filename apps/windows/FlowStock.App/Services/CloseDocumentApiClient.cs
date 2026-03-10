using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowStock.App;

public sealed class CloseDocumentApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<ServerConnectivityCheckResult> PingAsync(
        ServerCloseClientOptions options,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        Uri baseUri;
        try
        {
            baseUri = new Uri(options.BaseUrl, UriKind.Absolute);
        }
        catch (UriFormatException ex)
        {
            return ServerConnectivityCheckResult.Failure(
                ServerConnectivityCheckStatus.BadUrl,
                $"Bad URL: {options.BaseUrl}",
                ex);
        }

        using var handler = CreateHandler(options);
        using var client = new HttpClient(handler)
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds))
        };

        try
        {
            using var response = await client.GetAsync("/api/ping", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return ServerConnectivityCheckResult.Failure(
                    ServerConnectivityCheckStatus.NotReachable,
                    $"Server not reachable: {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var payload = await response.Content.ReadFromJsonAsync<ApiPingResponse>(JsonOptions, cancellationToken);
            var message = payload == null
                ? "Server reachable."
                : $"Server reachable. version={payload.Version ?? "unknown"} server_time={payload.ServerTime ?? "unknown"}";

            return ServerConnectivityCheckResult.Success(message);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ServerConnectivityCheckResult.Failure(
                ServerConnectivityCheckStatus.Timeout,
                $"Timeout while contacting server after {Math.Max(1, timeoutSeconds)}s.",
                ex);
        }
        catch (HttpRequestException ex) when (IsTlsFailure(ex))
        {
            return ServerConnectivityCheckResult.Failure(
                ServerConnectivityCheckStatus.TlsError,
                "TLS/certificate error while contacting server.",
                ex);
        }
        catch (HttpRequestException ex)
        {
            return ServerConnectivityCheckResult.Failure(
                ServerConnectivityCheckStatus.NotReachable,
                $"Server not reachable: {ex.Message}",
                ex);
        }
        catch (Exception ex)
        {
            return ServerConnectivityCheckResult.Failure(
                ServerConnectivityCheckStatus.UnexpectedError,
                $"Unexpected error while contacting server: {ex.Message}",
                ex);
        }
    }

    public async Task<CloseDocumentApiCallResult> CloseAsync(
        ServerCloseClientOptions options,
        string docUid,
        CloseDocumentApiRequest request,
        CancellationToken cancellationToken = default)
    {
        Uri baseUri;
        try
        {
            baseUri = new Uri(options.BaseUrl, UriKind.Absolute);
        }
        catch (UriFormatException ex)
        {
            return CloseDocumentApiCallResult.TransportFailure(
                CloseDocumentTransportFailureKind.InvalidConfiguration,
                $"Некорректный адрес сервера: {options.BaseUrl}",
                ex);
        }

        using var handler = CreateHandler(options);
        using var client = new HttpClient(handler)
        {
            BaseAddress = baseUri
        };

        using var responseMessage = await client.PostAsJsonAsync(
            $"/api/docs/{Uri.EscapeDataString(docUid)}/close",
            request,
            cancellationToken);

        if (responseMessage.StatusCode == HttpStatusCode.OK)
        {
            var payload = await responseMessage.Content.ReadFromJsonAsync<CloseDocumentApiResponse>(JsonOptions, cancellationToken);
            if (payload == null)
            {
                return CloseDocumentApiCallResult.TransportFailure(
                    CloseDocumentTransportFailureKind.InvalidResponse,
                    "Сервер вернул пустой ответ при закрытии документа.");
            }

            return CloseDocumentApiCallResult.Success(payload);
        }

        var error = await responseMessage.Content.ReadFromJsonAsync<ApiErrorResponse>(JsonOptions, cancellationToken);
        return CloseDocumentApiCallResult.HttpError(responseMessage.StatusCode, error);
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

    private static bool IsTlsFailure(HttpRequestException exception)
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

public sealed class ServerCloseClientOptions
{
    public string BaseUrl { get; init; } = string.Empty;
    public bool AllowInvalidTls { get; init; }
}

public sealed class CloseDocumentApiRequest
{
    [JsonPropertyName("event_id")]
    public string EventId { get; init; } = string.Empty;

    [JsonPropertyName("device_id")]
    public string? DeviceId { get; init; }
}

public sealed class CloseDocumentApiResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("closed")]
    public bool Closed { get; init; }

    [JsonPropertyName("doc_uid")]
    public string? DocUid { get; init; }

    [JsonPropertyName("doc_ref")]
    public string? DocRef { get; init; }

    [JsonPropertyName("doc_status")]
    public string? DocStatus { get; init; }

    [JsonPropertyName("result")]
    public string Result { get; init; } = string.Empty;

    [JsonPropertyName("errors")]
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    [JsonPropertyName("idempotent_replay")]
    public bool IdempotentReplay { get; init; }

    [JsonPropertyName("already_closed")]
    public bool AlreadyClosed { get; init; }
}

public sealed class ApiErrorResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

public sealed class ApiPingResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("server_time")]
    public string? ServerTime { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }
}

public sealed class CloseDocumentApiCallResult
{
    public CloseDocumentApiResponse? Response { get; init; }
    public ApiErrorResponse? Error { get; init; }
    public HttpStatusCode? StatusCode { get; init; }
    public CloseDocumentTransportFailureKind TransportFailureKind { get; init; }
    public string? TransportErrorMessage { get; init; }
    public Exception? TransportException { get; init; }

    public bool IsSuccessResponse => Response != null;
    public bool IsTransportFailure => TransportFailureKind != CloseDocumentTransportFailureKind.None;
    public bool IsHttpError => StatusCode.HasValue && !IsSuccessResponse;

    public static CloseDocumentApiCallResult Success(CloseDocumentApiResponse response)
    {
        return new CloseDocumentApiCallResult
        {
            Response = response
        };
    }

    public static CloseDocumentApiCallResult HttpError(HttpStatusCode statusCode, ApiErrorResponse? error)
    {
        return new CloseDocumentApiCallResult
        {
            StatusCode = statusCode,
            Error = error
        };
    }

    public static CloseDocumentApiCallResult TransportFailure(
        CloseDocumentTransportFailureKind kind,
        string message,
        Exception? exception = null)
    {
        return new CloseDocumentApiCallResult
        {
            TransportFailureKind = kind,
            TransportErrorMessage = message,
            TransportException = exception
        };
    }
}

public enum CloseDocumentTransportFailureKind
{
    None,
    InvalidConfiguration,
    Timeout,
    Network,
    InvalidResponse,
    Unexpected
}

public sealed class ServerConnectivityCheckResult
{
    public bool IsSuccess => Status == ServerConnectivityCheckStatus.Reachable;
    public ServerConnectivityCheckStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
    public Exception? Exception { get; init; }

    public static ServerConnectivityCheckResult Success(string message)
    {
        return new ServerConnectivityCheckResult
        {
            Status = ServerConnectivityCheckStatus.Reachable,
            Message = message
        };
    }

    public static ServerConnectivityCheckResult Failure(
        ServerConnectivityCheckStatus status,
        string message,
        Exception? exception = null)
    {
        return new ServerConnectivityCheckResult
        {
            Status = status,
            Message = message,
            Exception = exception
        };
    }
}

public enum ServerConnectivityCheckStatus
{
    Reachable,
    NotReachable,
    TlsError,
    Timeout,
    BadUrl,
    UnexpectedError
}
