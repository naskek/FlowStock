using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Models.Marking;

namespace FlowStock.Core.Services;

public sealed class ProductionPalletFillingCorrectionService
{
    private const string PayloadVersion = "v1";
    private const int ReasonTextMaxLength = 1000;
    private readonly IDataStore _data;

    public ProductionPalletFillingCorrectionService(IDataStore data)
    {
        _data = data;
    }

    public ProductionPalletFillingCorrectionPreview Preview(string? huCode)
    {
        var normalizedHu = NormalizeHu(huCode);
        if (normalizedHu == null)
        {
            return Blocked(
                string.Empty,
                ProductionPalletFillingCorrectionErrorCodes.HuRequired,
                "Укажите HU.");
        }

        return Analyze(_data, RequireCorrectionStore(_data), normalizedHu, lockData: false);
    }

    public IReadOnlyList<ProductionPalletFillingCorrectionHistoryEntry> History(string? huCode)
    {
        var normalizedHu = NormalizeHu(huCode);
        return normalizedHu == null
            ? Array.Empty<ProductionPalletFillingCorrectionHistoryEntry>()
            : RequireCorrectionStore(_data).GetFillingCorrectionHistory(normalizedHu);
    }

    public ProductionPalletFillingCorrectionResult Confirm(
        ProductionPalletFillingCorrectionConfirmRequest request)
    {
        var validation = ValidateRequest(request);
        if (validation.Error != null)
        {
            return validation.Error;
        }

        var requestId = validation.RequestId!.Value;
        var normalizedHu = validation.NormalizedHu!;
        var action = validation.Action!;
        var reason = validation.Reason!;
        var reasonCode = ProductionPalletFillingCorrectionReasonCode.ForAction(action);
        var payloadHash = BuildPayloadHash(normalizedHu, action, reason);
        var correctionStore = RequireCorrectionStore(_data);

        var existing = correctionStore.GetFillingAdjustment(requestId);
        if (existing != null)
        {
            return Replay(existing, payloadHash, normalizedHu);
        }

        ProductionPalletFillingCorrectionResult? committed = null;
        try
        {
            _data.ExecuteInTransaction(store =>
            {
                var txCorrectionStore = RequireCorrectionStore(store);
                if (!txCorrectionStore.TryClaimFillingAdjustment(
                        requestId,
                        payloadHash,
                        action,
                        reasonCode,
                        reason,
                        TrimToNull(request.ActorName),
                        TrimToNull(request.DeviceName),
                        TrimToNull(request.ClientName),
                        TrimToNull(request.ClientVersion),
                        DateTime.Now,
                        out var adjustmentId))
                {
                    var concurrent = txCorrectionStore.GetFillingAdjustment(requestId)
                                     ?? throw new InvalidOperationException(
                                         "Не удалось прочитать конкурентный adjustment.");
                    throw new CorrectionAbortException(Replay(concurrent, payloadHash, normalizedHu));
                }

                var blockStates = ClientBlockCatalog.MergeWithDefaults(store.GetClientBlockSettings());
                if (!blockStates[ClientBlockCatalog.PcHuCorrection])
                {
                    Abort(
                        ProductionPalletFillingCorrectionErrorCodes.BlockDisabled,
                        "Корректировка наполнения HU выключена администратором.",
                        normalizedHu);
                }

                var initialPallet = store.GetProductionPalletByHu(normalizedHu);
                if (initialPallet?.OrderId == null)
                {
                    Abort(
                        ProductionPalletFillingCorrectionErrorCodes.PalletNotFound,
                        "Производственная паллета с указанным HU не найдена.",
                        normalizedHu);
                }

                if (!store.LockOrdersForUpdate(new[] { initialPallet.OrderId.Value }))
                {
                    Abort(
                        ProductionPalletFillingCorrectionErrorCodes.CorrectionStateChanged,
                        "Заказ изменился. Повторите preview.",
                        normalizedHu);
                }

                txCorrectionStore.LockNormalizedHus(new[] { normalizedHu });
                var pallet = store.GetProductionPalletByHuForUpdate(normalizedHu);
                if (pallet == null)
                {
                    Abort(
                        ProductionPalletFillingCorrectionErrorCodes.CorrectionStateChanged,
                        "Состояние HU изменилось. Повторите preview.",
                        normalizedHu);
                }

                txCorrectionStore.LockDocumentsForUpdate(new[] { pallet.PrdDocId });
                var preview = Analyze(store, txCorrectionStore, normalizedHu, lockData: true);
                if (!preview.CanConfirm || preview.SourcePalletId != pallet.Id)
                {
                    var blocker = preview.Blockers.FirstOrDefault();
                    Abort(
                        blocker?.Code ?? ProductionPalletFillingCorrectionErrorCodes.CorrectionStateChanged,
                        blocker?.Message ?? "Состояние HU изменилось. Повторите preview.",
                        normalizedHu);
                }

                if (!string.Equals(preview.Action, action, StringComparison.Ordinal))
                {
                    Abort(
                        ProductionPalletFillingCorrectionErrorCodes.CorrectionStateChanged,
                        "Действие после preview изменилось.",
                        normalizedHu);
                }

                committed = action == ProductionPalletFillingCorrectionAction.CorrectFilled
                    ? ExecuteCorrectFilled(
                        store,
                        txCorrectionStore,
                        adjustmentId,
                        pallet,
                        preview,
                        reason,
                        request)
                    : ExecuteResetPartial(
                        store,
                        txCorrectionStore,
                        adjustmentId,
                        pallet,
                        reason);

                var predecessor = txCorrectionStore.GetPredecessorFillingAdjustment(pallet.Id);
                var rootPalletId = predecessor?.RootPalletId ?? pallet.Id;
                var resultJson = JsonSerializer.Serialize(committed);
                txCorrectionStore.CompleteFillingAdjustment(
                    adjustmentId,
                    pallet.Id,
                    rootPalletId,
                    pallet.PrdDocId,
                    committed.CorDocId,
                    committed.ReplacementPalletId,
                    committed.ReplacementPrdDocId,
                    predecessor?.Id,
                    resultJson);
            });
        }
        catch (CorrectionAbortException exception)
        {
            return exception.Result;
        }

        return committed ?? throw new InvalidOperationException("Correction transaction не вернула результат.");
    }

