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

    public IReadOnlyList<Partner> GetPartners()
    {
        return _data.GetPartners();
    }

    public long CreateItem(string name, string? barcode, string? gtin, string? uom)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        var item = new Item
        {
            Name = name.Trim(),
            Barcode = string.IsNullOrWhiteSpace(barcode) ? null : barcode.Trim(),
            Gtin = string.IsNullOrWhiteSpace(gtin) ? null : gtin.Trim(),
            Uom = string.IsNullOrWhiteSpace(uom) ? null : uom.Trim()
        };

        return _data.AddItem(item);
    }

    public long CreateLocation(string code, string name)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Code is required.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        var location = new Location
        {
            Code = code.Trim(),
            Name = name.Trim()
        };

        return _data.AddLocation(location);
    }

    public long CreatePartner(string name, string? code)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.", nameof(name));
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
            throw new ArgumentException("Barcode is required.", nameof(barcode));
        }

        _data.UpdateItemBarcode(itemId, barcode.Trim());
    }
}
