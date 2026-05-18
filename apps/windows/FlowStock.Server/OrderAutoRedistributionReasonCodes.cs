namespace FlowStock.Server;

public static class OrderAutoRedistributionReasonCodes
{
    public const string InsufficientProducedStock = "INSUFFICIENT_PRODUCED_STOCK";
    public const string NothingToTransfer = "NOTHING_TO_TRANSFER";
    public const string TransferExceedsRemaining = "TRANSFER_EXCEEDS_REMAINING";
    public const string CustomerReservationRequired = "CUSTOMER_RESERVATION_REQUIRED";
    public const string OrderNotEditable = "ORDER_NOT_EDITABLE";
    public const string SourceLineNotFound = "SOURCE_LINE_NOT_FOUND";
    public const string RedistributionFailed = "REDISTRIBUTION_FAILED";

    public static string MapFromExceptionMessage(string message)
    {
        if (message.Contains("Недостаточно выпущенного", StringComparison.OrdinalIgnoreCase))
        {
            return InsufficientProducedStock;
        }

        if (message.Contains("Нет доступного объема", StringComparison.OrdinalIgnoreCase))
        {
            return NothingToTransfer;
        }

        if (message.Contains("Нельзя перенести больше", StringComparison.OrdinalIgnoreCase))
        {
            return TransferExceedsRemaining;
        }

        if (message.Contains("резерв складского", StringComparison.OrdinalIgnoreCase)
            || message.Contains("резервирование HU", StringComparison.OrdinalIgnoreCase))
        {
            return CustomerReservationRequired;
        }

        if (message.Contains("недоступен для перераспределения", StringComparison.OrdinalIgnoreCase))
        {
            return OrderNotEditable;
        }

        if (message.Contains("Позиция не найдена", StringComparison.OrdinalIgnoreCase))
        {
            return SourceLineNotFound;
        }

        return RedistributionFailed;
    }

    public static string DescribeSkippedReason(string? skippedReason)
    {
        return skippedReason switch
        {
            "ORDER_NOT_FOUND" => "Заказ не найден на сервере.",
            "TARGET_NOT_CUSTOMER" => "Операция доступна только для клиентского заказа.",
            "ORDER_NOT_EDITABLE" => "Заказ недоступен для перераспределения.",
            "CUSTOMER_RESERVATION_DISABLED" =>
                "Автоперенос не выполнялся, потому что заказ сохранён без резерва складских HU. "
                + "Сохраните заказ с ответом «Да», чтобы разрешить резерв HU и автоперенос с INTERNAL.",
            "TARGET_CUSTOMER_HAS_NO_OPEN_LINES" =>
                "У клиентского заказа нет строк с количеством больше нуля для автопереноса.",
            "NO_OPEN_INTERNAL_ORDERS" =>
                "После обновления статусов нет открытых INTERNAL-заказов для автопереноса.",
            "OPEN_INTERNAL_WITHOUT_MATCHING_ITEM" =>
                "Открытые INTERNAL-заказы есть, но в них нет номенклатуры, совпадающей со строками клиентского заказа.",
            "OPEN_INTERNAL_MATCHING_ITEM_QTY_ZERO" =>
                "Есть открытый INTERNAL с совпадающей номенклатурой, но количество источника уже равно нулю.",
            "SOURCE_INTERNAL_HAS_PALLET_PLAN_BUT_QTY_ZERO" =>
                "INTERNAL имеет palletized PRD plan, но qty_ordered уже 0. Обычный auto-redistribute не может перенести строки. "
                + "Нужно удалить/перенести паллетный план отдельным действием.",
            "INTERNAL_STATUSES_REFRESHED" =>
                "Перед автопереносом обновлены устаревшие статусы INTERNAL-заказов.",
            _ => string.Empty
        };
    }
}
