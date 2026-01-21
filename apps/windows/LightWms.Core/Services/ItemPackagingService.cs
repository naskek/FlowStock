using LightWms.Core.Abstractions;
using LightWms.Core.Models;

namespace LightWms.Core.Services;

public sealed class ItemPackagingService
{
    private const string BaseUomCode = "BASE";
    private readonly IDataStore _data;

    public ItemPackagingService(IDataStore data)
    {
        _data = data;
    }

    public IReadOnlyList<ItemPackaging> GetPackagings(long itemId, bool includeInactive = false)
    {
        return _data.GetItemPackagings(itemId, includeInactive);
    }

    public long CreatePackaging(long itemId, string code, string name, double factorToBase, int sortOrder)
    {
        ValidatePackagingInput(code, name, factorToBase);
        EnsureItemExists(itemId);

        if (_data.FindItemPackagingByCode(itemId, code.Trim()) != null)
        {
            throw new ArgumentException("Код упаковки уже используется.", nameof(code));
        }

        var packaging = new ItemPackaging
        {
            ItemId = itemId,
            Code = code.Trim(),
            Name = name.Trim(),
            FactorToBase = factorToBase,
            SortOrder = sortOrder,
            IsActive = true
        };

        return _data.AddItemPackaging(packaging);
    }

    public void UpdatePackaging(long packagingId, long itemId, string code, string name, double factorToBase, int sortOrder, bool isActive)
    {
        ValidatePackagingInput(code, name, factorToBase);

        var existing = _data.GetItemPackaging(packagingId);
        if (existing == null)
        {
            throw new InvalidOperationException("Упаковка не найдена.");
        }

        EnsureItemExists(itemId);

        var duplicate = _data.FindItemPackagingByCode(itemId, code.Trim());
        if (duplicate != null && duplicate.Id != packagingId)
        {
            throw new ArgumentException("Код упаковки уже используется.", nameof(code));
        }

        var updated = new ItemPackaging
        {
            Id = packagingId,
            ItemId = itemId,
            Code = code.Trim(),
            Name = name.Trim(),
            FactorToBase = factorToBase,
            SortOrder = sortOrder,
            IsActive = isActive
        };

        _data.UpdateItemPackaging(updated);

        if (existing.ItemId != itemId)
        {
            var oldItem = _data.FindItemById(existing.ItemId);
            if (oldItem?.DefaultPackagingId == packagingId)
            {
                _data.UpdateItemDefaultPackaging(existing.ItemId, null);
            }
        }

        if (!isActive)
        {
            var item = _data.FindItemById(itemId);
            if (item?.DefaultPackagingId == packagingId)
            {
                _data.UpdateItemDefaultPackaging(itemId, null);
            }
        }
    }

    public void DeactivatePackaging(long packagingId)
    {
        var existing = _data.GetItemPackaging(packagingId);
        if (existing == null)
        {
            throw new InvalidOperationException("Упаковка не найдена.");
        }

        _data.DeactivateItemPackaging(packagingId);

        var item = _data.FindItemById(existing.ItemId);
        if (item?.DefaultPackagingId == packagingId)
        {
            _data.UpdateItemDefaultPackaging(existing.ItemId, null);
        }
    }

    public void SetDefaultPackaging(long itemId, long? packagingId)
    {
        EnsureItemExists(itemId);

        if (packagingId.HasValue)
        {
            var packaging = _data.GetItemPackaging(packagingId.Value);
            if (packaging == null || packaging.ItemId != itemId || !packaging.IsActive)
            {
                throw new InvalidOperationException("Упаковка не найдена.");
            }
        }

        _data.UpdateItemDefaultPackaging(itemId, packagingId);
    }

    public double ConvertToBase(long itemId, double qtyInput, string? uomCode)
    {
        if (qtyInput <= 0)
        {
            throw new ArgumentException("Количество должно быть больше 0.", nameof(qtyInput));
        }

        if (string.IsNullOrWhiteSpace(uomCode) || string.Equals(uomCode, BaseUomCode, StringComparison.OrdinalIgnoreCase))
        {
            return qtyInput;
        }

        var packaging = _data.FindItemPackagingByCode(itemId, uomCode.Trim());
        if (packaging == null || !packaging.IsActive)
        {
            throw new InvalidOperationException("Упаковка не найдена.");
        }

        return qtyInput * packaging.FactorToBase;
    }

    public string FormatAsPackaging(long itemId, double qtyBase)
    {
        var item = _data.FindItemById(itemId);
        var baseUom = item?.BaseUom ?? "шт";

        if (item?.DefaultPackagingId == null)
        {
            return $"{FormatQty(qtyBase)} {baseUom}";
        }

        var packaging = _data.GetItemPackaging(item.DefaultPackagingId.Value);
        if (packaging == null || !packaging.IsActive || packaging.FactorToBase <= 0)
        {
            return $"{FormatQty(qtyBase)} {baseUom}";
        }

        var packs = Math.Floor(qtyBase / packaging.FactorToBase);
        var remainder = qtyBase - packs * packaging.FactorToBase;

        if (packs <= 0)
        {
            return $"{FormatQty(qtyBase)} {baseUom}";
        }

        var result = $"{packs:0} × {packaging.Name}";
        if (remainder > 0)
        {
            result += $" + {FormatQty(remainder)} {baseUom}";
        }

        return result;
    }

    private void EnsureItemExists(long itemId)
    {
        if (_data.FindItemById(itemId) == null)
        {
            throw new InvalidOperationException("Товар не найден.");
        }
    }

    private static void ValidatePackagingInput(string code, string name, double factorToBase)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Код упаковки обязателен.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Наименование упаковки обязательно.", nameof(name));
        }

        if (factorToBase <= 0)
        {
            throw new ArgumentException("Коэффициент должен быть больше 0.", nameof(factorToBase));
        }
    }

    private static string FormatQty(double value)
    {
        return value.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture);
    }
}
