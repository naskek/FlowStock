using System.Text.Json;
using FlowStock.App;
using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderLineCanonicalPresentationTests
{
    [Fact]
    public void ResolveProductionHuCodesDisplay_UsesArrayWhenDisplayMissing()
    {
        var display = OrderLineCanonicalPresentation.ResolveProductionHuCodesDisplay(
            null,
            ["HU-0000540", "HU-0000523", "HU-0000524"]);

        Assert.Equal("HU-0000523, HU-0000524, HU-0000540", display);
    }

    [Fact]
    public void ApplyPersistedLine_UpdatesProductionHuCodes_ForWpfReload()
    {
        var target = new OrderLineView
        {
            Id = 191,
            OrderId = 86,
            ItemId = 6,
            ItemName = "Item",
            QtyOrdered = 1200,
            ProductionHuCodes = "HU-OLD-1, HU-OLD-2",
            QtyProduced = 1200,
            FilledPalletQty = 1200
        };
        var source = new OrderLineView
        {
            Id = 191,
            OrderId = 86,
            ItemId = 6,
            ItemName = "Item",
            QtyOrdered = 2400,
            ProductionHuCodes = "HU-0000523, HU-0000524, HU-0000540, HU-0000542",
            QtyProduced = 1200,
            QtyRemaining = 1200,
            FilledPalletQty = 1200,
            PlannedPalletQty = 1200,
            PlannedPalletCount = 2,
            FilledPalletCount = 2
        };

        var changed = false;
        target.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(OrderLineView.ProductionHuCodes))
            {
                changed = true;
            }
        };

        OrderLineCanonicalPresentation.ApplyPersistedLine(target, source, OrderType.Internal);

        Assert.True(changed);
        Assert.Equal(2400, target.QtyOrdered, 3);
        Assert.Contains("HU-0000540", target.ProductionHuCodes, StringComparison.Ordinal);
        Assert.Contains("HU-0000542", target.ProductionHuCodes, StringComparison.Ordinal);
        Assert.Equal(1200, target.QtyRemaining, 3);
    }

    [Fact]
    public void ApplyPersistedLine_RefreshesCanonicalHuPresentationAndOperatorRows()
    {
        var target = new OrderLineView
        {
            Id = 1631,
            OrderId = 1062,
            ItemId = 5,
            ItemName = "Товар",
            QtyOrdered = 30
        };
        var source = new OrderLineView
        {
            Id = 1631,
            OrderId = 1062,
            ItemId = 5,
            ItemName = "Товар",
            QtyOrdered = 30,
            HuPresentation = new OrderLineHuPresentation
            {
                OperationalHus =
                [
                    new OperationalHuPresentation
                    {
                        HuCode = "HU-0001744",
                        Qty = 10,
                        State = new HuSemanticStatePresentation("SHIPPED", "Отгружен")
                    }
                ]
            }
        };
        var operatorRowsChanged = false;
        target.PropertyChanged += (_, args) =>
            operatorRowsChanged |= args.PropertyName == nameof(OrderLineView.OperatorHuDisplayRows);

        OrderLineCanonicalPresentation.ApplyPersistedLine(target, source, OrderType.Customer);

        Assert.Same(source.HuPresentation, target.HuPresentation);
        Assert.Equal("HU-0001744", Assert.Single(target.OperatorHuDisplayRows).HuCode);
        Assert.True(operatorRowsChanged);
    }

    [Fact]
    public void AwaitingFillWithoutPrintedLabel_UsesItalicWarningPresentation()
    {
        var line = new OrderLineView
        {
            HuPresentation = new OrderLineHuPresentation
            {
                ProductionTasks =
                [
                    new ProductionTaskPresentation
                    {
                        HuCode = "HU-0002001",
                        Qty = 600,
                        State = new HuSemanticStatePresentation("AWAITING_FILL", "Ожидает наполнения")
                    }
                ]
            }
        };

        var row = Assert.Single(line.OperatorHuDisplayRows);

        Assert.True(row.IsItalic);
        Assert.Equal("Паллетная этикетка ещё не печаталась", row.ToolTip);
        Assert.Equal("HU-0002001 · Ожидает наполнения · 600", row.DisplayText);
    }

    [Fact]
    public void AwaitingFillWithPrintedLabel_UsesNormalPresentation()
    {
        var line = new OrderLineView
        {
            HuPresentation = new OrderLineHuPresentation
            {
                ProductionTasks =
                [
                    new ProductionTaskPresentation
                    {
                        HuCode = "HU-0002002",
                        Qty = 600,
                        IsLabelPrinted = true,
                        State = new HuSemanticStatePresentation("AWAITING_FILL", "Ожидает наполнения")
                    }
                ]
            }
        };

        var row = Assert.Single(line.OperatorHuDisplayRows);

        Assert.False(row.IsItalic);
        Assert.Null(row.ToolTip);
        Assert.Equal("HU-0002002 · Ожидает наполнения · 600", row.DisplayText);
    }

    [Fact]
    public void OperationalHu_DoesNotUseProductionPrintWarningPresentation()
    {
        var line = new OrderLineView
        {
            HuPresentation = new OrderLineHuPresentation
            {
                OperationalHus =
                [
                    new OperationalHuPresentation
                    {
                        HuCode = "HU-0002003",
                        Qty = 600,
                        State = new HuSemanticStatePresentation("ON_STOCK", "На складе")
                    }
                ]
            }
        };

        var row = Assert.Single(line.OperatorHuDisplayRows);

        Assert.False(row.IsItalic);
        Assert.Null(row.ToolTip);
    }

    [Fact]
    public void WpfReadMapper_PreservesProductionLabelPrintedFact()
    {
        using var json = JsonDocument.Parse("""
            {
              "id": 191,
              "order_id": 86,
              "item_id": 6,
              "item_name": "Товар",
              "qty_ordered": 600,
              "hu_presentation": {
                "production_tasks": [
                  {
                    "hu_code": "HU-0002004",
                    "qty": 600,
                    "uom": "шт",
                    "is_label_printed": true,
                    "state": { "code": "AWAITING_FILL", "label": "Ожидает наполнения" }
                  }
                ],
                "operational_hus": []
              }
            }
            """);

        var line = WpfReadApiService.MapOrderLineView(json.RootElement);

        Assert.True(Assert.Single(line.HuPresentation!.ProductionTasks).IsLabelPrinted);
        Assert.False(Assert.Single(line.OperatorHuDisplayRows).IsItalic);
    }

    [Fact]
    public void InternalOrderLine_HuDisplayRows_ExposeProductionHuEntries()
    {
        var line = new OrderLineView
        {
            Id = 191,
            OrderId = 58,
            ItemId = 6,
            ItemName = "Горчица",
            ProductionPurpose = ProductionLinePurpose.InternalStock,
            ProductionHuDisplayEntries =
            [
                new OrderLineHuDisplayEntry("HU-0000602", "план", 600, IsWarehouseBound: false, SortOrder: 2),
                new OrderLineHuDisplayEntry("HU-0000601", "план", 600, IsWarehouseBound: false, SortOrder: 1)
            ]
        };

        var rows = line.HuDisplayRows;

        Assert.Equal(2, rows.Count);
        Assert.Equal("HU-0000601", rows[0].HuCode);
        Assert.Equal("план", rows[0].Label);
        Assert.Equal(600, rows[0].Qty, 3);
        Assert.False(rows[0].IsBold);
        Assert.Equal("HU-0000601 · план · 600", rows[0].DisplayText);
        Assert.Equal("HU-0000602", rows[1].HuCode);
    }

    [Fact]
    public void HuDisplayRows_MergeFutureAndFateRows_InDeterministicCategoryOrder()
    {
        var line = new OrderLineView
        {
            ProductionHuDisplayEntries =
            [
                new OrderLineHuDisplayEntry("HU-PRINTED", "напечатано", 378, false, 2),
                new OrderLineHuDisplayEntry("HU-FILLED-OLD", "наполнено", 378, false, 2)
            ],
            HuFateDisplayEntries =
            [
                new OrderLineHuDisplayEntry("HU-SHIPPED", "отгружено", 600, false, OrderLineHuFateDisplayBuilder.ShippedSortOrder),
                new OrderLineHuDisplayEntry("HU-RESERVED", "резерв", 378, false, OrderLineHuFateDisplayBuilder.ReservedSortOrder),
                new OrderLineHuDisplayEntry("HU-FILLED-B", "наполнено", 378, false, OrderLineHuFateDisplayBuilder.FilledSortOrder),
                new OrderLineHuDisplayEntry("HU-FILLED-A", "наполнено", 378, false, OrderLineHuFateDisplayBuilder.FilledSortOrder)
            ]
        };

        Assert.Equal(
            ["HU-PRINTED", "HU-FILLED-A", "HU-FILLED-B", "HU-RESERVED", "HU-SHIPPED"],
            line.HuDisplayRows.Select(row => row.HuCode));
        Assert.DoesNotContain(line.HuDisplayRows, row => row.HuCode == "HU-FILLED-OLD");
    }

    [Fact]
    public void HuDisplayRows_AwaitingShipmentReplacesBareFilledStatusForSameHu()
    {
        var line = new OrderLineView
        {
            ProductionHuDisplayEntries =
            [
                new OrderLineHuDisplayEntry("HU-CUSTOMER", "наполнено", 600, false, 2)
            ],
            HuFateDisplayEntries =
            [
                new OrderLineHuDisplayEntry(
                    "HU-CUSTOMER",
                    OrderLineHuFateDisplayBuilder.AwaitingShipmentFateLabel,
                    600,
                    false,
                    OrderLineHuFateDisplayBuilder.AwaitingShipmentSortOrder,
                    FateCode: OrderLineHuFateDisplayBuilder.AwaitingShipmentFateCode,
                    FateLabel: OrderLineHuFateDisplayBuilder.AwaitingShipmentFateLabel,
                    FateQty: 600)
            ]
        };

        var row = Assert.Single(line.HuDisplayRows);
        Assert.Equal("HU-CUSTOMER", row.HuCode);
        Assert.Equal(OrderLineHuFateDisplayBuilder.AwaitingShipmentFateLabel, row.Label);
        Assert.DoesNotContain(line.HuDisplayRows, candidate =>
            string.Equals(candidate.Label, "наполнено", StringComparison.OrdinalIgnoreCase));
    }
}
