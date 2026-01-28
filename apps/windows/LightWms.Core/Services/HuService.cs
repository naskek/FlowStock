using LightWms.Core.Abstractions;
using LightWms.Core.Models;

namespace LightWms.Core.Services;

public sealed class HuService
{
    private readonly IDataStore _data;

    public HuService(IDataStore data)
    {
        _data = data;
    }

    public HuRecord CreateHu(string? createdBy = null)
    {
        return _data.CreateHuRecord(createdBy);
    }

    public HuRecord? GetHuByCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return _data.GetHuByCode(code.Trim());
    }

    public void EnsureHuActive(string code)
    {
        var record = GetHuByCode(code);
        if (record == null)
        {
            throw new InvalidOperationException("UNKNOWN_HU");
        }

        if (!string.Equals(record.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("HU_NOT_ACTIVE");
        }
    }
}
