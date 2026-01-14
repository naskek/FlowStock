from fastapi import APIRouter, Depends, HTTPException
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select
from ..deps import get_db, require_role
from ..models import User, UserRole
from ..schemas import UserBase, UserCreate, UserUpdate, PasswordChange
from ..security import hash_password
from ..services.audit import log_action

router = APIRouter()


@router.get("", response_model=list[UserBase])
async def list_users(db: AsyncSession = Depends(get_db), current_user=Depends(require_role(UserRole.admin))):
    res = await db.execute(select(User))
    return res.scalars().all()


@router.post("", response_model=UserBase)
async def create_user(
    payload: UserCreate,
    db: AsyncSession = Depends(get_db),
    current_user=Depends(require_role(UserRole.admin)),
):
    existing = await db.execute(select(User).where(User.login == payload.login))
    if existing.scalar_one_or_none():
        raise HTTPException(status_code=400, detail="User exists")
    user = User(
        login=payload.login,
        password_hash=hash_password(payload.password),
        role=payload.role,
        is_active=payload.is_active,
    )
    db.add(user)
    await db.commit()
    await db.refresh(user)
    await log_action(db, current_user.id, "user_create", "user", user.id, {"login": user.login})
    return user


@router.put("/{user_id}", response_model=UserBase)
async def update_user(
    user_id: int,
    payload: UserUpdate,
    db: AsyncSession = Depends(get_db),
    current_user=Depends(require_role(UserRole.admin)),
):
    res = await db.execute(select(User).where(User.id == user_id))
    user = res.scalar_one_or_none()
    if not user:
        raise HTTPException(status_code=404, detail="Not found")
    if payload.role is not None:
        user.role = payload.role
    if payload.is_active is not None:
        user.is_active = payload.is_active
    db.add(user)
    await db.commit()
    await db.refresh(user)
    await log_action(db, current_user.id, "user_update", "user", user.id, {"role": str(payload.role), "active": payload.is_active})
    return user


@router.post("/{user_id}/password")
async def change_password(
    user_id: int,
    payload: PasswordChange,
    db: AsyncSession = Depends(get_db),
    current_user=Depends(require_role(UserRole.admin)),
):
    res = await db.execute(select(User).where(User.id == user_id))
    user = res.scalar_one_or_none()
    if not user:
        raise HTTPException(status_code=404, detail="Not found")
    user.password_hash = hash_password(payload.password)
    db.add(user)
    await db.commit()
    await log_action(db, current_user.id, "user_password_change", "user", user.id, None)
    return {"detail": "ok"}
