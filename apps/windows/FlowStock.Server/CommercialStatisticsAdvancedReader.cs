using System.Globalization;
using FlowStock.Core.Models;
using Npgsql;
using NpgsqlTypes;

namespace FlowStock.Server;

public sealed class CommercialStatisticsAdvancedReader
{
    private readonly string _connectionString;

    public CommercialStatisticsAdvancedReader(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("PostgreSQL connection string is required.", nameof(connectionString))
            : connectionString;
    }

    public static bool IsRequired(CommercialStatisticsQuery query) =>
        !string.IsNullOrWhiteSpace(query.ItemNameContains)
        || NormalizeGtins(query.Gtins).Length > 0
        || NormalizeTextValues(query.Volumes).Length > 0;

    public CommercialStatisticsResult Get(CommercialStatisticsQuery query)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = BuildFactsSql(query);
        AddParameters(command, query);

        using var reader = command.ExecuteReader();
        var facts = new List<Fact>();
        while (reader.Read())
        {
            facts.Add(new Fact(
                FactDate: reader.GetString(0),
                OrderId: reader.GetInt64(1),
                DocId: reader.IsDBNull(2) ? null : reader.GetInt64(2),
                PartnerId: reader.IsDBNull(3) ? null : reader.GetInt64(3),
                PartnerLabel: reader.GetString(4),
                ItemId: reader.GetInt64(5),
                ItemLabel: reader.GetString(6),
                Gtin: reader.IsDBNull(7) ? null : reader.GetString(7),
                Brand: reader.IsDBNull(8) ? null : reader.GetString(8),
                Volume: reader.IsDBNull(9) ? null : reader.GetString(9),
                Quantity: reader.GetDecimal(10),
                Gross: reader.IsDBNull(11) ? null : reader.GetDecimal(11),
                Vat: reader.IsDBNull(12) ? null : reader.GetDecimal(12),
                UnitPriceGross: reader.IsDBNull(13) ? null : reader.GetDecimal(13),
                VatRate: reader.IsDBNull(14) ? null : reader.GetDecimal(14),
                IsLinked: reader.GetBoolean(15),
                ItemMismatch: reader.GetBoolean(16)));
        }

        var summary = AggregateAmounts(facts);
        var monthly = facts
            .GroupBy(fact => MonthKey(fact.FactDate), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new CommercialStatisticsMonth(group.Key, AggregateAmounts(group)))
            .ToArray();

        var detailFacts = query.DetailMonth.HasValue
            ? facts.Where(fact => string.Equals(
                MonthKey(fact.FactDate),
                query.DetailMonth.Value.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
            : facts;

        var grouped = detailFacts
            .GroupBy(fact => GroupIdentity(query.GroupBy, fact))
            .Select(group => new CommercialStatisticsRow(
                group.Key.Key,
                group.Key.Label,
                AggregateAmounts(group)))
            .ToArray();

        var sorted = SortGroups(grouped, query.Sort).ToArray();
        var totalGroupCount = sorted.Length;
        var page = sorted
            .Skip(Math.Max(0, query.Offset))
            .Take(Math.Clamp(query.Limit, 1, 500))
            .ToArray();

        return new CommercialStatisticsResult(
            summary,
            monthly,
            page,
            totalGroupCount,
            BuildDataQuality(facts));
    }

    public static string BuildConnectionString(IConfiguration configuration)
    {
        var host = configuration["FLOWSTOCK_PG_HOST"] ?? "127.0.0.1";
        var database = configuration["FLOWSTOCK_PG_DB"] ?? "flowstock";
        var user = configuration["FLOWSTOCK_PG_USER"] ?? "flowstock";
        var password = configuration["FLOWSTOCK_PG_PASSWORD"] ?? "flowstock";
        var port = int.TryParse(
            configuration["FLOWSTOCK_PG_PORT"],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsedPort)
            ? parsedPort
            : 5432;

        return new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = database,
            Username = user,
            Password = password
        }.ConnectionString;
    }

