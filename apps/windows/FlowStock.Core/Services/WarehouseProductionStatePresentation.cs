using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public static class WarehouseProductionStatePresentation
{
    public static string BuildWarehouseHuStatus(
        double qty,
        string? reservedCustomerOrderRef,
        string? _)
    {
        if (qty < -StockQuantityRules.QtyTolerance)
        {
            return "отрицательный остаток";
        }

        if (!string.IsNullOrWhiteSpace(reservedCustomerOrderRef))
        {
            return $"Зарезервирован: заказ {reservedCustomerOrderRef.Trim()}";
        }

        return "На складе";
    }

    public static string MapPalletStatusDisplay(string? status)
    {
        return status?.Trim().ToUpperInvariant() switch
        {
            "PLANNED" => "Ожидает",
            "PRINTED" => "Этикетка напечатана",
            "FILLED" => "Наполнена",
            _ => string.IsNullOrWhiteSpace(status) ? "—" : status.Trim()
        };
    }

    public static string BuildPalletStatusNote(string? status, bool prdIsOpen, bool inLedger)
    {
        if (inLedger)
        {
            return string.Empty;
        }

        if (string.Equals(status, "FILLED", StringComparison.OrdinalIgnoreCase) && prdIsOpen)
        {
            return "Наполнена, PRD не закрыт";
        }

        return string.Empty;
    }

    public static double ResolvePalletDisplayQty(string? status, double plannedQty, double filledQty)
    {
        if (string.Equals(status, "FILLED", StringComparison.OrdinalIgnoreCase))
        {
            return filledQty > StockQuantityRules.QtyTolerance ? filledQty : Math.Max(0d, plannedQty);
        }

        return Math.Max(0d, plannedQty);
    }
}