    public static string BuildPayloadHash(string normalizedHu, string action, string normalizedReason)
    {
        var payload = string.Join(
            "\n",
            PayloadVersion,
            normalizedHu,
            action,
            NormalizeReason(normalizedReason));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static ProductionPalletFillingCorrectionResult ExecuteCorrectFilled(
        IDataStore store,
        IProductionPalletFillingCorrectionStore correctionStore,
        long adjustmentId,
        ProductionPallet pallet,
        ProductionPalletFillingCorrectionPreview preview,
        string reason,
        ProductionPalletFillingCorrectionConfirmRequest request)
    {
        var sourceDoc = store.GetDoc(pallet.PrdDocId)
                        ?? throw new InvalidOperationException("Source PRD не найден.");
        var documents = new DocumentService(store);
        var corRef = documents.GenerateDocRef(DocType.InventoryCorrection, DateTime.Today);
        var corDocId = documents.CreateDoc(
            DocType.InventoryCorrection,
            corRef,
            reason,
            partnerId: null,
            orderRef: sourceDoc.OrderRef,
            shippingRef: pallet.HuCode,
            orderId: pallet.OrderId,
            hydrateOrderLines: false);
        documents.UpdateDocReason(
            corDocId,
            ProductionPalletFillingCorrectionReasonCode.ErroneousHuFill);

        var corLineBySourceLedgerId = new Dictionary<long, long>();
        foreach (var inversion in preview.LedgerInversion.OrderBy(line => line.SourceLedgerEntryId))
        {
            var sourceLine = CurrentLines(store.GetDocLines(pallet.PrdDocId))
                .Single(line => line.Id == inversion.SourceDocLineId);
            var corLineId = documents.AddDocLine(
                corDocId,
                inversion.ItemId,
                inversion.CorrectionQty,
                fromLocationId: null,
                toLocationId: inversion.LocationId,
                fromHu: null,
                toHu: pallet.HuCode,
                orderLineId: sourceLine.OrderLineId,
                productionPurpose: sourceLine.ProductionPurpose);
            corLineBySourceLedgerId[inversion.SourceLedgerEntryId] = corLineId;
        }

        correctionStore.LockDocumentsForUpdate(new[] { corDocId });
        var targetPrd = SelectOrCreateReplacementPrd(store, correctionStore, pallet, sourceDoc, documents);
        var markingCodes = correctionStore.LockReceiptMarkingCodes(pallet.PrdDocId);
        ValidateMarkingOrAbort(store, pallet, markingCodes);

        var close = documents.TryCloseDoc(corDocId, allowNegative: false);
        if (!close.Success)
        {
            Abort(
                ProductionPalletFillingCorrectionErrorCodes.CorPostingFailed,
                string.Join(" ", close.Errors.Concat(close.Warnings)),
                pallet.HuCode);
        }

        foreach (var inversion in preview.LedgerInversion)
        {
            var corLineId = corLineBySourceLedgerId[inversion.SourceLedgerEntryId];
            var generated = close.GeneratedLedgerEntries.SingleOrDefault(entry => entry.DocLineId == corLineId);
            if (generated == null
                || generated.ItemId != inversion.ItemId
                || generated.LocationId != inversion.LocationId
                || !SameHu(generated.HuCode, inversion.HuCode)
                || Math.Abs(generated.QtyDelta + inversion.SourceQty) > StockQuantityRules.QtyTolerance)
            {
                Abort(
                    ProductionPalletFillingCorrectionErrorCodes.CorLedgerMismatch,
                    "Проведённый COR не является точной инверсией source ledger.",
                    pallet.HuCode);
            }

            correctionStore.AddFillingAdjustmentLedgerLine(
                adjustmentId,
                inversion,
                corLineId,
                generated.LedgerEntryId);
        }

        if (markingCodes.Count > 0
            && correctionStore.RollbackReceiptMarkingCodes(
                adjustmentId,
                pallet.PrdDocId,
                corDocId,
                markingCodes,
                reason,
                TrimToNull(request.ActorName),
                TrimToNull(request.DeviceName),
                DateTime.Now) != markingCodes.Count)
        {
            Abort(
                ProductionPalletFillingCorrectionErrorCodes.MarkingRollbackBlocked,
                "Не удалось атомарно откатить полный набор кодов маркировки.",
                pallet.HuCode);
        }

        var replacementLineByComponent = new Dictionary<long, long>();
        foreach (var component in pallet.Lines.OrderBy(line => line.Id))
        {
            var sourceLine = CurrentLines(store.GetDocLines(pallet.PrdDocId))
                .Single(line => line.Id == component.DocLineId);
            var replacementLineId = documents.AddDocLine(
                targetPrd.Id,
                component.ItemId,
                component.PlannedQty,
                fromLocationId: null,
                toLocationId: pallet.ToLocationId,
                fromHu: null,
                toHu: pallet.HuCode,
                orderLineId: component.OrderLineId,
                productionPurpose: sourceLine.ProductionPurpose);
            store.UpdateDocLinePackSingleHu(replacementLineId, true);
            replacementLineByComponent[component.Id] = replacementLineId;
        }

        correctionStore.MarkProductionPalletCorrected(pallet.Id);
        var replacement = correctionStore.CreateReplacementProductionPallet(
            pallet.Id,
            targetPrd.Id,
            replacementLineByComponent,
            DateTime.Now);
        correctionStore.RecalculateProductionPalletNumbers(targetPrd.Id);
        foreach (var component in pallet.Lines)
        {
            correctionStore.AddFillingAdjustmentComponentLine(
                adjustmentId,
                "PALLET_COMPONENT",
                component,
                replacementLineByComponent[component.Id],
                replacement.BySourceComponentId[component.Id].ComponentId);
        }

        if (pallet.OrderId.HasValue)
        {
            new OrderService(store).RefreshPersistedStatus(pallet.OrderId.Value);
        }

        return new ProductionPalletFillingCorrectionResult
        {
            Success = true,
            Message = "Наполнение HU скорректировано.",
            AdjustmentId = adjustmentId,
            Action = ProductionPalletFillingCorrectionAction.CorrectFilled,
            HuCode = pallet.HuCode,
            SourcePalletId = pallet.Id,
            SourcePrdDocId = pallet.PrdDocId,
            CorDocId = corDocId,
            CorDocRef = corRef,
            ReplacementPalletId = replacement.PalletId,
            ReplacementPrdDocId = targetPrd.Id
        };
    }

    private static ProductionPalletFillingCorrectionResult ExecuteResetPartial(
        IDataStore store,
        IProductionPalletFillingCorrectionStore correctionStore,
        long adjustmentId,
        ProductionPallet pallet,
        string reason)
    {
        foreach (var component in pallet.Lines)
        {
            correctionStore.AddFillingAdjustmentComponentLine(
                adjustmentId,
                "RESET_COMPONENT",
                component,
                replacementDocLineId: null,
                replacementComponentId: null);
        }

        correctionStore.ResetPartialProductionPallet(pallet.Id);
        if (pallet.OrderId.HasValue)
        {
            new OrderService(store).RefreshPersistedStatus(pallet.OrderId.Value);
        }

        return new ProductionPalletFillingCorrectionResult
        {
            Success = true,
            Message = $"Частичное наполнение HU сброшено. Причина: {reason}",
            AdjustmentId = adjustmentId,
            Action = ProductionPalletFillingCorrectionAction.ResetPartial,
            HuCode = pallet.HuCode,
            SourcePalletId = pallet.Id,
            SourcePrdDocId = pallet.PrdDocId
        };
    }

    private static Doc SelectOrCreateReplacementPrd(
        IDataStore store,
        IProductionPalletFillingCorrectionStore correctionStore,
        ProductionPallet pallet,
        Doc sourceDoc,
        DocumentService documents)
    {
        var candidates = store.GetDocsByOrder(pallet.OrderId!.Value)
            .Where(doc => doc.Type == DocType.ProductionReceipt && doc.Status == DocStatus.Draft)
            .Where(doc => IsCompatibleReplacementPrd(store, doc, pallet))
            .OrderBy(doc => doc.Id)
            .ToList();
        if (candidates.Count > 1)
        {
            Abort(
                ProductionPalletFillingCorrectionErrorCodes.AmbiguousReplacementPrd,
                "Найдено несколько совместимых DRAFT PRD для replacement.",
                pallet.HuCode);
        }

        if (candidates.Count == 1)
        {
            correctionStore.LockDocumentsForUpdate(new[] { candidates[0].Id });
            var locked = store.GetDoc(candidates[0].Id)
                         ?? throw new InvalidOperationException("Target PRD исчез.");
            if (!IsCompatibleReplacementPrd(store, locked, pallet))
            {
                Abort(
                    ProductionPalletFillingCorrectionErrorCodes.CorrectionStateChanged,
                    "Совместимость target DRAFT PRD изменилась после блокировки.",
                    pallet.HuCode);
            }
            return locked;
        }

        var docRef = documents.GenerateDocRef(DocType.ProductionReceipt, DateTime.Today);
        var id = documents.CreateDoc(
            DocType.ProductionReceipt,
            docRef,
            $"Replacement после корректировки {sourceDoc.DocRef}",
            sourceDoc.PartnerId,
            sourceDoc.OrderRef,
            pallet.HuCode,
            pallet.OrderId,
            hydrateOrderLines: false);
        correctionStore.LockDocumentsForUpdate(new[] { id });
        return store.GetDoc(id) ?? throw new InvalidOperationException("Target PRD не создан.");
    }

    private static bool IsCompatibleReplacementPrd(
        IDataStore store,
        Doc doc,
        ProductionPallet replacementSource)
    {
        if (doc.Type != DocType.ProductionReceipt
            || doc.Status != DocStatus.Draft
            || doc.OrderId != replacementSource.OrderId
            || store.CountLedgerEntriesByDocId(doc.Id) != 0)
        {
            return false;
        }

        var sourceOrderLines = store.GetOrderLines(replacementSource.OrderId!.Value)
            .ToDictionary(line => line.Id);
        if (replacementSource.Lines.Any(line =>
                !line.OrderLineId.HasValue
                || !sourceOrderLines.TryGetValue(line.OrderLineId.Value, out var orderLine)
                || orderLine.ItemId != line.ItemId))
        {
            return false;
        }

        var currentLines = CurrentLines(store.GetDocLines(doc.Id)).ToArray();
        var currentLineById = currentLines.ToDictionary(line => line.Id);
        var pallets = store.GetProductionPalletsByDoc(doc.Id);
        if (pallets.Count == 0)
        {
            return currentLines.Length == 0;
        }

        if (pallets.Any(candidate =>
                !string.Equals(candidate.Status, ProductionPalletStatus.Planned, StringComparison.Ordinal)
                && !string.Equals(candidate.Status, ProductionPalletStatus.Printed, StringComparison.Ordinal))
            || pallets.Any(candidate =>
                candidate.FilledAt.HasValue
                || candidate.HasComponentProgress
                || candidate.Lines.Any(line => line.FilledAt.HasValue))
            || pallets.Any(candidate => SameHu(candidate.HuCode, replacementSource.HuCode)))
        {
            return false;
        }

        var planRows = pallets
            .SelectMany(candidate => candidate.Lines.Count > 0
                ? candidate.Lines.Select(line => (
                    Pallet: candidate,
                    DocLineId: line.DocLineId,
                    OrderLineId: line.OrderLineId,
                    ItemId: line.ItemId,
                    Qty: line.PlannedQty))
                : new[]
                {
                    (
                        Pallet: candidate,
                        DocLineId: candidate.DocLineId,
                        OrderLineId: candidate.OrderLineId,
                        ItemId: candidate.ItemId,
                        Qty: candidate.PlannedQty)
                })
            .ToArray();
        if (planRows.Length != currentLines.Length
            || planRows.Select(row => row.DocLineId).Distinct().Count() != planRows.Length)
        {
            return false;
        }

        return planRows.All(row =>
            currentLineById.TryGetValue(row.DocLineId, out var line)
            && line.ItemId == row.ItemId
            && line.OrderLineId == row.OrderLineId
            && line.ToLocationId == row.Pallet.ToLocationId
            && SameHu(line.ToHu, row.Pallet.HuCode)
            && Math.Abs(line.Qty - row.Qty) <= StockQuantityRules.QtyTolerance);
    }

    private static ProductionPalletFillingCorrectionPreview Analyze(
        IDataStore store,
        IProductionPalletFillingCorrectionStore correctionStore,
        string normalizedHu,
        bool lockData)
    {
        var blockers = new List<ProductionPalletFillingCorrectionBlocker>();
        var pallet = lockData
            ? store.GetProductionPalletByHuForUpdate(normalizedHu)
            : store.GetProductionPalletByHu(normalizedHu);
        if (pallet == null || !IsOperationalStatus(pallet.Status))
        {
            return Blocked(
                normalizedHu,
                ProductionPalletFillingCorrectionErrorCodes.PalletNotFound,
                "Активная производственная паллета с указанным HU не найдена.");
        }

        var sourceDoc = store.GetDoc(pallet.PrdDocId);
        var action = string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.Ordinal)
            ? ProductionPalletFillingCorrectionAction.CorrectFilled
            : pallet.IsMixedPallet
              && pallet.HasComponentProgress
              && !pallet.AreAllComponentsFilled
              && string.Equals(
                  pallet.EffectiveStatus,
                  ProductionPalletStatus.PartiallyFilled,
                  StringComparison.Ordinal)
              && (string.Equals(pallet.Status, ProductionPalletStatus.Planned, StringComparison.Ordinal)
                  || string.Equals(pallet.Status, ProductionPalletStatus.Printed, StringComparison.Ordinal))
              && sourceDoc?.Status == DocStatus.Draft
                ? ProductionPalletFillingCorrectionAction.ResetPartial
                : null;
        if (action == null)
        {
            AddBlocker(
                blockers,
                ProductionPalletFillingCorrectionErrorCodes.CorrectionStateChanged,
                "HU не находится в состоянии, допускающем CORRECT_FILLED или RESET_PARTIAL.");
        }

        var currentLines = sourceDoc == null
            ? Array.Empty<DocLine>()
            : CurrentLines(store.GetDocLines(sourceDoc.Id)).ToArray();
        var ledger = correctionStore.GetLedgerEntriesForHu(normalizedHu);
        var ledgerPreview = new List<ProductionPalletFillingCorrectionLedgerLine>();

        if (action == ProductionPalletFillingCorrectionAction.CorrectFilled)
        {
            AnalyzeCorrectFilled(store, correctionStore, pallet, sourceDoc, currentLines, ledger, blockers, ledgerPreview);
        }
        else if (action == ProductionPalletFillingCorrectionAction.ResetPartial)
        {
            if (sourceDoc == null || store.CountLedgerEntriesByDocId(sourceDoc.Id) != 0)
            {
                AddBlocker(
                    blockers,
                    ProductionPalletFillingCorrectionErrorCodes.LedgerMismatch,
                    "Текущий DRAFT PRD replacement не должен иметь ledger.");
            }

            var affectedStockKeys = pallet.Lines
                .Select(line => (line.ItemId, pallet.ToLocationId))
                .ToHashSet();
            var hasNonZeroCurrentBalance = ledger
                .Where(entry => affectedStockKeys.Contains((entry.ItemId, (long?)entry.LocationId)))
                .GroupBy(entry => (entry.ItemId, entry.LocationId))
                .Any(group => Math.Abs(group.Sum(entry => entry.QtyDelta))
                              > StockQuantityRules.QtyTolerance);
            if (hasNonZeroCurrentBalance)
            {
                AddBlocker(
                    blockers,
                    ProductionPalletFillingCorrectionErrorCodes.LedgerMismatch,
                    "Текущий физический ledger balance replacement HU не равен нулю.");
            }
            AnalyzeSharedBlockers(store, correctionStore, pallet, sourceDoc, normalizedHu, blockers, pallet.PrdDocId);
        }

        var markingCodes = correctionStore.LockReceiptMarkingCodes(pallet.PrdDocId);
        if (action == ProductionPalletFillingCorrectionAction.CorrectFilled)
        {
            AddMarkingBlockers(store, pallet, currentLines, markingCodes, blockers);
        }
        else if (markingCodes.Count > 0)
        {
            AddBlocker(
                blockers,
                ProductionPalletFillingCorrectionErrorCodes.MarkingRollbackBlocked,
                "Для частично наполненного HU уже есть receipt-bound коды.");
        }

        return new ProductionPalletFillingCorrectionPreview
        {
            HuCode = normalizedHu,
            Action = action,
            SourcePalletId = pallet.Id,
            SourcePrdDocId = pallet.PrdDocId,
            SourcePrdRef = sourceDoc?.DocRef,
            MarkingCodeCount = markingCodes.Count,
            Components = pallet.Lines.Select(line => new ProductionPalletFillingCorrectionComponent
            {
                ComponentId = line.Id,
                DocLineId = line.DocLineId,
                OrderLineId = line.OrderLineId,
                ItemId = line.ItemId,
                ItemName = line.ItemName,
                PlannedQty = line.PlannedQty,
                FilledQty = line.FilledQty
            }).ToArray(),
            LedgerInversion = ledgerPreview,
            Blockers = blockers
        };
    }

