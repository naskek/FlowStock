using System.Text.Json;
using FlowStock.App;
using FlowStock.Core.Services;

namespace FlowStock.Server.Tests.Orders;

public sealed class WpfOrderLineHuFateMappingTests
{
    [Fact]
    public void MapOrderLineView_CustomerCanonicalPresentation_MapsOperatorRowsWithNullableQtyAndDiagnostics()
    {
        using var payload = JsonDocument.Parse("""
        {
          "id": 1631,
          "order_id": 1062,
          "item_id": 5,
          "item_name": "Товар",
          "qty_ordered": 30,
          "hu_presentation": {
            "production_tasks": [
              {
                "hu_code": "HU-0001739",
                "qty": 10,
                "uom": "шт",
                "state": { "code": "AWAITING_FILL", "label": "Ожидает наполнения" }
              }
            ],
            "operational_hus": [
              {
                "hu_code": "HU-0001744",
                "qty": 10,
                "uom": "шт",
                "state": { "code": "SHIPPED", "label": "Отгружен" },
                "shipment_target": { "order_id": 1062, "order_ref": "012" }
              },
              {
                "hu_code": "HU-0001600",
                "qty": null,
                "uom": null,
                "state": { "code": "INCONSISTENT", "label": "Несогласованное состояние" },
                "diagnostics": [
                  { "code": "FILLED_WITHOUT_LEDGER_STOCK", "message": "Нет физического остатка" }
                ]
              }
            ]
          }
        }
        """);

        var line = WpfReadApiService.MapOrderLineView(payload.RootElement);

        Assert.NotNull(line.HuPresentation);
        var rows = line.OperatorHuDisplayRows.ToDictionary(row => row.HuCode, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(3, rows.Count);
        Assert.Equal(("AWAITING_FILL", "Ожидает наполнения"),
            (rows["HU-0001739"].StateCode, rows["HU-0001739"].Label));
        Assert.Equal(("SHIPPED", "Отгружен"),
            (rows["HU-0001744"].StateCode, rows["HU-0001744"].Label));
        Assert.Equal(("INCONSISTENT", "Несогласованное состояние"),
            (rows["HU-0001600"].StateCode, rows["HU-0001600"].Label));
        Assert.Equal(0, rows["HU-0001600"].Qty, 3);
    }

    [Fact]
    public void MapOrderLineView_MapsServerFateAndShippedFallbackWithoutDuplicates()
    {
        using var payload = JsonDocument.Parse("""
        {
          "id": 20,
          "order_id": 2,
          "item_id": 5,
          "item_name": "Товар",
          "qty_ordered": 100,
          "production_hu_rows": [
            {
              "hu_code": "HU-STOCK",
              "pallet_status": "FILLED",
              "planned_qty": 10,
              "filled_qty": 10,
              "fate_code": "ON_STOCK",
              "fate_label": "на складе",
              "fate_qty": 10
            },
            {
              "hu_code": "HU-RESERVED",
              "pallet_status": "FILLED",
              "planned_qty": 11,
              "filled_qty": 11,
              "fate_code": "RESERVED",
              "fate_label": "→ резерв заказ 004",
              "fate_order_id": 4,
              "fate_order_ref": "004",
              "fate_qty": 9
            },
            {
              "hu_code": "HU-AWAITING",
              "pallet_status": "FILLED",
              "planned_qty": 12,
              "filled_qty": 12,
              "fate_code": "AWAITING_SHIPMENT",
              "fate_label": "Ожидает отгрузки",
              "fate_qty": 12
            },
            {
              "hu_code": " HU-TRANSFERRED ",
              "pallet_status": "FILLED",
              "planned_qty": 13,
              "filled_qty": 13,
              "fate_code": "SHIPPED",
              "fate_label": "→ отгружено заказ 005",
              "fate_order_id": 5,
              "fate_order_ref": "005",
              "fate_doc_ref": "OUT-5",
              "fate_qty": 8
            },
            {
              "hu_code": "HU-SHIPPED-HERE",
              "pallet_status": "FILLED",
              "planned_qty": 14,
              "filled_qty": 14,
              "fate_code": "SHIPPED",
              "fate_label": "отгружено",
              "fate_order_id": 2,
              "fate_order_ref": "002",
              "fate_doc_ref": "OUT-2",
              "fate_qty": 7
            },
            {
              "hu_code": "HU-FUTURE",
              "pallet_status": "FILLED",
              "planned_qty": 15,
              "filled_qty": 15,
              "fate_code": "FUTURE_STATE",
              "fate_label": "Новый серверный статус",
              "fate_qty": 6
            },
            {
              "hu_code": "HU-PARTIAL",
              "pallet_status": "PARTIALLY_FILLED",
              "planned_qty": 20,
              "filled_qty": 5
            }
          ],
          "shipped_hu_rows": [
            { "hu_code": "hu-transferred", "qty": 99 },
            {
              "hu_code": "HU-EXTERNAL-SHIPPED",
              "qty": 4,
              "source_order_id": 3,
              "source_order_ref": "003"
            },
            {
              "hu_code": "HU-OWN-SHIPPED",
              "qty": 3,
              "source_order_id": 2,
              "source_order_ref": "002"
            },
            { "hu_code": "HU-UNKNOWN-SOURCE", "qty": 2 }
          ]
        }
        """);

        var line = WpfReadApiService.MapOrderLineView(payload.RootElement);
        var rows = line.HuFateDisplayEntries.ToDictionary(row => row.HuCode, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(9, rows.Count);
        Assert.DoesNotContain(rows.Keys, hu => string.Equals(hu, "HU-PARTIAL", StringComparison.OrdinalIgnoreCase));

        var stock = rows["HU-STOCK"];
        Assert.Equal("наполнено", stock.Label);
        Assert.Equal(10, stock.Qty, 3);
        Assert.Equal(OrderLineHuFateDisplayBuilder.OnStockFateCode, stock.FateCode);
        Assert.Equal("на складе", stock.FateLabel);

        var reserved = rows["HU-RESERVED"];
        Assert.Equal("наполнено", reserved.Label);
        Assert.Equal(11, reserved.Qty, 3);
        Assert.Equal("→ резерв заказ 004", reserved.FateSuffix);
        Assert.Equal(OrderLineHuFateDisplayBuilder.ReservedSortOrder, reserved.SortOrder);
        Assert.Equal("004", reserved.FateOrderRef);

        var awaiting = rows["HU-AWAITING"];
        Assert.Equal("Ожидает отгрузки", awaiting.Label);
        Assert.Equal(OrderLineHuFateDisplayBuilder.AwaitingShipmentFateCode, awaiting.FateCode);
        Assert.Null(awaiting.FateSuffix);

        var transferred = rows["HU-TRANSFERRED"];
        Assert.Equal("наполнено", transferred.Label);
        Assert.Equal(13, transferred.Qty, 3);
        Assert.Equal("→ отгружено заказ 005", transferred.FateSuffix);
        Assert.Equal("OUT-5", transferred.FateDocRef);
        Assert.Equal(8, transferred.FateQty);

        var shippedHere = rows["HU-SHIPPED-HERE"];
        Assert.Equal("отгружено", shippedHere.Label);
        Assert.Equal(7, shippedHere.Qty, 3);
        Assert.Null(shippedHere.FateSuffix);

        var future = rows["HU-FUTURE"];
        Assert.Equal("FUTURE_STATE", future.FateCode);
        Assert.Equal("Новый серверный статус", future.Label);
        Assert.Equal(6, future.Qty, 3);

        var fallback = rows["HU-EXTERNAL-SHIPPED"];
        Assert.Equal("отгружено", fallback.Label);
        Assert.Equal(4, fallback.Qty, 3);
        Assert.Equal(OrderLineHuFateDisplayBuilder.ShippedFateCode, fallback.FateCode);
        Assert.Equal("← выпуск заказ 003", fallback.FateSuffix);
        Assert.Equal("← выпуск заказ 003", fallback.FateLabel);
        Assert.Equal(4, fallback.FateQty);

        Assert.Null(rows["HU-OWN-SHIPPED"].FateSuffix);
        Assert.Equal("отгружено", rows["HU-OWN-SHIPPED"].FateLabel);
        Assert.Null(rows["HU-UNKNOWN-SOURCE"].FateSuffix);
    }

    [Fact]
    public void MapOrderLineView_MapsTargetReservationsFromCanonicalOperationalRows()
    {
        using var payload = JsonDocument.Parse("""
        {
          "id": 20,
          "order_id": 2,
          "item_id": 5,
          "item_name": "Товар",
          "qty_ordered": 20,
          "shipped_hu_rows": [
            {
              "hu_code": "HU-SHIPPED",
              "qty": 4,
              "source_order_id": 3,
              "source_order_ref": "003"
            }
          ],
          "hu_presentation": {
            "production_tasks": [],
            "operational_hus": [
              {
                "hu_code": "HU-TARGET-RESERVED",
                "qty": 5,
                "uom": "шт",
                "state": { "code": "RESERVED", "label": "Зарезервирован" },
                "reservation_target": { "order_id": 2, "order_ref": "002" },
                "source_production_order": { "order_id": 3, "order_ref": "003" },
                "is_mixed": false
              },
              {
                "hu_code": "HU-UNKNOWN-SOURCE",
                "qty": 3,
                "uom": "шт",
                "state": { "code": "RESERVED", "label": "Зарезервирован" },
                "reservation_target": { "order_id": 2, "order_ref": "002" },
                "is_mixed": false
              },
              {
                "hu_code": "HU-MIXED-AMBIGUOUS",
                "qty": 2,
                "uom": "шт",
                "state": { "code": "RESERVED", "label": "Зарезервирован" },
                "reservation_target": { "order_id": 2, "order_ref": "002" },
                "is_mixed": true
              },
              {
                "hu_code": "HU-SHIPPED",
                "qty": 4,
                "uom": "шт",
                "state": { "code": "SHIPPED", "label": "Отгружен" },
                "shipment_target": { "order_id": 2, "order_ref": "002" },
                "source_production_order": { "order_id": 3, "order_ref": "003" },
                "is_mixed": false
              }
            ]
          }
        }
        """);

        var line = WpfReadApiService.MapOrderLineView(payload.RootElement);
        var rows = line.HuFateDisplayEntries
            .ToDictionary(row => row.HuCode, StringComparer.OrdinalIgnoreCase);

        Assert.NotNull(line.HuPresentation);
        var canonicalRows = line.HuDisplayRows.ToDictionary(row => row.HuCode, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("SHIPPED", canonicalRows["HU-SHIPPED"].StateCode);
        Assert.Equal("Отгружен", canonicalRows["HU-SHIPPED"].Label);
        Assert.Equal("RESERVED", canonicalRows["HU-TARGET-RESERVED"].StateCode);
        Assert.All(line.HuDisplayRows, row => Assert.Null(row.FateSuffix));
        Assert.Equal("Отгружен", line.OperatorHuDisplayRows.Single(row => row.HuCode == "HU-SHIPPED").Label);

        Assert.Equal(4, rows.Count);
        var reserved = rows["HU-TARGET-RESERVED"];
        Assert.Equal("резерв", reserved.Label);
        Assert.Equal(5, reserved.Qty, 3);
        Assert.Equal("← выпуск заказ 003", reserved.FateSuffix);
        Assert.Equal(OrderLineHuFateDisplayBuilder.ReservedFateCode, reserved.FateCode);
        Assert.Equal(2, reserved.FateOrderId);
        Assert.Equal("002", reserved.FateOrderRef);

        Assert.Null(rows["HU-UNKNOWN-SOURCE"].FateSuffix);
        Assert.Null(rows["HU-MIXED-AMBIGUOUS"].FateSuffix);
        Assert.Equal(OrderLineHuFateDisplayBuilder.ShippedFateCode, rows["HU-SHIPPED"].FateCode);
        Assert.Equal("← выпуск заказ 003", rows["HU-SHIPPED"].FateSuffix);
    }

    [Fact]
    public void MapOrderLineView_OldServerDirectionalLabel_RemainsCompatibleWithoutFateOrderId()
    {
        using var payload = JsonDocument.Parse("""
        {
          "id": 20,
          "order_id": 2,
          "item_id": 5,
          "item_name": "Товар",
          "qty_ordered": 10,
          "production_hu_rows": [
            {
              "hu_code": "HU-OLD-SERVER",
              "pallet_status": "FILLED",
              "planned_qty": 10,
              "filled_qty": 10,
              "fate_code": "SHIPPED",
              "fate_label": "→ отгружено заказ 005",
              "fate_order_ref": "005",
              "fate_qty": 8
            }
          ]
        }
        """);

        var row = Assert.Single(WpfReadApiService.MapOrderLineView(payload.RootElement).HuFateDisplayEntries);

        Assert.Equal("наполнено", row.Label);
        Assert.Equal("→ отгружено заказ 005", row.FateSuffix);
    }

    [Fact]
    public void MapOrderLineView_WithoutOptionalFateRows_LeavesFateEmpty()
    {
        using var payload = JsonDocument.Parse("""
        {
          "id": 20,
          "order_id": 2,
          "item_id": 5,
          "item_name": "Товар",
          "qty_ordered": 100
        }
        """);

        var line = WpfReadApiService.MapOrderLineView(payload.RootElement);

        Assert.Empty(line.HuFateDisplayEntries);
    }

    [Fact]
    public void OperatorHuDisplayRows_WithoutCanonicalPresentation_DoNotExposeLegacyFateOrPalletStatus()
    {
        var line = new FlowStock.Core.Models.OrderLineView
        {
            ProductionHuDisplayEntries =
            [
                new FlowStock.Core.Models.OrderLineHuDisplayEntry("HU-PLANNED", "наполнено", 10, false, 1)
            ],
            HuFateDisplayEntries =
            [
                new FlowStock.Core.Models.OrderLineHuDisplayEntry(
                    "HU-FATE",
                    "отгружено",
                    5,
                    false,
                    2,
                    FateCode: "SHIPPED",
                    FateLabel: "отгружено")
            ]
        };

        Assert.Empty(line.OperatorHuDisplayRows);
        Assert.NotEmpty(line.HuDisplayRows); // Compatibility data remains available outside the operator view.
    }
}
