using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowStock.App;

public sealed class WpfCommercialStatisticsApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SettingsService _settings;

    public WpfCommercialStatisticsApiService(SettingsService settings, FileLogger logger)
    {
        _settings = settings;
    }

    public async Task<WpfCommercialStatisticsResult> GetAsync(
        WpfCommercialStatisticsRequest request,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string>
        {
            $"mode={Uri.EscapeDataString(request.Mode)}",
            $"group_by={Uri.EscapeDataString(request.GroupBy)}",
            $"from={request.From:yyyy-MM-dd}",
            $"to={request.To:yyyy-MM-dd}",
            $"limit={request.Limit.ToString(CultureInfo.InvariantCulture)}",
            $"offset={request.Offset.ToString(CultureInfo.InvariantCulture)}",
            $"sort={Uri.EscapeDataString(request.Sort)}"
        };
        Add(query, "detail_month", request.DetailMonth);
        Add(query, "partner_id", request.PartnerId?.ToString(CultureInfo.InvariantCulture));
        Add(query, "item_id", request.ItemId?.ToString(CultureInfo.InvariantCulture));
        Add(query, "gtin", request.Gtin);
        Add(query, "brand", request.Brand);
        Add(query, "volume", request.Volume);
        Add(query, "statuses", request.Statuses);

        using var client = CreateClient();
        using var response = await client.GetAsync(
            $"/api/commercial-statistics?{string.Join("&", query)}",
            cancellationToken).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            try
            {
                var error = JsonSerializer.Deserialize<ApiErrorDto>(raw, JsonOptions);
                throw new InvalidOperationException(error?.Message ?? error?.Error ?? "Сервер отклонил запрос статистики.");
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"Сервер вернул ошибку {(int)response.StatusCode}.");
            }
        }

        return JsonSerializer.Deserialize<WpfCommercialStatisticsResult>(raw, JsonOptions)
               ?? new WpfCommercialStatisticsResult();
    }

    private HttpClient CreateClient()
    {
        var server = _settings.Load().Server ?? new ServerSettings();
        var baseUrl = Environment.GetEnvironmentVariable("FLOWSTOCK_SERVER_BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = server.BaseUrl;
        }
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("Не настроен адрес сервера FlowStock.");
        }
        baseUrl = baseUrl.Trim();
        if (!baseUrl.Contains("://", StringComparison.Ordinal))
        {
            baseUrl = "https://" + baseUrl;
        }

        var handler = new HttpClientHandler();
        var allowInvalidTls = Environment.GetEnvironmentVariable("FLOWSTOCK_SERVER_ALLOW_INVALID_TLS")?.Trim().ToLowerInvariant()
                              is "1" or "true" or "yes" or "on"
                              || server.AllowInvalidTls;
        if (allowInvalidTls)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        return new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(Math.Max(1, server.CloseTimeoutSeconds))
        };
    }

    private static void Add(List<string> query, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            query.Add($"{name}={Uri.EscapeDataString(value.Trim())}");
        }
    }

    private sealed class ApiErrorDto
    {
        [JsonPropertyName("error")]
        public string? Error { get; set; }
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}

public sealed record WpfCommercialStatisticsRequest(
    string Mode,
    string GroupBy,
    DateTime From,
    DateTime To,
    string? DetailMonth = null,
    long? PartnerId = null,
    long? ItemId = null,
    string? Gtin = null,
    string? Brand = null,
    string? Volume = null,
    string? Statuses = null,
    int Limit = 100,
    int Offset = 0,
    string Sort = "gross_desc");

public sealed class WpfCommercialStatisticsResult
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;
    [JsonPropertyName("group_by")]
    public string GroupBy { get; set; } = string.Empty;
    [JsonPropertyName("summary")]
    public WpfCommercialStatisticsAmounts Summary { get; set; } = new();
    [JsonPropertyName("monthly")]
    public List<WpfCommercialStatisticsMonth> Monthly { get; set; } = [];
    [JsonPropertyName("groups")]
    public WpfCommercialStatisticsGroups Groups { get; set; } = new();
    [JsonPropertyName("data_quality")]
    public WpfCommercialStatisticsDataQuality DataQuality { get; set; } = new();
}

