using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Models.Marking;

namespace FlowStock.Core.Services;

public sealed class ProductionPlanConsistencyRepairService(IDataStore dataStore)
{
    public const string Repair067072MustardMode = "repair-067-072-mustard";
    private const long MustardItemId = 6;
    private const double PalletQty = 600d;
    private const double Order067TargetQty = 1200d;
    private static readonly string[] Order067Refs = ["067", "67"];
    private static readonly string[] Order072Refs = ["072", "72"];
    private static readonly string[] Pallets067Fill = ["HU-0000462", "HU-0000463"];
    private static readonly string[] Pallets072Cancel = ["HU-0000476", "HU-0000477"];
    private static readonly string[] Pallets072Keep = ["HU-0000478", "HU-0000479"];

    private readonly IDataStore _dataStore = dataStore;

    public ProductionPlanConsistencyRepairResult Repair(string mode, bool apply)
    {
        if (!string.Equals(mode, Repair067072MustardMode, StringComparison.OrdinalIgnoreCase))
        {
            return new ProductionPlanConsistencyRepairResult
            {
                Ok = false,
                Applied = false,
                Mode = mode,
                ValidationErrors = [$"Неизвестный mode: {mode}"]
            };
        }

        ProductionPlanConsistencyRepairResult? result = null;
        if (apply)
        {
            _dataStore.ExecuteInTransaction(store =>
            {
                result = BuildRepairResult(store, mode, apply: true);
            });
        }
        else
        {
            result = BuildRepairResult(_dataStore, mode, apply: false);
        }

        return result ?? new ProductionPlanConsistencyRepairResult
        {
            Ok = false,
            Applied = apply,
            Mode = mode,
            ValidationErrors = ["Repair не выполнен."]
        };
    }

