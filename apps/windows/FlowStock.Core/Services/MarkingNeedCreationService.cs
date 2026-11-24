using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class MarkingNeedCreationService
{
    public const string ProductionNeedSourceType = "PRODUCTION_NEED";
    public const string ProductionOrderSourceType = "PRODUCTION_ORDER";
    public MarkingNeedCreationService(IDataStore dataStore) { }
    public MarkingNeedCreationResult CreateFromProductionNeeds(DateTime createdAt) =>
        throw new InvalidOperationException("Внутренний учёт маркировки отключён. Формируйте Excel из заказа.");
}
