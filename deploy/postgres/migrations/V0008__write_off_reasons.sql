CREATE TABLE IF NOT EXISTS write_off_reasons (
    id BIGSERIAL PRIMARY KEY,
    code TEXT NOT NULL,
    name TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_write_off_reasons_code ON write_off_reasons(code);

INSERT INTO write_off_reasons(code, name)
SELECT 'DAMAGED', 'Повреждено'
WHERE NOT EXISTS (SELECT 1 FROM write_off_reasons WHERE code = 'DAMAGED');

INSERT INTO write_off_reasons(code, name)
SELECT 'EXPIRED', 'Просрочено'
WHERE NOT EXISTS (SELECT 1 FROM write_off_reasons WHERE code = 'EXPIRED');

INSERT INTO write_off_reasons(code, name)
SELECT 'DEFECT', 'Брак'
WHERE NOT EXISTS (SELECT 1 FROM write_off_reasons WHERE code = 'DEFECT');

INSERT INTO write_off_reasons(code, name)
SELECT 'SAMPLE', 'Проба'
WHERE NOT EXISTS (SELECT 1 FROM write_off_reasons WHERE code = 'SAMPLE');

INSERT INTO write_off_reasons(code, name)
SELECT 'PRODUCTION', 'Производство'
WHERE NOT EXISTS (SELECT 1 FROM write_off_reasons WHERE code = 'PRODUCTION');

INSERT INTO write_off_reasons(code, name)
SELECT 'OTHER', 'Прочее'
WHERE NOT EXISTS (SELECT 1 FROM write_off_reasons WHERE code = 'OTHER');
