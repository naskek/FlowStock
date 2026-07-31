ALTER TABLE orders
    ADD COLUMN IF NOT EXISTS allow_partial_outbound BOOLEAN NOT NULL DEFAULT FALSE;

UPDATE orders
SET allow_partial_outbound = FALSE
WHERE status IN ('SHIPPED', 'CANCELLED', 'MERGED')
  AND allow_partial_outbound IS DISTINCT FROM FALSE;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ck_orders_terminal_partial_outbound_false'
          AND conrelid = 'orders'::regclass
    ) THEN
        ALTER TABLE orders
            ADD CONSTRAINT ck_orders_terminal_partial_outbound_false
            CHECK (
                status NOT IN ('SHIPPED', 'CANCELLED', 'MERGED')
                OR allow_partial_outbound = FALSE
            );
    END IF;
END
$$;
