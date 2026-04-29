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

    Order? GetOrder(long id);
    IReadOnlyList<Order> GetOrders();
    long AddOrder(Order order);
    void UpdateOrder(Order order);
    void UpdateOrderStatus(long orderId, OrderStatus status);
    IReadOnlyList<OrderLine> GetOrderLines(long orderId);
    IReadOnlyList<OrderLineView> GetOrderLineViews(long orderId);
    IReadOnlyList<OrderReceiptLine> GetOrderReceiptRemaining(long orderId);
    IReadOnlyList<OrderReceiptLine> GetOrderReceiptRemainingWithoutReservedStock(long orderId);
    IReadOnlyList<OrderReceiptPlanLine> GetOrderReceiptPlanLines(long orderId);
    IReadOnlyCollection<string> GetReservedOrderReceiptHuCodes(long? excludeOrderId = null);
    void ReplaceOrderReceiptPlanLines(long orderId, IReadOnlyList<OrderReceiptPlanLine> lines);
    IReadOnlyList<OrderShipmentLine> GetOrderShipmentRemaining(long orderId);
    long AddOrderLine(OrderLine line);
    void UpdateOrderLineQty(long orderLineId, double qtyOrdered);
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
    IReadOnlyList<long> GetAvailableKmCodeIds(long? batchId, long? orderId, long skuId, string? gtin14, int take);
    IReadOnlyList<long> GetAvailableKmOnHandCodeIds(long? orderId, long skuId, string? gtin14, long? locationId, long? huId, int take);
    int AssignKmCodesToReceipt(IReadOnlyList<long> codeIds, long docId, long lineId, long? huId, long? locationId);
    void MarkKmCodeShipped(long codeId, long docId, long lineId, long? orderId);
    int DeleteKmCodesFromBatch(long batchId, IReadOnlyList<long> codeIds);
    void DeleteKmBatch(long batchId);
}

