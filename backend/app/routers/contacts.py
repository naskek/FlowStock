from fastapi import APIRouter, Depends, HTTPException, Query
from sqlalchemy import select, or_
from sqlalchemy.ext.asyncio import AsyncSession

from ..models import Contact, ContactType, User, UserRole
from ..schemas import ContactBase, ContactCreate, ContactUpdate
from ..deps import get_db, get_current_user, require_role
from ..services.audit import log_action

router = APIRouter()


@router.get("", response_model=list[ContactBase])
async def list_contacts(
    db: AsyncSession = Depends(get_db),
    q: str | None = Query(None),
    type: ContactType | None = Query(None),
    include_inactive: bool = Query(False),
    current_user: User = Depends(require_role(UserRole.admin, UserRole.worker)),
):
    stmt = select(Contact)
    if q:
        stmt = stmt.where(or_(Contact.name.ilike(f"%{q}%"), Contact.email.ilike(f"%{q}%"), Contact.phone.ilike(f"%{q}%")))
    if type:
        stmt = stmt.where(Contact.type == type)
    if not include_inactive:
        stmt = stmt.where(Contact.is_active == True)
    res = await db.execute(stmt.order_by(Contact.name))
    return res.scalars().all()


@router.get("/search", response_model=list[ContactBase])
async def search_contacts(
    q: str | None = Query(None),
    limit: int = Query(20, ge=1, le=100),
    contact_type: ContactType | None = Query(None, alias="type"),
    db: AsyncSession = Depends(get_db),
    current_user=Depends(get_current_user),
):
    if not q:
        return []
    stmt = select(Contact).where(
        Contact.is_active == True,
        or_(Contact.name.ilike(f"%{q}%"), Contact.email.ilike(f"%{q}%"), Contact.phone.ilike(f"%{q}%")),
    )
    if contact_type:
        stmt = stmt.where(Contact.type == contact_type)
    res = await db.execute(stmt.order_by(Contact.name).limit(limit))
    return res.scalars().all()


@router.post("", response_model=ContactBase)
async def create_contact(
    payload: ContactCreate,
    db: AsyncSession = Depends(get_db),
    current_user: User = Depends(require_role(UserRole.admin)),
):
    contact = Contact(**payload.model_dump())
    db.add(contact)
    await db.commit()
    await db.refresh(contact)
    await log_action(db, current_user.id, "contact_create", "contact", contact.id, payload.model_dump())
    return contact


@router.get("/{contact_id}", response_model=ContactBase)
async def get_contact(
    contact_id: int,
    db: AsyncSession = Depends(get_db),
    current_user=Depends(get_current_user),
):
    res = await db.execute(select(Contact).where(Contact.id == contact_id))
    contact = res.scalar_one_or_none()
    if not contact:
        raise HTTPException(status_code=404, detail="Contact not found")
    return contact


@router.put("/{contact_id}", response_model=ContactBase)
async def update_contact(
    contact_id: int,
    payload: ContactUpdate,
    db: AsyncSession = Depends(get_db),
    current_user: User = Depends(require_role(UserRole.admin)),
):
    res = await db.execute(select(Contact).where(Contact.id == contact_id))
    contact = res.scalar_one_or_none()
    if not contact:
        raise HTTPException(status_code=404, detail="Contact not found")
    updates = payload.model_dump(exclude_unset=True)
    for k, v in updates.items():
        setattr(contact, k, v)
    db.add(contact)
    await db.commit()
    await db.refresh(contact)
    await log_action(db, current_user.id, "contact_update", "contact", contact.id, updates)
    return contact


@router.delete("/{contact_id}")
async def delete_contact(
    contact_id: int,
    db: AsyncSession = Depends(get_db),
    current_user: User = Depends(require_role(UserRole.admin)),
):
    res = await db.execute(select(Contact).where(Contact.id == contact_id))
    contact = res.scalar_one_or_none()
    if not contact:
        raise HTTPException(status_code=404, detail="Contact not found")
    contact.is_active = False
    db.add(contact)
    await db.commit()
    await log_action(db, current_user.id, "contact_delete", "contact", contact.id, None)
    return {"detail": "deactivated"}
