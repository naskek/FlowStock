using LightWms.Core.Abstractions;
using LightWms.Core.Models;

namespace LightWms.Core.Services;

public static class DocRefGenerator
{
    public static string Generate(IDataStore data, DocType type, DateTime date)
    {
        var prefix = GetPrefix(type);
        var baseRef = $"{prefix}-{date:yyyyMMdd}-";
        var max = data.GetMaxDocRefSequence(type, baseRef);
        var next = max + 1;

        string candidate;
        do
        {
            candidate = $"{baseRef}{next:000}";
            next++;
        }
        while (data.FindDocByRef(candidate, type) != null);

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
