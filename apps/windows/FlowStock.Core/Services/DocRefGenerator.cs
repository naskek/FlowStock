using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public static class DocRefGenerator
{
    public static string Generate(IDataStore data, DocType type, DateTime date)
    {
        var prefix = GetPrefix(type);
        var year = date.Year;
        var baseRef = $"{prefix}-{year}-";
        var max = data.GetMaxDocRefSequenceByYear(year);
        var next = max + 1;

        string candidate;
        do
        {
            candidate = $"{baseRef}{next:000000}";
            next++;
        }
        while (data.FindDocByRef(candidate) != null);

        return candidate;
    }

    public static string GetPrefix(DocType type)
    {
        return type switch
        {
            DocType.Inbound => "IN",
            DocType.Outbound => "OUT",
            DocType.Move => "MOV",
            DocType.WriteOff => "WO",
            DocType.Inventory => "INV",
            _ => "DOC"
        };
    }
}

