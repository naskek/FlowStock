using System.Globalization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class DocsEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/docs", HandleList);
    }

    public static IResult HandleList(HttpRequest request, IDataStore store)
    {
        var op = request.Query["op"].ToString();
        var status = request.Query["status"].ToString();
        var typeFilter = string.IsNullOrWhiteSpace(op) ? null : DocTypeMapper.FromOpString(op);
        var statusFilter = string.IsNullOrWhiteSpace(status) ? null : DocTypeMapper.StatusFromString(status);

        var docs = store.GetDocs();
        if (typeFilter.HasValue)
        {
            docs = docs.Where(doc => doc.Type == typeFilter.Value).ToList();
        }
        if (statusFilter.HasValue)
        {
            docs = docs.Where(doc => doc.Status == statusFilter.Value).ToList();
        }

        var summariesByDocId = GetProductionPalletSummaries(store, docs);
        var list = docs
            .OrderByDescending(doc => doc.CreatedAt)
            .Select(doc =>
            {
                var summary = doc.Type == DocType.ProductionReceipt
                    && summariesByDocId.TryGetValue(doc.Id, out var found)
                        ? found
                        : new ProductionPalletSummary();
                var hasProductionPalletPlan = summary.PlannedPalletCount > 0;
                var productionPalletFillingStarted = doc.Type == DocType.ProductionReceipt
                                                     && doc.Status == DocStatus.Draft
                                                     && summary.FilledPalletCount > 0;
                return MapDoc(
                    doc,
                    productionPalletFillingStarted,
                    hasProductionPalletPlan,
                    summary,
                    BuildPalletFillingStatus(summary));
            })
            .ToList();
        return Results.Ok(list);
    }

    private static IReadOnlyDictionary<long, ProductionPalletSummary> GetProductionPalletSummaries(
        IDataStore store,
        IReadOnlyList<Doc> docs)
    {
        if (store is not IProductionPalletSummaryBatchStore summaryStore)
        {
            return new Dictionary<long, ProductionPalletSummary>();
        }

        var docIds = docs
            .Where(doc => doc.Type == DocType.ProductionReceipt)
            .Select(doc => doc.Id)
            .ToArray();
        return summaryStore.GetProductionPalletSummariesByDocIds(docIds);
    }

    private static object MapDoc(
        Doc doc,
        bool productionPalletFillingStarted,
        bool hasProductionPalletPlan,
        ProductionPalletSummary palletSummary,
        string palletFillingStatus)
    {
        return new
        {
            id = doc.Id,
            doc_ref = doc.DocRef,
            doc_uid = doc.ApiDocUid,
            op = DocTypeMapper.ToOpString(doc.Type),
            status = DocTypeMapper.StatusToString(doc.Status),
            created_at = doc.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            closed_at = doc.ClosedAt?.ToString("O", CultureInfo.InvariantCulture),
            partner_id = doc.PartnerId,
            partner_name = doc.PartnerName,
            partner_code = doc.PartnerCode,
            order_id = doc.OrderId,
            order_ref = doc.OrderRef,
            shipping_ref = doc.ShippingRef,
            reason_code = doc.ReasonCode,
            comment = doc.Comment,
            production_batch_no = doc.ProductionBatchNo,
            source_device_id = doc.SourceDeviceId,
            line_count = doc.LineCount,
            production_pallet_filling_started = productionPalletFillingStarted,
            has_production_pallet_plan = hasProductionPalletPlan,
            is_palletized = hasProductionPalletPlan,
            planned_pallet_count = palletSummary.PlannedPalletCount,
            filled_pallet_count = palletSummary.FilledPalletCount,
            planned_qty = palletSummary.PlannedQty,
            filled_qty = palletSummary.FilledQty,
            pallet_filling_status = palletFillingStatus
        };
    }

    private static string BuildPalletFillingStatus(ProductionPalletSummary summary)
    {
        if (summary.PlannedPalletCount <= 0)
        {
            return string.Empty;
        }

        if (summary.FilledPalletCount >= summary.PlannedPalletCount && summary.RemainingPalletCount <= 0)
        {
            return $"Наполнено полностью: {summary.FilledPalletCount} / {summary.PlannedPalletCount} паллет";
        }

        if (summary.FilledPalletCount > 0 || summary.FilledQty > 0)
        {
            return $"Наполнено {summary.FilledPalletCount} / {summary.PlannedPalletCount} паллет";
        }

        return "Паллетный выпуск по плану";
    }
}