    private static string BuildFactsSql(CommercialStatisticsQuery query)
    {
        var filters = new List<string>();
        var gtins = NormalizeGtins(query.Gtins);
        var volumes = NormalizeTextValues(query.Volumes);

        if (query.PartnerId.HasValue)
        {
            filters.Add(query.Mode == CommercialStatisticsMode.Orders
                ? "o.partner_id = @partner_id"
                : "d.partner_id = @partner_id");
        }
        if (query.ItemId.HasValue)
        {
            filters.Add(query.Mode == CommercialStatisticsMode.Orders
                ? "ol.item_id = @item_id"
                : "dl.item_id = @item_id");
        }
        if (!string.IsNullOrWhiteSpace(query.Gtin) && gtins.Length > 0)
        {
            filters.Add("(COALESCE(i.gtin, '') = @gtin OR COALESCE(i.gtin, '') = ANY(@gtins))");
        }
        else if (!string.IsNullOrWhiteSpace(query.Gtin))
        {
            filters.Add("COALESCE(i.gtin, '') = @gtin");
        }
        else if (gtins.Length > 0)
        {
            filters.Add("COALESCE(i.gtin, '') = ANY(@gtins)");
        }
        if (!string.IsNullOrWhiteSpace(query.ItemNameContains))
        {
            filters.Add("STRPOS(LOWER(COALESCE(i.name, '')), LOWER(@item_name_contains)) > 0");
        }
        if (!string.IsNullOrWhiteSpace(query.Brand))
        {
            filters.Add("COALESCE(i.brand, '') ILIKE @brand");
        }
        if (!string.IsNullOrWhiteSpace(query.Volume) && volumes.Length > 0)
        {
            filters.Add("(COALESCE(i.volume, '') ILIKE @volume OR LOWER(BTRIM(COALESCE(i.volume, ''))) = ANY(@volumes))");
        }
        else if (!string.IsNullOrWhiteSpace(query.Volume))
        {
            filters.Add("COALESCE(i.volume, '') ILIKE @volume");
        }
        else if (volumes.Length > 0)
        {
            filters.Add("LOWER(BTRIM(COALESCE(i.volume, ''))) = ANY(@volumes)");
        }

        var extraFilters = filters.Count == 0
            ? string.Empty
            : $"\n  AND {string.Join("\n  AND ", filters)}";

        var facts = query.Mode == CommercialStatisticsMode.Orders
            ? $@"
SELECT o.created_at AS fact_date,
       o.id AS order_id,
       NULL::bigint AS doc_id,
       o.partner_id,
       CASE
           WHEN o.partner_id IS NULL THEN 'Не указано'
           WHEN NULLIF(BTRIM(p.code), '') IS NOT NULL THEN p.code || ' — ' || p.name
           ELSE p.name
       END AS partner_label,
       ol.item_id,
       i.name AS item_label,
       i.gtin,
       i.brand,
       i.volume,
       ol.qty_ordered AS source_qty,
       ol.unit_price_gross,
       ol.vat_rate,
       TRUE AS is_linked,
       FALSE AS item_mismatch
FROM orders o
INNER JOIN order_lines ol ON ol.order_id = o.id
INNER JOIN items i ON i.id = ol.item_id
LEFT JOIN partners p ON p.id = o.partner_id
WHERE o.order_type = 'CUSTOMER'
  AND o.status = ANY(@statuses)
  AND o.status NOT IN ('CANCELLED', 'MERGED')
  AND ol.cancelled_at IS NULL
  AND o.created_at >= @from_date
  AND o.created_at < @to_date{extraFilters}"
            : $@"
SELECT d.closed_at AS fact_date,
       d.order_id,
       d.id AS doc_id,
       d.partner_id,
       CASE
           WHEN d.partner_id IS NULL THEN 'Не указано'
           WHEN NULLIF(BTRIM(p.code), '') IS NOT NULL THEN p.code || ' — ' || p.name
           ELSE p.name
       END AS partner_label,
       dl.item_id,
       i.name AS item_label,
       i.gtin,
       i.brand,
       i.volume,
       dl.qty AS source_qty,
       CASE WHEN ol.id IS NOT NULL AND ol.item_id = dl.item_id THEN ol.unit_price_gross END AS unit_price_gross,
       CASE WHEN ol.id IS NOT NULL AND ol.item_id = dl.item_id THEN ol.vat_rate END AS vat_rate,
       ol.id IS NOT NULL AS is_linked,
       ol.id IS NOT NULL AND ol.item_id <> dl.item_id AS item_mismatch
FROM docs d
INNER JOIN orders o ON o.id = d.order_id AND o.order_type = 'CUSTOMER'
INNER JOIN doc_lines dl ON dl.doc_id = d.id
INNER JOIN items i ON i.id = dl.item_id
LEFT JOIN order_lines ol ON ol.id = dl.order_line_id
LEFT JOIN partners p ON p.id = d.partner_id
WHERE d.type = 'OUTBOUND'
  AND d.status = 'CLOSED'
  AND d.closed_at IS NOT NULL
  AND dl.qty > @qty_tolerance
  AND d.closed_at >= @from_date
  AND d.closed_at < @to_date
  AND NOT EXISTS (
      SELECT 1
      FROM doc_lines newer
      WHERE newer.replaces_line_id = dl.id
  ){extraFilters}";

        return $@"
WITH facts AS (
{facts}
),
calculated AS (
    SELECT *,
           ROUND(source_qty::numeric, 6) AS quantity,
           CASE
               WHEN is_linked AND NOT item_mismatch
                    AND unit_price_gross IS NOT NULL AND vat_rate IS NOT NULL
               THEN ROUND(ROUND(source_qty::numeric, 6) * unit_price_gross, 2)
           END AS gross
    FROM facts
),
financial AS (
    SELECT *,
           CASE
               WHEN gross IS NOT NULL AND vat_rate = 0 THEN 0::numeric
               WHEN gross IS NOT NULL THEN ROUND(gross * vat_rate / (100 + vat_rate), 2)
           END AS vat
    FROM calculated
)
SELECT fact_date,
       order_id,
       doc_id,
       partner_id,
       partner_label,
       item_id,
       item_label,
       gtin,
       brand,
       volume,
       quantity,
       gross,
       vat,
       unit_price_gross,
       vat_rate,
       is_linked,
       item_mismatch
FROM financial;";
    }

