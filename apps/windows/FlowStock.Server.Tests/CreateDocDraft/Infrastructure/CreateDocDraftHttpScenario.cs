using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.CreateDocDraft.Infrastructure;

internal static class CreateDocDraftHttpScenario
{
    public static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore) CreateEmptyScenario()
    {
        return (new CloseDocumentHarness(), new InMemoryApiDocStore());
    }

    public static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore) CreateInboundScenario()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedPartner(new Partner
        {
            Id = 200,
            Code = "P-200",
            Name = "Тестовый контрагент",
            CreatedAt = new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLocation(new Location
        {
            Id = 10,
            Code = "A1",
            Name = "Зона A1"
        });
        harness.SeedLocation(new Location
        {
            Id = 20,
            Code = "B1",
            Name = "Зона B1"
        });
        harness.SeedHu(new HuRecord
        {
            Id = 1,
            Code = "HU-000001",
            Status = "ACTIVE",
            CreatedAt = new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc)
        });

        return (harness, new InMemoryApiDocStore());
    }

    public static (CloseDocumentHarness Harness, InMemoryApiDocStore ApiStore) CreateDocRefCollisionScenario(string existingDocRef)
    {
        var (harness, apiStore) = CreateInboundScenario();
        harness.SeedDoc(new Doc
        {
            Id = 1,
            DocRef = existingDocRef,
            Type = DocType.Inbound,
            Status = DocStatus.Draft,
            CreatedAt = new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc)
        });
        return (harness, apiStore);
    }
}
