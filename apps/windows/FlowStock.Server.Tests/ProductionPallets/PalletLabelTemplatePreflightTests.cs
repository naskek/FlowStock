using FlowStock.App;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class PalletLabelTemplatePreflightTests
{
    private static PalletLabelPrintRow BuildRow(DateTime? productionDate, string batchNumber, string storageConditions = "")
    {
        return new PalletLabelPrintRow
        {
            HuCode = "HU-0000001",
            ItemName = "Товар",
            Qty = 1,
            ProductionDate = productionDate,
            BatchNumber = batchNumber,
            StorageConditions = storageConditions
        };
    }

    [Fact]
    public void ResolveRequiredFields_BothEmpty_RequiresNothing()
    {
        var rows = new[] { BuildRow(productionDate: null, batchNumber: string.Empty) };

        var required = PalletLabelTemplatePreflight.ResolveRequiredFields(rows);

        Assert.Empty(required);
    }

    [Fact]
    public void ResolveRequiredFields_OnlyDate_RequiresProductionDate()
    {
        var rows = new[] { BuildRow(productionDate: new DateTime(2026, 6, 29), batchNumber: string.Empty) };

        var required = PalletLabelTemplatePreflight.ResolveRequiredFields(rows);

        var field = Assert.Single(required);
        Assert.Equal("ProductionDate", field.Name);
        Assert.Equal("29.06.2026", field.Value);
    }

    [Fact]
    public void ResolveRequiredFields_OnlyBatch_RequiresBatchNumber()
    {
        var rows = new[] { BuildRow(productionDate: null, batchNumber: "ПАРТИЯ-2026-15") };

        var required = PalletLabelTemplatePreflight.ResolveRequiredFields(rows);

        var field = Assert.Single(required);
        Assert.Equal("BatchNumber", field.Name);
        Assert.Equal("ПАРТИЯ-2026-15", field.Value);
    }

    [Fact]
    public void ResolveRequiredFields_OnlyStorageConditions_RequiresStorageConditions()
    {
        var rows = new[] { BuildRow(productionDate: null, batchNumber: string.Empty, storageConditions: "от 0С до +10С") };

        var required = PalletLabelTemplatePreflight.ResolveRequiredFields(rows);

        var field = Assert.Single(required);
        Assert.Equal("StorageConditions", field.Name);
        Assert.Equal("от 0С до +10С", field.Value);
    }

    [Fact]
    public void ResolveRequiredFields_EmptyStorageConditions_DoesNotRequireStorageConditions()
    {
        var rows = new[] { BuildRow(productionDate: null, batchNumber: string.Empty, storageConditions: string.Empty) };

        var required = PalletLabelTemplatePreflight.ResolveRequiredFields(rows);

        Assert.Empty(required);
    }

    [Fact]
    public void ResolveRequiredFields_BothFilled_RequiresBoth()
    {
        var rows = new[] { BuildRow(productionDate: new DateTime(2026, 6, 29), batchNumber: "ПАРТИЯ-2026-15") };

        var required = PalletLabelTemplatePreflight.ResolveRequiredFields(rows);

        Assert.Equal(2, required.Count);
        Assert.Contains(required, f => f.Name == "ProductionDate" && f.Value == "29.06.2026");
        Assert.Contains(required, f => f.Name == "BatchNumber" && f.Value == "ПАРТИЯ-2026-15");
    }

    [Fact]
    public void ResolveRequiredFields_UsesFirstNonEmptyValueAcrossRows()
    {
        var rows = new[]
        {
            BuildRow(productionDate: null, batchNumber: string.Empty),
            BuildRow(productionDate: new DateTime(2026, 6, 29), batchNumber: "ПАРТИЯ")
        };

        var required = PalletLabelTemplatePreflight.ResolveRequiredFields(rows);

        Assert.Contains(required, f => f.Name == "ProductionDate" && f.Value == "29.06.2026");
        Assert.Contains(required, f => f.Name == "BatchNumber" && f.Value == "ПАРТИЯ");
    }

    [Fact]
    public void MissingFieldMessage_FormatsExpectedText()
    {
        Assert.Equal("В шаблоне BarTender отсутствует поле ProductionDate.",
            PalletLabelTemplatePreflight.MissingFieldMessage("ProductionDate"));
        Assert.Equal("В шаблоне BarTender отсутствует поле BatchNumber.",
            PalletLabelTemplatePreflight.MissingFieldMessage("BatchNumber"));
        Assert.Equal("В шаблоне BarTender отсутствует поле StorageConditions.",
            PalletLabelTemplatePreflight.MissingFieldMessage("StorageConditions"));
    }
}
