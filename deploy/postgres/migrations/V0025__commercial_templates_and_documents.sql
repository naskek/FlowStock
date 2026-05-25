CREATE TABLE IF NOT EXISTS commercial_templates (
    id BIGSERIAL PRIMARY KEY,
    name TEXT NOT NULL,
    template_type TEXT NOT NULL,
    source_format TEXT NOT NULL DEFAULT 'DOCX',
    file_path TEXT NOT NULL,
    file_hash TEXT,
    version_no INTEGER NOT NULL DEFAULT 1,
    is_default BOOLEAN NOT NULL DEFAULT FALSE,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_commercial_templates_type ON commercial_templates(template_type);

CREATE TABLE IF NOT EXISTS generated_documents (
    id BIGSERIAL PRIMARY KEY,
    template_id BIGINT REFERENCES commercial_templates(id),
    source_type TEXT NOT NULL,
    source_id BIGINT NOT NULL,
    output_format TEXT NOT NULL,
    file_path TEXT NOT NULL,
    file_hash TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_generated_documents_source ON generated_documents(source_type, source_id);

CREATE TABLE IF NOT EXISTS price_tag_batches (
    id BIGSERIAL PRIMARY KEY,
    price_group_id BIGINT NOT NULL REFERENCES price_groups(id),
    template_id BIGINT REFERENCES commercial_templates(id),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    comment TEXT
);

CREATE TABLE IF NOT EXISTS price_tag_batch_lines (
    id BIGSERIAL PRIMARY KEY,
    batch_id BIGINT NOT NULL REFERENCES price_tag_batches(id) ON DELETE CASCADE,
    item_id BIGINT NOT NULL REFERENCES items(id),
    copies INTEGER NOT NULL DEFAULT 1,
    price NUMERIC(18,4) NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_price_tag_batch_lines_batch ON price_tag_batch_lines(batch_id);
