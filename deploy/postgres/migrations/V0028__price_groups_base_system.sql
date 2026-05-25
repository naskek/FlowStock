ALTER TABLE price_groups
    ADD COLUMN IF NOT EXISTS is_system BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS default_discount_percent NUMERIC(7,4) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS default_markup_percent NUMERIC(7,4) NOT NULL DEFAULT 0;

UPDATE price_groups
SET is_system = TRUE,
    is_default = TRUE,
    is_active = TRUE,
    name = 'Базовая цена'
WHERE is_system = FALSE
  AND (is_default = TRUE OR name ILIKE 'базов%');

INSERT INTO price_groups(name, description, currency, vat_mode, is_default, is_active, is_system, default_discount_percent, default_markup_percent)
SELECT 'Базовая цена', 'Системная базовая группа цен', 'RUB', 'INCLUDED', TRUE, TRUE, TRUE, 0, 0
WHERE NOT EXISTS (SELECT 1 FROM price_groups WHERE is_system = TRUE);

UPDATE price_groups
SET is_default = FALSE
WHERE is_system = FALSE AND is_default = TRUE;

UPDATE price_groups
SET is_default = TRUE,
    is_active = TRUE,
    name = 'Базовая цена'
WHERE is_system = TRUE;

CREATE UNIQUE INDEX IF NOT EXISTS ux_price_groups_single_system
    ON price_groups (is_system)
    WHERE is_system = TRUE;

CREATE UNIQUE INDEX IF NOT EXISTS ux_price_groups_single_default
    ON price_groups (is_default)
    WHERE is_default = TRUE;