public sealed class WpfCommercialStatisticsAmounts
{
    [JsonPropertyName("order_count")]
    public int OrderCount { get; set; }
    [JsonPropertyName("document_count")]
    public int DocumentCount { get; set; }
    [JsonPropertyName("fact_count")]
    public int FactCount { get; set; }
    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }
    [JsonPropertyName("known_financial_quantity")]
    public decimal KnownFinancialQuantity { get; set; }
    [JsonPropertyName("gross")]
    public decimal Gross { get; set; }
    [JsonPropertyName("net")]
    public decimal Net { get; set; }
    [JsonPropertyName("vat")]
    public decimal Vat { get; set; }
}

public sealed class WpfCommercialStatisticsMonth
{
    [JsonPropertyName("month")]
    public string Month { get; set; } = string.Empty;
    [JsonPropertyName("amounts")]
    public WpfCommercialStatisticsAmounts Amounts { get; set; } = new();
}

public sealed class WpfCommercialStatisticsGroups
{
    [JsonPropertyName("items")]
    public List<WpfCommercialStatisticsGroup> Items { get; set; } = [];
    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }
    [JsonPropertyName("limit")]
    public int Limit { get; set; }
    [JsonPropertyName("offset")]
    public int Offset { get; set; }
}

public sealed class WpfCommercialStatisticsGroup
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;
    [JsonPropertyName("amounts")]
    public WpfCommercialStatisticsAmounts Amounts { get; set; } = new();
}

public sealed class WpfCommercialStatisticsDataQuality
{
    [JsonPropertyName("missing_price_fact_count")]
    public int MissingPriceFactCount { get; set; }
    [JsonPropertyName("missing_price_quantity")]
    public decimal MissingPriceQuantity { get; set; }
    [JsonPropertyName("missing_vat_fact_count")]
    public int MissingVatFactCount { get; set; }
    [JsonPropertyName("missing_vat_quantity")]
    public decimal MissingVatQuantity { get; set; }
    [JsonPropertyName("financially_incomplete_fact_count")]
    public int FinanciallyIncompleteFactCount { get; set; }
    [JsonPropertyName("financially_incomplete_quantity")]
    public decimal FinanciallyIncompleteQuantity { get; set; }
    [JsonPropertyName("unlinked_sales_fact_count")]
    public int UnlinkedSalesFactCount { get; set; }
    [JsonPropertyName("unlinked_sales_quantity")]
    public decimal UnlinkedSalesQuantity { get; set; }
    [JsonPropertyName("item_mismatch_sales_fact_count")]
    public int ItemMismatchSalesFactCount { get; set; }
    [JsonPropertyName("item_mismatch_sales_quantity")]
    public decimal ItemMismatchSalesQuantity { get; set; }
    [JsonPropertyName("is_financially_complete")]
    public bool IsFinanciallyComplete { get; set; }
}

internal static class CommercialStatisticsDataQualityPresentation
{
    private static readonly CultureInfo RussianCulture = CultureInfo.GetCultureInfo("ru-RU");

    public static string Format(WpfCommercialStatisticsDataQuality quality)
    {
        if (quality.IsFinanciallyComplete)
        {
            return "Финансовые snapshots заполнены для всех фактов.";
        }

        return string.Format(
            RussianCulture,
            "Без цены: {0}, количество: {1:0.######}; без НДС: {2}, количество: {3:0.######}; "
            + "непривязанные продажи: {4}, количество: {5:0.######}; "
            + "несовпадения товара: {6}, количество: {7:0.######}; "
            + "всего неполных: {8}, количество: {9:0.######}.",
            quality.MissingPriceFactCount,
            quality.MissingPriceQuantity,
            quality.MissingVatFactCount,
            quality.MissingVatQuantity,
            quality.UnlinkedSalesFactCount,
            quality.UnlinkedSalesQuantity,
            quality.ItemMismatchSalesFactCount,
            quality.ItemMismatchSalesQuantity,
            quality.FinanciallyIncompleteFactCount,
            quality.FinanciallyIncompleteQuantity);
    }
}
