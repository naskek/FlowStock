using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.Server;

public static class OverShippedOrderDiagnosticsEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/diagnostics/over-shipped-orders", HandleGet);
    }

    private static IResult HandleGet(IDataStore store)
    {
        var items = new OverShippedOrderDiagnosticsService(store)
            .GetItems()
            .Select(MapItem)
            .ToArray();

        return Results.Ok(new
        {
            ok = true,
            items
        });
    }

    private static Dictionary<string, object?> MapItem(OverShippedOrderDiagnosticItem item)
    {
        return new Dictionary<string, object?>
        {
            ["order_id"] = item.OrderId,
            ["order_ref"] = item.OrderRef,
            ["item_id"] = item.ItemId,
            ["item_name"] = item.ItemName,
            ["qty_ordered"] = item.QtyOrdered,
            ["shipped_by_api/read_model"] = item.ShippedByApiReadModel,
            ["shipped_by_closed_outbound"] = item.ShippedByClosedOutbound,
            ["shipped_by_ledger"] = item.ShippedByLedger,
            ["over_shipped_qty"] = item.OverShippedQty,
            ["outbound_docs"] = item.OutboundDocs.Select(MapOutboundDoc).ToArray(),
            ["ledger_entries"] = item.LedgerEntries.Select(MapLedgerEntry).ToArray(),
            ["recommendation"] = item.Recommendation
        };
    }

    private static object MapOutboundDoc(OverShippedOutboundDocLine row)
    {
        return new
        {
            doc_id = row.DocId,
            doc_ref = row.DocRef,
            status = row.Status,
            closed_at = row.ClosedAt?.ToString("O"),
            doc_line_id = row.DocLineId,
            qty = row.Qty,
            from_hu = row.FromHu,
            order_line_id = row.OrderLineId
        };
    }

    private static object MapLedgerEntry(OverShippedLedgerEntry row)
    {
        return new
        {
            ledger_id = row.LedgerId,
            doc_id = row.DocId,
            item_id = row.ItemId,
            hu_code = row.HuCode,
            qty_delta = row.QtyDelta
        };
    }
}
