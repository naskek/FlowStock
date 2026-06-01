using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

/// <summary>
/// Closes production receipt for a single filled pallet (TSD-confirmed ledger flow).
/// </summary>
public sealed class ProductionFillCloseService
{
    private readonly IDataStore _data;
    private readonly DocumentService _documents;
    private readonly FlowStockLedgerFlowOptions _options;

    public ProductionFillCloseService(
        IDataStore data,
        DocumentService documents,
        FlowStockLedgerFlowOptions options)
    {
        _data = data;
        _documents = documents;
        _options = options;
    }

    public ProductionFillAutoCloseResult TryAutoCloseAfterFill(ProductionPallet pallet)
    {
        ArgumentNullException.ThrowIfNull(pallet);

        ProductionFillAutoCloseResult? result = null;
        try
        {
            _data.ExecuteInTransaction(store =>
            {
                var current = store.GetProductionPalletByHu(pallet.HuCode) ?? pallet;
                result = TryAutoCloseAfterFillInTransaction(store, current);
                if (result is { Attempted: true, Success: false })
                {
                    throw new ProductionFillAutoCloseRollbackException(
                        result.Error ?? "Не удалось провести выпуск после наполнения.");
                }
            });
        }
        catch (ProductionFillAutoCloseRollbackException ex)
        {
            return ProductionFillAutoCloseResult.Failure(ex.Message);
        }

        return result ?? ProductionFillAutoCloseResult.Skipped();
    }

    internal ProductionFillAutoCloseResult TryAutoCloseAfterFillInTransaction(
        IDataStore store,
        ProductionPallet pallet)
    {
        if (!_options.ProductionAutoCloseOnFill)
        {
            return ProductionFillAutoCloseResult.Skipped();
        }

        ArgumentNullException.ThrowIfNull(pallet);

        if (!string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
        {
            return ProductionFillAutoCloseResult.Failure("Паллета не наполнена.");
        }

        var doc = store.GetDoc(pallet.PrdDocId);
        if (doc == null || doc.Type != DocType.ProductionReceipt)
        {
            return ProductionFillAutoCloseResult.Failure("Документ выпуска не найден.");
        }

        if (doc.Status == DocStatus.Closed)
        {
            return ProductionFillAutoCloseResult.FromClosedDoc(doc.Id, doc.DocRef);
        }

        var closeDocId = ResolveDedicatedPrdDocId(store, pallet, doc);
        var documents = ReferenceEquals(store, _data) ? _documents : new DocumentService(store);
        var closeResult = documents.TryCloseDoc(closeDocId, allowNegative: false);
        if (!closeResult.Success)
        {
            var message = closeResult.Errors.Count > 0
                ? string.Join("; ", closeResult.Errors)
                : "Не удалось провести выпуск после наполнения.";
            return ProductionFillAutoCloseResult.Failure(message);
        }

        var closedDoc = store.GetDoc(closeDocId);
        return ProductionFillAutoCloseResult.Closed(
            closeDocId,
            closedDoc?.DocRef ?? string.Empty);
    }

    private static long ResolveDedicatedPrdDocId(IDataStore store, ProductionPallet pallet, Doc sourceDoc)
    {
        var activePallets = store.GetProductionPalletsByDoc(sourceDoc.Id)
            .Where(p => !string.Equals(p.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (activePallets.Count <= 1)
        {
            return sourceDoc.Id;
        }

        var dedicatedDoc = CreateDedicatedProductionReceipt(store, sourceDoc);
        store.AssignProductionPalletToPrdDoc(pallet.Id, dedicatedDoc.Id);

        return dedicatedDoc.Id;
    }

    private static Doc CreateDedicatedProductionReceipt(IDataStore store, Doc sourceDoc)
    {
        var orderId = sourceDoc.OrderId;
        Order? order = null;
        if (orderId.HasValue)
        {
            order = store.GetOrder(orderId.Value);
        }

        var docRef = DocRefGenerator.Generate(store, DocType.ProductionReceipt, DateTime.Now);
        var docId = store.AddDoc(new Doc
        {
            DocRef = docRef,
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            CreatedAt = DateTime.Now,
            OrderId = order?.Id ?? sourceDoc.OrderId,
            OrderRef = order?.OrderRef ?? sourceDoc.OrderRef,
            Comment = "TSD auto-close per pallet"
        });

        return store.GetDoc(docId) ?? throw new InvalidOperationException("Документ выпуска не найден.");
    }

    private sealed class ProductionFillAutoCloseRollbackException : Exception
    {
        public ProductionFillAutoCloseRollbackException(string message)
            : base(message)
        {
        }
    }
}

public sealed class ProductionFillAutoCloseResult
{
    public bool Attempted { get; init; }
    public bool Success { get; init; }
    public bool AlreadyClosed { get; init; }
    public long? ClosedPrdDocId { get; init; }
    public string? ClosedPrdDocRef { get; init; }
    public string? Error { get; init; }

    public static ProductionFillAutoCloseResult Skipped()
    {
        return new ProductionFillAutoCloseResult();
    }

    public static ProductionFillAutoCloseResult FromClosedDoc(long docId, string docRef)
    {
        return new ProductionFillAutoCloseResult
        {
            Attempted = true,
            Success = true,
            AlreadyClosed = true,
            ClosedPrdDocId = docId,
            ClosedPrdDocRef = docRef
        };
    }

    public static ProductionFillAutoCloseResult Closed(long docId, string docRef)
    {
        return new ProductionFillAutoCloseResult
        {
            Attempted = true,
            Success = true,
            ClosedPrdDocId = docId,
            ClosedPrdDocRef = docRef
        };
    }

    public static ProductionFillAutoCloseResult Failure(string error)
    {
        return new ProductionFillAutoCloseResult
        {
            Attempted = true,
            Success = false,
            Error = error
        };
    }
}
