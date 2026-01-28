using LightWms.Core.Abstractions;
using LightWms.Core.Models;

namespace LightWms.Core.Services;

public sealed class HuService
{
    private readonly IDataStore _data;
    private const int MaxBatchSize = 1000;

    public HuService(IDataStore data)
    {
        _data = data;
    }

    public HuRecord CreateHu(string? createdBy = null)
    {
        return _data.CreateHuRecord(createdBy);
    }

    public IReadOnlyList<string> Generate(int count, string? createdBy = null)
    {
        if (count < 1 || count > MaxBatchSize)
        {
            throw new ArgumentException("Некорректное количество HU.");
        }

        var codes = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var record = _data.CreateHuRecord(createdBy);
            codes.Add(record.Code);
        }

        return codes;
    }

    public IReadOnlyList<HuRecord> GetHus(string? search, int take = 200)
    {
        return _data.GetHus(search, take);
    }

    public void CloseHu(string code, string? note, string? closedBy = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("HU не задан.");
        }

        _data.CloseHu(code.Trim(), closedBy, note);
    }

    public HuRecord? GetHuByCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return _data.GetHuByCode(code.Trim());
    }

    public IReadOnlyList<HuLedgerRow> GetHuLedgerRows(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Array.Empty<HuLedgerRow>();
        }

        return _data.GetHuLedgerRows(code.Trim());
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
