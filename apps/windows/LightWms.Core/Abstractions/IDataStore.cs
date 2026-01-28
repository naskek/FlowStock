using LightWms.Core.Models;

namespace LightWms.Core.Abstractions;

public interface IDataStore
{
    void Initialize();
    void ExecuteInTransaction(Action<IDataStore> work);

    Item? FindItemByBarcode(string barcode);
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

    Partner? GetPartner(long id);
    Partner? FindPartnerByCode(string code);
    IReadOnlyList<Partner> GetPartners();
    long AddPartner(Partner partner);
    void UpdatePartner(Partner partner);
    void DeletePartner(long partnerId);
    bool IsPartnerUsed(long partnerId);

    Doc? FindDocByRef(string docRef, DocType type);
    Doc? GetDoc(long id);
    IReadOnlyList<Doc> GetDocs();
    IReadOnlyList<Doc> GetDocsByOrder(long orderId);
    int GetMaxDocRefSequence(DocType type, string prefix);
    long AddDoc(Doc doc);
    IReadOnlyList<DocLine> GetDocLines(long docId);
    IReadOnlyList<DocLineView> GetDocLineViews(long docId);
    long AddDocLine(DocLine line);
    void UpdateDocLineQty(long docLineId, double qty, double? qtyInput, string? uomCode);
    void DeleteDocLine(long docLineId);
    void DeleteDocLines(long docId);
    void UpdateDocHeader(long docId, long? partnerId, string? orderRef, string? shippingRef);
    void UpdateDocOrder(long docId, long? orderId, string? orderRef);
    void UpdateDocStatus(long docId, DocStatus status, DateTime? closedAt);

    Order? GetOrder(long id);
    IReadOnlyList<Order> GetOrders();
    long AddOrder(Order order);
    void UpdateOrder(Order order);
    void UpdateOrderStatus(long orderId, OrderStatus status);
    IReadOnlyList<OrderLine> GetOrderLines(long orderId);
    IReadOnlyList<OrderLineView> GetOrderLineViews(long orderId);
    long AddOrderLine(OrderLine line);
    void DeleteOrderLines(long orderId);
    void DeleteOrder(long orderId);
    IReadOnlyDictionary<long, double> GetLedgerTotalsByItem();
    IReadOnlyDictionary<long, double> GetShippedTotalsByOrder(long orderId);
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

    HuRecord CreateHuRecord(string? createdBy);
    HuRecord? GetHuByCode(string code);
    IReadOnlyList<HuRecord> GetHus(string? search, int take);
    void CloseHu(string code, string? closedBy, string? note);
    IReadOnlyList<HuLedgerRow> GetHuLedgerRows(string code);

    bool IsEventImported(string eventId);
    void AddImportedEvent(ImportedEvent ev);
    long AddImportError(ImportError err);
    IReadOnlyList<ImportError> GetImportErrors(string? reason);
    ImportError? GetImportError(long id);
    void DeleteImportError(long id);
}
