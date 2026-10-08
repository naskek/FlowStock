using System.Collections;
using System.Data;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace FlowStock.App;

/// <summary>
/// Deterministic, presentation-only sample rows. No AppServices, database, API or
/// persistent settings are involved. These values must never enter business flows.
/// </summary>
internal static class UiPreviewDemoData
{
    private const int RowCount = 24;

    private static readonly string[] Products =
    [
        "Хрен столовый классический 200 г",
        "Горчица русская острая 200 г",
        "Аджика домашняя 200 г",
        "Паста чесночная 200 г",
        "Хрен со свёклой 200 г — длинное демонстрационное наименование для проверки ширины колонки",
        "Горчица дижонская зернистая",
        "Пюре из чеснока фасованное",
        "Соус томатный с травами"
    ];

    private static readonly string[] Partners =
    [
        "ООО «Торговый дом Север»",
        "ООО «Гастрономия и традиции»",
        "ИП Иванов Иван Иванович",
        "ООО «Продуктовая логистика — Северо-Западный регион»",
        "ООО «Ресторанный поставщик»",
        "АО «Промышленное питание»"
    ];

    private static readonly string[] States = ["DRAFT", "IN_PROGRESS", "READY_TO_SHIP", "ACCEPTED", "SHIPPED", "CANCELLED"];
    private static readonly string[] DemoFields =
    [
        "ExpandMarker", "Details", "WarehouseHuRows", "ProductionReceipts", "NeedBreakdownRows",
        "ItemName", "ItemTypeName", "ItemNameDisplay", "ProductSubline", "Barcode", "Gtin",
        "BaseDisplay", "HuDisplay", "LocationCode", "OriginOrderDisplay",
        "ReservedOrderDisplay", "ReservedCustomerDisplay", "StockQtyDisplay",
        "MinStockSummary", "NeedSummary", "PlanSummary", "StockStatus", "HuCode",
        "PalletStatus", "QtyDisplay", "Location", "SourceOrderRef", "PrdRef",
        "StatusNote", "DemandToCloseDisplay", "DemandToMinDisplay", "AlreadyPlannedInternalDisplay",
        "MinStockQtyDisplay", "ShortageDisplay", "StockDisplay", "FilledPalletDisplay",
        "DocRef", "TypeDisplay", "StatusDisplay", "ProductionPalletBadge",
        "PartnerDisplay", "OrderRef", "OrderControlDisplay", "MarkingStatusShortDisplay",
        "ProductionPalletPlanShortDisplay", "ProductionPalletFillInProgress",
        "ProductionPalletFillCompleted", "StatusPresentationCode", "OperatorStatusLabel",
        "BundleRef", "Source", "CreatedBy", "Name", "Code", "Brand", "Volume",
        "TaraName", "BaseUom", "Label", "Month", "Amounts", "FileName",
        "BatchStatusDisplay", "IsActive", "IsMarked", "AutoHuDistributionEnabled"
    ];

    private static readonly HashSet<string> BooleanFields = new(StringComparer.Ordinal)
    {
        "IsActive", "IsMarked", "AutoHuDistributionEnabled",
        "ProductionPalletFillInProgress", "ProductionPalletFillCompleted"
    };

    private static readonly HashSet<string> DateFields = new(StringComparer.Ordinal)
    {
        "CreatedAt", "ClosedAt", "DueDate", "ShippedAt", "ImportedAt"
    };

    private static readonly HashSet<string> NumberFields = new(StringComparer.Ordinal)
    {
        "Id", "MaxHuSlots", "MinStockQty", "MaxQtyPerHu", "ShelfLifeMonths",
        "ToCloseOrdersQty", "QtyToCreate", "OpenInternalOrderQty", "TotalToMakeQty",
        "TotalCodes", "ErrorCount"
    };