    private static void AddParameters(NpgsqlCommand command, CommercialStatisticsQuery query)
    {
        command.Parameters.AddWithValue("@from_date", query.From.ToString("s", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@to_date", query.ToExclusive.ToString("s", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@qty_tolerance", 0.000001d);

        if (query.Mode == CommercialStatisticsMode.Orders)
        {
            command.Parameters.AddWithValue(
                "@statuses",
                query.Statuses.Select(OrderStatusMapper.StatusToString).ToArray());
        }
        if (query.PartnerId.HasValue)
        {
            command.Parameters.AddWithValue("@partner_id", query.PartnerId.Value);
        }
        if (query.ItemId.HasValue)
        {
            command.Parameters.AddWithValue("@item_id", query.ItemId.Value);
        }
        if (!string.IsNullOrWhiteSpace(query.Gtin))
        {
            command.Parameters.AddWithValue("@gtin", query.Gtin.Trim());
        }

        var gtins = NormalizeGtins(query.Gtins);
        if (gtins.Length > 0)
        {
            command.Parameters.Add("@gtins", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = gtins;
        }
        if (!string.IsNullOrWhiteSpace(query.ItemNameContains))
        {
            command.Parameters.AddWithValue("@item_name_contains", query.ItemNameContains.Trim());
        }
        if (!string.IsNullOrWhiteSpace(query.Brand))
        {
            command.Parameters.AddWithValue("@brand", query.Brand.Trim());
        }
        if (!string.IsNullOrWhiteSpace(query.Volume))
        {
            command.Parameters.AddWithValue("@volume", query.Volume.Trim());
        }

        var volumes = NormalizeTextValues(query.Volumes);
        if (volumes.Length > 0)
        {
            command.Parameters.Add("@volumes", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = volumes;
        }
    }

    private static CommercialStatisticsAmounts AggregateAmounts(IEnumerable<Fact> source)
    {
        var facts = source as IReadOnlyCollection<Fact> ?? source.ToArray();
        return new CommercialStatisticsAmounts(
            OrderCount: facts.Select(fact => fact.OrderId).Distinct().Count(),
            DocumentCount: facts.Where(fact => fact.DocId.HasValue).Select(fact => fact.DocId!.Value).Distinct().Count(),
            FactCount: facts.Count,
            Quantity: facts.Sum(fact => fact.Quantity),
            KnownFinancialQuantity: facts.Where(fact => fact.Gross.HasValue).Sum(fact => fact.Quantity),
            Gross: facts.Sum(fact => fact.Gross ?? 0m),
            Net: facts.Sum(fact => fact.Gross.HasValue ? fact.Gross.Value - (fact.Vat ?? 0m) : 0m),
            Vat: facts.Sum(fact => fact.Vat ?? 0m));
    }

    private static CommercialStatisticsDataQuality BuildDataQuality(IReadOnlyCollection<Fact> facts)
    {
        var missingPrice = facts.Where(fact => fact.IsLinked && !fact.ItemMismatch && !fact.UnitPriceGross.HasValue).ToArray();
        var missingVat = facts.Where(fact => fact.IsLinked && !fact.ItemMismatch && !fact.VatRate.HasValue).ToArray();
        var incomplete = facts.Where(fact =>
            !fact.IsLinked
            || fact.ItemMismatch
            || !fact.UnitPriceGross.HasValue
            || !fact.VatRate.HasValue).ToArray();
        var unlinked = facts.Where(fact => !fact.IsLinked).ToArray();
        var mismatch = facts.Where(fact => fact.ItemMismatch).ToArray();

        return new CommercialStatisticsDataQuality(
            missingPrice.Length,
            missingPrice.Sum(fact => fact.Quantity),
            missingVat.Length,
            missingVat.Sum(fact => fact.Quantity),
            incomplete.Length,
            incomplete.Sum(fact => fact.Quantity),
            unlinked.Length,
            unlinked.Sum(fact => fact.Quantity),
            mismatch.Length,
            mismatch.Sum(fact => fact.Quantity));
    }

    private static IEnumerable<CommercialStatisticsRow> SortGroups(
        IEnumerable<CommercialStatisticsRow> rows,
        string sort) =>
        sort switch
        {
            "quantity_desc" => rows
                .OrderByDescending(row => row.Amounts.Quantity)
                .ThenBy(row => row.Label, StringComparer.OrdinalIgnoreCase),
            "name_asc" => rows
                .OrderBy(row => row.Label, StringComparer.OrdinalIgnoreCase),
            _ => rows
                .OrderByDescending(row => row.Amounts.Gross)
                .ThenBy(row => row.Label, StringComparer.OrdinalIgnoreCase)
        };

    private static (string? Key, string Label) GroupIdentity(
        CommercialStatisticsGroupBy groupBy,
        Fact fact) =>
        groupBy switch
        {
            CommercialStatisticsGroupBy.Partner =>
                (fact.PartnerId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    string.IsNullOrWhiteSpace(fact.PartnerLabel) ? "Не указано" : fact.PartnerLabel),
            CommercialStatisticsGroupBy.Item =>
                (fact.ItemId.ToString(CultureInfo.InvariantCulture),
                    string.IsNullOrWhiteSpace(fact.ItemLabel) ? "Не указано" : fact.ItemLabel),
            CommercialStatisticsGroupBy.Gtin => TextIdentity(fact.Gtin),
            CommercialStatisticsGroupBy.Brand => TextIdentity(fact.Brand),
            CommercialStatisticsGroupBy.Volume => TextIdentity(fact.Volume),
            _ => throw new ArgumentOutOfRangeException(nameof(groupBy), groupBy, null)
        };

    private static (string? Key, string Label) TextIdentity(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        return (normalized, string.IsNullOrEmpty(normalized) ? "Не указано" : normalized);
    }

    private static string MonthKey(string factDate) =>
        factDate.Length >= 7 ? factDate[..7] : factDate;

    private static string[] NormalizeGtins(IReadOnlyList<string>? values) =>
        (values ?? Array.Empty<string>())
            .Select(value => string.Concat((value ?? string.Empty).Where(character => !char.IsWhiteSpace(character))))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string[] NormalizeTextValues(IReadOnlyList<string>? values) =>
        (values ?? Array.Empty<string>())
            .Select(value => (value ?? string.Empty).Trim().ToLowerInvariant())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private sealed record Fact(
        string FactDate,
        long OrderId,
        long? DocId,
        long? PartnerId,
        string PartnerLabel,
        long ItemId,
        string ItemLabel,
        string? Gtin,
        string? Brand,
        string? Volume,
        decimal Quantity,
        decimal? Gross,
        decimal? Vat,
        decimal? UnitPriceGross,
        decimal? VatRate,
        bool IsLinked,
        bool ItemMismatch);
}
