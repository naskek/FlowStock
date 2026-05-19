using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public static class WarehouseRefGenerator
{
    public static string GenerateBundleRef(IDataStore data, DateTime date)
    {
        var year = date.Year;
        var baseRef = $"BND-{year}-";
        var next = data.GetMaxWarehouseBundleRefSequenceByYear(year) + 1;
        string candidate;
        do
        {
            candidate = $"{baseRef}{next:000000}";
            next++;
        }
        while (data.FindWarehouseBundleByRef(candidate) != null);

        return candidate;
    }

    public static string GenerateTaskRef(IDataStore data, DateTime date)
    {
        var year = date.Year;
        var baseRef = $"TSK-{year}-";
        var next = data.GetMaxWarehouseTaskRefSequenceByYear(year) + 1;
        string candidate;
        do
        {
            candidate = $"{baseRef}{next:000000}";
            next++;
        }
        while (data.FindWarehouseTaskByRef(candidate) != null);

        return candidate;
    }
}
