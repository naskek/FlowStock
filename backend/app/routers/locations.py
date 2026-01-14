from fastapi import APIRouter, Depends, HTTPException, Query
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select
from ..models import Location, User, UserRole
from ..schemas import LocationBase, LocationCreate, LocationUpdate
from ..deps import get_db, require_role
from ..services.audit import log_action

router = APIRouter()


@router.get("", response_model=list[LocationBase])
async def list_locations(
    db: AsyncSession = Depends(get_db),
    warehouse: str | None = Query(None),
    cell_code: str | None = Query(None),
    zone: str | None = Query(None),
):
    stmt = select(Location)
    if warehouse:
        stmt = stmt.where(Location.warehouse == warehouse)
    if cell_code:
        stmt = stmt.where(Location.cell_code == cell_code)
    if zone:
        stmt = stmt.where(Location.zone == zone)
    res = await db.execute(stmt)
    return res.scalars().all()


@router.post("", response_model=LocationBase)
async def create_location(
    payload: LocationCreate,
    db: AsyncSession = Depends(get_db),
    current_user: User = Depends(require_role(UserRole.admin)),
):
    loc = Location(**payload.dict())
    db.add(loc)
    await db.commit()
    await db.refresh(loc)
    await log_action(db, current_user.id, "location_create", "location", loc.id, payload.dict())
    return loc


@router.put("/{loc_id}", response_model=LocationBase)
async def update_location(
    loc_id: int,
    payload: LocationUpdate,
    db: AsyncSession = Depends(get_db),
    current_user: User = Depends(require_role(UserRole.admin)),
):
    res = await db.execute(select(Location).where(Location.id == loc_id))
    loc = res.scalar_one_or_none()
    if not loc:
        raise HTTPException(status_code=404, detail="Location not found")
    for k, v in payload.dict().items():
        setattr(loc, k, v)
    db.add(loc)
    await db.commit()
    await db.refresh(loc)
    await log_action(db, current_user.id, "location_update", "location", loc.id, payload.dict())
    return loc


@router.delete("/{loc_id}")
async def delete_location(
    loc_id: int,
    db: AsyncSession = Depends(get_db),
    current_user: User = Depends(require_role(UserRole.admin)),
):
    res = await db.execute(select(Location).where(Location.id == loc_id))
    loc = res.scalar_one_or_none()
    if not loc:
        raise HTTPException(status_code=404, detail="Not found")
    await db.delete(loc)
    await db.commit()
    await log_action(db, current_user.id, "location_delete", "location", loc.id, None)
    return {"detail": "deleted"}
