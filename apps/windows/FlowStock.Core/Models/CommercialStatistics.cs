namespace FlowStock.Core.Models;

public enum CommercialStatisticsMode
{
    Orders,
    Sales
}

public enum CommercialStatisticsGroupBy
{
    Partner,
    Item,
    Gtin,
    Brand,
    Volume
}

public sealed record CommercialStatisticsQuery(
    CommercialStatisticsMode Mode,
    CommercialStatisticsGroupBy GroupBy,
    DateTime From,
    DateTime ToExclusive,
    DateTime? DetailMonth,
    long? PartnerId,
    long? ItemId,
    string? Gtin,
    string? Brand,
    string? Volume,
    IReadOnlyList<OrderStatus> Statuses,
    int Limit,
    int Offset,
    string Sort);

public sealed record CommercialStatisticsAmounts(
    int OrderCount,
    int DocumentCount,
    int FactCount,
    decimal Quantity,
    decimal KnownFinancialQuantity,
    decimal Gross,
    decimal Net,
    decimal Vat);

public sealed record CommercialStatisticsRow(
    string? Key,
    string Label,
    CommercialStatisticsAmounts Amounts);

public sealed record CommercialStatisticsMonth(
    string Month,
    CommercialStatisticsAmounts Amounts);

public sealed record CommercialStatisticsDataQuality(
    int MissingPriceFactCount,
    decimal MissingPriceQuantity,
    int MissingVatFactCount,
    decimal MissingVatQuantity,
    int FinanciallyIncompleteFactCount,
    decimal FinanciallyIncompleteQuantity,
    int UnlinkedSalesFactCount,
    decimal UnlinkedSalesQuantity,
    int ItemMismatchSalesFactCount,
    decimal ItemMismatchSalesQuantity)
{
    public bool IsFinanciallyComplete =>
        FinanciallyIncompleteFactCount == 0
        && UnlinkedSalesFactCount == 0
        && ItemMismatchSalesFactCount == 0;
}

public sealed record CommercialStatisticsResult(
    CommercialStatisticsAmounts Summary,
    IReadOnlyList<CommercialStatisticsMonth> Monthly,
    IReadOnlyList<CommercialStatisticsRow> Groups,
    int TotalGroupCount,
    CommercialStatisticsDataQuality DataQuality);
