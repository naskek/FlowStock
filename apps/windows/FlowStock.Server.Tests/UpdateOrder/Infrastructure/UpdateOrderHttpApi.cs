using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using FlowStock.Server;

namespace FlowStock.Server.Tests.UpdateOrder.Infrastructure;

internal static class UpdateOrderHttpApi
{
    public static async Task<UpdateOrderEnvelope> UpdateAsync(HttpClient client, long orderId, UpdateOrderRequest request)
    {
        using var response = await client.PutAsJsonAsync($"/api/orders/{orderId}", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<UpdateOrderEnvelope>();
        return Assert.IsType<UpdateOrderEnvelope>(payload);
    }

    public static async Task<HttpResponseMessage> PutRawAsync(HttpClient client, long orderId, string rawJson)
    {
        var content = new StringContent(rawJson, Encoding.UTF8, "application/json");
        return await client.PutAsync($"/api/orders/{orderId}", content);
    }

    public static async Task<ApiResult> ReadApiResultAsync(HttpResponseMessage response, HttpStatusCode expectedStatusCode)
    {
        Assert.Equal(expectedStatusCode, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiResult>();
        return Assert.IsType<ApiResult>(payload);
    }

    public static async Task<ApiErrorResult> ReadApiErrorResultAsync(HttpResponseMessage response, HttpStatusCode expectedStatusCode)
    {
        Assert.Equal(expectedStatusCode, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiErrorResult>();
        return Assert.IsType<ApiErrorResult>(payload);
    }

    internal sealed class UpdateOrderRequest
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
        public List<UpdateOrderLineRequest>? Lines { get; init; }
    }

    internal sealed class UpdateOrderLineRequest
    {
        [JsonPropertyName("order_line_id")]
        public long? OrderLineId { get; init; }

        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("qty_ordered")]
        public double QtyOrdered { get; init; }

        [JsonPropertyName("production_purpose")]
        public string? ProductionPurpose { get; init; }

        [JsonPropertyName("selected_hu_codes")]
        public IReadOnlyList<string>? SelectedHuCodes { get; init; }
    }
}
