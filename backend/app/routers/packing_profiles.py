from typing import List, Optional

from fastapi import APIRouter, Depends, HTTPException, Query, status
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import get_db, require_role
from ..models import PackingProfile, Product, UserRole
from ..schemas import PackingProfileBase, PackingProfileCreate, PackingProfileUpdate


router = APIRouter(prefix="/packing-profiles", tags=["packing-profiles"])


@router.get("", response_model=List[PackingProfileBase])
async def list_profiles(
    product_id: Optional[int] = Query(None),
    db: AsyncSession = Depends(get_db),
    current_user=Depends(require_role(UserRole.admin, UserRole.worker, UserRole.viewer)),
):
    stmt = select(PackingProfile)
    if product_id:
        stmt = stmt.where(PackingProfile.product_id == product_id)
    res = await db.execute(stmt.order_by(PackingProfile.id.desc()))
    return res.scalars().all()


@router.post("", response_model=PackingProfileBase, status_code=status.HTTP_201_CREATED)
async def create_profile(
    payload: PackingProfileCreate,
    db: AsyncSession = Depends(get_db),
    current_user=Depends(require_role(UserRole.admin, UserRole.worker)),
):
    product = await db.get(Product, payload.product_id)
    if not product:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Product not found")
    exists = await db.execute(
        select(PackingProfile).where(
            PackingProfile.product_id == payload.product_id,
            PackingProfile.pack_type == payload.pack_type,
        )
    )
    if exists.scalar_one_or_none():
        raise HTTPException(status_code=status.HTTP_409_CONFLICT, detail="Packing profile already exists")
    profile = PackingProfile(
        product_id=payload.product_id,
        pack_type=payload.pack_type,
        qty_per_pack=payload.qty_per_pack,
        is_active=payload.is_active,
    )
    db.add(profile)
    await db.commit()
    await db.refresh(profile)
    return profile


@router.put("/{profile_id}", response_model=PackingProfileBase)
async def update_profile(
    profile_id: int,
    payload: PackingProfileUpdate,
    db: AsyncSession = Depends(get_db),
    current_user=Depends(require_role(UserRole.admin, UserRole.worker)),
):
    profile = await db.get(PackingProfile, profile_id)
    if not profile:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Not found")
    if payload.pack_type is not None:
        profile.pack_type = payload.pack_type
    if payload.qty_per_pack is not None:
        profile.qty_per_pack = payload.qty_per_pack
    if payload.is_active is not None:
        profile.is_active = payload.is_active
    await db.commit()
    await db.refresh(profile)
    return profile


@router.delete("/{profile_id}", status_code=status.HTTP_204_NO_CONTENT)
async def delete_profile(
    profile_id: int,
    db: AsyncSession = Depends(get_db),
    current_user=Depends(require_role(UserRole.admin)),
):
    profile = await db.get(PackingProfile, profile_id)
    if not profile:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Not found")
    await db.delete(profile)
    await db.commit()
    return None
