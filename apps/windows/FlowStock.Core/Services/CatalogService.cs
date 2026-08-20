using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class CatalogIdentifierConflictException(
    string errorCode,
    string message,
    long existingItemId) : InvalidOperationException(message)
{
    public string ErrorCode { get; } = errorCode;
    public long ExistingItemId { get; } = existingItemId;
}

public sealed class UomNotFoundException(string message) : InvalidOperationException(message);

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

    public long CreateItem(string name, string? barcode, string? gtin, string? baseUom, string? brand, string? volume, int? shelfLifeMonths, long? taraId, bool isMarked, bool isActive = true, double? maxQtyPerHu = null, long? itemTypeId = null, double? minStockQty = null, string? storageConditions = null, decimal? defaultSalePriceGross = null, long? defaultSaleVatRateId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Наименование обязательно.", nameof(name));
        }

        var normalizedIdentifiers = NormalizeAndValidateItemIdentifiers(barcode, gtin, currentItemId: null);
        var normalizedUom = NormalizeAndValidateBaseUom(baseUom);
        ValidateShelfLife(shelfLifeMonths);
        ValidateTara(taraId);
        ValidateItemTypeId(itemTypeId);
        var normalizedMaxQtyPerHu = NormalizeMaxQtyPerHu(itemTypeId, maxQtyPerHu);
        var normalizedMinStock = NormalizeMinStock(itemTypeId, minStockQty);
        ValidateSalePrice(defaultSalePriceGross);
        var item = new Item
        {
            Name = name.Trim(),
            Barcode = normalizedIdentifiers.Barcode,
            Gtin = normalizedIdentifiers.Gtin,
            BaseUom = normalizedUom,
            Brand = string.IsNullOrWhiteSpace(brand) ? null : brand.Trim(),
            Volume = string.IsNullOrWhiteSpace(volume) ? null : volume.Trim(),
            ShelfLifeMonths = shelfLifeMonths,
            StorageConditions = NormalizeStorageConditions(storageConditions),
            MaxQtyPerHu = normalizedMaxQtyPerHu,
            TaraId = taraId,
            IsMarked = false,
            IsActive = isActive,
            ItemTypeId = itemTypeId,
            MinStockQty = normalizedMinStock,
            DefaultSalePriceGross = defaultSalePriceGross,
            DefaultSaleVatRateId = defaultSaleVatRateId
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

        var normalized = name.Trim();
        EnsureNotReservedLegacyUom(normalized, nameof(name));
        var uom = new Uom
        {
            Name = normalized
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
        if (uomId <= 0)
        {
            throw new ArgumentException("Некорректная единица измерения.", nameof(uomId));
        }

        _data.DeleteUom(uomId);
    }

    public void RenameUom(long uomId, string newName)
    {
        if (uomId <= 0)
        {
            throw new ArgumentException("Некорректная единица измерения.", nameof(uomId));
        }

        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new ArgumentException("Единица измерения обязательна.", nameof(newName));
        }

        var normalized = newName.Trim();
        EnsureNotReservedLegacyUom(normalized, nameof(newName));
        _data.RenameUom(uomId, normalized);
    }

    public long CreatePartner(string name, string? code, string partnerRole)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Наименование обязательно.", nameof(name));
        }

        var partner = new Partner
        {
            Name = name.Trim(),
            Code = NormalizeAndValidatePartnerCode(code),
            PartnerRole = NormalizePartnerRole(partnerRole),
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

    public void UpdateItem(long itemId, string name, string? barcode, string? gtin, string? baseUom, string? brand, string? volume, int? shelfLifeMonths, long? taraId, bool isMarked, bool? isActive = null, double? maxQtyPerHu = null, long? itemTypeId = null, double? minStockQty = null, string? storageConditions = null, decimal? defaultSalePriceGross = null, long? defaultSaleVatRateId = null)
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

        var normalizedIdentifiers = NormalizeAndValidateItemIdentifiers(barcode, gtin, itemId);
        var normalizedUom = NormalizeAndValidateBaseUom(baseUom);
        ValidateShelfLife(shelfLifeMonths);
        ValidateTara(taraId);
        ValidateItemTypeId(itemTypeId);
        var normalizedMaxQtyPerHu = NormalizeMaxQtyPerHu(itemTypeId, maxQtyPerHu);
        var normalizedMinStock = NormalizeMinStock(itemTypeId, minStockQty);
        ValidateSalePrice(defaultSalePriceGross);
        var item = new Item
        {
            Id = itemId,
            Name = name.Trim(),
            Barcode = normalizedIdentifiers.Barcode,
            Gtin = normalizedIdentifiers.Gtin,
            BaseUom = normalizedUom,
            DefaultPackagingId = existing.DefaultPackagingId,
            Brand = string.IsNullOrWhiteSpace(brand) ? null : brand.Trim(),
            Volume = string.IsNullOrWhiteSpace(volume) ? null : volume.Trim(),
            ShelfLifeMonths = shelfLifeMonths,
            StorageConditions = NormalizeStorageConditions(storageConditions),
            MaxQtyPerHu = normalizedMaxQtyPerHu,
            TaraId = taraId,
            IsMarked = existing.IsMarked,
            IsActive = isActive ?? existing.IsActive,
            ItemTypeId = itemTypeId,
            MinStockQty = normalizedMinStock,
            DefaultSalePriceGross = defaultSalePriceGross,
            DefaultSaleVatRateId = defaultSaleVatRateId
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

        if (_data.HasPartnerItemSalePricesForItem(itemId))
        {
            throw new InvalidOperationException(
                "Нельзя удалить товар, для которого настроены цены клиентов. Сначала удалите связанные записи цен.");
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

    public long CreateItemType(string name, string? code, int sortOrder, bool isActive, bool isVisibleInProductCatalog, bool enableMinStockControl, bool minStockUsesOrderBinding, bool enableOrderReservation, bool enableHuDistribution, bool enableMarking = false)
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
            EnableHuDistribution = enableHuDistribution,
            EnableMarking = enableMarking
        });
    }

    public void UpdateItemType(long itemTypeId, string name, string? code, int sortOrder, bool isActive, bool isVisibleInProductCatalog, bool enableMinStockControl, bool minStockUsesOrderBinding, bool enableOrderReservation, bool enableHuDistribution, bool enableMarking = false)
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
            EnableHuDistribution = enableHuDistribution,
            EnableMarking = enableMarking
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

    private static void ValidateSalePrice(decimal? price)
    {
        if (price < 0)
        {
            throw new ArgumentException("Цена продажи с НДС не может быть отрицательной.", nameof(price));
        }

        if (price.HasValue
            && (decimal.Round(price.Value, 4, MidpointRounding.AwayFromZero) != price.Value
                || price.Value > 999_999_999_999_999.9999m))
        {
            throw new ArgumentException(
                "Цена продажи с НДС должна соответствовать формату NUMERIC(19,4).",
                nameof(price));
        }
    }

    public void UpdatePartner(long partnerId, string name, string? code, string partnerRole)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Наименование обязательно.", nameof(name));
        }

        var normalizedCode = NormalizeAndValidatePartnerCode(code);
        var normalizedRole = NormalizePartnerRole(partnerRole);
        _data.ExecuteInTransaction(store =>
        {
            var existing = store.LockPartnerForUpdate(partnerId);
            if (existing == null)
            {
                throw new InvalidOperationException("Контрагент не найден.");
            }

            if (normalizedRole == "SUPPLIER"
                && store.HasPartnerItemSalePricesForPartner(partnerId))
            {
                throw new CommercialTermsException(
                    "PARTNER_HAS_CUSTOMER_PRICES",
                    "Нельзя изменить роль клиента на поставщика, пока для него существуют индивидуальные цены.");
            }

            store.UpdatePartner(new Partner
            {
                Id = partnerId,
                Name = name.Trim(),
                Code = normalizedCode,
                PartnerRole = normalizedRole,
                CreatedAt = existing.CreatedAt
            });
        });
    }

    public void DeletePartner(long partnerId)
    {
        var existing = _data.GetPartner(partnerId);
        if (existing == null)
        {
            throw new InvalidOperationException("Контрагент не найден.");
        }

        if (_data.HasPartnerItemSalePricesForPartner(partnerId))
        {
            throw new InvalidOperationException(
                "Нельзя удалить контрагента, для которого настроены цены клиентов. Сначала удалите связанные записи цен.");
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

    private static string? NormalizeStorageConditions(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static void ValidateItemTypeId(long? itemTypeId)
    {
        if (itemTypeId.HasValue && itemTypeId.Value <= 0)
        {
            throw new ArgumentException(
                "Идентификатор типа номенклатуры должен быть положительным.",
                nameof(itemTypeId));
        }
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

    private (string Barcode, string? Gtin) NormalizeAndValidateItemIdentifiers(
        string? barcode,
        string? gtin,
        long? currentItemId)
    {
        var normalizedBarcode = string.IsNullOrWhiteSpace(barcode) ? null : barcode.Trim();
        var normalizedGtin = string.IsNullOrWhiteSpace(gtin) ? null : gtin.Trim();
        normalizedBarcode ??= normalizedGtin;
        if (string.IsNullOrWhiteSpace(normalizedBarcode))
        {
            throw new ArgumentException("Введите SKU / штрихкод или GTIN.", nameof(barcode));
        }

        var items = _data.GetItems(null) ?? Array.Empty<Item>();
        var duplicateBarcode = items.FirstOrDefault(item =>
            (!currentItemId.HasValue || item.Id != currentItemId.Value)
            && !string.IsNullOrWhiteSpace(item.Barcode)
            && string.Equals(item.Barcode.Trim(), normalizedBarcode, StringComparison.OrdinalIgnoreCase));
        if (duplicateBarcode != null)
        {
            throw new CatalogIdentifierConflictException(
                "ITEM_BARCODE_DUPLICATE",
                $"SKU / штрихкод уже используется товаром «{duplicateBarcode.Name}» (ID {duplicateBarcode.Id}).",
                duplicateBarcode.Id);
        }

        if (!string.IsNullOrWhiteSpace(normalizedGtin))
        {
            var duplicateGtin = items.FirstOrDefault(item =>
                (!currentItemId.HasValue || item.Id != currentItemId.Value)
                && !string.IsNullOrWhiteSpace(item.Gtin)
                && string.Equals(item.Gtin.Trim(), normalizedGtin, StringComparison.OrdinalIgnoreCase));
            if (duplicateGtin != null)
            {
                throw new CatalogIdentifierConflictException(
                    "ITEM_GTIN_DUPLICATE",
                    $"GTIN уже используется товаром «{duplicateGtin.Name}» (ID {duplicateGtin.Id}).",
                    duplicateGtin.Id);
            }
        }

        return (normalizedBarcode, normalizedGtin);
    }

    private string NormalizeAndValidateBaseUom(string? baseUom)
    {
        var normalized = string.IsNullOrWhiteSpace(baseUom) ? "шт" : baseUom.Trim();
        var uoms = _data.GetUoms() ?? Array.Empty<Uom>();
        var isCanonicalLegacyDefault = string.Equals(normalized, "шт", StringComparison.OrdinalIgnoreCase);
        if (!isCanonicalLegacyDefault
            && !uoms.Any(uom => string.Equals(uom.Name.Trim(), normalized, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Выбранная единица измерения не найдена.", nameof(baseUom));
        }

        return normalized;
    }

    private static void EnsureNotReservedLegacyUom(string name, string parameterName)
    {
        if (string.Equals(name.Trim(), "шт", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Имя «шт» зарезервировано для legacy-совместимости и не может быть master-единицей измерения.",
                parameterName);
        }
    }

    private void ValidateTara(long? taraId)
    {
        if (taraId.HasValue && !_data.GetTaras().Any(tara => tara.Id == taraId.Value))
        {
            throw new ArgumentException("Выбранная тара не найдена.", nameof(taraId));
        }
    }

    private static void ValidateShelfLife(int? shelfLifeMonths)
    {
        if (shelfLifeMonths.HasValue && shelfLifeMonths.Value <= 0)
        {
            throw new ArgumentException(
                "Срок годности должен быть положительным целым числом месяцев.",
                nameof(shelfLifeMonths));
        }
    }

    private static string? NormalizeAndValidatePartnerCode(string? code)
    {
        var normalized = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        if (normalized != null && normalized.Any(ch => !char.IsDigit(ch)))
        {
            throw new ArgumentException("ИНН должен содержать только цифры.", nameof(code));
        }

        return normalized;
    }

    private static string NormalizePartnerRole(string partnerRole)
    {
        var normalized = partnerRole?.Trim().ToUpperInvariant();
        if (normalized is not ("SUPPLIER" or "CLIENT" or "BOTH"))
        {
            throw new ArgumentException("Некорректная роль контрагента.", nameof(partnerRole));
        }

        return normalized;
    }
}
