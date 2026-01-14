from fastapi import APIRouter, Depends, HTTPException
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import text, bindparam
from sqlalchemy.dialects.postgresql import JSONB

from ..deps import get_db, require_role
from ..models import UserRole
from ..schemas import CompanyInfo, CompanyEntry
from ..services.audit import log_action

router = APIRouter()


CREATE_SQL = """
CREATE TABLE IF NOT EXISTS company_settings (
    id SERIAL PRIMARY KEY,
    payload JSONB,
    is_active BOOLEAN DEFAULT FALSE,
    created_at TIMESTAMPTZ DEFAULT NOW()
);
"""


async def ensure_table(db: AsyncSession):
    await db.execute(text(CREATE_SQL))
    await db.execute(text("ALTER TABLE company_settings ADD COLUMN IF NOT EXISTS is_active BOOLEAN DEFAULT FALSE"))
    await db.commit()


@router.get("", response_model=CompanyInfo)
async def get_company(db: AsyncSession = Depends(get_db)):
    await ensure_table(db)
    res = await db.execute(
        text(
            "SELECT payload FROM company_settings WHERE is_active = TRUE ORDER BY id DESC LIMIT 1"
        )
    )
    row = res.first()
    if row and row[0]:
        return CompanyInfo(**row[0])
    res = await db.execute(text("SELECT payload FROM company_settings ORDER BY id DESC LIMIT 1"))
    row = res.first()
    data = row[0] if row and row[0] else {}
    return CompanyInfo(**data)


@router.get("/all", response_model=list[CompanyEntry])
async def list_companies(db: AsyncSession = Depends(get_db), current_user=Depends(require_role(UserRole.admin))):
    await ensure_table(db)
    res = await db.execute(text("SELECT id, payload, is_active FROM company_settings ORDER BY id DESC"))
    rows = res.fetchall()
    return [CompanyEntry(id=r[0], is_active=r[2], **(r[1] or {})) for r in rows]


@router.post("", response_model=CompanyEntry)
async def save_company(
    payload: CompanyInfo,
    db: AsyncSession = Depends(get_db),
    current_user=Depends(require_role(UserRole.admin)),
):
    await ensure_table(db)
    # делаем новую запись активной и сбрасываем старые
    await db.execute(text("UPDATE company_settings SET is_active = FALSE"))
    stmt = text(
        """
INSERT INTO company_settings (payload, is_active)
VALUES (:payload, TRUE)
RETURNING id, payload, is_active
"""
    ).bindparams(bindparam("payload", type_=JSONB))
    res = await db.execute(stmt, {"payload": payload.model_dump()})
    row = res.first()
    await db.commit()
    company_id = row[0]
    await log_action(db, current_user.id, "company_create", "company", company_id, payload.model_dump())
    return CompanyEntry(id=company_id, is_active=row[2], **payload.model_dump())


@router.put("/{company_id}", response_model=CompanyEntry)
async def update_company(
    company_id: int,
    payload: CompanyInfo,
    db: AsyncSession = Depends(get_db),
    current_user=Depends(require_role(UserRole.admin)),
):
    await ensure_table(db)
    stmt = text(
        """
UPDATE company_settings
SET payload = :payload
WHERE id = :company_id
RETURNING id, payload, is_active
"""
    ).bindparams(bindparam("payload", type_=JSONB))
    res = await db.execute(stmt, {"payload": payload.model_dump(), "company_id": company_id})
    row = res.first()
    if not row:
        raise HTTPException(status_code=404, detail="Company entry not found")
    await db.commit()
    await log_action(db, current_user.id, "company_update", "company", company_id, payload.model_dump())
    return CompanyEntry(id=company_id, is_active=row[2], **payload.model_dump())


@router.delete("/{company_id}")
async def delete_company(
    company_id: int,
    db: AsyncSession = Depends(get_db),
    current_user=Depends(require_role(UserRole.admin)),
):
    await ensure_table(db)
    await db.execute(text("DELETE FROM company_settings WHERE id = :cid"), {"cid": company_id})
    await db.commit()
    await log_action(db, current_user.id, "company_delete", "company", company_id, {})
    return {"status": "ok"}


@router.put("/{company_id}/activate", response_model=CompanyEntry)
async def activate_company(
    company_id: int,
    db: AsyncSession = Depends(get_db),
    current_user=Depends(require_role(UserRole.admin)),
):
    await ensure_table(db)
    res = await db.execute(text("SELECT payload FROM company_settings WHERE id = :cid"), {"cid": company_id})
    row = res.first()
    if not row:
        raise HTTPException(status_code=404, detail="Company entry not found")
    await db.execute(text("UPDATE company_settings SET is_active = FALSE"))
    await db.execute(text("UPDATE company_settings SET is_active = TRUE WHERE id = :cid"), {"cid": company_id})
    await db.commit()
    await log_action(db, current_user.id, "company_activate", "company", company_id, row[0] or {})
    return CompanyEntry(id=company_id, is_active=True, **(row[0] or {}))
