BEGIN;

CREATE INDEX IF NOT EXISTS ix_orders_status_due_created_id
    ON orders(status, due_date, created_at DESC, id DESC);

CREATE INDEX IF NOT EXISTS ix_orders_type_status_due_created_id
    ON orders(order_type, status, due_date, created_at DESC, id DESC);

CREATE INDEX IF NOT EXISTS ix_order_lines_order_item_id
    ON order_lines(order_id, item_id, id);

CREATE INDEX IF NOT EXISTS ix_docs_order_type_status_closed
    ON docs(order_id, type, status, closed_at);

CREATE INDEX IF NOT EXISTS ix_docs_type_status_order
    ON docs(type, status, order_id);

CREATE INDEX IF NOT EXISTS ix_doc_lines_order_line_doc_item_positive
    ON doc_lines(order_line_id, doc_id, item_id)
    WHERE qty > 0;

CREATE INDEX IF NOT EXISTS ix_doc_lines_doc_item_unlinked_positive
    ON doc_lines(doc_id, item_id)
    WHERE order_line_id IS NULL AND qty > 0;

COMMIT;
