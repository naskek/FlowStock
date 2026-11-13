-- Исторические события не изменяются; payload отсутствует, пока snapshot не был записан.
ALTER TABLE business_notifications ADD COLUMN IF NOT EXISTS payload JSONB NULL;
ALTER TABLE orders ADD COLUMN IF NOT EXISTS notification_revision BIGINT NOT NULL DEFAULT 0;

-- Группируем только доказанную пару внутри одной транзакции и одного заказа.
-- Если в транзакции проведено несколько отгрузок заказа, неоднозначная связь не группируется.
CREATE OR REPLACE VIEW business_notification_journal_members AS
SELECT n.*,
       CASE WHEN n.event_type = 'OUTBOUND_CLOSED' AND n.payload->>'version' = '1'
             AND n.payload->>'order_id' IS NOT NULL
             AND (SELECT COUNT(*) FROM business_notifications d
                  WHERE d.event_type = 'OUTBOUND_CLOSED' AND d.payload->>'version' = '1'
                    AND d.payload->>'correlation_id' = n.payload->>'correlation_id'
                    AND d.payload->>'order_id' = n.payload->>'order_id') = 1
             AND (SELECT COUNT(*) FROM business_notifications s
                  WHERE s.event_type IN ('CUSTOMER_ORDER_SHIPPED','CUSTOMER_ORDER_PARTIALLY_SHIPPED')
                    AND s.payload->>'version' = '1'
                    AND s.payload->>'correlation_id' = n.payload->>'correlation_id'
                    AND s.payload->>'order_id' = n.payload->>'order_id') = 1
            THEN COALESCE((SELECT s.id FROM business_notifications s
                           WHERE s.event_type IN ('CUSTOMER_ORDER_SHIPPED','CUSTOMER_ORDER_PARTIALLY_SHIPPED')
                             AND s.payload->>'version' = '1'
                             AND s.payload->>'correlation_id' = n.payload->>'correlation_id'
                             AND s.payload->>'order_id' = n.payload->>'order_id'
                           ORDER BY s.id DESC LIMIT 1), n.id)
            ELSE n.id END AS journal_id
FROM business_notifications n;

CREATE INDEX IF NOT EXISTS ix_business_notifications_correlation
    ON business_notifications ((payload->>'correlation_id'), (payload->>'order_id'))
    WHERE payload IS NOT NULL;
