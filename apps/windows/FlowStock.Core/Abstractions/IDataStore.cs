using FlowStock.Core.Models;
using FlowStock.Core.Models.Marking;

namespace FlowStock.Core.Abstractions;

public interface IDataStore
{
    void Initialize();
    void ExecuteInTransaction(Action<IDataStore> work);

    Item? FindItemByBarcode(string barcode);
    Item? FindItemByGtin(string gtin);
    Item? FindItemById(long id);
    IReadOnlyList<Item> GetItems(string? search);
    long AddItem(Item item);
    void UpdateItemBarcode(long itemId, string barcode);
    void UpdateItem(Item item);
    void DeleteItem(long itemId);
    bool IsItemUsed(long itemId);
    void UpdateItemDefaultPackaging(long itemId, long? packagingId);

    IReadOnlyList<ItemPackaging> GetItemPackagings(long itemId, bool includeInactive);
    ItemPackaging? GetItemPackaging(long packagingId);
    ItemPackaging? FindItemPackagingByCode(long itemId, string code);
    long AddItemPackaging(ItemPackaging packaging);
    void UpdateItemPackaging(ItemPackaging packaging);
    void DeactivateItemPackaging(long packagingId);

    Location? FindLocationByCode(string code);
    Location? FindLocationById(long id);
    IReadOnlyList<Location> GetLocations();
    long AddLocation(Location location);
    void UpdateLocation(Location location);
    void DeleteLocation(long locationId);
    bool IsLocationUsed(long locationId);

    IReadOnlyList<Uom> GetUoms();
    long AddUom(Uom uom);
    void DeleteUom(long uomId);
    bool IsUomUsed(long uomId);

    IReadOnlyList<WriteOffReason> GetWriteOffReasons();
    long AddWriteOffReason(WriteOffReason reason);
    void DeleteWriteOffReason(long reasonId);

    IReadOnlyList<Tara> GetTaras();
    long AddTara(Tara tara);
    void UpdateTara(Tara tara);
    void DeleteTara(long taraId);
    bool IsTaraUsed(long taraId);

    IReadOnlyList<ItemType> GetItemTypes(bool includeInactive);
    ItemType? GetItemType(long id);
    long AddItemType(ItemType itemType);
    void UpdateItemType(ItemType itemType);
    void DeleteItemType(long itemTypeId);
    void DeactivateItemType(long itemTypeId);
    bool IsItemTypeUsed(long itemTypeId);

    Partner? GetPartner(long id);
    Partner? FindPartnerByCode(string code);
    IReadOnlyList<Partner> GetPartners();
    long AddPartner(Partner partner);
    void UpdatePartner(Partner partner);
    void DeletePartner(long partnerId);
    bool IsPartnerUsed(long partnerId);

