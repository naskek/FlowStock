using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

/// <summary>
/// Клиентская prevalidation изменения qty строки заказа (WPF и др.).
/// </summary>
public static class OrderLineEditPrevalidation
{
    public static bool TryValidateQtyChange(
        double newQty,
        OrderLineView line,
        OrderType orderType,
        out string? errorMessage)
    {
        return TryValidateQtyChange(
            newQty,
            line.QtyShipped,
            line.QtyProduced,
            line.FilledPalletQty,
            orderType,
            out errorMessage);
    }

    public static bool TryValidateQtyChange(
        double newQty,
        double qtyShipped,
        double qtyProduced,
        double filledPalletQty,
        OrderType orderType,
        out string? errorMessage)
    {
        return OrderLineQtyChangeRules.TryValidateQtyChangeForPresentation(
            newQty,
            qtyShipped,
            qtyProduced,
            filledPalletQty,
            reservedPlanQty: 0,
            orderType,
            out errorMessage);
    }

    /// <summary>
    /// UI: показывать предупреждение и не применять qty к строке, если prevalidation не прошла.
    /// </summary>
    public static bool ShouldBlockLocalQtyApply(bool validationAllowed) => !validationAllowed;

    /// <summary>
    /// После неуспешного PUT нужно перечитать canonical metrics с сервера (сброс локального qty).
    /// </summary>
    public static bool ShouldReloadLineMetricsAfterFailedPersist(bool persistSucceeded) => !persistSucceeded;

    /// <summary>
    /// После успешного PUT нужен полный canonical reload заказа.
    /// </summary>
    public static bool ShouldReloadCanonicalOrderAfterSuccessfulPersist(bool persistSucceeded) => persistSucceeded;
}
