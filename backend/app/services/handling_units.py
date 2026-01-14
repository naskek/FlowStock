from typing import Optional

from fastapi import HTTPException, status
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..config import get_settings
from ..models import HandlingUnit, HandlingUnitContent, HandlingUnitStatus, HandlingUnitType, Location, SSCCSequence, Document, Product, DocumentStatus
from ..services.documents import resolve_location, tx, add_line
from .sscc import compute_gs1_mod10_check_digit, normalize_sscc, validate_sscc
from fastapi import HTTPException, status

settings = get_settings()


async def _resolve_location(db: AsyncSession, location_id: Optional[int], cell_code: Optional[str]) -> Optional[Location]:
    if not location_id and not cell_code:
        return None
    return await resolve_location(db, location_id, cell_code, None)


async def allocate_sscc(db: AsyncSession) -> str:
    prefix = settings.gs1_company_prefix
    ext_digit = settings.sscc_extension_digit
    serial_length = settings.sscc_serial_length
    if len(ext_digit + prefix) >= 17:
        raise HTTPException(status_code=500, detail="Invalid SSCC configuration")

    async with tx(db):
        stmt = (
            select(SSCCSequence)
            .where(SSCCSequence.company_prefix == prefix)
            .with_for_update()
        )
        res = await db.execute(stmt)
        seq = res.scalar_one_or_none()
        if not seq:
            seq = SSCCSequence(
                company_prefix=prefix,
                extension_digit=ext_digit,
                serial_length=serial_length,
                next_serial=1,
            )
            db.add(seq)
            await db.flush()
        serial = seq.next_serial
        seq.next_serial = serial + 1
        db.add(seq)
        serial_str = str(serial).zfill(serial_length)
    base17 = f"{ext_digit}{prefix}{serial_str}"
    cd = compute_gs1_mod10_check_digit(base17)
    sscc = base17 + cd
    validate_sscc(sscc, validate_cd=True)
    return sscc


async def create_hu_auto(
    db: AsyncSession,
    location_id: Optional[int] = None,
    cell_code: Optional[str] = None,
) -> HandlingUnit:
    location = await _resolve_location(db, location_id, cell_code)
    sscc = await allocate_sscc(db)
    async with tx(db):
        hu = HandlingUnit(
            sscc=sscc,
            type=HandlingUnitType.pallet,
            status=HandlingUnitStatus.created,
            location_id=location.id if location else None,
        )
        db.add(hu)
        await db.flush()
    await db.refresh(hu, ["location", "contents"])
    return hu


async def consume_from_hu(
    db: AsyncSession,
    sscc_scan: str,
    product_id: int,
    qty: float,
    batch: Optional[str] = None,
    expiry_date: Optional[str] = None,
    doc_id: Optional[int] = None,
) -> HandlingUnit:
    sscc = normalize_sscc(sscc_scan)
    async with tx(db):
        res = await db.execute(
            select(HandlingUnit)
            .where(HandlingUnit.sscc == sscc)
            .with_for_update()
            .options(selectinload(HandlingUnit.contents), selectinload(HandlingUnit.location))
        )
        hu: HandlingUnit | None = res.scalar_one_or_none()
        if not hu:
            raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Handling unit not found")
        if qty <= 0:
            raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="qty must be > 0")
        matched = None
        for c in hu.contents:
            if c.product_id == product_id and (c.batch or None) == (batch or None) and (c.expiry_date or None) == (expiry_date or None):
                matched = c
                break
        if not matched:
            raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="No such product in handling unit")
        if matched.qty < qty:
            raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="Not enough qty in handling unit")
        matched.qty = matched.qty - qty
        if matched.qty == 0:
            await db.delete(matched)
        db.add(hu)
        if doc_id:
            doc_res = await db.execute(select(Document).where(Document.id == doc_id).with_for_update())
            doc = doc_res.scalar_one_or_none()
            if not doc:
                raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Document not found")
            if doc.status in {DocumentStatus.done, DocumentStatus.canceled}:
                raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="Document is closed")
            product_res = await db.execute(select(Product).where(Product.id == product_id))
            product = product_res.scalar_one_or_none()
            if not product:
                raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Product not found")
            if not hu.location_id:
                raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="Handling unit has no location")
            location_res = await db.execute(select(Location).where(Location.id == hu.location_id))
            location = location_res.scalar_one_or_none()
            if not location:
                raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Location not found")
            await add_line(db, doc, product, location, qty_delta=qty, batch=batch, expiry_date=expiry_date)
    await db.refresh(hu, ["contents", "location"])
    return hu


async def create_hu_manual(
    db: AsyncSession,
    sscc_scan: str,
    location_id: Optional[int] = None,
    cell_code: Optional[str] = None,
) -> HandlingUnit:
    sscc = normalize_sscc(sscc_scan)
    validate_sscc(sscc, validate_cd=True)
    async with tx(db):
        res = await db.execute(select(HandlingUnit).where(HandlingUnit.sscc == sscc).with_for_update())
        existing = res.scalar_one_or_none()
        if existing:
            raise HTTPException(status_code=status.HTTP_409_CONFLICT, detail="SSCC already exists")
        location = await _resolve_location(db, location_id, cell_code)
        hu = HandlingUnit(
            sscc=sscc,
            type=HandlingUnitType.pallet,
            status=HandlingUnitStatus.created,
            location_id=location.id if location else None,
        )
        db.add(hu)
        await db.flush()
    await db.refresh(hu, ["location", "contents"])
    return hu
