"""set counterparty fk on delete set null

Revision ID: 20251224_0007
Revises: 20251223_0006
Create Date: 2025-12-24
"""

from alembic import op
import sqlalchemy as sa


revision = "20251224_0007"
down_revision = "20251223_0006"
branch_labels = None
depends_on = None


def upgrade():
    with op.batch_alter_table("documents") as batch_op:
        batch_op.drop_constraint("fk_documents_counterparty", type_="foreignkey")
        batch_op.create_foreign_key(
            "fk_documents_counterparty",
            "contacts",
            ["counterparty_id"],
            ["id"],
            ondelete="SET NULL",
        )


def downgrade():
    with op.batch_alter_table("documents") as batch_op:
        batch_op.drop_constraint("fk_documents_counterparty", type_="foreignkey")
        batch_op.create_foreign_key(
            "fk_documents_counterparty",
            "contacts",
            ["counterparty_id"],
            ["id"],
        )
