ALTER TABLE production_pallets
    ALTER COLUMN prd_doc_id DROP NOT NULL,
    ALTER COLUMN doc_line_id DROP NOT NULL;

ALTER TABLE production_pallet_lines
    ALTER COLUMN doc_line_id DROP NOT NULL;
