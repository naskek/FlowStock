BEGIN;

ALTER TABLE IF EXISTS item_types
    ALTER COLUMN is_visible_in_product_catalog SET DEFAULT TRUE;

UPDATE item_types
SET is_visible_in_product_catalog = TRUE
WHERE is_visible_in_product_catalog = FALSE
  AND (
      name = 'Без типа'
      OR code = 'GENERAL'
  );

COMMIT;
