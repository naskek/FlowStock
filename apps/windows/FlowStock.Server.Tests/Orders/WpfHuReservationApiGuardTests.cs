namespace FlowStock.Server.Tests.Orders;

public sealed class WpfHuReservationApiGuardTests
{
    [Fact]
    public void WpfReadApiService_BuildsHuReservationCandidatesRequest()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfReadApiService.cs");
        var models = ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfHuReservationApiModels.cs");

        Assert.Contains("TryGetHuReservationCandidates(", source);
        Assert.Contains("/api/orders/hu-reservation-candidates", source);
        Assert.Contains("TryPost(", source);
        Assert.Contains("[JsonPropertyName(\"order_line_id\")]", models);
        Assert.Contains("[JsonPropertyName(\"selected_hu_codes\")]", models);
        Assert.Contains("[JsonPropertyName(\"client_line_key\")]", models);
        Assert.Contains("[JsonPropertyName(\"item_id\")]", models);
        Assert.Contains("[JsonPropertyName(\"qty_ordered\")]", models);
        Assert.Contains("[JsonPropertyName(\"exclude_hu_codes\")]", models);
    }

    [Fact]
    public void WpfReadApiService_BuildsHuReservationsApplyRequest()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfReadApiService.cs");

        Assert.Contains("TryApplyHuReservations(", source);
        Assert.Contains("/hu-reservations/apply", source);
        Assert.Contains("HttpMethod.Post", source);
        Assert.Contains("JsonContent.Create(body, options: JsonOptions)", source);
    }

    [Fact]
    public void OrderDetailsWindow_UsesHuBindingFlow()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs");

        Assert.Contains("CustomerOrderHuBindingCoordinator", source);
        Assert.Contains("TryApplyHuReservationsAfterSave", source);
        Assert.Contains("HuReservationPickerWindow", source);
        Assert.DoesNotContain("TryResolveBindReservedStockForSave", source);
        Assert.DoesNotContain("auto-redistribute-from-internal", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reserve-produced-hu", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/api/orders/redistribute", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ApplyCustomerOrderSaveFollowUp", source, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