    private static void AnalyzeCorrectFilled(
        IDataStore store,
        IProductionPalletFillingCorrectionStore correctionStore,
        ProductionPallet pallet,
        Doc? sourceDoc,
        IReadOnlyList<DocLine> currentLines,
        IReadOnlyList<LedgerEntry> allHuLedger,
        ICollection<ProductionPalletFillingCorrectionBlocker> blockers,
        ICollection<ProductionPalletFillingCorrectionLedgerLine> ledgerPreview)
    {
        if (sourceDoc?.Status != DocStatus.Closed)
        {
            AddBlocker(blockers, ProductionPalletFillingCorrectionErrorCodes.SourcePrdNotDedicated, "Source PRD не закрыт.");
        }

        var sourcePallets = store.GetProductionPalletsByDoc(pallet.PrdDocId)
            .Where(candidate => ProductionPalletStatus.IsOperational(candidate.Status))
            .ToArray();
        var componentLineIds = pallet.Lines.Select(line => line.DocLineId).OrderBy(id => id).ToArray();
        var currentPositiveLineIds = currentLines
            .Where(line => line.Qty > StockQuantityRules.QtyTolerance)
            .Select(line => line.Id)
            .OrderBy(id => id)
            .ToArray();
        var currentLinesMatchPallet = currentLines.All(line =>
        {
            var components = pallet.Lines.Where(candidate => candidate.DocLineId == line.Id).ToArray();
            if (components.Length != 1)
            {
                return false;
            }

            var component = components[0];
            return component.ItemId == line.ItemId
                   && component.OrderLineId == line.OrderLineId
                   && line.ToLocationId == pallet.ToLocationId
                   && SameHu(line.ToHu, pallet.HuCode)
                   && Math.Abs(component.PlannedQty - line.Qty) <= StockQuantityRules.QtyTolerance;
        });
        if (sourcePallets.Length != 1
            || sourcePallets[0].Id != pallet.Id
            || !componentLineIds.SequenceEqual(currentPositiveLineIds)
            || !currentLinesMatchPallet)
        {
            AddBlocker(
                blockers,
                ProductionPalletFillingCorrectionErrorCodes.SourcePrdNotDedicated,
                "Source PRD не является dedicated PRD выбранной паллеты.");
        }

        var sourceLedger = allHuLedger
            .Where(entry => entry.DocId == pallet.PrdDocId && entry.QtyDelta > StockQuantityRules.QtyTolerance)
            .OrderBy(entry => entry.Id)
            .ToArray();
        var ledgerMapping = new List<(LedgerEntry Ledger, ProductionPalletComponentLine Component)>();
        if (sourceLedger.Length == 0
            || sourceLedger.Length != store.CountLedgerEntriesByDocId(pallet.PrdDocId))
        {
            AddBlocker(
                blockers,
                ProductionPalletFillingCorrectionErrorCodes.LedgerMismatch,
                "Source ledger отсутствует либо содержит движения вне dedicated HU.");
        }
        else
        {
            var maxSourceId = sourceLedger.Max(entry => entry.Id);
            if (allHuLedger.Any(entry => entry.Id > maxSourceId))
            {
                AddBlocker(
                    blockers,
                    ProductionPalletFillingCorrectionErrorCodes.LaterLedgerMovement,
                    "После source PRD существуют движения ledger по HU.");
            }

            var docLineById = currentLines.ToDictionary(line => line.Id);
            var lineMappingIsBijective =
                currentLines.Count == pallet.Lines.Count
                && currentLines.All(line =>
                {
                    var matches = pallet.Lines.Where(component =>
                        component.DocLineId == line.Id
                        && component.ItemId == line.ItemId
                        && component.OrderLineId == line.OrderLineId
                        && line.ToLocationId == pallet.ToLocationId
                        && SameHu(line.ToHu, pallet.HuCode)
                        && Math.Abs(component.PlannedQty - line.Qty)
                        <= StockQuantityRules.QtyTolerance).ToArray();
                    return matches.Length == 1;
                })
                && pallet.Lines.All(component =>
                    docLineById.TryGetValue(component.DocLineId, out var line)
                    && line.ItemId == component.ItemId
                    && line.OrderLineId == component.OrderLineId
                    && line.ToLocationId == pallet.ToLocationId
                    && SameHu(line.ToHu, pallet.HuCode)
                    && Math.Abs(line.Qty - component.PlannedQty)
                    <= StockQuantityRules.QtyTolerance);

            var ledgerMappingIsBijective =
                sourceLedger.Length == pallet.Lines.Count
                && sourceLedger.All(entry =>
                {
                    var matches = pallet.Lines.Where(component =>
                        component.ItemId == entry.ItemId
                        && pallet.ToLocationId == entry.LocationId
                        && SameHu(entry.HuCode, pallet.HuCode)
                        && Math.Abs(component.PlannedQty - entry.QtyDelta)
                        <= StockQuantityRules.QtyTolerance).ToArray();
                    if (matches.Length != 1)
                    {
                        return false;
                    }
                    ledgerMapping.Add((entry, matches[0]));
                    return true;
                })
                && pallet.Lines.All(component =>
                    sourceLedger.Count(entry =>
                        component.ItemId == entry.ItemId
                        && pallet.ToLocationId == entry.LocationId
                        && SameHu(entry.HuCode, pallet.HuCode)
                        && Math.Abs(component.PlannedQty - entry.QtyDelta)
                        <= StockQuantityRules.QtyTolerance) == 1)
                && ledgerMapping.Select(mapping => mapping.Component.Id).Distinct().Count()
                   == pallet.Lines.Count;

            if (!lineMappingIsBijective || !ledgerMappingIsBijective)
            {
                ledgerMapping.Clear();
                AddBlocker(
                    blockers,
                    ProductionPalletFillingCorrectionErrorCodes.LedgerMismatch,
                    "Source DocLines, компоненты и ledger не образуют взаимно-однозначное полное соответствие.");
            }

            var sourceTotals = sourceLedger
                .GroupBy(entry => (entry.ItemId, entry.LocationId))
                .ToDictionary(group => group.Key, group => group.Sum(entry => entry.QtyDelta));
            var currentTotals = allHuLedger
                .GroupBy(entry => (entry.ItemId, entry.LocationId))
                .Where(group => Math.Abs(group.Sum(entry => entry.QtyDelta))
                                > StockQuantityRules.QtyTolerance)
                .ToDictionary(group => group.Key, group => group.Sum(entry => entry.QtyDelta));
            if (sourceTotals.Count != currentTotals.Count
                || sourceTotals.Any(pair => !currentTotals.TryGetValue(pair.Key, out var qty)
                                             || Math.Abs(qty - pair.Value) > StockQuantityRules.QtyTolerance))
            {
                AddBlocker(
                    blockers,
                    ProductionPalletFillingCorrectionErrorCodes.LedgerMismatch,
                    "Текущий ledger balance HU не равен source production receipt.");
            }

            if (lineMappingIsBijective
                && ledgerMappingIsBijective
                && !blockers.Any(blocker =>
                    blocker.Code == ProductionPalletFillingCorrectionErrorCodes.LedgerMismatch))
            {
                foreach (var mapping in ledgerMapping.OrderBy(mapping => mapping.Ledger.Id))
                {
                    ledgerPreview.Add(new ProductionPalletFillingCorrectionLedgerLine
                    {
                        SourceLedgerEntryId = mapping.Ledger.Id,
                        SourceDocLineId = mapping.Component.DocLineId,
                        ItemId = mapping.Ledger.ItemId,
                        LocationId = mapping.Ledger.LocationId,
                        HuCode = pallet.HuCode,
                        SourceQty = mapping.Ledger.QtyDelta
                    });
                }
            }
        }

        AnalyzeSharedBlockers(store, correctionStore, pallet, sourceDoc, pallet.HuCode, blockers, excludedDraftDocId: null);
    }

