ALTER TABLE IF EXISTS order_lines
    ADD COLUMN IF NOT EXISTS production_purpose TEXT;

UPDATE order_lines ol
SET production_purpose = CASE
    WHEN COALESCE(o.order_type, 'CUSTOMER') = 'CUSTOMER' THEN 'CUSTOMER_ORDER'
    ELSE 'INTERNAL_STOCK'
END
FROM orders o
WHERE o.id = ol.order_id
  AND (ol.production_purpose IS NULL OR ol.production_purpose = '');

ALTER TABLE IF EXISTS order_lines
    ALTER COLUMN production_purpose SET DEFAULT 'INTERNAL_STOCK';

ALTER TABLE IF EXISTS order_lines
    ALTER COLUMN production_purpose SET NOT NULL;

ALTER TABLE IF EXISTS doc_lines
    ADD COLUMN IF NOT EXISTS production_purpose TEXT;

UPDATE doc_lines
SET production_purpose = CASE
    WHEN order_line_id IS NOT NULL THEN 'CUSTOMER_ORDER'
    ELSE 'INTERNAL_STOCK'
END
WHERE production_purpose IS NULL OR production_purpose = '';

ALTER TABLE IF EXISTS doc_lines
    ALTER COLUMN production_purpose SET DEFAULT 'INTERNAL_STOCK';

ALTER TABLE IF EXISTS doc_lines
    ALTER COLUMN production_purpose SET NOT NULL;
