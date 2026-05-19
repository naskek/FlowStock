-- Warehouse Task Board: hide TSD menu entry by default (experimental module).
UPDATE client_blocks
SET is_enabled = FALSE,
    updated_at = TO_CHAR(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS')
WHERE block_key = 'tsd_warehouse_tasks';