    private static void AnalyzeSharedBlockers(
        IDataStore store,
        IProductionPalletFillingCorrectionStore correctionStore,
        ProductionPallet pallet,
        Doc? sourceDoc,
        string normalizedHu,
        ICollection<ProductionPalletFillingCorrectionBlocker> blockers,
        long? excludedDraftDocId)
    {
        if (pallet.OrderId.HasValue)
        {
            var order = store.GetOrder(pallet.OrderId.Value);
            if (order?.Type == OrderType.Customer && order.Status == OrderStatus.Shipped)
            {
                AddBlocker(
                    blockers,
                    ProductionPalletFillingCorrectionErrorCodes.CustomerShipped,
                    "CUSTOMER-заказ уже отгружен.");
            }
            if (store.HasActiveOrderControlForOrder(pallet.OrderId.Value))
            {
                AddBlocker(
                    blockers,
                    ProductionPalletFillingCorrectionErrorCodes.ActiveOrderControl,
                    "Заказ находится в активном контроле.");
            }
        }

        if (correctionStore.HasActiveReservationForHu(normalizedHu))
        {
            AddBlocker(
                blockers,
                ProductionPalletFillingCorrectionErrorCodes.ActiveReservation,
                "HU присутствует в активном клиентском резерве.");
        }
        if (correctionStore.HasActiveDraftReference(normalizedHu, excludedDraftDocId))
        {
            AddBlocker(
                blockers,
                ProductionPalletFillingCorrectionErrorCodes.ActiveDraftReference,
                "HU используется актуальной строкой постороннего DRAFT-документа.");
        }
    }