    Doc? FindDocByRef(string docRef);
    Doc? GetDoc(long id);
    IReadOnlyList<Doc> GetDocs();
    IReadOnlyList<Doc> GetDocsByOrder(long orderId);
    int GetMaxDocRefSequenceByYear(int year);
    bool IsDocRefSequenceTaken(int year, int sequence);
    long AddDoc(Doc doc);
    void DeleteDoc(long docId);
    IReadOnlyList<DocLine> GetDocLines(long docId);
    IReadOnlyList<DocLineView> GetDocLineViews(long docId);
    long AddDocLine(DocLine line);
    void UpdateDocLineQty(long docLineId, double qty, double? qtyInput, string? uomCode);
    void UpdateDocLineHu(long docLineId, string? fromHu, string? toHu);
    void UpdateDocLinePackSingleHu(long docLineId, bool packSingleHu);
    void UpdateDocLineOrderLineId(long docLineId, long? orderLineId);
    void DeleteDocLine(long docLineId);
    void DeleteDocLines(long docId);
    void UpdateDocHeader(long docId, long? partnerId, string? orderRef, string? shippingRef);
    void UpdateDocReason(long docId, string? reasonCode);
    void UpdateDocComment(long docId, string? comment);
    void UpdateDocProductionBatch(long docId, string? productionBatchNo);
    void UpdateDocOrder(long docId, long? orderId, string? orderRef);
    void UpdateDocStatus(long docId, DocStatus status, DateTime? closedAt);
    IReadOnlyList<ProductionPallet> PlanProductionPallets(long docId, DateTime createdAt);
    string CreateProductionPalletHuCode(string? createdBy);
    IReadOnlyList<ProductionPallet> GetProductionPalletsByDoc(long docId);
    ProductionPallet? GetProductionPalletByHu(string huCode);
    IReadOnlyList<ProductionPalletWorkItem> GetActiveProductionPalletWorkItems();
    bool HasProductionPallets(long docId);
    bool HasProductionPalletLinesForDoc(long docId);
    void ClearPlannedProductionPalletPlan(long docId);
    int CountLedgerEntriesByDocId(long docId);
    ProductionPalletPlanCleanupCounts CancelProductionPalletPlan(long docId);
    ProductionPalletPlanAdoptionResult AdoptProductionPalletPlan(
        long sourcePrdDocId,
        long targetPrdDocId,
        long sourceOrderId,
        long targetOrderId,
        IReadOnlyDictionary<long, long> targetOrderLineIdByItemId);
    double GetFilledProductionPalletQtyByOrderLine(long orderLineId, long? excludePalletId = null);
    void UpdateProductionPalletHu(long palletId, string huCode);
    void ReassignOpenProductionPalletsByHu(
        long sourceOrderId,
        long targetOrderId,
        long targetOrderLineId,
        long itemId,
        IReadOnlyList<string> huCodes);
    void MarkProductionPalletFilled(long palletId, DateTime filledAt, string? deviceId);
    int CancelProductionPallets(IReadOnlyList<long> palletIds);
    int MarkProductionPalletsPrintedByOrder(long orderId, DateTime printedAt);
    IReadOnlyList<ProductionPallet> GetFilledProductionPalletsByItemAndLocation(long itemId, long locationId);
    IReadOnlyList<FilledProductionPalletStockMetrics> GetFilledProductionPalletStockMetrics();

    Order? GetOrder(long id);
    IReadOnlyList<Order> GetOrders();
    IReadOnlyList<Order> GetOrdersPage(bool includeInternal, string? query, int limit, int offset, bool includeCancelledMerged = false);
    long AddOrder(Order order);
    void UpdateOrder(Order order);
    void UpdateOrderStatus(long orderId, OrderStatus status);
    IReadOnlyList<MarkingOrderQueueRow> GetMarkingOrderQueue(bool includeCompleted);
    IReadOnlyList<MarkingOrderLineCandidate> GetMarkingOrderLineCandidates(IReadOnlyCollection<long> orderIds);
    IReadOnlyList<MarkingOrder> GetMarkingOrdersByIds(IReadOnlyCollection<Guid> ids);
    IReadOnlyList<MarkingOrder> GetMarkingOrdersByItemIds(IReadOnlyCollection<long> itemIds);
    void AddMarkingOrder(MarkingOrder order);
    void MarkMarkingOrdersPrinted(IReadOnlyCollection<Guid> ids, DateTime printedAt);
    void MarkOrdersPrinted(IReadOnlyCollection<long> orderIds, DateTime printedAt);
    void UpdateOrderMarkingStatusForBackfill(long orderId, MarkingStatus status, DateTime timestamp);
    IReadOnlyList<OrderLine> GetOrderLines(long orderId);
    IReadOnlyDictionary<long, long> GetOrderIdsByOrderLineIds(IReadOnlyCollection<long> orderLineIds);
    IReadOnlyList<OrderLineView> GetOrderLineViews(long orderId);
    IReadOnlyList<OrderReceiptLine> GetOrderReceiptRemaining(long orderId);
    IReadOnlyList<OrderReceiptLine> GetOrderReceiptRemainingWithoutReservedStock(long orderId);
    IReadOnlyList<OrderReceiptPlanLine> GetOrderReceiptPlanLines(long orderId);
    IReadOnlyDictionary<long, double> GetReservedFilledHuQtyByOrderLine(long customerOrderId);
    IReadOnlyCollection<string> GetReservedOrderReceiptHuCodes(long? excludeOrderId = null);
    void ReplaceOrderReceiptPlanLines(long orderId, IReadOnlyList<OrderReceiptPlanLine> lines);
    void ReplaceOrderReceiptPlanLinesForOrderLines(
        long orderId,
        IReadOnlyCollection<long> orderLineIds,
        IReadOnlyList<OrderReceiptPlanLine> replacementLines);
    IReadOnlyList<OrderShipmentLine> GetOrderShipmentRemaining(long orderId);
    long AddOrderLine(OrderLine line);
    void UpdateOrderLineQty(long orderLineId, double qtyOrdered);
    void UpdateOrderLinePurpose(long orderLineId, ProductionLinePurpose purpose);
    void UpdateOrderLineProductionPalletGroup(long orderLineId, string? groupCode);
    void DeleteOrderLine(long orderLineId);
    void DeleteOrderLines(long orderId);
    void DeleteOrder(long orderId);
    long CountLedgerEntries();
    IReadOnlyDictionary<long, double> GetLedgerTotalsByItem();
    IReadOnlyDictionary<long, double> GetShippedTotalsByOrder(long orderId);
    IReadOnlyDictionary<long, double> GetShippedTotalsByOrderLine(long orderId);
    DateTime? GetOrderShippedAt(long orderId);
    bool HasOutboundDocs(long orderId);

