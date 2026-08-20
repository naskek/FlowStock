ALTER TABLE partners
    ADD COLUMN IF NOT EXISTS partner_role TEXT NULL;

ALTER TABLE partners
    DROP CONSTRAINT IF EXISTS ck_partners_partner_role;

ALTER TABLE partners
    ADD CONSTRAINT ck_partners_partner_role
    CHECK (partner_role IS NULL OR partner_role IN ('SUPPLIER', 'CLIENT', 'BOTH'));
