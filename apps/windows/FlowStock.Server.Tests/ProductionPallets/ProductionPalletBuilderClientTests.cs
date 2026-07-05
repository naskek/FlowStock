using FlowStock.App;

namespace FlowStock.Server.Tests.ProductionPallets;

/// <summary>
/// WPF client-side contract for the pallet constructor endpoints: parsing of the
/// three-section preview and of structured plan errors (error_code +
/// current_preview_fingerprint + details.lines with stable snake_case names).
/// </summary>
public sealed class ProductionPalletBuilderClientTests
{
    private const string PreviewJson = """
    {
      "order_id": 10,
      "order_ref": "056",
      "order_type": "INTERNAL",
      "order_status": "INPROGRESS",
      "production_required": true,
      "preview_fingerprint": "FP-1",
      "lines": [
        { "order_line_id": 101, "item_id": 100, "item_name": "Хрен столовый", "max_qty_per_hu": 2250, "shortfall_qty": 3375 },
        { "order_line_id": 102, "item_id": 200, "item_name": "Хрен со свёклой", "max_qty_per_hu": 2250, "shortfall_qty": 1125 }
      ],
      "suggested_pallets": [
        {
          "temp_no": 1,
          "capacity_qty": 2250,
          "total_qty": 2250,
          "is_mixed": false,
          "components": [ { "order_line_id": 101, "item_id": 100, "item_name": "Хрен столовый", "qty": 2250 } ]
        }
      ],
      "open_plan_pallets": [
        {
          "kind": "open",
          "pallet_id": 7,
          "hu_code": "HU-0000007",
          "prd_doc_id": 900,
          "prd_ref": "PRD-1",
          "status": "PRINTED",
          "effective_status": "PARTIALLY_FILLED",
          "capacity_qty": 2250,
          "total_qty": 2250,
          "is_mixed": true,
          "has_component_progress": true,
          "can_delete": false,
          "disabled_reason": "Паллета частично наполнена",
          "components": [
            { "production_pallet_line_id": 71, "order_line_id": 101, "item_id": 100, "item_name": "Хрен столовый", "planned_qty": 1125, "filled_qty": 1125, "is_completed": true }
          ]
        }
      ],
      "historical_pallets": [
        {
          "kind": "historical",
          "pallet_id": 8,
          "hu_code": "HU-0000008",
          "prd_doc_id": 901,
          "prd_ref": "PRD-2",
          "status": "FILLED",
          "effective_status": "FILLED",
          "capacity_qty": null,
          "total_qty": 600,
          "is_mixed": false,
          "has_component_progress": true,
          "can_delete": false,
          "disabled_reason": "Паллета наполнена/выпущена",
          "components": []
        }
      ]
    }
    """;

    [Fact]
    public void ParsePlanPreviewJson_ReadsThreeSectionsAndFingerprint()
    {
        var preview = WpfProductionPalletApiService.ParsePlanPreviewJson(PreviewJson);

        Assert.NotNull(preview);
        Assert.Equal(10, preview!.OrderId);
        Assert.Equal("FP-1", preview.PreviewFingerprint);
        Assert.True(preview.ProductionRequired);

        var line = Assert.Single(preview.Lines, line => line.OrderLineId == 101);
        Assert.Equal(3375, line.ShortfallQty, 3);
        Assert.Equal(2250, line.MaxQtyPerHu);

        var suggested = Assert.Single(preview.SuggestedPallets);
        Assert.Equal(2250, suggested.CapacityQty);
        Assert.False(suggested.IsMixed);
        var component = Assert.Single(suggested.Components);
        Assert.Equal(101, component.OrderLineId);
        Assert.Equal(2250, component.Qty, 3);

        var open = Assert.Single(preview.OpenPlanPallets);
        Assert.Equal("HU-0000007", open.HuCode);
        Assert.Equal("PARTIALLY_FILLED", open.EffectiveStatus);
        Assert.True(open.IsMixed);
        Assert.False(open.CanDelete);
        Assert.Equal("Паллета частично наполнена", open.DisabledReason);
        var openComponent = Assert.Single(open.Components);
        Assert.Equal(1125, openComponent.FilledQty, 3);
        Assert.True(openComponent.IsCompleted);

        var historical = Assert.Single(preview.HistoricalPallets);
        Assert.Equal("FILLED", historical.Status);
        Assert.Null(historical.CapacityQty);
    }

    [Fact]
    public void ParsePlanErrorJson_LineAllocationMismatch_ReadsTypedDetailsLines()
    {
        const string json = """
        {
          "ok": false,
          "error_code": "LINE_ALLOCATION_MISMATCH",
          "message": "Распределение по строкам не совпадает с производственной нехваткой.",
          "details": {
            "lines": [
              { "order_line_id": 101, "required_qty": 3375, "allocated_qty": 2250, "difference_qty": -1125 },
              { "order_line_id": 102, "required_qty": 1125, "allocated_qty": 0, "difference_qty": -1125 }
            ]
          },
          "current_preview_fingerprint": null
        }
        """;

        var error = WpfProductionPalletApiService.ParsePlanErrorJson(json);

        Assert.Equal("LINE_ALLOCATION_MISMATCH", error.ErrorCode);
        Assert.Contains("нехваткой", error.Message);
        Assert.Null(error.CurrentPreviewFingerprint);
        Assert.Equal(2, error.AllocationLines.Count);
        var mismatch = Assert.Single(error.AllocationLines, line => line.OrderLineId == 101);
        Assert.Equal(3375, mismatch.RequiredQty, 3);
        Assert.Equal(2250, mismatch.AllocatedQty, 3);
        Assert.Equal(-1125, mismatch.DifferenceQty, 3);
    }

    [Fact]
    public void ParsePlanErrorJson_Stale_ReadsCurrentFingerprint()
    {
        const string json = """
        {
          "ok": false,
          "error_code": "PLAN_PREVIEW_STALE",
          "message": "Данные заказа изменились с момента предпросмотра. Обновите план паллет.",
          "details": null,
          "current_preview_fingerprint": "FP-NEW"
        }
        """;

        var error = WpfProductionPalletApiService.ParsePlanErrorJson(json);

        Assert.Equal("PLAN_PREVIEW_STALE", error.ErrorCode);
        Assert.Equal("FP-NEW", error.CurrentPreviewFingerprint);
        Assert.Empty(error.AllocationLines);
    }

    [Fact]
    public void ParsePlanErrorJson_NonJsonBody_FallsBackToUnknownCode()
    {
        var error = WpfProductionPalletApiService.ParsePlanErrorJson("<html>502</html>");

        Assert.Equal("UNKNOWN", error.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void WpfConfirmApi_PostsSnakeCaseDeltaToPlanExplicitEndpoint()
    {
        var source = TestSources.ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfProductionPalletApiService.cs");

        Assert.Contains("/api/orders/{orderId}/production-pallets/plan-explicit", source, StringComparison.Ordinal);
        Assert.Contains("/api/orders/{orderId}/production-pallets/plan-preview", source, StringComparison.Ordinal);
        Assert.Contains("preview_fingerprint = previewFingerprint", source, StringComparison.Ordinal);
        Assert.Contains("order_line_id = component.OrderLineId", source, StringComparison.Ordinal);
        Assert.Contains("qty = component.Qty", source, StringComparison.Ordinal);
    }
}

internal static class TestSources
{
    public static string ReadRepoFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FlowStock.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(new[] { directory!.FullName }.Concat(segments.Skip(2)).ToArray()));
    }
}
