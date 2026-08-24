namespace FlowStock.Core.Services;

/// <summary>
/// Historical source labels retained only for reading pre-cutover request provenance.
/// The former global/item-based request creation workflow has been removed.
/// </summary>
public static class MarkingNeedCreationService
{
    public const string ProductionNeedSourceType = "PRODUCTION_NEED";
    public const string ProductionOrderSourceType = "PRODUCTION_ORDER";
}