    private static void ValidateMarkingOrAbort(
        IDataStore store,
        ProductionPallet pallet,
        IReadOnlyList<ProductionPalletCorrectionMarkingCode> codes)
    {
        var blockers = new List<ProductionPalletFillingCorrectionBlocker>();
        AddMarkingBlockers(
            store,
            pallet,
            CurrentLines(store.GetDocLines(pallet.PrdDocId)).ToArray(),
            codes,
            blockers);
        if (blockers.Count > 0)
        {
            Abort(blockers[0].Code, blockers[0].Message, pallet.HuCode);
        }
    }

    private static void AddMarkingBlockers(
        IDataStore store,
        ProductionPallet pallet,
        IReadOnlyList<DocLine> currentLines,
        IReadOnlyList<ProductionPalletCorrectionMarkingCode> codes,
        ICollection<ProductionPalletFillingCorrectionBlocker> blockers)
    {
        var items = pallet.Lines
            .Select(line => line.ItemId)
            .Distinct()
            .Select(store.FindItemById)
            .Where(item => item != null)
            .Select(item => item!)
            .ToDictionary(item => item.Id);
        var markedLines = currentLines
            .Where(line => items.TryGetValue(line.ItemId, out var item) && item.IsChestnyZnakMarkingRequired)
            .ToArray();

        foreach (var line in markedLines)
        {
            var rounded = Math.Round(line.Qty);
            var expected = Math.Abs(line.Qty - rounded) <= StockQuantityRules.QtyTolerance
                ? (int)rounded
                : -1;
            var bound = codes.Where(code => code.ReceiptLineId == line.Id).ToArray();
            if (expected < 0 || bound.Length != expected)
            {
                AddBlocker(
                    blockers,
                    ProductionPalletFillingCorrectionErrorCodes.MarkingRollbackBlocked,
                    "Набор кодов не соответствует маркируемому количеству source PRD.");
            }
        }

        var markedLineIds = markedLines.Select(line => line.Id).ToHashSet();
        if (codes.Any(code => !code.ReceiptLineId.HasValue || !markedLineIds.Contains(code.ReceiptLineId.Value)))
        {
            AddBlocker(
                blockers,
                ProductionPalletFillingCorrectionErrorCodes.MarkingRollbackBlocked,
                "Код связан с неожиданной строкой source PRD.");
        }

        var lineById = currentLines.ToDictionary(line => line.Id);
        if (codes.Any(code =>
                !string.Equals(code.Status, MarkingCodeStatus.Applied, StringComparison.Ordinal)
                || code.ReceiptDocId != pallet.PrdDocId
                || !code.ReceiptLineId.HasValue
                || !lineById.TryGetValue(code.ReceiptLineId.Value, out var line)
                || code.MarkingOrderLineId != line.OrderLineId
                || code.ReportedAt.HasValue
                || code.IntroducedAt.HasValue))
        {
            AddBlocker(
                blockers,
                ProductionPalletFillingCorrectionErrorCodes.MarkingRollbackBlocked,
                "Маркировка имеет недопустимое состояние, связь или последующее событие.");
        }
    }

