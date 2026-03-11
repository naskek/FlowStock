using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using FlowStock.Server;

namespace FlowStock.Server.Tests.CreateDocDraft.Infrastructure;

internal static class CreateDocDraftHttpApi
{
    public static async Task<CreateDocEnvelope> CreateAsync(HttpClient client, CreateDocRequest request)
    {
        using var response = await client.PostAsJsonAsync("/api/docs", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<CreateDocEnvelope>();
        return Assert.IsType<CreateDocEnvelope>(payload);
    }

    public static async Task<HttpResponseMessage> PostRawAsync(HttpClient client, string rawJson)
    {
        var content = new StringContent(rawJson, Encoding.UTF8, "application/json");
        return await client.PostAsync("/api/docs", content);
    }

    public static async Task<ApiResult> ReadApiResultAsync(HttpResponseMessage response, HttpStatusCode expectedStatusCode)
    {
        Assert.Equal(expectedStatusCode, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiResult>();
        return Assert.IsType<ApiResult>(payload);
    }

    internal sealed class CreateDocEnvelope
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; init; }

        [JsonPropertyName("doc")]
        public CreateDocPayload? Doc { get; init; }
    }

    internal sealed class CreateDocPayload
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
}
