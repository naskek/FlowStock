using LightWms.Core.Abstractions;
using LightWms.Core.Models;

namespace LightWms.Core.Services;

public sealed class CatalogService
{
    private readonly IDataStore _data;

    public CatalogService(IDataStore data)
    {
        _data = data;
    }

    public IReadOnlyList<Item> GetItems(string? search)
    {
        return _data.GetItems(search);
    }

    public IReadOnlyList<Location> GetLocations()
    {
        return _data.GetLocations();
    }

    public IReadOnlyList<Uom> GetUoms()
    {
        return _data.GetUoms();
    }

    public IReadOnlyList<Partner> GetPartners()
    {
        return _data.GetPartners();
    }

    public long CreateItem(string name, string? barcode, string? gtin, string? baseUom)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Наименование обязательно.", nameof(name));
        }

        var normalizedUom = string.IsNullOrWhiteSpace(baseUom) ? "шт" : baseUom.Trim();
        var item = new Item
        {
            Name = name.Trim(),
            Barcode = string.IsNullOrWhiteSpace(barcode) ? null : barcode.Trim(),
            Gtin = string.IsNullOrWhiteSpace(gtin) ? null : gtin.Trim(),
            BaseUom = normalizedUom
        };

        return _data.AddItem(item);
    }

    public long CreateLocation(string code, string name)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Код обязателен.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Наименование обязательно.", nameof(name));
        }

        var location = new Location
        {
            Code = code.Trim(),
            Name = name.Trim()
        };

        return _data.AddLocation(location);
    }

    public long CreateUom(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Единица измерения обязательна.", nameof(name));
        }

        var uom = new Uom
        {
            Name = name.Trim()
        };

        return _data.AddUom(uom);
    }

    public long CreatePartner(string name, string? code)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Наименование обязательно.", nameof(name));
        }

        var partner = new Partner
        {
            Name = name.Trim(),
            Code = string.IsNullOrWhiteSpace(code) ? null : code.Trim(),
            CreatedAt = DateTime.Now
        };

        return _data.AddPartner(partner);
    }

    public void AssignBarcode(long itemId, string barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode))
        {
            throw new ArgumentException("Штрихкод обязателен.", nameof(barcode));
        }

        _data.UpdateItemBarcode(itemId, barcode.Trim());
    }

    public void UpdateItem(long itemId, string name, string? barcode, string? gtin, string? baseUom)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Наименование обязательно.", nameof(name));
        }

        var existing = _data.FindItemById(itemId);
        if (existing == null)
        {
            throw new InvalidOperationException("Товар не найден.");
        }

        var normalizedUom = string.IsNullOrWhiteSpace(baseUom) ? "шт" : baseUom.Trim();
        var item = new Item
        {
            Id = itemId,
            Name = name.Trim(),
            Barcode = string.IsNullOrWhiteSpace(barcode) ? null : barcode.Trim(),
            Gtin = string.IsNullOrWhiteSpace(gtin) ? null : gtin.Trim(),
            BaseUom = normalizedUom,
            DefaultPackagingId = existing.DefaultPackagingId
        };

        _data.UpdateItem(item);
    }

    public void DeleteItem(long itemId)
    {
        var existing = _data.FindItemById(itemId);
        if (existing == null)
        {
            throw new InvalidOperationException("Товар не найден.");
        }

        if (_data.IsItemUsed(itemId))
        {
            throw new InvalidOperationException("Нельзя удалить товар, который используется в документах или остатках.");
        }

        _data.DeleteItem(itemId);
    }

    public void UpdateLocation(long locationId, string code, string name)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Код обязателен.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Наименование обязательно.", nameof(name));
        }

        var existing = _data.FindLocationById(locationId);
        if (existing == null)
        {
            throw new InvalidOperationException("Место хранения не найдено.");
        }

        var location = new Location
        {
            Id = locationId,
            Code = code.Trim(),
            Name = name.Trim()
        };

        _data.UpdateLocation(location);
    }

    public void DeleteLocation(long locationId)
    {
        var existing = _data.FindLocationById(locationId);
        if (existing == null)
        {
            throw new InvalidOperationException("Место хранения не найдено.");
        }

        if (_data.IsLocationUsed(locationId))
        {
            throw new InvalidOperationException("Нельзя удалить место хранения, которое используется в документах или остатках.");
        }

        _data.DeleteLocation(locationId);
    }

    public void UpdatePartner(long partnerId, string name, string? code)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Наименование обязательно.", nameof(name));
        }

        var existing = _data.GetPartner(partnerId);
        if (existing == null)
        {
            throw new InvalidOperationException("Контрагент не найден.");
        }

        var partner = new Partner
        {
            Id = partnerId,
            Name = name.Trim(),
            Code = string.IsNullOrWhiteSpace(code) ? null : code.Trim(),
            CreatedAt = existing.CreatedAt
        };

        _data.UpdatePartner(partner);
    }

    public void DeletePartner(long partnerId)
    {
        var existing = _data.GetPartner(partnerId);
        if (existing == null)
        {
            throw new InvalidOperationException("Контрагент не найден.");
        }

        if (_data.IsPartnerUsed(partnerId))
        {
            throw new InvalidOperationException("Нельзя удалить контрагента, который используется в документах.");
        }

        _data.DeletePartner(partnerId);
    }
}