    private ProductionPlanConsistencyRepairResult BuildRepairResult(IDataStore store, string mode, bool apply)
    {
        var steps = new List<ProductionPlanConsistencyRepairStep>();
        var validationErrors = new List<string>();
        var context = ResolveScenario(store, validationErrors);
        if (validationErrors.Count > 0)
        {
            return new ProductionPlanConsistencyRepairResult
            {
                Ok = false,
                Applied = false,
                Mode = mode,
                ValidationErrors = validationErrors,
                Steps = steps
            };
        }

        PlanOrder067Restore(store, context, steps, apply);
        PlanOrder072EmptyPallets(store, context, steps, apply);

        if (apply)
        {
            ApplyOrder067Restore(store, context, steps);
            ApplyOrder072EmptyPallets(store, context, steps);
        }

        var diagnosticsAfter = new ProductionPlanConsistencyDiagnosticsService(store).GetItems()
            .Where(item => item.OrderId == context.Order067.Id || item.OrderId == context.Order072.Id)
            .ToArray();

        if (apply)
        {
            var blocking067 = diagnosticsAfter
                .Where(item => item.OrderId == context.Order067.Id
                               && string.Equals(item.Severity, ProductionPlanConsistencySeverity.Error, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var blocking072 = diagnosticsAfter
                .Where(item => item.OrderId == context.Order072.Id
                               && item.ItemId == MustardItemId
                               && (item.ProblemCode == ProductionPlanConsistencyProblemCode.PalletsExceedOrderQty
                                   || item.ProblemCode == ProductionPlanConsistencyProblemCode.PrdLinesExceedOrderQty))
                .ToArray();
            if (blocking067.Length > 0 || blocking072.Length > 0)
            {
                validationErrors.Add("После repair остались ERROR-диагностики по 067/072.");
            }
        }

        return new ProductionPlanConsistencyRepairResult
        {
            Ok = validationErrors.Count == 0,
            Applied = apply,
            Mode = mode,
            ValidationErrors = validationErrors,
            Steps = steps,
            DiagnosticsAfter = diagnosticsAfter
        };
    }

    private RepairScenarioContext ResolveScenario(IDataStore store, List<string> validationErrors)
    {
        var order067 = FindOrderByRef(store, Order067Refs);
        if (order067 == null)
        {
            validationErrors.Add("Не найден INTERNAL заказ 067.");
            return RepairScenarioContext.Empty;
        }

        var order072 = FindOrderByRef(store, Order072Refs);
        if (order072 == null)
        {
            validationErrors.Add("Не найден INTERNAL заказ 072.");
            return RepairScenarioContext.Empty;
        }

        var line067 = store.GetOrderLines(order067.Id).FirstOrDefault(line => line.ItemId == MustardItemId);
        if (line067 == null)
        {
            validationErrors.Add("В заказе 067 нет строки item_id=6.");
            return RepairScenarioContext.Empty;
        }

        var line072 = store.GetOrderLines(order072.Id).FirstOrDefault(line => line.ItemId == MustardItemId);
        if (line072 == null)
        {
            validationErrors.Add("В заказе 072 нет строки item_id=6.");
            return RepairScenarioContext.Empty;
        }

        var draftPrd067 = store.GetDocsByOrder(order067.Id)
            .Where(doc => doc.Type == DocType.ProductionReceipt && doc.Status == DocStatus.Draft)
            .OrderByDescending(doc => doc.Id)
            .FirstOrDefault();
        if (draftPrd067 == null)
        {
            validationErrors.Add("Для 067 не найден DRAFT PRD.");
            return RepairScenarioContext.Empty;
        }

        var openPrd072 = store.GetDocsByOrder(order072.Id)
            .Where(doc => doc.Type == DocType.ProductionReceipt && doc.Status != DocStatus.Closed)
            .OrderByDescending(doc => doc.Id)
            .FirstOrDefault();
        if (openPrd072 == null)
        {
            validationErrors.Add("Для 072 не найден open PRD.");
            return RepairScenarioContext.Empty;
        }

        var pallets067 = ResolvePallets(store, draftPrd067.Id, Pallets067Fill, validationErrors, "067");
        var pallets072Cancel = ResolvePallets(store, openPrd072.Id, Pallets072Cancel, validationErrors, "072 cancel");
        var pallets072Keep = ResolvePallets(store, openPrd072.Id, Pallets072Keep, validationErrors, "072 keep");
        if (validationErrors.Count > 0)
        {
            return RepairScenarioContext.Empty;
        }

        var markingOrder = store.GetMarkingOrdersByItemIds(new[] { MustardItemId })
            .Where(order => order.OrderId == order067.Id || order.SourceOrderId == order067.Id)
            .OrderByDescending(order => order.CreatedAt)
            .FirstOrDefault();
        if (markingOrder == null)
        {
            validationErrors.Add("Для 067/item 6 не найден marking_order.");
            return RepairScenarioContext.Empty;
        }

        return new RepairScenarioContext(
            order067,
            order072,
            line067,
            line072,
            draftPrd067,
            openPrd072,
            pallets067,
            pallets072Cancel,
            pallets072Keep,
            markingOrder);
    }

    private static void PlanOrder067Restore(
        IDataStore store,
        RepairScenarioContext context,
        List<ProductionPlanConsistencyRepairStep> steps,
        bool apply)
    {
        var currentQty = context.Line067.QtyOrdered;
        if (currentQty <= StockQuantityRules.QtyTolerance)
        {
            steps.Add(Step(
                "restore_order_line_qty",
                $"order {context.Order067.OrderRef}/item {MustardItemId}",
                $"qty_ordered: 0 -> {Order067TargetQty:0.###}",
                skipped: false));
        }

        foreach (var pallet in context.Pallets067)
        {
            var ledgerQty = pallet.ToLocationId.HasValue
                ? store.GetLedgerBalance(MustardItemId, pallet.ToLocationId.Value, pallet.HuCode)
                : 0d;
            if (!string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
            {
                steps.Add(Step(
                    "mark_pallet_filled",
                    pallet.HuCode,
                    $"status {pallet.Status} -> FILLED",
                    skipped: false));
            }

            if (ledgerQty + StockQuantityRules.QtyTolerance < PalletQty)
            {
                steps.Add(Step(
                    "add_ledger",
                    pallet.HuCode,
                    $"PRD {context.DraftPrd067.DocRef}: +{PalletQty:0.###} (current {ledgerQty:0.###})",
                    skipped: false));
            }
        }

        var reservedCount = store.CountMarkingCodesByMarkingOrder(context.MarkingOrder.Id);
        steps.Add(Step(
            "assign_marking_codes",
            context.MarkingOrder.RequestNumber,
            $"Reserved codes {reservedCount} -> PRD {context.DraftPrd067.DocRef} lines for {string.Join(", ", Pallets067Fill)}",
            skipped: reservedCount <= 0));
    }

    private static void PlanOrder072EmptyPallets(
        IDataStore store,
        RepairScenarioContext context,
        List<ProductionPlanConsistencyRepairStep> steps,
        bool apply)
    {
        foreach (var pallet in context.Pallets072Cancel)
        {
            if (pallet.ToLocationId.HasValue)
            {
                var ledgerQty = store.GetLedgerBalance(MustardItemId, pallet.ToLocationId.Value, pallet.HuCode);
                if (ledgerQty > StockQuantityRules.QtyTolerance)
                {
                    steps.Add(Step(
                        "cancel_empty_pallet",
                        pallet.HuCode,
                        $"SKIP: ledger already {ledgerQty:0.###}",
                        skipped: true));
                    PlanTombstonePrdDocLine(store, context.OpenPrd072.Id, pallet.DocLineId, steps);
                    continue;
                }
            }

            var alreadyCancelled = string.Equals(
                pallet.Status,
                ProductionPalletStatus.Cancelled,
                StringComparison.OrdinalIgnoreCase);
            steps.Add(Step(
                "cancel_empty_pallet",
                pallet.HuCode,
                alreadyCancelled ? $"status {pallet.Status}, no changes" : $"status {pallet.Status} -> CANCELLED",
                skipped: alreadyCancelled));

            PlanTombstonePrdDocLine(store, context.OpenPrd072.Id, pallet.DocLineId, steps);
        }

        foreach (var pallet in context.Pallets072Keep)
        {
            steps.Add(Step(
                "keep_pallet",
                pallet.HuCode,
                $"status {pallet.Status}, no changes",
                skipped: true));
        }
    }

    private static void ApplyOrder067Restore(
        IDataStore store,
        RepairScenarioContext context,
        List<ProductionPlanConsistencyRepairStep> steps)
    {
        if (context.Line067.QtyOrdered <= StockQuantityRules.QtyTolerance)
        {
            store.UpdateOrderLineQty(context.Line067.Id, Order067TargetQty);
        }

        var appliedAt = DateTime.UtcNow;
        foreach (var pallet in context.Pallets067)
        {
            if (!string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
            {
                store.MarkProductionPalletFilled(pallet.PalletId, appliedAt, "repair-067-072-mustard");
            }

            if (!pallet.ToLocationId.HasValue)
            {
                continue;
            }

            var ledgerQty = store.GetLedgerBalance(MustardItemId, pallet.ToLocationId.Value, pallet.HuCode);
            var missingQty = Math.Max(0, PalletQty - ledgerQty);
            if (missingQty > StockQuantityRules.QtyTolerance)
            {
                store.AddLedgerEntry(new LedgerEntry
                {
                    Timestamp = DateTime.Now,
                    DocId = context.DraftPrd067.Id,
                    ItemId = MustardItemId,
                    LocationId = pallet.ToLocationId.Value,
                    QtyDelta = missingQty,
                    HuCode = pallet.HuCode
                });
            }
        }

        var docLinesByHu = store.GetDocLines(context.DraftPrd067.Id)
            .Where(line => line.ItemId == MustardItemId && line.Qty > StockQuantityRules.QtyTolerance)
            .GroupBy(line => NormalizeHu(line.ToHu), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderBy(line => line.Id).First(), StringComparer.OrdinalIgnoreCase);

        foreach (var pallet in context.Pallets067)
        {
            if (!docLinesByHu.TryGetValue(NormalizeHu(pallet.HuCode), out var docLine))
            {
                continue;
            }

            var required = (int)Math.Round(PalletQty);
            var assigned = store.CountProductionMarkingCodesByReceiptLine(docLine.Id);
            var missing = required - assigned;
            if (missing <= 0)
            {
                continue;
            }

            var item = store.FindItemById(MustardItemId);
            var codeIds = store.GetAvailableProductionMarkingCodeIdsForReceipt(
                context.Order067.Id,
                MustardItemId,
                item?.Gtin,
                missing);
            if (codeIds.Count > 0)
            {
                store.AssignProductionMarkingCodesToReceipt(codeIds, context.DraftPrd067.Id, docLine.Id, appliedAt);
            }
        }
    }

    private static void ApplyOrder072EmptyPallets(
        IDataStore store,
        RepairScenarioContext context,
        List<ProductionPlanConsistencyRepairStep> steps)
    {
        var toCancel = new List<long>();
        foreach (var pallet in context.Pallets072Cancel)
        {
            if (string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
            {
                ApplyTombstonePrdDocLine(store, context.OpenPrd072.Id, pallet.DocLineId, steps);
                continue;
            }

            if (pallet.ToLocationId.HasValue)
            {
                var ledgerQty = store.GetLedgerBalance(MustardItemId, pallet.ToLocationId.Value, pallet.HuCode);
                if (ledgerQty > StockQuantityRules.QtyTolerance)
                {
                    ApplyTombstonePrdDocLine(store, context.OpenPrd072.Id, pallet.DocLineId, steps);
                    continue;
                }
            }

            if (!string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            {
                toCancel.Add(pallet.PalletId);
            }

            ApplyTombstonePrdDocLine(store, context.OpenPrd072.Id, pallet.DocLineId, steps);
        }

        if (toCancel.Count > 0)
        {
            store.CancelProductionPallets(toCancel);
        }
    }

    private static void PlanTombstonePrdDocLine(
        IDataStore store,
        long prdDocId,
        long? docLineId,
        List<ProductionPlanConsistencyRepairStep> steps)
    {
        if (!docLineId.HasValue)
        {
            return;
        }

        var activeLine = store.GetDocLines(prdDocId).FirstOrDefault(line => line.Id == docLineId.Value);
        if (activeLine == null)
        {
            steps.Add(Step(
                "tombstone_prd_doc_line",
                $"doc_line {docLineId.Value}",
                "already inactive",
                skipped: true));
            return;
        }

        steps.Add(Step(
            "tombstone_prd_doc_line",
            $"doc_line {docLineId.Value}",
            $"qty {activeLine.Qty:0.###} -> 0 (replaces_line_id={docLineId.Value})",
            skipped: false));
    }

    private static void ApplyTombstonePrdDocLine(
        IDataStore store,
        long prdDocId,
        long? docLineId,
        List<ProductionPlanConsistencyRepairStep> steps)
    {
        if (!docLineId.HasValue)
        {
            return;
        }

        var activeLine = store.GetDocLines(prdDocId).FirstOrDefault(line => line.Id == docLineId.Value);
        if (activeLine == null)
        {
            return;
        }

        store.AddDocLine(new DocLine
        {
            DocId = prdDocId,
            ReplacesLineId = activeLine.Id,
            OrderLineId = activeLine.OrderLineId,
            ProductionPurpose = activeLine.ProductionPurpose,
            ItemId = activeLine.ItemId,
            Qty = 0,
            QtyInput = null,
            UomCode = activeLine.UomCode,
            FromLocationId = activeLine.FromLocationId,
            ToLocationId = activeLine.ToLocationId,
            FromHu = activeLine.FromHu,
            ToHu = activeLine.ToHu,
            PackSingleHu = activeLine.PackSingleHu
        });
    }

    private static List<ResolvedPallet> ResolvePallets(
        IDataStore store,
        long prdDocId,
        IReadOnlyList<string> huCodes,
        List<string> validationErrors,
        string label)
    {
        var pallets = store.GetProductionPalletsByDoc(prdDocId);
        var result = new List<ResolvedPallet>();
        foreach (var huCode in huCodes)
        {
            var pallet = pallets.FirstOrDefault(candidate =>
                string.Equals(NormalizeHu(candidate.HuCode), NormalizeHu(huCode), StringComparison.OrdinalIgnoreCase));
            if (pallet == null)
            {
                validationErrors.Add($"[{label}] паллета {huCode} не найдена в PRD {prdDocId}.");
                continue;
            }

            var docLineId = pallet.DocLineId > 0
                ? pallet.DocLineId
                : pallet.Lines.FirstOrDefault()?.DocLineId;
            long? locationId = pallet.ToLocationId;
            if (docLineId.HasValue)
            {
                var docLine = store.GetDocLines(prdDocId).FirstOrDefault(line => line.Id == docLineId.Value);
                locationId ??= docLine?.ToLocationId;
            }

            result.Add(new ResolvedPallet(
                pallet.Id,
                pallet.HuCode,
                pallet.Status,
                pallet.ItemId,
                locationId,
                docLineId));
        }

        return result;
    }

    private static Order? FindOrderByRef(IDataStore store, IReadOnlyList<string> refs)
    {
        return store.GetOrders()
            .FirstOrDefault(order => refs.Any(reference =>
                string.Equals(order.OrderRef, reference, StringComparison.OrdinalIgnoreCase)
                || order.OrderRef.TrimStart('0') == reference.TrimStart('0')));
    }

    private static ProductionPlanConsistencyRepairStep Step(string action, string target, string detail, bool skipped)
    {
        return new ProductionPlanConsistencyRepairStep
        {
            Action = action,
            Target = target,
            Detail = detail,
            Skipped = skipped
        };
    }

    private static string NormalizeHu(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
    }

    private sealed record RepairScenarioContext(
        Order Order067,
        Order Order072,
        OrderLine Line067,
        OrderLine Line072,
        Doc DraftPrd067,
        Doc OpenPrd072,
        IReadOnlyList<ResolvedPallet> Pallets067,
        IReadOnlyList<ResolvedPallet> Pallets072Cancel,
        IReadOnlyList<ResolvedPallet> Pallets072Keep,
        MarkingOrder MarkingOrder)
    {
        public static RepairScenarioContext Empty { get; } = new(
            new Order(),
            new Order(),
            new OrderLine(),
            new OrderLine(),
            new Doc(),
            new Doc(),
            Array.Empty<ResolvedPallet>(),
            Array.Empty<ResolvedPallet>(),
            Array.Empty<ResolvedPallet>(),
            new MarkingOrder());
    }

    private sealed record ResolvedPallet(
        long PalletId,
        string HuCode,
        string Status,
        long ItemId,
        long? ToLocationId,
        long? DocLineId);
}