    private static IReadOnlyList<DocLine> CurrentLines(IReadOnlyList<DocLine> lines)
    {
        var replacedIds = lines
            .Where(line => line.ReplacesLineId.HasValue)
            .Select(line => line.ReplacesLineId!.Value)
            .ToHashSet();
        return lines
            .Where(line => !replacedIds.Contains(line.Id))
            .Where(line => line.Qty > StockQuantityRules.QtyTolerance)
            .ToArray();
    }

    private static RequestValidation ValidateRequest(ProductionPalletFillingCorrectionConfirmRequest request)
    {
        if (!Guid.TryParse(request.RequestId, out var requestId))
        {
            return RequestValidation.Failed(
                ProductionPalletFillingCorrectionErrorCodes.InvalidRequestId,
                "request_id должен быть UUID.");
        }

        var hu = NormalizeHu(request.HuCode);
        if (hu == null)
        {
            return RequestValidation.Failed(
                ProductionPalletFillingCorrectionErrorCodes.HuRequired,
                "Укажите HU.");
        }

        var action = request.ExpectedAction?.Trim().ToUpperInvariant();
        if (!ProductionPalletFillingCorrectionAction.IsKnown(action))
        {
            return RequestValidation.Failed(
                ProductionPalletFillingCorrectionErrorCodes.InvalidAction,
                "Неизвестное expected_action.");
        }

        var reason = NormalizeReason(request.ReasonText);
        if (string.IsNullOrWhiteSpace(reason))
        {
            return RequestValidation.Failed(
                ProductionPalletFillingCorrectionErrorCodes.ReasonRequired,
                "Причина корректировки обязательна.");
        }
        if (reason.Length > ReasonTextMaxLength)
        {
            return RequestValidation.Failed(
                ProductionPalletFillingCorrectionErrorCodes.ReasonTooLong,
                $"Причина корректировки не должна превышать {ReasonTextMaxLength} символов.");
        }

        return new RequestValidation(requestId, hu, action, reason, null);
    }

