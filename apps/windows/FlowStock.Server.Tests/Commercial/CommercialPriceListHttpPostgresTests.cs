using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlowStock.Core.Services;
using FlowStock.Data;
using FlowStock.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Npgsql;

namespace FlowStock.Server.Tests.Commercial;

// Real HTTP routing, auth and serialization against the migrated PG16 test DB.
public sealed class CommercialPriceListHttpPostgresTests
{
    private const string WpfKey = "test-price-list-admin-key-min-32-characters";

    [Fact]
    public async Task Endpoint_enforces_401_403_and_trusted_or_admin_access()
    {
        var cs = RequiredConnection();
        await using var anonymous = await StartHostAsync(cs, null);
        using (var response = await anonymous.Client.GetAsync("/api/commercial-pricing/report?mode=all&item_id=1"))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await using var operatorHost = await StartHostAsync(cs,
            new PcWebIdentity(1, "device", "operator", "PC", PcAccessRole.Operator));
        using (var response = await operatorHost.Client.GetAsync("/api/commercial-pricing/report?mode=all&item_id=1"))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var adminHost = await StartHostAsync(cs,
            new PcWebIdentity(2, "device", "admin", "PC", PcAccessRole.Admin));
        using (var response = await adminHost.Client.GetAsync("/api/commercial-pricing/report?mode=all&item_id=1"))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var req = new HttpRequestMessage(HttpMethod.Get,
            "/api/commercial-pricing/report?mode=all&item_id=1"))
        {
            req.Headers.Add(WpfMachineAuthorization.KeyHeader, WpfKey);
            using var response = await anonymous.Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("?mode=item", "INVALID_PRICING_FILTER")]
    [InlineData("?mode=partner", "INVALID_PRICING_FILTER")]
    [InlineData("?mode=all&item_id=-1", "INVALID_PRICING_FILTER")]
    [InlineData("?mode=all&active_items_only=garbage", "INVALID_PRICING_FILTER")]
    public async Task Http_filter_validation_rejects_invalid_requests(string args, string code)
    {
        await using var host = await StartHostAsync(RequiredConnection(), null);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/commercial-pricing/report" + args);
        request.Headers.Add(WpfMachineAuthorization.KeyHeader, WpfKey);
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(code, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Http_supplier_is_rejected_even_with_correct_auth()
    {
        var cs = RequiredConnection();
        var catalog = new CatalogService(new PostgresDataStore(cs));
        var supplier = catalog.CreatePartner("Прайс поставщик " + Guid.NewGuid().ToString("N"), null, "SUPPLIER");
        await using var host = await StartHostAsync(cs, null);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/commercial-pricing/report?mode=partner&partner_id={supplier}");
        request.Headers.Add(WpfMachineAuthorization.KeyHeader, WpfKey);
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("PARTNER_IS_SUPPLIER", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Http_returns_authoritative_typed_prices_with_zero_and_fallback()
    {
        var cs = RequiredConnection();
        var catalog = new CatalogService(new PostgresDataStore(cs));
        var prices = new PartnerItemSalePriceService(new PostgresDataStore(cs));
        var id = Guid.NewGuid().ToString("N");
        var partner = catalog.CreatePartner("Прайс HTTP клиент " + id, null, "CLIENT");
        var item = catalog.CreateItem(
            "Прайс HTTP товар " + id, "PL-HTTP-" + id, null,
            "шт", null, null, null, null, false,
            defaultSalePriceGross: 178.50m);
        prices.Create(partner, item, 0m, true);

        await using var host = await StartHostAsync(cs, null);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/commercial-pricing/report?mode=partner&partner_id={partner}&item_id={item}");
        request.Headers.Add(WpfMachineAuthorization.KeyHeader, WpfKey);
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("partner", data.RootElement.GetProperty("mode").GetString());
        Assert.Equal(1, data.RootElement.GetProperty("total_count").GetInt32());
        var row = Assert.Single(data.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(partner, row.GetProperty("partner_id").GetInt64());
        Assert.Equal(item, row.GetProperty("item_id").GetInt64());
        Assert.Equal(178.50m, row.GetProperty("default_price_gross").GetDecimal());
        Assert.Equal(0m, row.GetProperty("partner_price_gross").GetDecimal());
        Assert.Equal(0m, row.GetProperty("effective_price_gross").GetDecimal());
        Assert.Equal("PARTNER_ITEM", row.GetProperty("price_source").GetString());
    }

    private static async Task<PricingHost> StartHostAsync(string connectionString, PcWebIdentity? identity)
    {
        var db = new NpgsqlConnectionStringBuilder(connectionString);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(CommercialPriceListEndpoint).Assembly.FullName
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FLOWSTOCK_PG_HOST"] = db.Host,
            ["FLOWSTOCK_PG_PORT"] = db.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["FLOWSTOCK_PG_DB"] = db.Database,
            ["FLOWSTOCK_PG_USER"] = db.Username,
            ["FLOWSTOCK_PG_PASSWORD"] = db.Password
        });
        var session = new Mock<IPcWebSessionResolver>();
        session.Setup(s => s.Resolve(It.IsAny<HttpRequest>())).Returns(identity);
        builder.Services.AddSingleton(session.Object);
        builder.Services.AddSingleton(new WpfMachineAuthorization(WpfKey));
        builder.Services.AddSingleton<CatalogAuthorization>();
        builder.Services.AddSingleton(new PartnerRoleResolver(
            new PostgresDataStore(connectionString), Path.Combine(Path.GetTempPath(), $"pricing-role-{Guid.NewGuid():N}.json")));
        var app = builder.Build();
        CommercialPriceListEndpoint.Map(app);
        await app.StartAsync();
        return new PricingHost(app, app.GetTestClient());
    }

    private static string RequiredConnection()
    {
        foreach (var key in new[] { "FLOWSTOCK_POSTGRES_TEST_CONNECTION",
            "FLOWSTOCK_POSTGRES_CONNECTION", "POSTGRES_CONNECTION_STRING" })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        throw new InvalidOperationException("FLOWSTOCK_POSTGRES_TEST_CONNECTION is required");
    }

    private sealed class PricingHost(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
