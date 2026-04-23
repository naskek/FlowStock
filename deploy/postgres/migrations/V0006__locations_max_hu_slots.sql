ALTER TABLE locations
ADD COLUMN IF NOT EXISTS max_hu_slots INTEGER;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ck_locations_max_hu_slots_positive'
    ) THEN
        ALTER TABLE locations
        ADD CONSTRAINT ck_locations_max_hu_slots_positive
        CHECK (max_hu_slots IS NULL OR max_hu_slots > 0);
    END IF;
END$$;
