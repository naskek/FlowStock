"""add handling units and sscc sequence

Revision ID: 20251223_0003
Revises: 20251216_0002
Create Date: 2025-12-23
"""

from alembic import op
import sqlalchemy as sa
from sqlalchemy.dialects import postgresql as pg


revision = "20251223_0003"
down_revision = "20251216_0002"
branch_labels = None
depends_on = None


def upgrade():
    # Ensure enum types exist and recreate cleanly if a previous failed attempt left them behind
    if op.get_bind().dialect.name == "postgresql":
        op.execute(
            """
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_type WHERE typname = 'handlingunittype') THEN
                    DROP TYPE handlingunittype;
                END IF;
                IF EXISTS (SELECT 1 FROM pg_type WHERE typname = 'handlingunitstatus') THEN
                    DROP TYPE handlingunitstatus;
                END IF;
                CREATE TYPE handlingunittype AS ENUM ('pallet');
                CREATE TYPE handlingunitstatus AS ENUM ('created','putaway','reserved','shipped','quarantine');
            END $$;
            """
        )

    hu_status = pg.ENUM(
        "created",
        "putaway",
        "reserved",
        "shipped",
        "quarantine",
        name="handlingunitstatus",
        create_type=False,
    )
    hu_type = pg.ENUM("pallet", name="handlingunittype", create_type=False)

    op.create_table(
        "handling_units",
        sa.Column("id", sa.Integer(), primary_key=True, nullable=False),
        sa.Column("sscc", sa.String(length=18), nullable=False),
        sa.Column("type", hu_type, nullable=False, server_default="pallet"),
        sa.Column("status", hu_status, nullable=False, server_default="created"),
        sa.Column("location_id", sa.Integer(), sa.ForeignKey("locations.id"), nullable=True),
        sa.Column("source_doc_id", sa.Integer(), sa.ForeignKey("documents.id"), nullable=True),
        sa.Column("reserved_doc_id", sa.Integer(), sa.ForeignKey("documents.id"), nullable=True),
        sa.Column("created_at", sa.DateTime(), nullable=False, server_default=sa.func.now()),
        sa.UniqueConstraint("sscc"),
    )
    op.create_index("ix_handling_units_sscc", "handling_units", ["sscc"], unique=False)

    op.create_table(
        "handling_unit_contents",
        sa.Column("id", sa.Integer(), primary_key=True, nullable=False),
        sa.Column("hu_id", sa.Integer(), sa.ForeignKey("handling_units.id"), nullable=False),
        sa.Column("product_id", sa.Integer(), sa.ForeignKey("products.id"), nullable=False),
        sa.Column("qty", sa.Numeric(scale=2), nullable=False, server_default="0"),
        sa.Column("batch", sa.String(length=64), nullable=True),
        sa.Column("expiry_date", sa.Date(), nullable=True),
        sa.UniqueConstraint("hu_id", "product_id", "batch", "expiry_date", name="uq_handling_unit_content"),
    )

    op.create_table(
        "sscc_sequences",
        sa.Column("id", sa.Integer(), primary_key=True, nullable=False),
        sa.Column("company_prefix", sa.String(length=32), nullable=False),
        sa.Column("extension_digit", sa.String(length=1), nullable=False),
        sa.Column("serial_length", sa.Integer(), nullable=False),
        sa.Column("next_serial", sa.Integer(), nullable=False, server_default="1"),
        sa.Column("updated_at", sa.DateTime(), nullable=False, server_default=sa.func.now()),
        sa.UniqueConstraint("company_prefix"),
    )


def downgrade():
    op.drop_table("sscc_sequences")
    op.drop_table("handling_unit_contents")
    op.drop_index("ix_handling_units_sscc", table_name="handling_units")
    op.drop_table("handling_units")
    op.execute("DROP TYPE IF EXISTS handlingunittype")
    op.execute("DROP TYPE IF EXISTS handlingunitstatus")
