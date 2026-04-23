ALTER TABLE IF EXISTS locations
ADD COLUMN IF NOT EXISTS auto_hu_distribution_enabled BOOLEAN NOT NULL DEFAULT TRUE;

UPDATE locations
SET auto_hu_distribution_enabled = TRUE
WHERE auto_hu_distribution_enabled IS NULL;
