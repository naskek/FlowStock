using System.Globalization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Commercial;
using Npgsql;
using NpgsqlTypes;

namespace FlowStock.Data;

public sealed class PostgresCommercialDataStore : ICommercialDataStore
{
    private const string PriceGroupSelectColumns =
        "id, name, description, currency, vat_mode, is_default, is_active, is_system, default_discount_percent, default_markup_percent, created_at, updated_at";

    private readonly string _connectionString;

    public PostgresCommercialDataStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    public IReadOnlyList<PriceGroup> GetPriceGroups(bool includeInactive)
    {
        return WithConnection(connection =>
        {
            var sql = includeInactive
                ? $"SELECT {PriceGroupSelectColumns} FROM price_groups ORDER BY is_system DESC, name"
                : $"SELECT {PriceGroupSelectColumns} FROM price_groups WHERE is_active = TRUE ORDER BY is_system DESC, name";
            using var command = CreateCommand(connection, sql);
            return ReadPriceGroups(command);
        });
    }

    public PriceGroup? GetPriceGroup(long id)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection,
                $"SELECT {PriceGroupSelectColumns} FROM price_groups WHERE id = @id");
            command.Parameters.AddWithValue("@id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadPriceGroup(reader) : null;
        });
    }

    public PriceGroup? GetDefaultPriceGroup()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection,
                $"SELECT {PriceGroupSelectColumns} FROM price_groups WHERE is_default = TRUE AND is_active = TRUE LIMIT 1");
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadPriceGroup(reader) : null;
        });
    }

    public PriceGroup? GetSystemBasePriceGroup()
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection,
                $"SELECT {PriceGroupSelectColumns} FROM price_groups WHERE is_system = TRUE LIMIT 1");
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadPriceGroup(reader) : null;
        });
    }

    public long EnsureSystemBasePriceGroup()
    {
        return WithConnection(connection =>
        {
            using (var findCommand = CreateCommand(connection, "SELECT id FROM price_groups WHERE is_system = TRUE LIMIT 1"))
            {
                var existing = findCommand.ExecuteScalar();
                if (existing != null && existing != DBNull.Value)
                {
                    var id = Convert.ToInt64(existing, CultureInfo.InvariantCulture);
                    using var fixCommand = CreateCommand(connection, @"
UPDATE price_groups
SET name = @name,
    is_default = TRUE,
    is_active = TRUE,
    updated_at = now()
WHERE id = @id;");
                    fixCommand.Parameters.AddWithValue("@name", CommercialPricingConstants.BasePriceGroupName);
                    fixCommand.Parameters.AddWithValue("@id", id);
                    fixCommand.ExecuteNonQuery();

                    using var clearCommand = CreateCommand(connection,
                        "UPDATE price_groups SET is_default = FALSE, updated_at = now() WHERE is_system = FALSE AND is_default = TRUE");
                    clearCommand.ExecuteNonQuery();
                    return id;
                }
            }

            using var clearDefaults = CreateCommand(connection,
                "UPDATE price_groups SET is_default = FALSE, updated_at = now() WHERE is_default = TRUE");
            clearDefaults.ExecuteNonQuery();

            var now = DateTime.UtcNow;
            using var insertCommand = CreateCommand(connection, @"
INSERT INTO price_groups(name, description, currency, vat_mode, is_default, is_active, is_system, default_discount_percent, default_markup_percent, created_at, updated_at)
VALUES(@name, @description, @currency, @vat_mode, TRUE, TRUE, TRUE, 0, 0, @created_at, @updated_at)
RETURNING id;");
            insertCommand.Parameters.AddWithValue("@name", CommercialPricingConstants.BasePriceGroupName);
            insertCommand.Parameters.AddWithValue("@description", "Системная базовая группа цен");
            insertCommand.Parameters.AddWithValue("@currency", "RUB");
            insertCommand.Parameters.AddWithValue("@vat_mode", VatModeMapper.ToCode(VatMode.Included));
            insertCommand.Parameters.AddWithValue("@created_at", now);
            insertCommand.Parameters.AddWithValue("@updated_at", now);
            return (long)(insertCommand.ExecuteScalar() ?? 0L);
        });
    }

    public long AddPriceGroup(PriceGroup group)
    {
        return WithConnection(connection =>
        {
            if (group.IsDefault && !group.IsSystem)
            {
                group = new PriceGroup
                {
                    Name = group.Name,
                    Description = group.Description,
                    Currency = group.Currency,
                    VatMode = group.VatMode,
                    IsDefault = false,
                    IsSystem = false,
                    IsActive = group.IsActive,
                    DefaultDiscountPercent = group.DefaultDiscountPercent,
                    DefaultMarkupPercent = group.DefaultMarkupPercent,
                    CreatedAt = group.CreatedAt,
                    UpdatedAt = group.UpdatedAt
                };
            }

            using var command = CreateCommand(connection, @"
INSERT INTO price_groups(name, description, currency, vat_mode, is_default, is_active, is_system, default_discount_percent, default_markup_percent, created_at, updated_at)
VALUES(@name, @description, @currency, @vat_mode, @is_default, @is_active, @is_system, @default_discount_percent, @default_markup_percent, @created_at, @updated_at)
RETURNING id;");
            BindPriceGroup(command, group);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void UpdatePriceGroup(PriceGroup group)
    {
        WithConnection(connection =>
        {
            if (group.IsDefault && !group.IsSystem)
            {
                ClearDefaultPriceGroup(connection, group.Id);
            }

            using var command = CreateCommand(connection, @"
UPDATE price_groups
SET name = @name,
    description = @description,
    currency = @currency,
    vat_mode = @vat_mode,
    is_default = @is_default,
    is_active = @is_active,
    default_discount_percent = @default_discount_percent,
    default_markup_percent = @default_markup_percent,
    updated_at = @updated_at
WHERE id = @id;");
            BindPriceGroup(command, group);
            command.Parameters.AddWithValue("@id", group.Id);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void SetDefaultPriceGroup(long priceGroupId)
    {
        WithConnection(connection =>
        {
            using var guard = CreateCommand(connection, "SELECT is_system FROM price_groups WHERE id = @id");
            guard.Parameters.AddWithValue("@id", priceGroupId);
            var isSystemValue = guard.ExecuteScalar();
            if (isSystemValue is not bool isSystem || !isSystem)
            {
                throw new InvalidOperationException("ONLY_SYSTEM_GROUP_CAN_BE_DEFAULT");
            }

            ClearDefaultPriceGroup(connection);
            using var command = CreateCommand(connection, "UPDATE price_groups SET is_default = TRUE, updated_at = now() WHERE id = @id");
            command.Parameters.AddWithValue("@id", priceGroupId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public PartnerCommercialSettings? GetPartnerCommercialSettings(long partnerId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT partner_id, price_group_id, default_discount_percent, payment_terms, delivery_terms, valid_from, valid_to, updated_at
FROM partner_commercial_settings
WHERE partner_id = @partner_id;");
            command.Parameters.AddWithValue("@partner_id", partnerId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadPartnerCommercialSettings(reader) : null;
        });
    }

    public void UpsertPartnerCommercialSettings(PartnerCommercialSettings settings)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO partner_commercial_settings(partner_id, price_group_id, default_discount_percent, payment_terms, delivery_terms, valid_from, valid_to, updated_at)
VALUES(@partner_id, @price_group_id, @default_discount_percent, @payment_terms, @delivery_terms, @valid_from, @valid_to, @updated_at)
ON CONFLICT(partner_id) DO UPDATE SET
    price_group_id = EXCLUDED.price_group_id,
    default_discount_percent = EXCLUDED.default_discount_percent,
    payment_terms = EXCLUDED.payment_terms,
    delivery_terms = EXCLUDED.delivery_terms,
    valid_from = EXCLUDED.valid_from,
    valid_to = EXCLUDED.valid_to,
    updated_at = EXCLUDED.updated_at;");
            command.Parameters.AddWithValue("@partner_id", settings.PartnerId);
            command.Parameters.AddWithValue("@price_group_id", (object?)settings.PriceGroupId ?? DBNull.Value);
            command.Parameters.AddWithValue("@default_discount_percent", settings.DefaultDiscountPercent);
            command.Parameters.AddWithValue("@payment_terms", (object?)settings.PaymentTerms ?? DBNull.Value);
            command.Parameters.AddWithValue("@delivery_terms", (object?)settings.DeliveryTerms ?? DBNull.Value);
            command.Parameters.AddWithValue("@valid_from", ToDbDate(settings.ValidFrom));
            command.Parameters.AddWithValue("@valid_to", ToDbDate(settings.ValidTo));
            command.Parameters.AddWithValue("@updated_at", settings.UpdatedAt);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public IReadOnlyList<ItemPrice> GetItemPrices(long itemId, long? priceGroupId)
    {
        return WithConnection(connection =>
        {
            var sql = priceGroupId.HasValue
                ? @"SELECT id, item_id, price_group_id, price, currency, vat_rate, vat_included, uom_code, valid_from, valid_to, is_active, comment, created_at
FROM item_prices WHERE item_id = @item_id AND price_group_id = @price_group_id ORDER BY valid_from DESC, id DESC"
                : @"SELECT id, item_id, price_group_id, price, currency, vat_rate, vat_included, uom_code, valid_from, valid_to, is_active, comment, created_at
FROM item_prices WHERE item_id = @item_id ORDER BY valid_from DESC, id DESC";
            using var command = CreateCommand(connection, sql);
            command.Parameters.AddWithValue("@item_id", itemId);
            if (priceGroupId.HasValue)
            {
                command.Parameters.AddWithValue("@price_group_id", priceGroupId.Value);
            }

            return ReadItemPrices(command);
        });
    }

    public IReadOnlyList<ItemPrice> GetItemPricesForGroup(long priceGroupId, string? search, long? itemTypeId, bool? hasPrice)
    {
        var baseGroup = GetSystemBasePriceGroup() ?? GetDefaultPriceGroup();
        var baseGroupId = baseGroup?.Id ?? priceGroupId;
        return GetItemPriceCatalogForGroup(priceGroupId, baseGroupId, search, itemTypeId, hasPrice)
            .Where(row => row.HasPrice)
            .Select(row => new ItemPrice
            {
                Id = row.ItemPriceId ?? 0,
                ItemId = row.ItemId,
                PriceGroupId = row.PriceGroupId,
                Price = row.Price ?? 0m,
                Currency = row.Currency ?? "RUB",
                ValidFrom = row.ValidFrom ?? DateOnly.FromDateTime(DateTime.Today),
                ValidTo = row.ValidTo,
                IsActive = row.IsActive ?? true,
                Comment = row.Comment,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();
    }

    public IReadOnlyList<ItemPriceCatalogRow> GetItemPriceCatalogForGroup(long priceGroupId, long basePriceGroupId, string? search, long? itemTypeId, bool? hasPrice)
    {
        return WithConnection(connection =>
        {
            var sql = @"
SELECT
    i.id,
    i.name,
    i.barcode,
    i.gtin,
    t.name,
    ip.id,
    ip.price,
    ip.currency,
    ip.valid_from,
    ip.valid_to,
    ip.is_active,
    ip.comment,
    base_ip.id,
    base_ip.price,
    base_ip.currency,
    base_ip.valid_from,
    base_ip.valid_to,
    base_ip.is_active,
    base_ip.comment
FROM items i
LEFT JOIN item_types t ON t.id = i.item_type_id
LEFT JOIN LATERAL (
    SELECT id, price, currency, valid_from, valid_to, is_active, comment
    FROM item_prices
    WHERE item_id = i.id
      AND price_group_id = @price_group_id
      AND is_active = TRUE
      AND valid_from <= CURRENT_DATE
      AND (valid_to IS NULL OR valid_to >= CURRENT_DATE)
    ORDER BY valid_from DESC, id DESC
    LIMIT 1
) ip ON TRUE
LEFT JOIN LATERAL (
    SELECT id, price, currency, valid_from, valid_to, is_active, comment
    FROM item_prices
    WHERE item_id = i.id
      AND price_group_id = @base_price_group_id
      AND is_active = TRUE
      AND valid_from <= CURRENT_DATE
      AND (valid_to IS NULL OR valid_to >= CURRENT_DATE)
    ORDER BY valid_from DESC, id DESC
    LIMIT 1
) base_ip ON TRUE
WHERE TRUE";
            if (!string.IsNullOrWhiteSpace(search))
            {
                sql += " AND (i.name ILIKE @search OR COALESCE(i.barcode, '') ILIKE @search OR COALESCE(i.gtin, '') ILIKE @search)";
            }

            if (itemTypeId.HasValue)
            {
                sql += " AND i.item_type_id = @item_type_id";
            }

            if (hasPrice == true)
            {
                sql += " AND (base_ip.id IS NOT NULL OR ip.id IS NOT NULL)";
            }
            else if (hasPrice == false)
            {
                sql += " AND base_ip.id IS NULL";
            }

            sql += " ORDER BY i.name";
            using var command = CreateCommand(connection, sql);
            command.Parameters.AddWithValue("@price_group_id", priceGroupId);
            command.Parameters.AddWithValue("@base_price_group_id", basePriceGroupId);
            if (!string.IsNullOrWhiteSpace(search))
            {
                command.Parameters.AddWithValue("@search", $"%{search.Trim()}%");
            }

            if (itemTypeId.HasValue)
            {
                command.Parameters.AddWithValue("@item_type_id", itemTypeId.Value);
            }

            using var reader = command.ExecuteReader();
            var list = new List<ItemPriceCatalogRow>();
            while (reader.Read())
            {
                list.Add(ReadItemPriceCatalogRow(reader, priceGroupId));
            }

            return list;
        });
    }

    public void CloseOverlappingActiveItemPrices(long itemId, long priceGroupId, DateOnly validFrom, DateOnly? validTo)
    {
        WithConnection(connection =>
        {
            var closeDate = validFrom.AddDays(-1);
            using var command = CreateCommand(connection, @"
UPDATE item_prices
SET is_active = FALSE,
    valid_to = CASE
        WHEN valid_to IS NULL OR valid_to > @close_date THEN @close_date
        ELSE valid_to
    END
WHERE item_id = @item_id
  AND price_group_id = @price_group_id
  AND is_active = TRUE
  AND valid_from <= COALESCE(@valid_to, DATE '9999-12-31')
  AND (valid_to IS NULL OR valid_to >= @valid_from);");
            command.Parameters.AddWithValue("@item_id", itemId);
            command.Parameters.AddWithValue("@price_group_id", priceGroupId);
            command.Parameters.AddWithValue("@valid_from", validFrom);
            command.Parameters.AddWithValue("@valid_to", ToDbDate(validTo) ?? DBNull.Value);
            command.Parameters.AddWithValue("@close_date", closeDate);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public ItemPrice? GetItemPrice(long itemPriceId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT id, item_id, price_group_id, price, currency, vat_rate, vat_included, uom_code, valid_from, valid_to, is_active, comment, created_at
FROM item_prices
WHERE id = @id;");
            command.Parameters.AddWithValue("@id", itemPriceId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadItemPrice(reader) : null;
        });
    }

    public ItemPrice? GetActiveItemPrice(long itemId, long priceGroupId, DateOnly asOfDate)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT id, item_id, price_group_id, price, currency, vat_rate, vat_included, uom_code, valid_from, valid_to, is_active, comment, created_at
FROM item_prices
WHERE item_id = @item_id
  AND price_group_id = @price_group_id
  AND is_active = TRUE
  AND valid_from <= @as_of
  AND (valid_to IS NULL OR valid_to >= @as_of)
ORDER BY valid_from DESC, id DESC
LIMIT 1;");
            command.Parameters.AddWithValue("@item_id", itemId);
            command.Parameters.AddWithValue("@price_group_id", priceGroupId);
            command.Parameters.AddWithValue("@as_of", asOfDate);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadItemPrice(reader) : null;
        });
    }

    public long AddItemPrice(ItemPrice price)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO item_prices(item_id, price_group_id, price, currency, vat_rate, vat_included, uom_code, valid_from, valid_to, is_active, comment, created_at)
VALUES(@item_id, @price_group_id, @price, @currency, @vat_rate, @vat_included, @uom_code, @valid_from, @valid_to, @is_active, @comment, @created_at)
RETURNING id;");
            BindItemPrice(command, price);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void DeactivateItemPrice(long itemPriceId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "UPDATE item_prices SET is_active = FALSE WHERE id = @id");
            command.Parameters.AddWithValue("@id", itemPriceId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public IReadOnlyList<VolumeDiscountRule> GetVolumeDiscountRules(bool includeInactive)
    {
        return WithConnection(connection =>
        {
            var sql = includeInactive
                ? "SELECT id, scope_type, price_group_id, partner_id, item_type_id, item_id, min_qty, discount_percent, valid_from, valid_to, is_active, comment FROM volume_discount_rules ORDER BY id"
                : "SELECT id, scope_type, price_group_id, partner_id, item_type_id, item_id, min_qty, discount_percent, valid_from, valid_to, is_active, comment FROM volume_discount_rules WHERE is_active = TRUE ORDER BY id";
            using var command = CreateCommand(connection, sql);
            using var reader = command.ExecuteReader();
            var list = new List<VolumeDiscountRule>();
            while (reader.Read())
            {
                list.Add(ReadVolumeDiscountRule(reader));
            }

            return list;
        });
    }

    public long AddVolumeDiscountRule(VolumeDiscountRule rule)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO volume_discount_rules(scope_type, price_group_id, partner_id, item_type_id, item_id, min_qty, discount_percent, valid_from, valid_to, is_active, comment)
VALUES(@scope_type, @price_group_id, @partner_id, @item_type_id, @item_id, @min_qty, @discount_percent, @valid_from, @valid_to, @is_active, @comment)
RETURNING id;");
            BindVolumeDiscountRule(command, rule);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void UpdateVolumeDiscountRule(VolumeDiscountRule rule)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE volume_discount_rules
SET scope_type = @scope_type,
    price_group_id = @price_group_id,
    partner_id = @partner_id,
    item_type_id = @item_type_id,
    item_id = @item_id,
    min_qty = @min_qty,
    discount_percent = @discount_percent,
    valid_from = @valid_from,
    valid_to = @valid_to,
    is_active = @is_active,
    comment = @comment
WHERE id = @id;");
            BindVolumeDiscountRule(command, rule);
            command.Parameters.AddWithValue("@id", rule.Id);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public IReadOnlyList<CommercialOffer> GetCommercialOffers(string? status, long? partnerId, DateOnly? from, DateOnly? to)
    {
        return WithConnection(connection =>
        {
            var sql = @"
SELECT o.id, o.offer_ref, o.partner_id, p.name, p.code, o.contact_person, o.contact_phone, o.contact_email,
       o.price_group_id, pg.name, o.status, o.currency, o.valid_until, o.payment_terms, o.delivery_terms, o.comment,
       o.manager_name, o.subtotal, o.discount_total, o.total, o.next_follow_up_at, o.converted_order_id,
       o.created_at, o.updated_at, o.sent_at, o.closed_at
FROM commercial_offers o
LEFT JOIN partners p ON p.id = o.partner_id
LEFT JOIN price_groups pg ON pg.id = o.price_group_id
WHERE 1=1";
            if (!string.IsNullOrWhiteSpace(status))
            {
                sql += " AND o.status = @status";
            }

            if (partnerId.HasValue)
            {
                sql += " AND o.partner_id = @partner_id";
            }

            if (from.HasValue)
            {
                sql += " AND o.created_at::date >= @from_date";
            }

            if (to.HasValue)
            {
                sql += " AND o.created_at::date <= @to_date";
            }

            sql += " ORDER BY o.created_at DESC, o.id DESC";
            using var command = CreateCommand(connection, sql);
            if (!string.IsNullOrWhiteSpace(status))
            {
                command.Parameters.AddWithValue("@status", status.Trim().ToUpperInvariant());
            }

            if (partnerId.HasValue)
            {
                command.Parameters.AddWithValue("@partner_id", partnerId.Value);
            }

            if (from.HasValue)
            {
                command.Parameters.AddWithValue("@from_date", from.Value);
            }

            if (to.HasValue)
            {
                command.Parameters.AddWithValue("@to_date", to.Value);
            }

            return ReadCommercialOffers(command);
        });
    }

    public CommercialOffer? GetCommercialOffer(long id)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, BuildCommercialOfferSelectSql() + " WHERE o.id = @id");
            command.Parameters.AddWithValue("@id", id);
            return ReadCommercialOffers(command).FirstOrDefault();
        });
    }

    public CommercialOffer? GetCommercialOfferByRef(string offerRef)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, BuildCommercialOfferSelectSql() + " WHERE o.offer_ref = @offer_ref");
            command.Parameters.AddWithValue("@offer_ref", offerRef);
            return ReadCommercialOffers(command).FirstOrDefault();
        });
    }

    public int GetMaxCommercialOfferRefSequenceByYear(int year)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT COALESCE(MAX(CAST(SPLIT_PART(offer_ref, '-', 3) AS INTEGER)), 0)
FROM commercial_offers
WHERE offer_ref LIKE @prefix;");
            command.Parameters.AddWithValue("@prefix", $"CO-{year}-%");
            return Convert.ToInt32(command.ExecuteScalar() ?? 0);
        });
    }

    public long AddCommercialOffer(CommercialOffer offer)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO commercial_offers(
    offer_ref, partner_id, contact_person, contact_phone, contact_email, price_group_id, status, currency,
    valid_until, payment_terms, delivery_terms, comment, manager_name, subtotal, discount_total, total,
    next_follow_up_at, converted_order_id, created_at, updated_at, sent_at, closed_at)
VALUES(
    @offer_ref, @partner_id, @contact_person, @contact_phone, @contact_email, @price_group_id, @status, @currency,
    @valid_until, @payment_terms, @delivery_terms, @comment, @manager_name, @subtotal, @discount_total, @total,
    @next_follow_up_at, @converted_order_id, @created_at, @updated_at, @sent_at, @closed_at)
RETURNING id;");
            BindCommercialOffer(command, offer);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void UpdateCommercialOffer(CommercialOffer offer)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE commercial_offers SET
    offer_ref = @offer_ref,
    partner_id = @partner_id,
    contact_person = @contact_person,
    contact_phone = @contact_phone,
    contact_email = @contact_email,
    price_group_id = @price_group_id,
    status = @status,
    currency = @currency,
    valid_until = @valid_until,
    payment_terms = @payment_terms,
    delivery_terms = @delivery_terms,
    comment = @comment,
    manager_name = @manager_name,
    subtotal = @subtotal,
    discount_total = @discount_total,
    total = @total,
    next_follow_up_at = @next_follow_up_at,
    converted_order_id = @converted_order_id,
    updated_at = @updated_at,
    sent_at = @sent_at,
    closed_at = @closed_at
WHERE id = @id;");
            BindCommercialOffer(command, offer);
            command.Parameters.AddWithValue("@id", offer.Id);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void DeleteCommercialOffer(long offerId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "DELETE FROM commercial_offers WHERE id = @id");
            command.Parameters.AddWithValue("@id", offerId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public IReadOnlyList<CommercialOfferLine> GetCommercialOfferLines(long offerId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT l.id, l.offer_id, l.line_no, l.item_id, i.name, i.barcode, i.gtin, i.brand, i.volume,
       l.qty, l.uom_code, l.base_price, l.volume_discount_percent, l.manual_discount_percent,
       l.final_discount_percent, l.final_price, l.line_total, l.comment
FROM commercial_offer_lines l
LEFT JOIN items i ON i.id = l.item_id
WHERE l.offer_id = @offer_id
ORDER BY l.line_no, l.id;");
            command.Parameters.AddWithValue("@offer_id", offerId);
            using var reader = command.ExecuteReader();
            var list = new List<CommercialOfferLine>();
            while (reader.Read())
            {
                list.Add(ReadCommercialOfferLine(reader));
            }

            return list;
        });
    }

    public long AddCommercialOfferLine(CommercialOfferLine line)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO commercial_offer_lines(
    offer_id, line_no, item_id, qty, uom_code, base_price, volume_discount_percent, manual_discount_percent,
    final_discount_percent, final_price, line_total, comment)
VALUES(
    @offer_id, @line_no, @item_id, @qty, @uom_code, @base_price, @volume_discount_percent, @manual_discount_percent,
    @final_discount_percent, @final_price, @line_total, @comment)
RETURNING id;");
            BindCommercialOfferLine(command, line);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void UpdateCommercialOfferLine(CommercialOfferLine line)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
UPDATE commercial_offer_lines SET
    line_no = @line_no,
    item_id = @item_id,
    qty = @qty,
    uom_code = @uom_code,
    base_price = @base_price,
    volume_discount_percent = @volume_discount_percent,
    manual_discount_percent = @manual_discount_percent,
    final_discount_percent = @final_discount_percent,
    final_price = @final_price,
    line_total = @line_total,
    comment = @comment
WHERE id = @id;");
            BindCommercialOfferLine(command, line);
            command.Parameters.AddWithValue("@id", line.Id);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void DeleteCommercialOfferLine(long lineId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "DELETE FROM commercial_offer_lines WHERE id = @id");
            command.Parameters.AddWithValue("@id", lineId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void DeleteCommercialOfferLines(long offerId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "DELETE FROM commercial_offer_lines WHERE offer_id = @offer_id");
            command.Parameters.AddWithValue("@offer_id", offerId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public IReadOnlyList<CommercialOfferStatusHistoryEntry> GetCommercialOfferStatusHistory(long offerId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT id, offer_id, old_status, new_status, comment, changed_at, changed_by
FROM commercial_offer_status_history
WHERE offer_id = @offer_id
ORDER BY changed_at, id;");
            command.Parameters.AddWithValue("@offer_id", offerId);
            using var reader = command.ExecuteReader();
            var list = new List<CommercialOfferStatusHistoryEntry>();
            while (reader.Read())
            {
                list.Add(new CommercialOfferStatusHistoryEntry
                {
                    Id = reader.GetInt64(0),
                    OfferId = reader.GetInt64(1),
                    OldStatus = CommercialOfferStatusMapper.FromCode(reader.IsDBNull(2) ? null : reader.GetString(2)),
                    NewStatus = CommercialOfferStatusMapper.FromCode(reader.GetString(3)) ?? CommercialOfferStatus.Draft,
                    Comment = reader.IsDBNull(4) ? null : reader.GetString(4),
                    ChangedAt = reader.GetDateTime(5),
                    ChangedBy = reader.IsDBNull(6) ? null : reader.GetString(6)
                });
            }

            return list;
        });
    }

    public void AddCommercialOfferStatusHistory(CommercialOfferStatusHistoryEntry entry)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO commercial_offer_status_history(offer_id, old_status, new_status, comment, changed_at, changed_by)
VALUES(@offer_id, @old_status, @new_status, @comment, @changed_at, @changed_by);");
            command.Parameters.AddWithValue("@offer_id", entry.OfferId);
            command.Parameters.AddWithValue("@old_status", entry.OldStatus.HasValue ? CommercialOfferStatusMapper.ToCode(entry.OldStatus.Value) : DBNull.Value);
            command.Parameters.AddWithValue("@new_status", CommercialOfferStatusMapper.ToCode(entry.NewStatus));
            command.Parameters.AddWithValue("@comment", (object?)entry.Comment ?? DBNull.Value);
            command.Parameters.AddWithValue("@changed_at", entry.ChangedAt);
            command.Parameters.AddWithValue("@changed_by", (object?)entry.ChangedBy ?? DBNull.Value);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public IReadOnlyList<CommercialTemplate> GetCommercialTemplates(CommercialTemplateType? templateType, bool includeInactive)
    {
        return WithConnection(connection =>
        {
            var sql = "SELECT id, name, template_type, source_format, file_path, file_hash, version_no, is_default, is_active, created_at, updated_at FROM commercial_templates WHERE 1=1";
            if (!includeInactive)
            {
                sql += " AND is_active = TRUE";
            }

            if (templateType.HasValue)
            {
                sql += " AND template_type = @template_type";
            }

            sql += " ORDER BY name, version_no DESC";
            using var command = CreateCommand(connection, sql);
            if (templateType.HasValue)
            {
                command.Parameters.AddWithValue("@template_type", CommercialTemplateTypeMapper.ToCode(templateType.Value));
            }

            using var reader = command.ExecuteReader();
            var list = new List<CommercialTemplate>();
            while (reader.Read())
            {
                list.Add(ReadCommercialTemplate(reader));
            }

            return list;
        });
    }

    public CommercialTemplate? GetCommercialTemplate(long id)
    {
        return GetCommercialTemplates(null, true).FirstOrDefault(t => t.Id == id);
    }

    public CommercialTemplate? GetDefaultCommercialTemplate(CommercialTemplateType templateType)
    {
        return GetCommercialTemplates(templateType, false).FirstOrDefault(t => t.IsDefault)
            ?? GetCommercialTemplates(templateType, false).FirstOrDefault();
    }

    public long AddCommercialTemplate(CommercialTemplate template)
    {
        return WithConnection(connection =>
        {
            if (template.IsDefault)
            {
                ClearDefaultTemplate(connection, template.TemplateType);
            }

            using var command = CreateCommand(connection, @"
INSERT INTO commercial_templates(name, template_type, source_format, file_path, file_hash, version_no, is_default, is_active, created_at, updated_at)
VALUES(@name, @template_type, @source_format, @file_path, @file_hash, @version_no, @is_default, @is_active, @created_at, @updated_at)
RETURNING id;");
            BindCommercialTemplate(command, template);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void UpdateCommercialTemplate(CommercialTemplate template)
    {
        WithConnection(connection =>
        {
            if (template.IsDefault)
            {
                ClearDefaultTemplate(connection, template.TemplateType, template.Id);
            }

            using var command = CreateCommand(connection, @"
UPDATE commercial_templates SET
    name = @name,
    template_type = @template_type,
    source_format = @source_format,
    file_path = @file_path,
    file_hash = @file_hash,
    version_no = @version_no,
    is_default = @is_default,
    is_active = @is_active,
    updated_at = @updated_at
WHERE id = @id;");
            BindCommercialTemplate(command, template);
            command.Parameters.AddWithValue("@id", template.Id);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void SetDefaultCommercialTemplate(long templateId)
    {
        var template = GetCommercialTemplate(templateId)
            ?? throw new InvalidOperationException("TEMPLATE_NOT_FOUND");
        WithConnection(connection =>
        {
            ClearDefaultTemplate(connection, template.TemplateType);
            using var command = CreateCommand(connection, "UPDATE commercial_templates SET is_default = TRUE, updated_at = now() WHERE id = @id");
            command.Parameters.AddWithValue("@id", templateId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public long AddGeneratedDocument(GeneratedDocument document)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO generated_documents(template_id, source_type, source_id, output_format, file_path, file_hash, created_at)
VALUES(@template_id, @source_type, @source_id, @output_format, @file_path, @file_hash, @created_at)
RETURNING id;");
            command.Parameters.AddWithValue("@template_id", (object?)document.TemplateId ?? DBNull.Value);
            command.Parameters.AddWithValue("@source_type", document.SourceType);
            command.Parameters.AddWithValue("@source_id", document.SourceId);
            command.Parameters.AddWithValue("@output_format", document.OutputFormat);
            command.Parameters.AddWithValue("@file_path", document.FilePath);
            command.Parameters.AddWithValue("@file_hash", (object?)document.FileHash ?? DBNull.Value);
            command.Parameters.AddWithValue("@created_at", document.CreatedAt);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public IReadOnlyList<GeneratedDocument> GetGeneratedDocuments(string sourceType, long sourceId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT id, template_id, source_type, source_id, output_format, file_path, file_hash, created_at
FROM generated_documents
WHERE source_type = @source_type AND source_id = @source_id
ORDER BY created_at DESC, id DESC;");
            command.Parameters.AddWithValue("@source_type", sourceType);
            command.Parameters.AddWithValue("@source_id", sourceId);
            using var reader = command.ExecuteReader();
            var list = new List<GeneratedDocument>();
            while (reader.Read())
            {
                list.Add(new GeneratedDocument
                {
                    Id = reader.GetInt64(0),
                    TemplateId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    SourceType = reader.GetString(2),
                    SourceId = reader.GetInt64(3),
                    OutputFormat = reader.GetString(4),
                    FilePath = reader.GetString(5),
                    FileHash = reader.IsDBNull(6) ? null : reader.GetString(6),
                    CreatedAt = reader.GetDateTime(7)
                });
            }

            return list;
        });
    }

    public long AddPriceTagBatch(PriceTagBatch batch)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO price_tag_batches(price_group_id, template_id, created_at, comment)
VALUES(@price_group_id, @template_id, @created_at, @comment)
RETURNING id;");
            command.Parameters.AddWithValue("@price_group_id", batch.PriceGroupId);
            command.Parameters.AddWithValue("@template_id", (object?)batch.TemplateId ?? DBNull.Value);
            command.Parameters.AddWithValue("@created_at", batch.CreatedAt);
            command.Parameters.AddWithValue("@comment", (object?)batch.Comment ?? DBNull.Value);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    public void AddPriceTagBatchLine(PriceTagBatchLine line)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
INSERT INTO price_tag_batch_lines(batch_id, item_id, copies, price)
VALUES(@batch_id, @item_id, @copies, @price);");
            command.Parameters.AddWithValue("@batch_id", line.BatchId);
            command.Parameters.AddWithValue("@item_id", line.ItemId);
            command.Parameters.AddWithValue("@copies", line.Copies);
            command.Parameters.AddWithValue("@price", line.Price);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public IReadOnlyList<PriceTagBatchLine> GetPriceTagBatchLines(long batchId)
    {
        return WithConnection(connection =>
        {
            using var command = CreateCommand(connection, @"
SELECT l.id, l.batch_id, l.item_id, i.name, l.copies, l.price
FROM price_tag_batch_lines l
LEFT JOIN items i ON i.id = l.item_id
WHERE l.batch_id = @batch_id
ORDER BY l.id;");
            command.Parameters.AddWithValue("@batch_id", batchId);
            using var reader = command.ExecuteReader();
            var list = new List<PriceTagBatchLine>();
            while (reader.Read())
            {
                list.Add(new PriceTagBatchLine
                {
                    Id = reader.GetInt64(0),
                    BatchId = reader.GetInt64(1),
                    ItemId = reader.GetInt64(2),
                    ItemName = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Copies = reader.GetInt32(4),
                    Price = reader.GetDecimal(5)
                });
            }

            return list;
        });
    }

    public void SetOrderCommercialOfferId(long orderId, long commercialOfferId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "UPDATE orders SET commercial_offer_id = @commercial_offer_id WHERE id = @order_id");
            command.Parameters.AddWithValue("@commercial_offer_id", commercialOfferId);
            command.Parameters.AddWithValue("@order_id", orderId);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    private static string BuildCommercialOfferSelectSql() => @"
SELECT o.id, o.offer_ref, o.partner_id, p.name, p.code, o.contact_person, o.contact_phone, o.contact_email,
       o.price_group_id, pg.name, o.status, o.currency, o.valid_until, o.payment_terms, o.delivery_terms, o.comment,
       o.manager_name, o.subtotal, o.discount_total, o.total, o.next_follow_up_at, o.converted_order_id,
       o.created_at, o.updated_at, o.sent_at, o.closed_at
FROM commercial_offers o
LEFT JOIN partners p ON p.id = o.partner_id
LEFT JOIN price_groups pg ON pg.id = o.price_group_id";

    private static List<CommercialOffer> ReadCommercialOffers(NpgsqlCommand command)
    {
        using var reader = command.ExecuteReader();
        var list = new List<CommercialOffer>();
        while (reader.Read())
        {
            list.Add(ReadCommercialOffer(reader));
        }

        return list;
    }

    private static CommercialOffer ReadCommercialOffer(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        OfferRef = reader.GetString(1),
        PartnerId = reader.GetInt64(2),
        PartnerName = reader.IsDBNull(3) ? null : reader.GetString(3),
        PartnerCode = reader.IsDBNull(4) ? null : reader.GetString(4),
        ContactPerson = reader.IsDBNull(5) ? null : reader.GetString(5),
        ContactPhone = reader.IsDBNull(6) ? null : reader.GetString(6),
        ContactEmail = reader.IsDBNull(7) ? null : reader.GetString(7),
        PriceGroupId = reader.GetInt64(8),
        PriceGroupName = reader.IsDBNull(9) ? null : reader.GetString(9),
        Status = CommercialOfferStatusMapper.FromCode(reader.GetString(10)) ?? CommercialOfferStatus.Draft,
        Currency = reader.GetString(11),
        ValidUntil = reader.IsDBNull(12) ? null : DateOnly.FromDateTime(reader.GetDateTime(12)),
        PaymentTerms = reader.IsDBNull(13) ? null : reader.GetString(13),
        DeliveryTerms = reader.IsDBNull(14) ? null : reader.GetString(14),
        Comment = reader.IsDBNull(15) ? null : reader.GetString(15),
        ManagerName = reader.IsDBNull(16) ? null : reader.GetString(16),
        Subtotal = reader.GetDecimal(17),
        DiscountTotal = reader.GetDecimal(18),
        Total = reader.GetDecimal(19),
        NextFollowUpAt = reader.IsDBNull(20) ? null : reader.GetDateTime(20),
        ConvertedOrderId = reader.IsDBNull(21) ? null : reader.GetInt64(21),
        CreatedAt = reader.GetDateTime(22),
        UpdatedAt = reader.GetDateTime(23),
        SentAt = reader.IsDBNull(24) ? null : reader.GetDateTime(24),
        ClosedAt = reader.IsDBNull(25) ? null : reader.GetDateTime(25)
    };

    private static CommercialOfferLine ReadCommercialOfferLine(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        OfferId = reader.GetInt64(1),
        LineNo = reader.GetInt32(2),
        ItemId = reader.GetInt64(3),
        ItemName = reader.IsDBNull(4) ? null : reader.GetString(4),
        ItemBarcode = reader.IsDBNull(5) ? null : reader.GetString(5),
        ItemGtin = reader.IsDBNull(6) ? null : reader.GetString(6),
        ItemBrand = reader.IsDBNull(7) ? null : reader.GetString(7),
        ItemVolume = reader.IsDBNull(8) ? null : reader.GetString(8),
        Qty = Convert.ToDouble(reader.GetValue(9), CultureInfo.InvariantCulture),
        UomCode = reader.IsDBNull(10) ? null : reader.GetString(10),
        BasePrice = reader.GetDecimal(11),
        VolumeDiscountPercent = reader.GetDecimal(12),
        ManualDiscountPercent = reader.GetDecimal(13),
        FinalDiscountPercent = reader.GetDecimal(14),
        FinalPrice = reader.GetDecimal(15),
        LineTotal = reader.GetDecimal(16),
        Comment = reader.IsDBNull(17) ? null : reader.GetString(17)
    };

    private static List<PriceGroup> ReadPriceGroups(NpgsqlCommand command)
    {
        using var reader = command.ExecuteReader();
        var list = new List<PriceGroup>();
        while (reader.Read())
        {
            list.Add(ReadPriceGroup(reader));
        }

        return list;
    }

    private static PriceGroup ReadPriceGroup(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Name = reader.GetString(1),
        Description = reader.IsDBNull(2) ? null : reader.GetString(2),
        Currency = reader.GetString(3),
        VatMode = VatModeMapper.FromCode(reader.GetString(4)),
        IsDefault = reader.GetBoolean(5),
        IsActive = reader.GetBoolean(6),
        IsSystem = reader.GetBoolean(7),
        DefaultDiscountPercent = reader.GetDecimal(8),
        DefaultMarkupPercent = reader.GetDecimal(9),
        CreatedAt = reader.GetDateTime(10),
        UpdatedAt = reader.GetDateTime(11)
    };

    private static PartnerCommercialSettings ReadPartnerCommercialSettings(NpgsqlDataReader reader) => new()
    {
        PartnerId = reader.GetInt64(0),
        PriceGroupId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
        DefaultDiscountPercent = reader.GetDecimal(2),
        PaymentTerms = reader.IsDBNull(3) ? null : reader.GetString(3),
        DeliveryTerms = reader.IsDBNull(4) ? null : reader.GetString(4),
        ValidFrom = reader.IsDBNull(5) ? null : DateOnly.FromDateTime(reader.GetDateTime(5)),
        ValidTo = reader.IsDBNull(6) ? null : DateOnly.FromDateTime(reader.GetDateTime(6)),
        UpdatedAt = reader.GetDateTime(7)
    };

    private static List<ItemPrice> ReadItemPrices(NpgsqlCommand command)
    {
        using var reader = command.ExecuteReader();
        var list = new List<ItemPrice>();
        while (reader.Read())
        {
            list.Add(ReadItemPrice(reader));
        }

        return list;
    }

    private static ItemPriceCatalogRow ReadItemPriceCatalogRow(NpgsqlDataReader reader, long priceGroupId) => new()
    {
        ItemId = reader.GetInt64(0),
        ItemName = reader.GetString(1),
        Barcode = reader.IsDBNull(2) ? null : reader.GetString(2),
        Gtin = reader.IsDBNull(3) ? null : reader.GetString(3),
        ItemTypeName = reader.IsDBNull(4) ? null : reader.GetString(4),
        ItemPriceId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
        GroupOverridePrice = reader.IsDBNull(6) ? null : reader.GetDecimal(6),
        Currency = reader.IsDBNull(7) ? null : reader.GetString(7),
        ValidFrom = ReadDateOnlyOrNull(reader, 8),
        ValidTo = ReadDateOnlyOrNull(reader, 9),
        IsActive = reader.IsDBNull(10) ? null : reader.GetBoolean(10),
        Comment = reader.IsDBNull(11) ? null : reader.GetString(11),
        BaseItemPriceId = reader.IsDBNull(12) ? null : reader.GetInt64(12),
        BasePrice = reader.IsDBNull(13) ? null : reader.GetDecimal(13),
        BaseCurrency = reader.IsDBNull(14) ? null : reader.GetString(14),
        BaseValidFrom = ReadDateOnlyOrNull(reader, 15),
        BaseValidTo = ReadDateOnlyOrNull(reader, 16),
        BaseIsActive = reader.IsDBNull(17) ? null : reader.GetBoolean(17),
        BaseComment = reader.IsDBNull(18) ? null : reader.GetString(18),
        PriceGroupId = priceGroupId
    };

    private static DateOnly? ReadDateOnlyOrNull(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return reader.GetFieldValue<DateOnly>(ordinal);
    }

    private static ItemPrice ReadItemPrice(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        ItemId = reader.GetInt64(1),
        PriceGroupId = reader.GetInt64(2),
        Price = reader.GetDecimal(3),
        Currency = reader.GetString(4),
        VatRate = reader.IsDBNull(5) ? null : reader.GetDecimal(5),
        VatIncluded = reader.IsDBNull(6) ? null : reader.GetBoolean(6),
        UomCode = reader.IsDBNull(7) ? null : reader.GetString(7),
        ValidFrom = DateOnly.FromDateTime(reader.GetDateTime(8)),
        ValidTo = reader.IsDBNull(9) ? null : DateOnly.FromDateTime(reader.GetDateTime(9)),
        IsActive = reader.GetBoolean(10),
        Comment = reader.IsDBNull(11) ? null : reader.GetString(11),
        CreatedAt = reader.GetDateTime(12)
    };

    private static VolumeDiscountRule ReadVolumeDiscountRule(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        ScopeType = VolumeDiscountScopeMapper.FromCode(reader.GetString(1)) ?? VolumeDiscountScope.Global,
        PriceGroupId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
        PartnerId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
        ItemId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
        ItemTypeId = reader.IsDBNull(4) ? null : reader.GetInt64(4),
        MinQty = Convert.ToDouble(reader.GetValue(6), CultureInfo.InvariantCulture),
        DiscountPercent = reader.GetDecimal(7),
        ValidFrom = reader.IsDBNull(8) ? null : DateOnly.FromDateTime(reader.GetDateTime(8)),
        ValidTo = reader.IsDBNull(9) ? null : DateOnly.FromDateTime(reader.GetDateTime(9)),
        IsActive = reader.GetBoolean(10),
        Comment = reader.IsDBNull(11) ? null : reader.GetString(11)
    };

    private static CommercialTemplate ReadCommercialTemplate(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Name = reader.GetString(1),
        TemplateType = CommercialTemplateTypeMapper.FromCode(reader.GetString(2)) ?? CommercialTemplateType.CommercialOffer,
        SourceFormat = reader.GetString(3),
        FilePath = reader.GetString(4),
        FileHash = reader.IsDBNull(5) ? null : reader.GetString(5),
        VersionNo = reader.GetInt32(6),
        IsDefault = reader.GetBoolean(7),
        IsActive = reader.GetBoolean(8),
        CreatedAt = reader.GetDateTime(9),
        UpdatedAt = reader.GetDateTime(10)
    };

    private static void BindPriceGroup(NpgsqlCommand command, PriceGroup group)
    {
        command.Parameters.AddWithValue("@name", group.Name);
        command.Parameters.AddWithValue("@description", (object?)group.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("@currency", group.Currency);
        command.Parameters.AddWithValue("@vat_mode", VatModeMapper.ToCode(group.VatMode));
        command.Parameters.AddWithValue("@is_default", group.IsDefault);
        command.Parameters.AddWithValue("@is_active", group.IsActive);
        command.Parameters.AddWithValue("@is_system", group.IsSystem);
        command.Parameters.AddWithValue("@default_discount_percent", group.DefaultDiscountPercent);
        command.Parameters.AddWithValue("@default_markup_percent", group.DefaultMarkupPercent);
        command.Parameters.AddWithValue("@created_at", group.CreatedAt);
        command.Parameters.AddWithValue("@updated_at", group.UpdatedAt);
    }

    private static void BindItemPrice(NpgsqlCommand command, ItemPrice price)
    {
        command.Parameters.AddWithValue("@item_id", price.ItemId);
        command.Parameters.AddWithValue("@price_group_id", price.PriceGroupId);
        command.Parameters.AddWithValue("@price", price.Price);
        command.Parameters.AddWithValue("@currency", price.Currency);
        command.Parameters.AddWithValue("@vat_rate", (object?)price.VatRate ?? DBNull.Value);
        command.Parameters.AddWithValue("@vat_included", price.VatIncluded.HasValue ? price.VatIncluded.Value : DBNull.Value);
        command.Parameters.AddWithValue("@uom_code", (object?)price.UomCode ?? DBNull.Value);
        command.Parameters.AddWithValue("@valid_from", price.ValidFrom);
        command.Parameters.AddWithValue("@valid_to", ToDbDate(price.ValidTo));
        command.Parameters.AddWithValue("@is_active", price.IsActive);
        command.Parameters.AddWithValue("@comment", (object?)price.Comment ?? DBNull.Value);
        command.Parameters.AddWithValue("@created_at", price.CreatedAt);
    }

    private static void BindVolumeDiscountRule(NpgsqlCommand command, VolumeDiscountRule rule)
    {
        command.Parameters.AddWithValue("@scope_type", VolumeDiscountScopeMapper.ToCode(rule.ScopeType));
        command.Parameters.AddWithValue("@price_group_id", (object?)rule.PriceGroupId ?? DBNull.Value);
        command.Parameters.AddWithValue("@partner_id", (object?)rule.PartnerId ?? DBNull.Value);
        command.Parameters.AddWithValue("@item_type_id", (object?)rule.ItemTypeId ?? DBNull.Value);
        command.Parameters.AddWithValue("@item_id", (object?)rule.ItemId ?? DBNull.Value);
        command.Parameters.AddWithValue("@min_qty", rule.MinQty);
        command.Parameters.AddWithValue("@discount_percent", rule.DiscountPercent);
        command.Parameters.AddWithValue("@valid_from", ToDbDate(rule.ValidFrom));
        command.Parameters.AddWithValue("@valid_to", ToDbDate(rule.ValidTo));
        command.Parameters.AddWithValue("@is_active", rule.IsActive);
        command.Parameters.AddWithValue("@comment", (object?)rule.Comment ?? DBNull.Value);
    }

    private static void BindCommercialOffer(NpgsqlCommand command, CommercialOffer offer)
    {
        command.Parameters.AddWithValue("@offer_ref", offer.OfferRef);
        command.Parameters.AddWithValue("@partner_id", offer.PartnerId);
        command.Parameters.AddWithValue("@contact_person", (object?)offer.ContactPerson ?? DBNull.Value);
        command.Parameters.AddWithValue("@contact_phone", (object?)offer.ContactPhone ?? DBNull.Value);
        command.Parameters.AddWithValue("@contact_email", (object?)offer.ContactEmail ?? DBNull.Value);
        command.Parameters.AddWithValue("@price_group_id", offer.PriceGroupId);
        command.Parameters.AddWithValue("@status", CommercialOfferStatusMapper.ToCode(offer.Status));
        command.Parameters.AddWithValue("@currency", offer.Currency);
        command.Parameters.AddWithValue("@valid_until", ToDbDate(offer.ValidUntil));
        command.Parameters.AddWithValue("@payment_terms", (object?)offer.PaymentTerms ?? DBNull.Value);
        command.Parameters.AddWithValue("@delivery_terms", (object?)offer.DeliveryTerms ?? DBNull.Value);
        command.Parameters.AddWithValue("@comment", (object?)offer.Comment ?? DBNull.Value);
        command.Parameters.AddWithValue("@manager_name", (object?)offer.ManagerName ?? DBNull.Value);
        command.Parameters.AddWithValue("@subtotal", offer.Subtotal);
        command.Parameters.AddWithValue("@discount_total", offer.DiscountTotal);
        command.Parameters.AddWithValue("@total", offer.Total);
        command.Parameters.AddWithValue("@next_follow_up_at", (object?)offer.NextFollowUpAt ?? DBNull.Value);
        command.Parameters.AddWithValue("@converted_order_id", (object?)offer.ConvertedOrderId ?? DBNull.Value);
        command.Parameters.AddWithValue("@created_at", offer.CreatedAt);
        command.Parameters.AddWithValue("@updated_at", offer.UpdatedAt);
        command.Parameters.AddWithValue("@sent_at", (object?)offer.SentAt ?? DBNull.Value);
        command.Parameters.AddWithValue("@closed_at", (object?)offer.ClosedAt ?? DBNull.Value);
    }

    private static void BindCommercialOfferLine(NpgsqlCommand command, CommercialOfferLine line)
    {
        command.Parameters.AddWithValue("@offer_id", line.OfferId);
        command.Parameters.AddWithValue("@line_no", line.LineNo);
        command.Parameters.AddWithValue("@item_id", line.ItemId);
        command.Parameters.AddWithValue("@qty", line.Qty);
        command.Parameters.AddWithValue("@uom_code", (object?)line.UomCode ?? DBNull.Value);
        command.Parameters.AddWithValue("@base_price", line.BasePrice);
        command.Parameters.AddWithValue("@volume_discount_percent", line.VolumeDiscountPercent);
        command.Parameters.AddWithValue("@manual_discount_percent", line.ManualDiscountPercent);
        command.Parameters.AddWithValue("@final_discount_percent", line.FinalDiscountPercent);
        command.Parameters.AddWithValue("@final_price", line.FinalPrice);
        command.Parameters.AddWithValue("@line_total", line.LineTotal);
        command.Parameters.AddWithValue("@comment", (object?)line.Comment ?? DBNull.Value);
    }

    private static void BindCommercialTemplate(NpgsqlCommand command, CommercialTemplate template)
    {
        command.Parameters.AddWithValue("@name", template.Name);
        command.Parameters.AddWithValue("@template_type", CommercialTemplateTypeMapper.ToCode(template.TemplateType));
        command.Parameters.AddWithValue("@source_format", template.SourceFormat);
        command.Parameters.AddWithValue("@file_path", template.FilePath);
        command.Parameters.AddWithValue("@file_hash", (object?)template.FileHash ?? DBNull.Value);
        command.Parameters.AddWithValue("@version_no", template.VersionNo);
        command.Parameters.AddWithValue("@is_default", template.IsDefault);
        command.Parameters.AddWithValue("@is_active", template.IsActive);
        command.Parameters.AddWithValue("@created_at", template.CreatedAt);
        command.Parameters.AddWithValue("@updated_at", template.UpdatedAt);
    }

    private static void ClearDefaultPriceGroup(NpgsqlConnection connection, long? exceptId = null)
    {
        var sql = exceptId.HasValue
            ? "UPDATE price_groups SET is_default = FALSE, updated_at = now() WHERE is_default = TRUE AND is_system = FALSE AND id <> @id"
            : "UPDATE price_groups SET is_default = FALSE, updated_at = now() WHERE is_default = TRUE AND is_system = FALSE";
        using var command = CreateCommand(connection, sql);
        if (exceptId.HasValue)
        {
            command.Parameters.AddWithValue("@id", exceptId.Value);
        }

        command.ExecuteNonQuery();
    }

    private static void ClearDefaultTemplate(NpgsqlConnection connection, CommercialTemplateType templateType, long? exceptId = null)
    {
        var sql = "UPDATE commercial_templates SET is_default = FALSE, updated_at = now() WHERE template_type = @template_type AND is_default = TRUE";
        if (exceptId.HasValue)
        {
            sql += " AND id <> @id";
        }

        using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@template_type", CommercialTemplateTypeMapper.ToCode(templateType));
        if (exceptId.HasValue)
        {
            command.Parameters.AddWithValue("@id", exceptId.Value);
        }

        command.ExecuteNonQuery();
    }

    private static object ToDbDate(DateOnly? date) => date.HasValue ? date.Value : DBNull.Value;

    private T WithConnection<T>(Func<NpgsqlConnection, T> work)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        return work(connection);
    }

    private static NpgsqlCommand CreateCommand(NpgsqlConnection connection, string sql) => new(sql, connection);
}
