"""adjust stock index to include batch and expiry

Revision ID: 20251223_0005
Revises: 20251223_0004
Create Date: 2025-12-23
"""

from alembic import op
import sqlalchemy as sa


revision = "20251223_0005"
down_revision = "20251223_0004"
branch_labels = None
depends_on = None


def upgrade():
    op.drop_index("idx_stock_product_location", table_name="stock")
    op.create_index(
        "idx_stock_product_location_batch_expiry",
        "stock",
        ["product_id", "location_id", "batch", "expiry_date"],
        unique=True,
    )


def downgrade():
    op.drop_index("idx_stock_product_location_batch_expiry", table_name="stock")
    op.create_index("idx_stock_product_location", "stock", ["product_id", "location_id"], unique=True)
