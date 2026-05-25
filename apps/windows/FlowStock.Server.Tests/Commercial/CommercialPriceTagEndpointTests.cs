using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Commercial;
using FlowStock.Core.Models;
using FlowStock.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace FlowStock.Server.Tests.Commercial;

public sealed class CommercialPriceTagEndpointTests
{
    private static readonly PriceGroup BaseGroup = new()
    {
        Id = 1,
        Name = CommercialPricingConstants.BasePriceGroupName,
        IsActive = true,
        IsSystem = true,
        IsDefault = true
    };

    [Fact]
    public async Task CreatePriceGroup_PersistsDefaultDiscountAndMarkup()
    {
        var commercial = new Mock<ICommercialDataStore>(MockBehavior.Strict);
        var data = new Mock<IDataStore>(MockBehavior.Strict);
        PriceGroup? savedGroup = null;
        commercial.Setup(s => s.AddPriceGroup(It.IsAny<PriceGroup>()))
            .Callback<PriceGroup>(group => savedGroup = group)
            .Returns(5L);

        await using var host = await CommercialEndpointHost.StartAsync(commercial.Object, data.Object);

        using var response = await host.Client.PostAsJsonAsync("/api/price-groups", new
        {
            name = "TEST-COMMERCIAL-Дистрибьютор -15%",
            currency = "RUB",
            vat_mode = "INCLUDED",
            default_discount_percent = 15m,
            default_markup_percent = 2m
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(savedGroup);
        Assert.Equal(15m, savedGroup.DefaultDiscountPercent);
        Assert.Equal(2m, savedGroup.DefaultMarkupPercent);
        Assert.False(savedGroup.IsDefault);
        Assert.False(savedGroup.IsSystem);
    }

    [Fact]
    public async Task GeneratePriceTags_ReturnsPriceNotFound_WhenQuoteFails()
    {
        var commercial = new Mock<ICommercialDataStore>(MockBehavior.Strict);
        var data = new Mock<IDataStore>(MockBehavior.Strict);
        data.Setup(d => d.FindItemById(100)).Returns(new Item { Id = 100, Name = "No price" });
        data.Setup(d => d.GetPartner(10)).Returns(new Partner { Id = 10, Name = "Client" });
        commercial.Setup(s => s.GetSystemBasePriceGroup()).Returns(BaseGroup);
        commercial.Setup(s => s.GetPriceGroup(5)).Returns(new PriceGroup { Id = 5, Name = "Retail", IsActive = true });
        commercial.Setup(s => s.GetActiveItemPrice(100, 1, It.IsAny<DateOnly>())).Returns((ItemPrice?)null);

        await using var host = await CommercialEndpointHost.StartAsync(commercial.Object, data.Object);

        using var response = await host.Client.PostAsJsonAsync("/api/commercial/price-tags/generate", new
        {
            price_group_id = 5,
            partner_id = 10,
            lines = new[] { new { item_id = 100, copies = 1 } }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(payload.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("PRICE_NOT_FOUND", payload.RootElement.GetProperty("error").GetString());
        Assert.Equal(100, payload.RootElement.GetProperty("item_id").GetInt64());
        Assert.Equal(1, payload.RootElement.GetProperty("line_index").GetInt32());
        commercial.Verify(s => s.AddPriceTagBatch(It.IsAny<PriceTagBatch>()), Times.Never);
        commercial.Verify(s => s.AddPriceTagBatchLine(It.IsAny<PriceTagBatchLine>()), Times.Never);
    }

    [Fact]
    public async Task GeneratePriceTags_WithExplicitPositivePrice_SavesLine()
    {
        var commercial = new Mock<ICommercialDataStore>(MockBehavior.Strict);
        var data = new Mock<IDataStore>(MockBehavior.Strict);
        PriceTagBatchLine? savedLine = null;
        commercial.Setup(s => s.AddPriceTagBatch(It.Is<PriceTagBatch>(batch => batch.PriceGroupId == 5)))
            .Returns(55L);
        commercial.Setup(s => s.AddPriceTagBatchLine(It.IsAny<PriceTagBatchLine>()))
            .Callback<PriceTagBatchLine>(line => savedLine = line);

        await using var host = await CommercialEndpointHost.StartAsync(commercial.Object, data.Object);

        using var response = await host.Client.PostAsJsonAsync("/api/commercial/price-tags/generate", new
        {
            price_group_id = 5,
            lines = new[] { new { item_id = 100, copies = 2, price = 123.45m } }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(payload.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(55, payload.RootElement.GetProperty("batch_id").GetInt64());
        Assert.NotNull(savedLine);
        Assert.Equal(55, savedLine.BatchId);
        Assert.Equal(100, savedLine.ItemId);
        Assert.Equal(2, savedLine.Copies);
        Assert.Equal(123.45m, savedLine.Price);
    }

    private sealed class CommercialEndpointHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private CommercialEndpointHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<CommercialEndpointHost> StartAsync(ICommercialDataStore commercial, IDataStore data)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(CommercialTemplateEndpoints).Assembly.FullName,
                EnvironmentName = Environments.Production
            });

            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(commercial);
            builder.Services.AddSingleton(data);
            builder.Services.AddSingleton<CommercialPricingService>();
            builder.Services.AddSingleton<DocxPlaceholderRenderer>();
            builder.Services.AddSingleton<CommercialDocumentService>();

            var app = builder.Build();
            CommercialPricingEndpoints.Map(app);
            CommercialTemplateEndpoints.Map(app);
            await app.StartAsync();

            var addresses = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>();
            var address = addresses?.Addresses.SingleOrDefault();
            if (string.IsNullOrWhiteSpace(address))
            {
                await app.StopAsync();
                await app.DisposeAsync();
                throw new InvalidOperationException("HTTP test host did not expose a listening address.");
            }

            return new CommercialEndpointHost(app, new HttpClient { BaseAddress = new Uri(address) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