    void AddLedgerEntry(LedgerEntry entry);
    IReadOnlyList<StockRow> GetStock(string? search);
    double GetLedgerBalance(long itemId, long locationId);
    double GetLedgerBalance(long itemId, long locationId, string? huCode);
    IReadOnlyList<string?> GetHuCodesByLocation(long locationId);
    IReadOnlyList<string> GetAllHuCodes();
    IReadOnlyList<Item> GetItemsByLocationAndHu(long locationId, string? huCode);
    double GetAvailableQty(long itemId, long locationId, string? huCode);
    IReadOnlyDictionary<string, double> GetLedgerTotalsByHu();
    IReadOnlyList<HuStockRow> GetHuStockRows();

    IReadOnlyList<NegativeStockBalanceRow> GetNegativeStockBalances();
    IReadOnlyList<HuOrderContextRow> GetHuOrderContextRows();

    HuRecord CreateHuRecord(string? createdBy);
    HuRecord CreateHuRecord(string code, string? createdBy);
    HuRecord? GetHuByCode(string code);
    IReadOnlyList<HuRecord> GetHus(string? search, int take);
    void CloseHu(string code, string? closedBy, string? note);
    void ReopenHu(string code, string? reopenedBy, string? note);
    IReadOnlyList<HuLedgerRow> GetHuLedgerRows(string code);

    bool IsEventImported(string eventId);
    void AddImportedEvent(ImportedEvent ev);
    long AddImportError(ImportError err);
    IReadOnlyList<ImportError> GetImportErrors(string? reason);
    ImportError? GetImportError(long id);
    void DeleteImportError(long id);

    long AddItemRequest(ItemRequest request);
    IReadOnlyList<ItemRequest> GetItemRequests(bool includeResolved);
    void MarkItemRequestResolved(long requestId);

    long AddOrderRequest(OrderRequest request);
    IReadOnlyList<OrderRequest> GetOrderRequests(bool includeResolved);
    void ResolveOrderRequest(long requestId, string status, string resolvedBy, string? note, long? appliedOrderId);

    Guid AddMarkingCodeImport(MarkingCodeImport import);
    MarkingCodeImport? FindMarkingCodeImportByHash(string fileHash);
    void UpdateMarkingCodeImport(MarkingCodeImport import);
    bool ExistsMarkingCodeByRaw(string code);
    void AddMarkingCodes(IReadOnlyList<MarkingCode> codes);
    int CountMarkingCodesByMarkingOrder(Guid markingOrderId);
    int CountFreeProductionMarkingCodesByItem(long itemId, string? gtin);
    MarkingOrder? FindMarkingOrderByRequestNumber(string requestNumber);
    void UpdateMarkingOrderStatus(Guid id, string status, DateTime? codesBoundAt, DateTime updatedAt);
    IReadOnlyList<ClientBlockSetting> GetClientBlockSettings();
    void SaveClientBlockSettings(IReadOnlyList<ClientBlockSetting> settings);

    long AddKmCodeBatch(KmCodeBatch batch);
    void UpdateKmCodeBatchStats(long batchId, int totalCodes, int errorCount);
    KmCodeBatch? GetKmCodeBatch(long batchId);
    KmCodeBatch? FindKmCodeBatchByHash(string fileHash);
    IReadOnlyList<KmCodeBatch> GetKmCodeBatches();
    void UpdateKmCodeBatchOrder(long batchId, long? orderId);

