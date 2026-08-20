namespace FlowStock.DesktopUpdate;

public static class DesktopUpdateEndpointResolver
{
    public static Uri Resolve() => Resolve(Environment.GetEnvironmentVariable);

    public static Uri Resolve(Func<string, string?> readEnvironment)
    {
        ArgumentNullException.ThrowIfNull(readEnvironment);
        var configured = readEnvironment(DesktopUpdateConstants.UpdateServerBaseUrlEnvironmentVariable);
        return Validate(string.IsNullOrWhiteSpace(configured)
            ? DesktopUpdateConstants.DefaultUpdateServerBaseUrl
            : configured);
    }

    public static Uri Validate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(
                $"Некорректный доверенный server обновлений: '{value}'. Ожидается absolute root URL.");
        }

        return Validate(uri);
    }

    public static Uri Validate(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri)
        {
            throw new InvalidOperationException(
                $"Некорректный доверенный server обновлений: '{uri}'. Ожидается absolute root URL.");
        }

        var isHttps = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isLoopbackHttp = uri.IsLoopback
                             && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(uri.Host)
            || (!isHttps && !isLoopbackHttp)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                $"Некорректный доверенный server обновлений: '{uri}'. "
                + "Production endpoint должен быть HTTPS root URL без credentials, path, query и fragment; "
                + "HTTP разрешён только для loopback test harness.");
        }

        return new Uri(uri.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/", UriKind.Absolute);
    }
}

public static class DesktopUpdateHttpClientFactory
{
    public static HttpClientHandler CreateHandler() => new();

    public static HttpClient Create(TimeSpan timeout) =>
        new(CreateHandler()) { Timeout = timeout };
}
