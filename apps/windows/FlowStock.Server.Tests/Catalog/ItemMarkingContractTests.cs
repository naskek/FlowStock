using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace FlowStock.Server.Tests.Catalog;

public sealed class ItemMarkingContractTests
{
    [Theory]
    [InlineData(true, false, true, "GTIN_REQUIRED")]
    [InlineData(true, true, false, null)]
    [InlineData(false, false, false, null)]
    public async Task LookupReturnsCanonicalApplicabilityAndCompatibilityAlias(
        bool typeEnabled,
        bool exempt,
        bool expectedApplicable,
        string? expectedError)
    {
        var item = new Item
        {
            Id = 1,
            Name = "Товар",
            Barcode = "SKU-1",
            Gtin = null,
            ItemTypeEnableMarking = typeEnabled,
            ChzMarkingExempt = exempt
        };
        var store = new Mock<IDataStore>();
        store.Setup(candidate => candidate.FindItemByBarcode("SKU-1")).Returns(item);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(store.Object);
        builder.Services.AddSingleton<CatalogService>();
        builder.Services.AddSingleton(new WpfMachineAuthorization("test-machine-key-at-least-32-characters"));
        builder.Services.AddSingleton<IPcWebSessionResolver>(new NullSessionResolver());
        builder.Services.AddSingleton<CatalogAuthorization>();
        var app = builder.Build();
        ItemCatalogEndpoints.Map(app, postgresConnectionString: null);
        await app.StartAsync();
        await using (app)
        {
            using var response = await app.GetTestClient().GetAsync("/api/items/by-barcode/SKU-1");
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            Assert.Equal(exempt, root.GetProperty("chz_marking_exempt").GetBoolean());
            Assert.Equal(expectedApplicable, root.GetProperty("chz_marking_applicable").GetBoolean());
            Assert.Equal(expectedApplicable, root.GetProperty("cz_marking_required").GetBoolean());
            var error = root.GetProperty("chz_marking_configuration_error");
            if (expectedError == null)
            {
                Assert.Equal(JsonValueKind.Null, error.ValueKind);
            }
            else
            {
                Assert.Equal(expectedError, error.GetString());
            }
        }
    }

    private sealed class NullSessionResolver : IPcWebSessionResolver
    {
        public PcWebIdentity? Resolve(HttpRequest request) => null;
    }
}
