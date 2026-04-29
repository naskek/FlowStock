using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

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

    public long CreateItem(string name, string? barcode, string? gtin, string? baseUom, string? brand, string? volume, int? shelfLifeMonths, long? taraId, bool isMarked, bool isActive = true, double? maxQtyPerHu = null, long? itemTypeId = null, double? minStockQty = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Наименование обязательно.", nameof(name));
        }

        var normalizedUom = string.IsNullOrWhiteSpace(baseUom) ? "шт" : baseUom.Trim();
        var normalizedMaxQtyPerHu = NormalizeMaxQtyPerHu(itemTypeId, maxQtyPerHu);
        var normalizedMinStock = NormalizeMinStock(itemTypeId, minStockQty);
        var item = new Item
        {
            Name = name.Trim(),
            Barcode = string.IsNullOrWhiteSpace(barcode) ? null : barcode.Trim(),
            Gtin = string.IsNullOrWhiteSpace(gtin) ? null : gtin.Trim(),
            BaseUom = normalizedUom,
            Brand = string.IsNullOrWhiteSpace(brand) ? null : brand.Trim(),
            Volume = string.IsNullOrWhiteSpace(volume) ? null : volume.Trim(),
            ShelfLifeMonths = shelfLifeMonths,
            MaxQtyPerHu = normalizedMaxQtyPerHu,
            TaraId = taraId,
            IsMarked = isMarked,
            IsActive = isActive,
            ItemTypeId = itemTypeId,
            MinStockQty = normalizedMinStock
        };

        return _data.AddItem(item);
    }

    public long CreateLocation(string code, string name, int? maxHuSlots, bool? autoHuDistributionEnabled)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Код обязателен.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Наименование обязательно.", nameof(name));
        }
        if (maxHuSlots.HasValue && maxHuSlots.Value <= 0)
        {
            throw new ArgumentException("Лимит HU должен быть больше 0.", nameof(maxHuSlots));
        }

        var location = new Location
        {
            Code = code.Trim(),
            Name = name.Trim(),
            MaxHuSlots = maxHuSlots,
            AutoHuDistributionEnabled = autoHuDistributionEnabled ?? true
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

    public IReadOnlyList<WriteOffReason> GetWriteOffReasons()
    {
        return _data.GetWriteOffReasons();
    }

    public long CreateWriteOffReason(string code, string name)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Код причины обязателен.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Наименование причины обязательно.", nameof(name));
        }

        var reason = new WriteOffReason
        {
            Code = code.Trim().ToUpperInvariant(),
            Name = name.Trim()
        };

        return _data.AddWriteOffReason(reason);
    }

    public void DeleteWriteOffReason(long reasonId)
    {
        if (reasonId <= 0)
        {
            throw new ArgumentException("Некорректная причина списания.", nameof(reasonId));
        }

        _data.DeleteWriteOffReason(reasonId);
    }

    public void DeleteUom(long uomId)
    {
        if (_data.IsUomUsed(uomId))
        {
            throw new InvalidOperationException("Нельзя удалить единицу измерения, которая используется в товарах.");
        }

        _data.DeleteUom(uomId);
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

    public void UpdateItem(long itemId, string name, string? barcode, string? gtin, string? baseUom, string? brand, string? volume, int? shelfLifeMonths, long? taraId, bool isMarked, bool? isActive = null, double? maxQtyPerHu = null, long? itemTypeId = null, double? minStockQty = null)
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
        var normalizedMaxQtyPerHu = NormalizeMaxQtyPerHu(itemTypeId, maxQtyPerHu);
        var normalizedMinStock = NormalizeMinStock(itemTypeId, minStockQty);
        var item = new Item
        {
            Id = itemId,
            Name = name.Trim(),
            Barcode = string.IsNullOrWhiteSpace(barcode) ? null : barcode.Trim(),
            Gtin = string.IsNullOrWhiteSpace(gtin) ? null : gtin.Trim(),
            BaseUom = normalizedUom,
            DefaultPackagingId = existing.DefaultPackagingId,
            Brand = string.IsNullOrWhiteSpace(brand) ? null : brand.Trim(),
            Volume = string.IsNullOrWhiteSpace(volume) ? null : volume.Trim(),
            ShelfLifeMonths = shelfLifeMonths,
            MaxQtyPerHu = normalizedMaxQtyPerHu,
            TaraId = taraId,
            IsMarked = isMarked,
            IsActive = isActive ?? existing.IsActive,
            ItemTypeId = itemTypeId,
            MinStockQty = normalizedMinStock
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

    public void UpdateLocation(long locationId, string code, string name, int? maxHuSlots, bool? autoHuDistributionEnabled)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Код обязателен.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Наименование обязательно.", nameof(name));
        }
        if (maxHuSlots.HasValue && maxHuSlots.Value <= 0)
        {
            throw new ArgumentException("Лимит HU должен быть больше 0.", nameof(maxHuSlots));
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
            Name = name.Trim(),
            MaxHuSlots = maxHuSlots,
            AutoHuDistributionEnabled = autoHuDistributionEnabled ?? existing.AutoHuDistributionEnabled
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

    public IReadOnlyList<Tara> GetTaras()
    {
        return _data.GetTaras();
    }

    public IReadOnlyList<ItemType> GetItemTypes(bool includeInactive)
    {
        return _data.GetItemTypes(includeInactive);
    }

    public long CreateTara(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Наименование обязательно.", nameof(name));
        }

        var tara = new Tara
        {
            Name = name.Trim()
        };

        return _data.AddTara(tara);
    }

    public void UpdateTara(long taraId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Наименование обязательно.", nameof(name));
        }

        var tara = new Tara
        {
            Id = taraId,
            Name = name.Trim()
        };

        _data.UpdateTara(tara);
    }

    public void DeleteTara(long taraId)
    {
        if (_data.IsTaraUsed(taraId))
        {
            throw new InvalidOperationException("Нельзя удалить тару, которая используется в товарах.");
        }

        _data.DeleteTara(taraId);
    }

    public long CreateItemType(string name, string? code, int sortOrder, bool isActive, bool isVisibleInProductCatalog, bool enableMinStockControl, bool minStockUsesOrderBinding, bool enableOrderReservation, bool enableHuDistribution)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Наименование обязательно.", nameof(name));
        }

        return _data.AddItemType(new ItemType
        {
            Name = name.Trim(),
            Code = string.IsNullOrWhiteSpace(code) ? null : code.Trim(),
            SortOrder = sortOrder,
            IsActive = isActive,
            IsVisibleInProductCatalog = isVisibleInProductCatalog,
            EnableMinStockControl = enableMinStockControl,
            MinStockUsesOrderBinding = minStockUsesOrderBinding,
            EnableOrderReservation = enableOrderReservation,
            EnableHuDistribution = enableHuDistribution
        });
    }

    public void UpdateItemType(long itemTypeId, string name, string? code, int sortOrder, bool isActive, bool isVisibleInProductCatalog, bool enableMinStockControl, bool minStockUsesOrderBinding, bool enableOrderReservation, bool enableHuDistribution)
    {
        if (itemTypeId <= 0)
        {
            throw new ArgumentException("Некорректный тип номенклатуры.", nameof(itemTypeId));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Наименование обязательно.", nameof(name));
        }

        if (_data.GetItemType(itemTypeId) == null)
        {
            throw new InvalidOperationException("Тип номенклатуры не найден.");
        }

        _data.UpdateItemType(new ItemType
        {
            Id = itemTypeId,
            Name = name.Trim(),
            Code = string.IsNullOrWhiteSpace(code) ? null : code.Trim(),
            SortOrder = sortOrder,
            IsActive = isActive,
            IsVisibleInProductCatalog = isVisibleInProductCatalog,
            EnableMinStockControl = enableMinStockControl,
            MinStockUsesOrderBinding = minStockUsesOrderBinding,
            EnableOrderReservation = enableOrderReservation,
            EnableHuDistribution = enableHuDistribution
        });
    }

    public void DeleteItemType(long itemTypeId)
    {
        if (_data.GetItemType(itemTypeId) == null)
        {
            throw new InvalidOperationException("Тип номенклатуры не найден.");
        }

        if (_data.IsItemTypeUsed(itemTypeId))
        {
            _data.DeactivateItemType(itemTypeId);
            return;
        }

        _data.DeleteItemType(itemTypeId);
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

    private double? NormalizeMinStock(long? itemTypeId, double? minStockQty)
    {
        if (!itemTypeId.HasValue || itemTypeId.Value <= 0)
        {
            return null;
        }

        var itemType = _data.GetItemType(itemTypeId.Value);
        if (itemType == null)
        {
            throw new ArgumentException("Выбранный тип номенклатуры не найден.", nameof(itemTypeId));
        }

        if (!itemType.IsActive)
        {
            throw new ArgumentException("Выбранный тип номенклатуры неактивен.", nameof(itemTypeId));
        }

        if (!itemType.EnableMinStockControl)
        {
            return null;
        }

        if (!minStockQty.HasValue)
        {
            return null;
        }

        if (minStockQty.Value < 0)
        {
            throw new ArgumentException("Минимальный остаток не может быть отрицательным.", nameof(minStockQty));
        }

        return minStockQty.Value;
    }

    private double? NormalizeMaxQtyPerHu(long? itemTypeId, double? maxQtyPerHu)
    {
        if (maxQtyPerHu.HasValue && maxQtyPerHu.Value <= 0)
        {
            throw new ArgumentException("Лимит HU должен быть больше 0.", nameof(maxQtyPerHu));
        }

        if (!itemTypeId.HasValue || itemTypeId.Value <= 0)
        {
            return maxQtyPerHu;
        }

        var itemType = _data.GetItemType(itemTypeId.Value);
        if (itemType == null)
        {
            throw new ArgumentException("Выбранный тип номенклатуры не найден.", nameof(itemTypeId));
        }

        if (!itemType.IsActive)
        {
            throw new ArgumentException("Выбранный тип номенклатуры неактивен.", nameof(itemTypeId));
        }

        if (itemType.EnableHuDistribution && !maxQtyPerHu.HasValue)
        {
            throw new ArgumentException("Для выбранного типа номенклатуры обязательно заполнить \"Макс шт на 1 HU\".", nameof(maxQtyPerHu));
        }

        return maxQtyPerHu;
    }
}

