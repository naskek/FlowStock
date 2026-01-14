from typing import Optional, List

from fastapi import APIRouter, Depends, HTTPException, Query, status
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy.orm import selectinload

from ..deps import get_db, require_role
from ..models import HandlingUnit, HandlingUnitContent, HandlingUnitStatus, Location, UserRole
from ..schemas import (
    HandlingUnitCreateAuto,
    HandlingUnitCreateManual,
    HandlingUnitDetail,
    HandlingUnitOut,
    HandlingUnitConsumePayload,
)
from ..services.handling_units import allocate_sscc, create_hu_auto, create_hu_manual, consume_from_hu
from ..services.sscc import normalize_sscc


router = APIRouter(prefix="/handling-units", tags=["handling-units"])


@router.post("/auto", response_model=HandlingUnitOut)
async def create_auto(
    payload: HandlingUnitCreateAuto,
    db: AsyncSession = Depends(get_db),
    current_user=Depends(require_role(UserRole.admin, UserRole.worker)),
):
    hu = await create_hu_auto(db, payload.location_id, payload.cell_code)
    return hu


@router.post("/manual", response_model=HandlingUnitOut)
async def create_manual(
    payload: HandlingUnitCreateManual,
    db: AsyncSession = Depends(get_db),
    current_user=Depends(require_role(UserRole.admin, UserRole.worker)),
):
    hu = await create_hu_manual(db, payload.sscc_scan, payload.location_id, payload.cell_code)
    return hu


@router.get("/{sscc_scan}", response_model=HandlingUnitDetail)
async def get_by_sscc(
    sscc_scan: str,
    db: AsyncSession = Depends(get_db),
    current_user=Depends(require_role(UserRole.admin, UserRole.worker, UserRole.viewer)),
):
    sscc = normalize_sscc(sscc_scan)
    stmt = (
        select(HandlingUnit)
        .where(HandlingUnit.sscc == sscc)
        .options(
            selectinload(HandlingUnit.location),
            selectinload(HandlingUnit.contents).selectinload(HandlingUnitContent.product),
        )
    )
    res = await db.execute(stmt)
    hu = res.scalar_one_or_none()
    if not hu:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Handling unit not found")
    return hu


@router.post("/{sscc_scan}/putaway", response_model=HandlingUnitOut)
async def putaway_hu(
    sscc_scan: str,
    location_id: Optional[int] = None,
    cell_code: Optional[str] = None,
    db: AsyncSession = Depends(get_db),
    current_user=Depends(require_role(UserRole.admin, UserRole.worker)),
):
    sscc = normalize_sscc(sscc_scan)
    stmt = select(HandlingUnit).where(HandlingUnit.sscc == sscc).options(selectinload(HandlingUnit.location))
    res = await db.execute(stmt)
    hu: HandlingUnit | None = res.scalar_one_or_none()
    if not hu:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Handling unit not found")
    loc: Location | None = None
    if location_id or cell_code:
        loc = await db.get(Location, location_id) if location_id else None
        if not loc and cell_code:
            res_loc = await db.execute(select(Location).where(Location.cell_code == cell_code))
            loc = res_loc.scalar_one_or_none()
        if not loc:
            raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="Location not found")
        hu.location_id = loc.id
    hu.status = HandlingUnitStatus.putaway
    db.add(hu)
    await db.commit()
    await db.refresh(hu, ["location"])
    return hu


@router.delete("/{sscc_scan}", status_code=status.HTTP_204_NO_CONTENT)
async def delete_hu(
    sscc_scan: str,
    db: AsyncSession = Depends(get_db),
    current_user=Depends(require_role(UserRole.admin, UserRole.worker)),
):
    sscc = normalize_sscc(sscc_scan)
    stmt = (
      select(HandlingUnit)
      .where(HandlingUnit.sscc == sscc)
      .options(selectinload(HandlingUnit.contents))
    )
    res = await db.execute(stmt)
    hu: HandlingUnit | None = res.scalar_one_or_none()
    if not hu:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Handling unit not found")
    if hu.status == HandlingUnitStatus.shipped or hu.reserved_doc_id:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="Cannot delete shipped or reserved handling unit")
    # delete contents first
    for content in list(hu.contents):
        await db.delete(content)
    await db.delete(hu)
    await db.commit()


@router.post("/{sscc_scan}/consume", response_model=HandlingUnitDetail)
async def consume_hu(
    sscc_scan: str,
    payload: HandlingUnitConsumePayload,
    db: AsyncSession = Depends(get_db),
    current_user=Depends(require_role(UserRole.admin, UserRole.worker)),
):
    hu = await consume_from_hu(
        db,
        sscc_scan=sscc_scan,
        product_id=payload.product_id,
        qty=payload.qty,
        batch=payload.batch,
        expiry_date=payload.expiry_date.isoformat() if payload.expiry_date else None,
        doc_id=payload.doc_id,
    )
    return hu


@router.get("", response_model=List[HandlingUnitOut])
async def list_handling_units(
    status_: Optional[HandlingUnitStatus] = Query(None, alias="status"),
    cell_code: Optional[str] = Query(None),
    db: AsyncSession = Depends(get_db),
    current_user=Depends(require_role(UserRole.admin, UserRole.worker, UserRole.viewer)),
):
    stmt = select(HandlingUnit).options(selectinload(HandlingUnit.location))
    if status_:
        stmt = stmt.where(HandlingUnit.status == status_)
    if cell_code:
        stmt = stmt.join(HandlingUnit.location).where(Location.cell_code == cell_code)
    res = await db.execute(stmt.order_by(HandlingUnit.id.desc()))
    return res.scalars().all()
