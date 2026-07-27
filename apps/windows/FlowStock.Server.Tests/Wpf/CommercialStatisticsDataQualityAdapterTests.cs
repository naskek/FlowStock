using System.Text.Json;
using FlowStock.App;

namespace FlowStock.Server.Tests.Wpf;

public sealed class CommercialStatisticsDataQualityAdapterTests
{
    [Fact]
    public void Server_json_maps_every_data_quality_counter()
    {
        const string json = """
{
  "data_quality": {
    "missing_price_fact_count": 1,
    "missing_price_quantity": 1.25,
    "missing_vat_fact_count": 2,
    "missing_vat_quantity": 2.5,
    "unlinked_sales_fact_count": 3,
    "unlinked_sales_quantity": 3.75,
    "item_mismatch_sales_fact_count": 4,
    "item_mismatch_sales_quantity": 4.5,
    "financially_incomplete_fact_count": 5,
    "financially_incomplete_quantity": 5.25,
    "is_financially_complete": false
  }
}
""";

        var result = JsonSerializer.Deserialize<WpfCommercialStatisticsResult>(json);
        var quality = Assert.IsType<WpfCommercialStatisticsDataQuality>(result?.DataQuality);

        Assert.Equal(1, quality.MissingPriceFactCount);
        Assert.Equal(1.25m, quality.MissingPriceQuantity);
        Assert.Equal(2, quality.MissingVatFactCount);
        Assert.Equal(2.5m, quality.MissingVatQuantity);
        Assert.Equal(3, quality.UnlinkedSalesFactCount);
        Assert.Equal(3.75m, quality.UnlinkedSalesQuantity);
        Assert.Equal(4, quality.ItemMismatchSalesFactCount);
        Assert.Equal(4.5m, quality.ItemMismatchSalesQuantity);
        Assert.Equal(5, quality.FinanciallyIncompleteFactCount);
        Assert.Equal(5.25m, quality.FinanciallyIncompleteQuantity);
        Assert.False(quality.IsFinanciallyComplete);
    }

    [Fact]
    public void Presentation_lists_each_incomplete_data_reason_with_quantity()
    {
        var quality = new WpfCommercialStatisticsDataQuality
        {
            MissingPriceFactCount = 1,
            MissingPriceQuantity = 1.25m,
            MissingVatFactCount = 2,
            MissingVatQuantity = 2.5m,
            UnlinkedSalesFactCount = 3,
            UnlinkedSalesQuantity = 3.75m,
            ItemMismatchSalesFactCount = 4,
            ItemMismatchSalesQuantity = 4.5m,
            FinanciallyIncompleteFactCount = 5,
            FinanciallyIncompleteQuantity = 5.25m,
            IsFinanciallyComplete = false
        };

        var text = CommercialStatisticsDataQualityPresentation.Format(quality);

        Assert.Contains("Без цены: 1, количество: 1,25", text);
        Assert.Contains("без НДС: 2, количество: 2,5", text);
        Assert.Contains("непривязанные продажи: 3, количество: 3,75", text);
        Assert.Contains("несовпадения товара: 4, количество: 4,5", text);
        Assert.Contains("всего неполных: 5, количество: 5,25", text);
    }
}
