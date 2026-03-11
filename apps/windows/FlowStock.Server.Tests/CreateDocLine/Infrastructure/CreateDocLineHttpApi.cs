using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using FlowStock.Server;

namespace FlowStock.Server.Tests.CreateDocLine.Infrastructure;

internal static class CreateDocLineHttpApi
{
    public static async Task<AddDocLineEnvelope> AddAsync(HttpClient client, string docUid, AddDocLineRequest request)
    {
        using var response = await client.PostAsJsonAsync($"/api/docs/{docUid}/lines", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<AddDocLineEnvelope>();
        return Assert.IsType<AddDocLineEnvelope>(payload);
    }

    public static async Task<HttpResponseMessage> PostRawAsync(HttpClient client, string docUid, string rawJson)
    {
        var content = new StringContent(rawJson, Encoding.UTF8, "application/json");
        return await client.PostAsync($"/api/docs/{docUid}/lines", content);
    }

    public static async Task<ApiResult> ReadApiResultAsync(HttpResponseMessage response, HttpStatusCode expectedStatusCode)
    {
        Assert.Equal(expectedStatusCode, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiResult>();
        return Assert.IsType<ApiResult>(payload);
    }

    internal sealed class AddDocLineEnvelope
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; init; }

        [JsonPropertyName("result")]
        public string? Result { get; init; }

        [JsonPropertyName("doc_uid")]
        public string? DocUid { get; init; }

        [JsonPropertyName("doc_status")]
        public string? DocStatus { get; init; }

        [JsonPropertyName("appended")]
        public bool Appended { get; init; }

        [JsonPropertyName("idempotent_replay")]
        public bool IdempotentReplay { get; init; }

        [JsonPropertyName("line")]
        public AddDocLinePayload? Line { get; init; }
    }

    internal sealed class AddDocLinePayload
    {
        [JsonPropertyName("id")]
        public long Id { get; init; }

        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("qty")]
        public double Qty { get; init; }

        [JsonPropertyName("uom_code")]
        public string? UomCode { get; init; }

        [JsonPropertyName("order_line_id")]
        public long? OrderLineId { get; init; }

        [JsonPropertyName("from_location_id")]
        public long? FromLocationId { get; init; }

        [JsonPropertyName("to_location_id")]
        public long? ToLocationId { get; init; }

        [JsonPropertyName("from_hu")]
        public string? FromHu { get; init; }

        [JsonPropertyName("to_hu")]
        public string? ToHu { get; init; }
    }
}
