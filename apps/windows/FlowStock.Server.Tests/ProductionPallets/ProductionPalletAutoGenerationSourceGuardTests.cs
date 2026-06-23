namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class ProductionPalletAutoGenerationSourceGuardTests
{
    [Fact]
    public void SyncOrderLinePlan_DoesNotAppendMissingProductionPallets()
    {
        var source = File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.Core", "Services", "ProductionPalletService.cs"));
        var method = ExtractMethod(source, "internal void SyncOrderLinePlanInStore");

        Assert.DoesNotContain("AppendPlannedPalletsForOrderLinesInStore", method, StringComparison.Ordinal);
        Assert.DoesNotContain("FindPreparedOpenProductionReceipt", method, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateProductionPalletHuCode", method, StringComparison.Ordinal);
    }

    [Fact]
    public void HuBindingDetach_DoesNotRestoreProductionPlan()
    {
        var shared = File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.Core", "Services", "HuBindingApplyShared.cs"));
        var manage = File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.Core", "Services", "OrderHuBindingManageApplyService.cs"));
        var orderScoped = File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.Core", "Services", "OrderHuBindingApplyFinalService.cs"));

        Assert.DoesNotContain("RestoreProductionPlanForOrderLine", shared + manage + orderScoped, StringComparison.Ordinal);
        Assert.DoesNotContain("HU_BINDING_DETACH", shared + manage + orderScoped, StringComparison.Ordinal);
        Assert.DoesNotContain("SyncOrderLinePlanInStore", manage + orderScoped, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateOrder_DoesNotRebuildOrAllocateHu()
    {
        var source = File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.Core", "Services", "OrderService.cs"));
        var method = ExtractMethod(source, "public void UpdateOrder(");

        Assert.DoesNotContain("TryRebuildOrderReceiptPlan", method, StringComparison.Ordinal);
        Assert.DoesNotContain("RebuildInternalOrderReceiptPlan", method, StringComparison.Ordinal);
        Assert.DoesNotContain("AllocateHuCodesForPlan", method, StringComparison.Ordinal);
        Assert.DoesNotContain("AppendPlannedPalletsForOrderLinesInStore", method, StringComparison.Ordinal);
        Assert.DoesNotContain("RestoreProductionPlanForOrderLine", method, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateOrderCore_DoesNotRebuildOrAllocateHu()
    {
        var source = File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.Core", "Services", "OrderService.cs"));
        var method = ExtractMethod(source, "private long CreateOrderCore(");

        Assert.DoesNotContain("TryRebuildOrderReceiptPlan", method, StringComparison.Ordinal);
        Assert.DoesNotContain("RebuildInternalOrderReceiptPlan", method, StringComparison.Ordinal);
        Assert.DoesNotContain("AllocateHuCodesForPlan", method, StringComparison.Ordinal);
        Assert.DoesNotContain("AppendPlannedPalletsForOrderLinesInStore", method, StringComparison.Ordinal);
    }

    [Fact]
    public void HuBindingCancellation_IsSurplusBasedAndExcludesFilled()
    {
        var shared = File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.Core", "Services", "HuBindingApplyShared.cs"));
        var manage = File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.Core", "Services", "OrderHuBindingManageApplyService.cs"));
        var orderScoped = File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.Core", "Services", "OrderHuBindingApplyFinalService.cs"));

        // Отмена будущего плана теперь основана на реальном surplus, FILLED больше не маскируется как "expected=PLANNED".
        Assert.DoesNotContain("SelectSafeWholePlannedPalletsToCancel", shared + manage + orderScoped, StringComparison.Ordinal);
        Assert.DoesNotContain("status=FILLED expected=PLANNED", shared, StringComparison.Ordinal);
        Assert.Contains("ComputeCancellableFuturePlanSurplus", shared, StringComparison.Ordinal);
        Assert.Contains("SelectFuturePlanPalletsToCancel", shared, StringComparison.Ordinal);
        Assert.Contains("ComputeCancellableFuturePlanSurplus", manage, StringComparison.Ordinal);
        Assert.Contains("ComputeCancellableFuturePlanSurplus", orderScoped, StringComparison.Ordinal);
    }

    private static string ExtractMethod(string source, string signature)
    {
        var index = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Method signature not found: {signature}");
        var openingBrace = source.IndexOf('{', index);
        Assert.True(openingBrace >= 0, $"Method body not found: {signature}");

        var depth = 0;
        for (var i = openingBrace; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[openingBrace..(i + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Method body is incomplete: {signature}");
    }

    private static string GetRepoFile(params string[] parts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(new[] { current.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Repository file not found: {Path.Combine(parts)}");
    }
}
