using System.Text.Json;
using FlowStock.Core.Models;
using NpgsqlTypes;

namespace FlowStock.Data;

public sealed partial class PostgresDataStore
{
    private static readonly JsonSerializerOptions NotificationJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public BusinessNotificationPage GetBusinessNotificationPage(BusinessNotificationQuery query, string readerKey)
    {
        return WithConnection(connection =>
        {
            // One statement gives items, filtered total and global unread count the same MVCC snapshot.
            using var command = CreateCommand(connection, @"
WITH entries AS (
    SELECT n.id, n.event_type, n.severity, n.title, n.message, n.entity_type, n.entity_id,
           n.entity_ref, n.created_at, n.source, n.dedupe_key,
           n.payload || COALESCE((
               SELECT jsonb_build_object('doc_id', d.payload->'doc_id', 'doc_ref', d.payload->'doc_ref')
               FROM business_notification_journal_members d
               WHERE d.journal_id = n.id AND d.id <> n.id AND d.payload->>'doc_id' IS NOT NULL
               ORDER BY d.id DESC LIMIT 1), '{}'::jsonb) AS payload,
           NOT EXISTS (
               SELECT 1 FROM business_notification_journal_members m
               LEFT JOIN business_notification_reads r ON r.notification_id = m.id AND r.reader_key = @reader
               WHERE m.journal_id = n.id AND r.notification_id IS NULL) AS is_read,
           CASE WHEN n.severity = 'ERROR' THEN 'ERROR'
                WHEN n.event_type = 'CUSTOMER_ORDER_READY' THEN 'READY'
                WHEN n.event_type IN ('OUTBOUND_CLOSED','CUSTOMER_ORDER_SHIPPED','CUSTOMER_ORDER_PARTIALLY_SHIPPED') THEN 'SHIPMENT'
                WHEN n.event_type = 'INTERNAL_ORDER_COMPLETED' THEN 'PRODUCTION'
                ELSE 'OTHER' END AS category
    FROM business_notification_journal_members n WHERE n.id = n.journal_id
), filtered AS (
    SELECT * FROM entries
    WHERE (NOT @unread OR NOT is_read)
      AND (@category IS NULL OR category = @category)
      AND (@from IS NULL OR created_at >= @from)
      AND (@to IS NULL OR created_at < @to)
      AND (@search = '' OR strpos(lower(concat_ws(' ', entity_ref, title,
                  payload->>'order_ref', payload->>'partner_name', payload->>'doc_ref')), lower(@search)) > 0)
), page AS (
    SELECT * FROM filtered ORDER BY created_at DESC, id DESC LIMIT @limit OFFSET @offset
)
SELECT jsonb_build_object(
    'items', COALESCE((SELECT jsonb_agg(to_jsonb(p) ORDER BY p.created_at DESC, p.id DESC) FROM page p), '[]'::jsonb),
    'total', (SELECT COUNT(*) FROM filtered),
    'unread_count', (SELECT COUNT(*) FROM entries WHERE NOT is_read));");
            command.Parameters.AddWithValue("@reader", readerKey);
            command.Parameters.AddWithValue("@unread", query.UnreadOnly);
            command.Parameters.AddWithValue("@limit", Math.Clamp(query.Limit, 1, 100));
            command.Parameters.AddWithValue("@offset", Math.Max(0, query.Offset));
            command.Parameters.AddWithValue("@search", query.Search?.Trim() ?? "");
            command.Parameters.Add("@category", NpgsqlDbType.Text).Value = (object?)query.Category ?? DBNull.Value;
            command.Parameters.Add("@from", NpgsqlDbType.TimestampTz).Value = (object?)query.FromUtc ?? DBNull.Value;
            command.Parameters.Add("@to", NpgsqlDbType.TimestampTz).Value = (object?)query.ToUtc ?? DBNull.Value;
            var json = (string)command.ExecuteScalar()!;
            return JsonSerializer.Deserialize<BusinessNotificationPage>(json, NotificationJsonOptions)!;
        });
    }
}
