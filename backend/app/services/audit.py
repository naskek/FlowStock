from datetime import datetime
from typing import Optional
from sqlalchemy.ext.asyncio import AsyncSession
from ..models import AuditLog


async def log_action(
    db: AsyncSession,
    user_id: Optional[int],
    action: str,
    entity_type: str,
    entity_id: Optional[int],
    payload: Optional[dict] = None,
):
    entry = AuditLog(
        user_id=user_id,
        action=action,
        entity_type=entity_type,
        entity_id=entity_id,
        payload_json=payload,
        created_at=datetime.utcnow(),
    )
    db.add(entry)
    await db.commit()
