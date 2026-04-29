BEGIN;

ALTER TABLE IF EXISTS orders
    ADD COLUMN IF NOT EXISTS bind_reserved_stock BOOLEAN;

UPDATE orders o
SET bind_reserved_stock = TRUE
WHERE (o.bind_reserved_stock IS NULL OR o.bind_reserved_stock = FALSE)
  AND COALESCE(o.order_type, 'CUSTOMER') = 'CUSTOMER'
  AND EXISTS (
      SELECT 1
      FROM order_receipt_plan_lines p
      WHERE p.order_id = o.id
        AND p.qty_planned > 0
        AND p.to_hu IS NOT NULL
        AND p.to_hu <> ''
  );

UPDATE orders
SET bind_reserved_stock = FALSE
WHERE bind_reserved_stock IS NULL;

ALTER TABLE IF EXISTS orders
    ALTER COLUMN bind_reserved_stock SET DEFAULT FALSE;

ALTER TABLE IF EXISTS orders
    ALTER COLUMN bind_reserved_stock SET NOT NULL;

COMMIT;
