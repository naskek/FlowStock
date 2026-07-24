using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class VatRateService
{
    private readonly IDataStore _data;

    public VatRateService(IDataStore data)
    {
        _data = data;
    }

    public IReadOnlyList<VatRate> GetVatRates(bool includeInactive)
    {
        return _data.GetVatRates(includeInactive);
    }

    public long CreateVatRate(string name, decimal rate, int sortOrder, bool isActive)
    {
        Validate(name, rate);
        return _data.AddVatRate(new VatRate
        {
            Name = name.Trim(),
            Rate = rate,
            SortOrder = sortOrder,
            IsActive = isActive
        });
    }

    public void UpdateVatRate(long id, string name, decimal rate, int sortOrder, bool isActive)
    {
        Validate(name, rate);
        _data.UpdateVatRate(new VatRate
        {
            Id = id,
            Name = name.Trim(),
            Rate = rate,
            SortOrder = sortOrder,
            IsActive = isActive
        });
    }

    public void DeleteVatRate(long id)
    {
        _data.DeleteVatRate(id);
    }

    private static void Validate(string name, decimal rate)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Наименование ставки НДС обязательно.", nameof(name));
        }

        if (rate < 0)
        {
            throw new ArgumentException("Ставка НДС не может быть отрицательной.", nameof(rate));
        }

        if (decimal.Round(rate, 4, MidpointRounding.AwayFromZero) != rate)
        {
            throw new ArgumentException("Ставка НДС может содержать не более четырёх знаков после запятой.", nameof(rate));
        }

        if (rate > 999.9999m)
        {
            throw new ArgumentException("Ставка НДС выходит за допустимый формат NUMERIC(7,4).", nameof(rate));
        }
    }
}