    internal static IEnumerable ForGrid(DataGrid grid)
    {
        var table = new DataTable("Preview_" + grid.Name) { Locale = CultureInfo.InvariantCulture };
        var fields = new Dictionary<string, Type>(StringComparer.Ordinal);

        foreach (var column in grid.Columns.OfType<DataGridBoundColumn>())
        {
            if (column.Binding is not Binding { Path.Path: { } path }) continue;
            var property = path.Split('.')[0];
            if (string.IsNullOrWhiteSpace(property) || property == ".") continue;
            fields[property] = column is DataGridCheckBoxColumn ? typeof(bool) : FieldType(property);
        }

        // Template columns bind to named fields inside their DataTemplates.
        if (grid.Columns.Count > 0)
        {
            foreach (var name in DemoFields)
                fields.TryAdd(name, FieldType(name));
        }

        if (fields.Count == 0)
        {
            foreach (var name in new[] { "Code", "Name", "StatusDisplay", "QtyDisplay", "CreatedAt" })
                fields.Add(name, FieldType(name));
        }

        foreach (var (name, type) in fields)
            table.Columns.Add(name, type);

        for (var index = 0; index < RowCount; index++)
        {
            var row = table.NewRow();
            foreach (var (name, _) in fields)
                row[name] = Value(grid.Name, name, index) ?? DBNull.Value;
            table.Rows.Add(row);
        }
        return table.DefaultView;
    }

    private static Type FieldType(string name) =>
        BooleanFields.Contains(name) ? typeof(bool)
        : DateFields.Contains(name) ? typeof(DateTime)
        : NumberFields.Contains(name) ? typeof(decimal)
        : name is "Details" or "WarehouseHuRows" or "ProductionReceipts" or "NeedBreakdownRows" or "Amounts"
            ? typeof(object)
        : typeof(string);

    private static object? Value(string grid, string name, int i)
    {
        var product = Products[i % Products.Length];
        var partner = Partners[i % Partners.Length];
        var date = new DateTime(2026, 9, 1).AddDays(i);
        var qty = (i + 1) * 125;
        var state = States[i % States.Length];

        if (BooleanFields.Contains(name)) return i % 3 != 0;
        if (DateFields.Contains(name)) return name is "ClosedAt" or "ShippedAt" && i % 3 != 0
            ? null : date;
        if (NumberFields.Contains(name)) return (decimal)(name switch
        {
            "Id" => 100 + i,
            "ShelfLifeMonths" => 12,
            "MaxHuSlots" => 24 + i,
            "MaxQtyPerHu" => 1800 + 150 * i,
            "TotalCodes" => 500 + 20 * i,
            "ErrorCount" => i % 7 == 0 ? 2 : 0,
            _ => qty
        });

