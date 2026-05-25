namespace FlowStock.Server.Tests.Orders;

public sealed class CustomerOrderHuBindingCoordinatorSourceTests
{
    [Fact]
    public void Coordinator_RefreshesCandidatesAfterEndLoad()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "CustomerOrderHuBindingCoordinator.cs");

        Assert.Contains("public void EndLoad()", source);
        Assert.Contains("ScheduleCandidatesRefresh();", source);
        Assert.Contains("ApplyInitialAutoSelection", source);
        Assert.Contains("ManualSelectionTouched", source);
        Assert.Contains("BeginServerReservationReload", source);

        var applyPickerSlice = SliceMethod(source, "public void ApplyPickerSelection");
        Assert.DoesNotContain("ScheduleCandidatesRefresh", applyPickerSlice);
    }

    [Fact]
    public void Coordinator_ClearsSelectionsBeforeMergingCanonicalServerPlan()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "CustomerOrderHuBindingCoordinator.cs");

        var setContext = SliceMethod(source, "public void SetOrderContext");
        Assert.Contains("state.BeginServerReservationReload();", setContext);
        Assert.Contains("ApplyExistingPlanLines(orderId.Value);", setContext);
        Assert.True(
            setContext.IndexOf("state.BeginServerReservationReload();", StringComparison.Ordinal)
            < setContext.IndexOf("ApplyExistingPlanLines(orderId.Value);", StringComparison.Ordinal));

        var resetMethod = SliceMethod(source, "public void BeginServerReservationReload");
        Assert.Contains("_selectedHuCodes.Clear();", resetMethod);
        Assert.Contains("_selectedQtyByHu.Clear();", resetMethod);
        Assert.Contains("_existingOnlyReservations.Clear();", resetMethod);
        Assert.Contains("_manualSelectionTouched = false;", resetMethod);
    }

    [Fact]
    public void Coordinator_DoesNotExcludeOwnSelectedHuFromBatchCandidates()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "CustomerOrderHuBindingCoordinator.cs");

        Assert.Contains("Array.Empty<string>()", source);
        Assert.Contains("GetPickerCandidates()", source);
    }

    [Fact]
    public void OrderDetailsWindow_LoadsCandidatesBeforeOpeningPicker()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs");

        Assert.Contains("EnsureLineCandidatesLoaded", source);
        Assert.Contains("GetPickerCandidates()", source);
        Assert.Contains("GetSelectedHuCodesOnOtherLines", source);
    }

    [Fact]
    public void HuReservationPickerWindow_UsesEnablementRules()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "HuReservationPickerWindow.xaml.cs");

        Assert.Contains("CustomerOrderHuPickerRules.ApplyRowEnablement", source);
        Assert.Contains("CustomerOrderHuPickerRules.TrySelectRow", source);
        Assert.Contains("SetEnablement", source);
    }

    private static string SliceMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var next = source.IndexOf("\n    public ", start + signature.Length, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
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