    long AddKmCode(KmCode code);
    KmCode? FindKmCodeByRaw(string codeRaw);
    bool ExistsKmCodeByRawIgnoreCase(string codeRaw);
    IReadOnlyList<KmCode> GetKmCodesByBatch(long batchId, string? search, KmCodeStatus? status, int take);
    IReadOnlyList<KmCode> GetKmCodesByReceiptLine(long receiptLineId);
    IReadOnlyList<KmCode> GetKmCodesByShipmentLine(long shipLineId);
    int CountKmCodesByBatch(long batchId, KmCodeStatus? status);
    int CountKmCodesWithoutSku(long batchId);
    int CountKmCodesByReceiptLine(long receiptLineId);
    int CountKmCodesByShipmentLine(long shipLineId);
    int CountProductionMarkingCodesByReceiptLine(long receiptLineId);
    int CountAvailableProductionMarkingCodesForReceipt(long? sourceOrderId, long itemId, string? gtin);
    IReadOnlyList<Guid> GetAvailableProductionMarkingCodeIdsForReceipt(long? sourceOrderId, long itemId, string? gtin, int take);
    int AssignProductionMarkingCodesToReceipt(IReadOnlyList<Guid> codeIds, long docId, long lineId, DateTime appliedAt);
    IReadOnlyList<long> GetAvailableKmCodeIds(long? batchId, long? orderId, long skuId, string? gtin14, int take);
    IReadOnlyList<long> GetAvailableKmOnHandCodeIds(long? orderId, long skuId, string? gtin14, long? locationId, long? huId, int take);
    int AssignKmCodesToReceipt(IReadOnlyList<long> codeIds, long docId, long lineId, long? huId, long? locationId);
    void MarkKmCodeShipped(long codeId, long docId, long lineId, long? orderId);
    int DeleteKmCodesFromBatch(long batchId, IReadOnlyList<long> codeIds);
    void DeleteKmBatch(long batchId);

    WarehouseActionBundle? GetWarehouseActionBundle(long id);
    WarehouseActionBundle? FindWarehouseBundleByRef(string bundleRef);
    IReadOnlyList<WarehouseActionBundle> GetWarehouseActionBundles(string? status);
    int GetMaxWarehouseBundleRefSequenceByYear(int year);
    long AddWarehouseActionBundle(WarehouseActionBundle bundle);
    void UpdateWarehouseActionBundleStatus(
        long bundleId,
        string status,
        DateTime? approvedAt,
        string? approvedBy,
        DateTime? executedAt,
        DateTime? completedAt,
        DateTime? rejectedAt,
        string? rejectedBy,
        string? errorCode,
        string? errorMessage);

    WarehouseActionLine? GetWarehouseActionLine(long lineId);
    IReadOnlyList<WarehouseActionLine> GetWarehouseActionLines(long bundleId);
    int GetNextWarehouseActionLineNo(long bundleId);
    long AddWarehouseActionLine(WarehouseActionLine line);
    void UpdateWarehouseActionLine(
        long lineId,
        string status,
        long? targetDocId,
        string? resultJson,
        string? errorCode,
        string? errorMessage,
        DateTime updatedAt);

    WarehouseTask? GetWarehouseTask(long taskId);
    WarehouseTask? FindWarehouseTaskByRef(string taskRef);
    IReadOnlyList<WarehouseTask> GetWarehouseTasksByBundle(long bundleId);
    IReadOnlyList<WarehouseTask> GetActiveWarehouseTasks(string? deviceId);
    int GetMaxWarehouseTaskRefSequenceByYear(int year);
    long AddWarehouseTask(WarehouseTask task);
    void UpdateWarehouseTaskStatus(
        long taskId,
        string status,
        DateTime? startedAt,
        DateTime? executedAt,
        DateTime? confirmedAt,
        DateTime? cancelledAt,
        string? assignedToDeviceId,
        string? assignedToUser);

    WarehouseTaskLine? GetWarehouseTaskLine(long lineId);
    IReadOnlyList<WarehouseTaskLine> GetWarehouseTaskLines(long taskId);
    long AddWarehouseTaskLine(WarehouseTaskLine line);
    void UpdateWarehouseTaskLineScan(
        long lineId,
        string status,
        string? scannedHuCode,
        long? scannedLocationId,
        DateTime? scannedAt,
        string? deviceId,
        string? operatorId,
        string? errorCode,
        string? errorMessage);

    long AddWarehouseTaskEvent(WarehouseTaskEvent warehouseEvent);
    IReadOnlyList<WarehouseTaskEvent> GetWarehouseTaskEvents(long taskId);
    bool IsHuLockedByActiveWarehouseTask(string huCode, long? excludeBundleId);
}

