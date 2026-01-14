from datetime import datetime
from fastapi import APIRouter, Depends, Query
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select
from ..models import AuditLog, User, UserRole
from ..schemas import AuditEntry
from ..deps import get_db, require_role


router = APIRouter()


@router.get("", response_model=list[AuditEntry])
async def list_audit(
    db: AsyncSession = Depends(get_db),
    entity_type: str | None = Query(None),
    user: int | None = Query(None, alias="user_id"),
    date_from: datetime | None = Query(None),
    date_to: datetime | None = Query(None),
    current_user: User = Depends(require_role(UserRole.admin, UserRole.viewer)),
):
    stmt = select(AuditLog)
    if entity_type:
        stmt = stmt.where(AuditLog.entity_type == entity_type)
    if user:
        stmt = stmt.where(AuditLog.user_id == user)
    if date_from:
        stmt = stmt.where(AuditLog.created_at >= date_from)
    if date_to:
        stmt = stmt.where(AuditLog.created_at <= date_to)
    stmt = stmt.order_by(AuditLog.created_at.desc()).limit(200)
    res = await db.execute(stmt)
    return res.scalars().all()

