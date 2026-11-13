using System.Net;
using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace FlowStock.Server.Tests.Orders;

public sealed class BusinessNotificationEndpointTests
{
    [Fact]
    public async Task Journal_ValidatesFiltersAndPreservesSnapshotWireContract()
    {
        var mock = new Mock<IDataStore>();
        BusinessNotificationQuery? captured = null;
        mock.As<IBusinessNotificationJournalStore>().Setup(s => s.GetBusinessNotificationPage(It.IsAny<BusinessNotificationQuery>(), "wpf-global"))
            .Callback<BusinessNotificationQuery, string>((q, _) => captured = q)
            .Returns(new BusinessNotificationPage { Total = 70, UnreadCount = 120, Items = new[]
            {
                new BusinessNotification { Id = 1, EventType = "CUSTOMER_ORDER_READY", Payload = new() { OrderId = 42, OrderRef = "SO-42", PartnerName = "Клиент" } }
            } });
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(mock.Object);
        await using var app = builder.Build();
        BusinessNotificationEndpoints.Map(app);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        using var response = await client.GetAsync("/api/notifications/journal?unreadOnly=false&limit=50&offset=50&category=READY&search=SO-42&from=2026-11-01T00:00:00Z&to=2026-12-01T00:00:00Z");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(120, json.RootElement.GetProperty("unread_count").GetInt32());
        Assert.Equal("Клиент", json.RootElement.GetProperty("items")[0].GetProperty("payload").GetProperty("partner_name").GetString());
        Assert.Equal(50, captured!.Offset); Assert.False(captured.UnreadOnly);
        Assert.Equal(DateTimeKind.Utc, captured.FromUtc!.Value.Kind);
        foreach (var query in new[] { "limit=0", "offset=-1", "category=raw", "from=bad", "unreadOnly=bad", "from=2026-12-01&to=2026-11-01" })
        {
            using var invalid = await client.GetAsync("/api/notifications/journal?" + query);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        mock.As<IBusinessNotificationJournalStore>().Verify(s => s.GetBusinessNotificationPage(It.IsAny<BusinessNotificationQuery>(), "wpf-global"), Times.Once);
    }
}
