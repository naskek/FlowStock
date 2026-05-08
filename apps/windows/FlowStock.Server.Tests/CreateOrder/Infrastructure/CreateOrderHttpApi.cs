using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using FlowStock.Server;

namespace FlowStock.Server.Tests.CreateOrder.Infrastructure;

internal static class CreateOrderHttpApi
{
    public static async Task<CreateOrderEnvelope> CreateAsync(HttpClient client, CreateOrderRequest request)
    {
        using var response = await client.PostAsJsonAsync("/api/orders", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<CreateOrderEnvelope>();
        return Assert.IsType<CreateOrderEnvelope>(payload);
    }

    public static async Task<HttpResponseMessage> PostRawAsync(HttpClient client, string rawJson)
    {
        var content = new StringContent(rawJson, Encoding.UTF8, "application/json");
        return await client.PostAsync("/api/orders", content);
    }

    public static async Task<ApiResult> ReadApiResultAsync(HttpResponseMessage response, HttpStatusCode expectedStatusCode)
    {
        Assert.Equal(expectedStatusCode, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiResult>();
        return Assert.IsType<ApiResult>(payload);
    }

    internal sealed class CreateOrderRequest
    {
        [JsonPropertyName("order_ref")]
        public string? OrderRef { get; init; }

        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("partner_id")]
        public long? PartnerId { get; init; }

        [JsonPropertyName("due_date")]
        public string? DueDate { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("comment")]
        public string? Comment { get; init; }

        [JsonPropertyName("lines")]
        public List<CreateOrderLineRequest>? Lines { get; init; }
    }

    internal sealed class CreateOrderLineRequest
    {
        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("qty_ordered")]
        public double QtyOrdered { get; init; }

        [JsonPropertyName("production_purpose")]
        public string? ProductionPurpose { get; init; }
    }

    internal sealed class CreateOrderEnvelope
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; init; }

        [JsonPropertyName("result")]
        public string? Result { get; init; }

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
}
