using System.IO;
using System.Net.Http;

namespace FlowStock.App.Services;

public sealed class WpfLiveUpdateClient : IDisposable
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromMilliseconds(2500);
    private readonly SettingsService _settings;
    private readonly FileLogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private Task? _runTask;

    public WpfLiveUpdateClient(SettingsService settings, FileLogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public event EventHandler? Changed;

    public event EventHandler? ResyncRequired;

    public void Start()
    {
        _runTask ??= Task.Run(() => RunAsync(_stop.Token));
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var handler = CreateHandler();
                using var client = new HttpClient(handler)
                {
                    BaseAddress = new Uri(GetServerBaseUrl(), UriKind.Absolute),
                    Timeout = Timeout.InfiniteTimeSpan
                };
                using var response = await client.GetAsync(
                    "/api/live",
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                ResyncRequired?.Invoke(this, EventArgs.Empty);

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                await ReadEventsAsync(reader, () => Changed?.Invoke(this, EventArgs.Empty), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warn($"WPF live SSE disconnected: {ex.Message}");
            }

            try
            {
                await Task.Delay(ReconnectDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal static async Task ReadEventsAsync(
        TextReader reader,
        Action onChanged,
        CancellationToken cancellationToken)
    {
        string? eventName = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line == null)
            {
                if (string.Equals(eventName, "changed", StringComparison.OrdinalIgnoreCase))
                {
                    onChanged();
                }
                return;
            }

            if (line.Length == 0)
            {
                if (string.Equals(eventName, "changed", StringComparison.OrdinalIgnoreCase))
                {
                    onChanged();
                }
                eventName = null;
                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventName = line["event:".Length..].Trim();
            }
        }
    }

    private HttpClientHandler CreateHandler()
    {
        var handler = new HttpClientHandler();
        if (ReadEnvBool("FLOWSTOCK_SERVER_ALLOW_INVALID_TLS") ?? _settings.Load().Server.AllowInvalidTls)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        return handler;
    }

    private string GetServerBaseUrl()
    {
        var env = Environment.GetEnvironmentVariable("FLOWSTOCK_SERVER_BASE_URL");
        return !string.IsNullOrWhiteSpace(env)
            ? FlowStockUrlHelper.NormalizeRootUrlOrDefault(env, FlowStockEndpointDefaults.ServerBaseUrl, Uri.UriSchemeHttps)
            : _settings.Load().Server.GetServerBaseUrlOrDefault();
    }

    private static bool? ReadEnvBool(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => null
        };
    }
}