    private static ProductionPalletFillingCorrectionResult Replay(
        ProductionPalletFillingAdjustment existing,
        string payloadHash,
        string normalizedHu)
    {
        if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
        {
            return Error(
                ProductionPalletFillingCorrectionErrorCodes.IdempotencyKeyReused,
                "request_id уже использован с другим business payload.",
                normalizedHu);
        }

        if (string.IsNullOrWhiteSpace(existing.ResultJson))
        {
            throw new InvalidOperationException("Adjustment найден без committed result.");
        }

        var result = JsonSerializer.Deserialize<ProductionPalletFillingCorrectionResult>(existing.ResultJson)
                     ?? throw new InvalidOperationException("Сохранённый result_json повреждён.");
        return new ProductionPalletFillingCorrectionResult
        {
            Success = result.Success,
            Replay = true,
            ErrorCode = result.ErrorCode,
            Message = result.Message,
            AdjustmentId = result.AdjustmentId,
            Action = result.Action,
            HuCode = result.HuCode,
            SourcePalletId = result.SourcePalletId,
            SourcePrdDocId = result.SourcePrdDocId,
            CorDocId = result.CorDocId,
            CorDocRef = result.CorDocRef,
            ReplacementPalletId = result.ReplacementPalletId,
            ReplacementPrdDocId = result.ReplacementPrdDocId
        };
    }

