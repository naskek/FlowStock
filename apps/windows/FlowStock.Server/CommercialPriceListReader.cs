using Npgsql;
using NpgsqlTypes;

namespace FlowStock.Server;

// Current-state commercial report. Never reads order_line price snapshots.
// The ALL/ITEM branches enumerate items + explicit overrides, not partner × item.
public sealed record CommercialPriceListQuery(
    string Mode,
    long? PartnerId = null,
    long? ItemId = null,
    string? Gtin = null,
    string? Search = null,
    string? Brand = null,
    string? Volume = null,
    bool ActiveItemsOnly = true,
    bool IncludeInactiveOverrides = false,
    bool IncludeUnpriced = false);

public sealed record CommercialPriceListRow(
    string RowKind,
    long? PartnerId,
    string? PartnerName,
    string? PartnerCode,
    long ItemId,
    string ItemName,
    string? Gtin,
    string? Brand,
    string? Volume,
    decimal? DefaultPriceGross,
    decimal? PartnerPriceGross,
    decimal? EffectivePriceGross,
    string PriceSource,
    decimal? VatRate,
    string? VatRateName,
    bool ItemIsActive,
    bool? PartnerPriceIsActive);

public sealed record CommercialPriceListResult(
    string Mode,
    IReadOnlyList<CommercialPriceListRow> Items,
    int TotalCount);

public sealed class CommercialPriceListTooLargeException : Exception
{
    public CommercialPriceListTooLargeException()
        : base("Слишком много строк прайса. Уточните фильтры (максимум 10 000 строк).")
    {
    }
}

public sealed class CommercialPriceListReader
{
    public const int MaxReportRows = 10_000;
    private readonly string _connectionString;

    public CommercialPriceListReader(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("PostgreSQL connection is required.", nameof(connectionString))
            : connectionString;
    }

