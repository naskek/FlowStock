"""add production doc types

Revision ID: 20251216_0002
Revises: 20251215_0001
Create Date: 2025-12-16
"""

from alembic import op

revision = "20251216_0002"
down_revision = "20251215_0001"
branch_labels = None
depends_on = None


def upgrade():
    op.execute("ALTER TYPE documenttype ADD VALUE IF NOT EXISTS 'production_issue'")
    op.execute("ALTER TYPE documenttype ADD VALUE IF NOT EXISTS 'production_receipt'")


def downgrade():
    # невозможно безопасно удалить значения из ENUM в Postgres без пересоздания типа
    pass
