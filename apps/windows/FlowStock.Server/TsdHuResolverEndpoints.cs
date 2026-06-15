using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.Server;

public static class TsdHuResolverEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/tsd/hu/resolve", HandleResolve);
        app.MapGet("/api/tsd/hu/card", HandleCard);
    }

    private static IResult HandleResolve(string? code, TsdHuResolverService service)
        => Handle(code, service.Resolve, includeCard: false);

    private static IResult HandleCard(string? code, TsdHuResolverService service)
        => Handle(code, service.GetCard, includeCard: true);

    private static IResult Handle(string? code, Func<string, TsdHuView> resolve, bool includeCard)
    {
        try
        {
            return Results.Ok(Map(resolve(code ?? string.Empty), includeCard));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { ok = false, error = "INVALID_HU_CODE", message = ex.Message });
        }
    }

    private static object Map(TsdHuView view, bool includeCard)
    {
        return new
        {
            known = view.Known,
            hu_code = view.HuCode,
            state = view.State,
            title = view.Title,
            description = view.Description,
            card_action = MapAction(view.CardAction),
            document_actions = view.DocumentActions.Select(MapAction).ToArray(),
            stock = includeCard ? view.Stock.Select(row => new
            {
                item_id = row.ItemId,
                item_name = row.ItemName,
                uom = row.Uom,
                location_id = row.LocationId,
                location_code = row.LocationCode,
                qty = row.Qty
            }).ToArray() : null,
            production_pallets = includeCard ? view.ProductionPallets.Select(pallet => new
            {
                pallet_id = pallet.PalletId,
                status = pallet.Status,
                prd_doc_id = pallet.PrdDocId,
                prd_doc_ref = pallet.PrdDocRef,
                prd_doc_status = pallet.PrdDocStatus,
                order_id = pallet.OrderId,
                order_ref = pallet.OrderRef,
                pallet_no = pallet.PalletNo,
                pallet_count = pallet.PalletCount,
                filled_at = pallet.FilledAt,
                components = pallet.Components.Select(component => new
                {
                    item_id = component.ItemId,
                    item_name = component.ItemName,
                    uom = component.Uom,
                    planned_qty = component.PlannedQty,
                    filled_qty = component.FilledQty
                }).ToArray()
            }).ToArray() : null,
            reservations = includeCard ? view.Reservations.Select(row => new
            {
                order_id = row.OrderId,
                order_ref = row.OrderRef,
                order_type = row.OrderType,
                order_status = row.OrderStatus,
                item_id = row.ItemId,
                item_name = row.ItemName,
                qty = row.Qty
            }).ToArray() : null,
            documents = includeCard ? view.Documents.Select(doc => new
            {
                doc_id = doc.DocId,
                doc_ref = doc.DocRef,
                doc_type = doc.DocType,
                doc_status = doc.DocStatus,
                order_id = doc.OrderId,
                order_ref = doc.OrderRef,
                direction = doc.Direction,
                item_id = doc.ItemId,
                item_name = doc.ItemName,
                uom = doc.Uom,
                qty = doc.Qty,
                created_at = doc.CreatedAt,
                closed_at = doc.ClosedAt
            }).ToArray() : null,
            latest_movement = includeCard && view.LatestMovement != null ? new
            {
                ledger_id = view.LatestMovement.LedgerId,
                doc_id = view.LatestMovement.DocId,
                doc_ref = view.LatestMovement.DocRef,
                doc_type = view.LatestMovement.DocType,
                item_id = view.LatestMovement.ItemId,
                item_name = view.LatestMovement.ItemName,
                location_code = view.LatestMovement.LocationCode,
                qty_delta = view.LatestMovement.QtyDelta,
                timestamp = view.LatestMovement.Timestamp
            } : null
        };
    }

    private static object? MapAction(TsdHuAction? action)
    {
        return action == null ? null : new
        {
            type = action.Type,
            label = action.Label,
            hu_code = action.HuCode,
            order_id = action.OrderId,
            order_ref = action.OrderRef,
            doc_id = action.DocId,
            doc_ref = action.DocRef,
            message = action.Message
        };
    }
}