    private static IProductionPalletFillingCorrectionStore RequireCorrectionStore(IDataStore store)
    {
        return store as IProductionPalletFillingCorrectionStore
               ?? throw new NotSupportedException(
                   "Хранилище не поддерживает безопасную корректировку наполнения HU.");
    }

    private static ProductionPalletFillingCorrectionPreview Blocked(string hu, string code, string message)
    {
        return new ProductionPalletFillingCorrectionPreview
        {
            HuCode = hu,
            Blockers = new[]
            {
                new ProductionPalletFillingCorrectionBlocker { Code = code, Message = message }
            }
        };
    }

    private static void AddBlocker(
        ICollection<ProductionPalletFillingCorrectionBlocker> blockers,
        string code,
        string message)
    {
        if (blockers.Any(blocker => blocker.Code == code && blocker.Message == message))
        {
            return;
        }
        blockers.Add(new ProductionPalletFillingCorrectionBlocker { Code = code, Message = message });
    }

    private static bool IsOperationalStatus(string status) =>
        string.Equals(status, ProductionPalletStatus.Planned, StringComparison.Ordinal)
        || string.Equals(status, ProductionPalletStatus.Printed, StringComparison.Ordinal)
        || string.Equals(status, ProductionPalletStatus.Filled, StringComparison.Ordinal);

    private static string? NormalizeHu(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static bool SameHu(string? left, string? right) =>
        string.Equals(NormalizeHu(left), NormalizeHu(right), StringComparison.Ordinal);

    private static string NormalizeReason(string? value) =>
        (value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

    private static string? TrimToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ProductionPalletFillingCorrectionResult Error(string code, string message, string hu) =>
        new()
        {
            Success = false,
            ErrorCode = code,
            Message = message,
            HuCode = hu
        };

    [DoesNotReturn]
    private static void Abort(string code, string message, string hu) =>
        throw new CorrectionAbortException(Error(code, message, hu));

    private sealed record RequestValidation(
        Guid? RequestId,
        string? NormalizedHu,
        string? Action,
        string? Reason,
        ProductionPalletFillingCorrectionResult? Error)
    {
        public static RequestValidation Failed(string code, string message) =>
            new(null, null, null, null, ProductionPalletFillingCorrectionService.Error(code, message, string.Empty));
    }

    private sealed class CorrectionAbortException : Exception
    {
        public CorrectionAbortException(ProductionPalletFillingCorrectionResult result)
            : base(result.Message)
        {
            Result = result;
        }

        public ProductionPalletFillingCorrectionResult Result { get; }
    }
}
