"""add packing profiles

Revision ID: 20251223_0004
Revises: 20251223_0003
Create Date: 2025-12-23
"""

from alembic import op
import sqlalchemy as sa


revision = "20251223_0004"
down_revision = "20251223_0003"
branch_labels = None
depends_on = None


def upgrade():
    op.create_table(
        "packing_profiles",
        sa.Column("id", sa.Integer(), primary_key=True, nullable=False),
        sa.Column("product_id", sa.Integer(), sa.ForeignKey("products.id"), nullable=False),
        sa.Column("pack_type", sa.String(length=64), nullable=False),
        sa.Column("qty_per_pack", sa.Numeric(scale=3), nullable=False),
        sa.Column("is_active", sa.Boolean(), nullable=False, server_default=sa.text("true")),
        sa.UniqueConstraint("product_id", "pack_type", name="uq_packing_profile_product_type"),
    )


def downgrade():
    op.drop_table("packing_profiles")