    // One read-only SQL statement gives one consistent PostgreSQL statement snapshot
    // for preview and export. The WPF exports the very same downloaded result.
    public CommercialPriceListResult Get(CommercialPriceListQuery query, int maxRows = MaxReportRows)
    {
        if (maxRows < 1 || maxRows > MaxReportRows) throw new ArgumentOutOfRangeException(nameof(maxRows));
        if (query.Mode is not ("all" or "item" or "partner"))
        {
            throw new ArgumentException("Invalid pricing report mode.", nameof(query));
        }
        if (query.Mode == "partner" && query.PartnerId is null
            || query.Mode == "item" && query.ItemId is null)
        {
            throw new ArgumentException("Selected partner/item required.", nameof(query));
        }

        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
WITH price_rows AS (
    -- Baseline item rows in general and product audits.
    SELECT 'BASE'::text AS row_kind,
           NULL::bigint AS partner_id, NULL::text AS partner_name,
           NULL::text AS partner_code,
           i.id AS item_id, i.name AS item_name,
           i.gtin, i.brand, i.volume,
           i.default_sale_price_gross AS default_price_gross,
           NULL::numeric AS partner_price_gross,
           i.default_sale_price_gross AS effective_price_gross,
           CASE WHEN i.default_sale_price_gross IS NOT NULL THEN 'ITEM_DEFAULT'
                ELSE 'NONE' END::text AS price_source,
           vr.rate AS vat_rate, vr.name AS vat_rate_name,
           COALESCE(i.is_active, TRUE) AS item_is_active,
           NULL::boolean AS partner_price_is_active
      FROM items i
      LEFT JOIN vat_rates vr ON vr.id = i.default_sale_vat_rate_id
     WHERE @mode IN ('all', 'item')
    UNION ALL
    -- Existing explicit assignments only: never materialize all partner×item pairs.
    SELECT 'PARTNER'::text, p.id, p.name, p.code,
           i.id, i.name, i.gtin, i.brand, i.volume,
           i.default_sale_price_gross, pi.unit_price_gross,
           CASE WHEN pi.is_active THEN pi.unit_price_gross
                ELSE i.default_sale_price_gross END,
           CASE WHEN pi.is_active THEN 'PARTNER_ITEM'
                WHEN i.default_sale_price_gross IS NOT NULL THEN 'ITEM_DEFAULT'
                ELSE 'NONE' END,
           vr.rate, vr.name, COALESCE(i.is_active, TRUE), pi.is_active
      FROM partner_item_sale_prices pi
      JOIN partners p ON p.id = pi.partner_id
      JOIN items i ON i.id = pi.item_id
      LEFT JOIN vat_rates vr ON vr.id = i.default_sale_vat_rate_id
     WHERE @mode IN ('all', 'item')
       AND (pi.is_active OR @include_inactive)
       AND (@partner_id IS NULL OR pi.partner_id = @partner_id)
    UNION ALL
    -- A single selected customer across catalog items; inactive override falls back.
    SELECT 'PARTNER'::text, p.id, p.name, p.code,
           i.id, i.name, i.gtin, i.brand, i.volume,
           i.default_sale_price_gross, pi.unit_price_gross,
           CASE WHEN pi.is_active THEN pi.unit_price_gross
                ELSE i.default_sale_price_gross END,
           CASE WHEN pi.is_active THEN 'PARTNER_ITEM'
                WHEN i.default_sale_price_gross IS NOT NULL THEN 'ITEM_DEFAULT'
                ELSE 'NONE' END,
           vr.rate, vr.name, COALESCE(i.is_active, TRUE), pi.is_active
      FROM items i
      JOIN partners p ON p.id = @partner_id
      -- The HTTP boundary validates the authoritative PartnerRoleResolver,
      -- including legacy roles; NULL here must not silently hide clients.
      LEFT JOIN partner_item_sale_prices pi
             ON pi.item_id = i.id AND pi.partner_id = p.id
            AND (pi.is_active OR @include_inactive)
      LEFT JOIN vat_rates vr ON vr.id = i.default_sale_vat_rate_id
     WHERE @mode = 'partner'
)
SELECT row_kind, partner_id, partner_name, partner_code, item_id, item_name,
       gtin, brand, volume, default_price_gross, partner_price_gross,
       effective_price_gross, price_source, vat_rate, vat_rate_name,
       item_is_active, partner_price_is_active
  FROM price_rows
 WHERE (@item_id IS NULL OR item_id = @item_id)
   AND (@gtin IS NULL OR gtin = @gtin)
   AND (@search IS NULL OR item_name ILIKE '%' || @search || '%'
        OR COALESCE(gtin, '') ILIKE '%' || @search || '%')
   AND (@brand IS NULL OR LOWER(COALESCE(brand, '')) = LOWER(@brand))
   AND (@volume IS NULL OR LOWER(COALESCE(volume, '')) = LOWER(@volume))
   AND (NOT @active_items_only OR item_is_active)
   AND (@mode <> 'partner' OR @include_unpriced OR effective_price_gross IS NOT NULL)
 ORDER BY item_name COLLATE "C", item_id,
          CASE WHEN row_kind = 'BASE' THEN 0 ELSE 1 END,
          partner_name COLLATE "C" NULLS FIRST, partner_id NULLS FIRST
 LIMIT @max_rows_plus_one
""";
        command.Parameters.Add("mode", NpgsqlDbType.Text).Value = query.Mode;
        AddNullable(command, "partner_id", NpgsqlDbType.Bigint, query.PartnerId);
        AddNullable(command, "item_id", NpgsqlDbType.Bigint, query.ItemId);
        AddNullable(command, "gtin", NpgsqlDbType.Text, Normalize(query.Gtin));
        AddNullable(command, "search", NpgsqlDbType.Text, Normalize(query.Search));
        AddNullable(command, "brand", NpgsqlDbType.Text, Normalize(query.Brand));
        AddNullable(command, "volume", NpgsqlDbType.Text, Normalize(query.Volume));
        command.Parameters.Add("active_items_only", NpgsqlDbType.Boolean).Value = query.ActiveItemsOnly;
        command.Parameters.Add("include_inactive", NpgsqlDbType.Boolean).Value = query.IncludeInactiveOverrides;
        command.Parameters.Add("include_unpriced", NpgsqlDbType.Boolean).Value = query.IncludeUnpriced;
        command.Parameters.Add("max_rows_plus_one", NpgsqlDbType.Integer).Value = maxRows + 1;
        using var reader = command.ExecuteReader();
        var rows = new List<CommercialPriceListRow>();
        while (reader.Read())
        {
            if (rows.Count == maxRows)
                throw new CommercialPriceListTooLargeException();
            rows.Add(new CommercialPriceListRow(
                reader.GetString(0),
                NullableLong(reader, 1),
                NullableText(reader, 2),
                NullableText(reader, 3),
                reader.GetInt64(4),
                reader.GetString(5),
                NullableText(reader, 6),
                NullableText(reader, 7),
                NullableText(reader, 8),
                NullableDecimal(reader, 9),
                NullableDecimal(reader, 10),
                NullableDecimal(reader, 11),
                reader.GetString(12),
                NullableDecimal(reader, 13),
                NullableText(reader, 14),
                reader.GetBoolean(15),
                reader.IsDBNull(16) ? null : reader.GetBoolean(16)));
        }

        return new CommercialPriceListResult(query.Mode, rows, rows.Count);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void AddNullable(NpgsqlCommand command, string name, NpgsqlDbType type, object? value) =>
        command.Parameters.Add(name, type).Value = value ?? DBNull.Value;

    private static long? NullableLong(NpgsqlDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetInt64(index);

    private static string? NullableText(NpgsqlDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index);

    private static decimal? NullableDecimal(NpgsqlDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetDecimal(index);
}
