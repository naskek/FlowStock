using System.Globalization;
using FlowStock.Core.Models;
using Npgsql;
using NpgsqlTypes;

namespace FlowStock.Server;

internal sealed class CommercialStatisticsFilterOptionsReader
{
    private readonly string _connectionString;

    public CommercialStatisticsFilterOptionsReader(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("PostgreSQL connection string is required.", nameof(connectionString))
            : connectionString;
    }

    public IReadOnlyList<long> GetAvailableItemIds(
        CommercialStatisticsMode mode,
        DateTime from,
        DateTime toExclusive,
        long partnerId,
        IReadOnlyList<OrderStatus> statuses)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = mode == CommercialStatisticsMode.Orders
            ? @"
SELECT DISTINCT ol.item_id
FROM orders o
INNER JOIN order_lines ol ON ol.order_id = o.id
WHERE o.order_type = 'CUSTOMER'
  AND o.partner_id = @partner_id
  AND o.status = ANY(@statuses)
  AND o.status NOT IN ('CANCELLED', 'MERGED')
  AND ol.cancelled_at IS NULL
  AND o.created_at >= @from_date
  AND o.created_at < @to_date
ORDER BY ol.item_id;"
            : @"
SELECT DISTINCT dl.item_id
FROM docs d
INNER JOIN orders o ON o.id = d.order_id AND o.order_type = 'CUSTOMER'
INNER JOIN doc_lines dl ON dl.doc_id = d.id
WHERE d.type = 'OUTBOUND'
  AND d.status = 'CLOSED'
  AND d.closed_at IS NOT NULL
  AND d.partner_id = @partner_id
  AND dl.qty > @qty_tolerance
  AND d.closed_at >= @from_date
  AND d.closed_at < @to_date
  AND NOT EXISTS (
      SELECT 1
      FROM doc_lines newer
      WHERE newer.replaces_line_id = dl.id
  )
ORDER BY dl.item_id;";

        command.Parameters.AddWithValue("@partner_id", partnerId);
        command.Parameters.AddWithValue("@from_date", from.ToString("s", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@to_date", toExclusive.ToString("s", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@qty_tolerance", 0.000001d);
        if (mode == CommercialStatisticsMode.Orders)
        {
            command.Parameters.Add("@statuses", NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                statuses.Select(OrderStatusMapper.StatusToString).ToArray();
        }

        using var reader = command.ExecuteReader();
        var result = new List<long>();
        while (reader.Read())
        {
            result.Add(reader.GetInt64(0));
        }

        return result;
    }
}
