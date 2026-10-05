using FlowStock.Core.Models;
using Npgsql;
using NpgsqlTypes;

namespace FlowStock.Server;

public sealed class CommercialStatisticsDisplayLabels
{
    private readonly string _connectionString;

    public CommercialStatisticsDisplayLabels(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("PostgreSQL connection string is required.", nameof(connectionString))
            : connectionString;
    }

    public IReadOnlyList<CommercialStatisticsRow> Apply(
        CommercialStatisticsGroupBy groupBy,
        IReadOnlyList<CommercialStatisticsRow> rows)
    {
        if (rows.Count == 0)
        {
            return rows;
        }

        return groupBy switch
        {
            CommercialStatisticsGroupBy.Partner => ApplyPartnerNames(rows),
            CommercialStatisticsGroupBy.Item => ApplyItemLabels(rows),
            _ => rows
        };
    }

    private IReadOnlyList<CommercialStatisticsRow> ApplyPartnerNames(
        IReadOnlyList<CommercialStatisticsRow> rows)
    {
        var ids = ParseIds(rows);
        if (ids.Length == 0)
        {
            return rows;
        }

        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, name
FROM partners
WHERE id = ANY(@ids);";
        command.Parameters.Add("@ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value = ids;

        using var reader = command.ExecuteReader();
        var labels = new Dictionary<long, string>();
        while (reader.Read())
        {
            labels[reader.GetInt64(0)] = reader.IsDBNull(1) || string.IsNullOrWhiteSpace(reader.GetString(1))
                ? "Не указано"
                : reader.GetString(1).Trim();
        }

        return Rewrite(rows, labels);
    }

    private IReadOnlyList<CommercialStatisticsRow> ApplyItemLabels(
        IReadOnlyList<CommercialStatisticsRow> rows)
    {
        var ids = ParseIds(rows);
        if (ids.Length == 0)
        {
            return rows;
        }

        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, gtin, name
FROM items
WHERE id = ANY(@ids);";
        command.Parameters.Add("@ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value = ids;

        using var reader = command.ExecuteReader();
        var labels = new Dictionary<long, string>();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var gtin = reader.IsDBNull(1) ? null : reader.GetString(1)?.Trim();
            var name = reader.IsDBNull(2) ? null : reader.GetString(2)?.Trim();
            labels[id] = FormatItemLabel(gtin, name);
        }

        return Rewrite(rows, labels);
    }

    internal static string FormatItemLabel(string? gtin, string? name)
    {
        var normalizedGtin = string.IsNullOrWhiteSpace(gtin) ? null : gtin.Trim();
        var normalizedName = string.IsNullOrWhiteSpace(name) ? "Не указано" : name.Trim();
        return normalizedGtin is null
            ? normalizedName
            : $"{normalizedGtin} — {normalizedName}";
    }

    private static long[] ParseIds(IEnumerable<CommercialStatisticsRow> rows) =>
        rows.Select(row => long.TryParse(row.Key, out var id) && id > 0 ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToArray();

    private static IReadOnlyList<CommercialStatisticsRow> Rewrite(
        IReadOnlyList<CommercialStatisticsRow> rows,
        IReadOnlyDictionary<long, string> labels) =>
        rows.Select(row =>
        {
            if (!long.TryParse(row.Key, out var id) || !labels.TryGetValue(id, out var label))
            {
                return row;
            }

            return row with { Label = label };
        }).ToArray();
}
