namespace FlowStock.Core.Models;

public static class WarehouseBundleStatus
{
    public const string Draft = "DRAFT";
    public const string Submitted = "SUBMITTED";
    public const string Approved = "APPROVED";
    public const string InExecution = "IN_EXECUTION";
    public const string Executed = "EXECUTED";
    public const string Completed = "COMPLETED";
    public const string Rejected = "REJECTED";
    public const string Cancelled = "CANCELLED";
    public const string Failed = "FAILED";

    public static bool IsActiveForHuLock(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        return status.Equals(Submitted, StringComparison.OrdinalIgnoreCase)
               || status.Equals(Approved, StringComparison.OrdinalIgnoreCase)
               || status.Equals(InExecution, StringComparison.OrdinalIgnoreCase)
               || status.Equals(Executed, StringComparison.OrdinalIgnoreCase);
    }
}

public static class WarehouseBundleSource
{
    public const string WebPlanner = "WEB_PLANNER";
    public const string Wpf = "WPF";
    public const string Api = "API";
}

public static class WarehouseActionType
{
    public const string MoveHu = "MOVE_HU";
    public const string AdoptPalletPlan = "ADOPT_PALLET_PLAN";
    public const string ShipHu = "SHIP_HU";
    public const string CreateOutboundDraft = "CREATE_OUTBOUND_DRAFT";
    public const string DeletePalletPlan = "DELETE_PALLET_PLAN";
    public const string MergeInternalToCustomer = "MERGE_INTERNAL_TO_CUSTOMER";
    public const string CreateProductionPalletPlan = "CREATE_PRODUCTION_PALLET_PLAN";

    public static bool RequiresTsd(string actionType)
    {
        return actionType.Equals(MoveHu, StringComparison.OrdinalIgnoreCase)
               || actionType.Equals(ShipHu, StringComparison.OrdinalIgnoreCase);
    }
}

public static class WarehouseActionLineStatus
{
    public const string Pending = "PENDING";
    public const string Done = "DONE";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";
}

public static class WarehouseTaskStatus
{
    public const string New = "NEW";
    public const string Assigned = "ASSIGNED";
    public const string InExecution = "IN_EXECUTION";
    public const string Executed = "EXECUTED";
    public const string Confirmed = "CONFIRMED";
    public const string Cancelled = "CANCELLED";
    public const string Failed = "FAILED";
}

public static class WarehouseTaskLineStatus
{
    public const string Pending = "PENDING";
    public const string Scanned = "SCANNED";
    public const string Done = "DONE";
    public const string Cancelled = "CANCELLED";
    public const string Failed = "FAILED";
}

public static class WarehouseTaskEventType
{
    public const string ScanHu = "SCAN_HU";
    public const string ScanLocation = "SCAN_LOCATION";
    public const string ConfirmLine = "CONFIRM_LINE";
    public const string CompleteTask = "COMPLETE_TASK";
    public const string Error = "ERROR";
}