        return name switch
        {
            "Name" or "ItemName" or "ItemNameDisplay" => product,
            "ItemTypeName" => i % 5 == 0 ? "Сырьё" : "Товар",
            "ProductSubline" => $"GTIN 0460000000{i:000} · {200 + 50 * (i % 4)} г · SKU DEMO-{i + 1:000}",
            "Barcode" => $"4600000{i + 1:000000}",
            "Gtin" => $"0460000000{i + 1:000}",
            "Brand" => i % 2 == 0 ? "Русские закуски" : "СТМ / демонстрация",
            "Volume" => i % 2 == 0 ? "200 г" : "1 кг",
            "BaseUom" => "шт",
            "TaraName" => i % 2 == 0 ? "Банка стеклянная ТО53" : "Ведро",
            "PartnerDisplay" => grid == "DocsGrid" && i % 4 == 0 ? "—" : partner,
            "Code" => grid == "LocationsGrid" ? $"A-{i / 6 + 1:00}-{i % 6 + 1:00}"
                : grid == "PartnersGrid" ? $"770100{i:0000}" : $"DEMO-{i + 1:0000}",
            "LocationCode" or "Location" => $"A-{i / 6 + 1:00}-{i % 6 + 1:00}",
            "HuDisplay" or "HuCode" => $"HU-DEMO-{i + 1:000000}",
            "OriginOrderDisplay" or "ReservedOrderDisplay" => $"ORD-2026-{i + 100:0000}",
            "ReservedCustomerDisplay" => partner,
            "BaseDisplay" or "StockDisplay" or "StockQtyDisplay" => $"{qty:N0} шт",
            "MinStockQtyDisplay" or "MinStockSummary" => $"{qty / 2:N0} шт",
            "NeedSummary" or "ShortageDisplay" => $"{qty / 3:N0} шт",
            "PlanSummary" or "FilledPalletDisplay" => $"{qty / 2:N0} шт",
            "QtyDisplay" => $"{qty:N0} шт",
            "StockStatus" => state,
            "PalletStatus" => i % 2 == 0 ? "FILLED" : "PLANNED",
            "SourceOrderRef" or "OrderRef" => $"ORD-2026-{i + 100:0000}",
            "PrdRef" or "DocRef" => $"PRD-2026-{i + 70:0000}",
            "StatusNote" => i % 3 == 0 ? "Проверка длинного примечания для визуальной оценки" : "—",
            "DemandToCloseDisplay" => $"{qty / 4:N0} шт",
            "DemandToMinDisplay" => $"{qty / 5:N0} шт",
            "AlreadyPlannedInternalDisplay" => $"{qty / 6:N0} шт",
            "TypeDisplay" => grid == "DocsGrid" ? new[] { "Отгрузка", "Выпуск продукции", "Перемещение", "Приёмка" }[i % 4]
                : i % 3 == 0 ? "Внутренний" : "Клиентский",
            "StatusDisplay" or "StatusPresentationCode" => state,
            "OperatorStatusLabel" => new[] { "Черновик", "В работе", "Готов к отгрузке", "Принят", "Выполнен", "Отменён" }[i % 6],
            "ProductionPalletBadge" => i % 3 == 0 ? "2/5 готово" : "5/5",
            "ProductionPalletPlanShortDisplay" => i % 3 == 0 ? "План не сформирован" : "План сформирован",
            "MarkingStatusShortDisplay" => i % 2 == 0 ? "Маркировка проведена" : "Маркировка не проведена",
            "OrderControlDisplay" => i % 4 == 0 ? "Требует внимания" : "—",
            "BundleRef" => $"TASK-2026-{i + 1:0000}",
            "Source" => i % 2 == 0 ? "WPF" : "TSD",
            "CreatedBy" => i % 2 == 0 ? "DEMO-OPERATOR" : "DEMO-ADMIN",
            "Label" => grid == "StatisticsGroupsGrid" ? partner : product,
            "Month" => date.AddMonths(-i).ToString("yyyy-MM", CultureInfo.InvariantCulture),
            "Amounts" => new PreviewAmounts(qty, qty * 187m, qty * 170m, qty * 17m),
            "FileName" => $"demo_marking_{i + 1:000}.csv",
            "BatchStatusDisplay" => i % 3 == 0 ? "Ошибки" : "Принят",
            "ExpandMarker" => "▸",
            "Details" => new[] { new PreviewDetail($"A-{i + 1:00}", $"HU-DEMO-{i + 1:000000}", $"{qty} шт", $"ORD-{i + 1:000}", partner) },
            "WarehouseHuRows" => new[] { new PreviewHuPart($"HU-DEMO-{i + 1:000000}", $"{qty} шт", "FILLED", $"A-{i + 1:00}") },
            "ProductionReceipts" => new[] { new PreviewReceipt($"HU-DEMO-{i + 10:000000}", "PLANNED", $"{qty} шт", $"ORD-{i + 1:000}", $"PRD-{i + 1:000}") },
            "NeedBreakdownRows" => new[] { new PreviewNeed($"{qty / 2} шт", $"{qty / 3} шт", $"{qty / 4} шт") },
            _ => $"DEMO {i + 1:000}"
        };
    }

    private sealed record PreviewAmounts(decimal Quantity, decimal Gross, decimal Net, decimal Vat);
    private sealed record PreviewDetail(string LocationCode, string HuDisplay, string BaseDisplay, string OriginOrderDisplay, string ReservedCustomerDisplay)
    {
        public string ReservedOrderDisplay => OriginOrderDisplay;
    }
    private sealed record PreviewHuPart(string HuCode, string QtyDisplay, string StockStatus, string Location);
    private sealed record PreviewReceipt(string HuCode, string PalletStatus, string QtyDisplay, string SourceOrderRef, string PrdRef)
    {
        public string StatusNote => "Демонстрационный выпуск";
    }
    private sealed record PreviewNeed(string DemandToCloseDisplay, string DemandToMinDisplay, string AlreadyPlannedInternalDisplay);
}
