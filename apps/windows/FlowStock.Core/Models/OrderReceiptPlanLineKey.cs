namespace FlowStock.Core.Models;

/// <summary>
/// Точная пара (заказ, строка заказа), задающая scope batch-замены строк
/// <c>order_receipt_plan_lines</c>. Используется для удаления исходящих привязок
/// именно по этим парам и для исключения этих пар из проверки конфликтов HU.
/// </summary>
public readonly record struct OrderReceiptPlanLineKey(long OrderId, long OrderLineId);
