"""add contacts and counterparty to documents

Revision ID: 20251223_0006
Revises: 20251223_0005
Create Date: 2025-12-23
"""

from alembic import op
import sqlalchemy as sa


revision = "20251223_0006"
down_revision = "20251223_0005"
branch_labels = None
depends_on = None


def upgrade():
    bind = op.get_bind()
    is_pg = bind.dialect.name == "postgresql"
    if is_pg:
        contact_type = sa.dialects.postgresql.ENUM(
            "supplier", "customer", "both", name="contacttype", create_type=False
        )
    else:
        contact_type = sa.Enum("supplier", "customer", "both", name="contacttype", create_type=False)
    if is_pg:
        exists = bind.execute(sa.text("SELECT 1 FROM pg_type WHERE typname = 'contacttype'")).scalar()
        if not exists:
            bind.execute(sa.text("CREATE TYPE contacttype AS ENUM ('supplier', 'customer', 'both')"))
    else:
        # non-PG backends (sqlite) don't have named enums; safe to let SQLAlchemy manage constraints
        contact_type.create(bind, checkfirst=True)

    op.create_table(
        "contacts",
        sa.Column("id", sa.Integer(), nullable=False),
        sa.Column("name", sa.String(length=255), nullable=False),
        sa.Column("type", contact_type, nullable=False),
        sa.Column("phone", sa.String(length=64), nullable=True),
        sa.Column("email", sa.String(length=255), nullable=True),
        sa.Column("note", sa.Text(), nullable=True),
        sa.Column("is_active", sa.Boolean(), nullable=False, server_default=sa.text("true")),
        sa.PrimaryKeyConstraint("id"),
    )

    op.add_column("documents", sa.Column("counterparty_id", sa.Integer(), nullable=True))
    op.create_foreign_key(
        "fk_documents_counterparty",
        "documents",
        "contacts",
        ["counterparty_id"],
        ["id"],
    )
    op.create_index("idx_documents_counterparty", "documents", ["counterparty_id"], unique=False)


def downgrade():
    op.drop_index("idx_documents_counterparty", table_name="documents")
    op.drop_constraint("fk_documents_counterparty", "documents", type_="foreignkey")
    op.drop_column("documents", "counterparty_id")

    op.drop_table("contacts")
    bind = op.get_bind()
    if bind.dialect.name == "postgresql":
        bind.execute(sa.text("DROP TYPE IF EXISTS contacttype"))
