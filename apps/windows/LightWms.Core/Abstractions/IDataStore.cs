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

    Location? FindLocationByCode(string code);
    IReadOnlyList<Location> GetLocations();
    long AddLocation(Location location);

    Partner? GetPartner(long id);
    IReadOnlyList<Partner> GetPartners();
    long AddPartner(Partner partner);

    Doc? FindDocByRef(string docRef, DocType type);
    Doc? GetDoc(long id);
    IReadOnlyList<Doc> GetDocs();
    long AddDoc(Doc doc);
    IReadOnlyList<DocLine> GetDocLines(long docId);
    IReadOnlyList<DocLineView> GetDocLineViews(long docId);
    long AddDocLine(DocLine line);
    void UpdateDocLineQty(long docLineId, double qty);
    void DeleteDocLine(long docLineId);
    void UpdateDocHeader(long docId, long? partnerId, string? orderRef, string? shippingRef);
    void UpdateDocStatus(long docId, DocStatus status, DateTime? closedAt);

    void AddLedgerEntry(LedgerEntry entry);
    IReadOnlyList<StockRow> GetStock(string? search);
    double GetLedgerBalance(long itemId, long locationId);

    bool IsEventImported(string eventId);
    void AddImportedEvent(ImportedEvent ev);
    long AddImportError(ImportError err);
    IReadOnlyList<ImportError> GetImportErrors(string? reason);
    ImportError? GetImportError(long id);
    void DeleteImportError(long id);
}
