ALTER TABLE production_pallet_lines
    ADD COLUMN IF NOT EXISTS filled_at TEXT NULL;

UPDATE production_pallet_lines pll
SET filled_at = pp.filled_at
FROM production_pallets pp
WHERE pp.id = pll.production_pallet_id
  AND pp.status = 'FILLED'
  AND pll.filled_qty >= pll.planned_qty
  AND pll.filled_at IS NULL
  AND pp.filled_at IS NOT NULL;
