from datetime import datetime
import logging
from contextlib import asynccontextmanager
from typing import Optional
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select
from fastapi import HTTPException, status
from ..models import (
    Document,
    DocumentLine,
    DocumentStatus,
    DocumentType,
    Product,
    Location,
    Stock,
    Contact,
    User,
    UserRole,
    HandlingUnit,
    HandlingUnitStatus,
)
from ..config import get_settings
from .audit import log_action

settings = get_settings()
logger = logging.getLogger(__name__)
UNSET = object()


@asynccontextmanager
async def tx(db: AsyncSession):
    """
    Transaction helper tolerant to autobegin.
    If no transaction is active, opens one via begin().
    If a transaction is already active (autobegin after a SELECT),
    performs work and commits/rolls back manually.
    """
    if not db.in_transaction():
        async with db.begin():
            yield
    else:
        try:
            yield
            await db.commit()
        except Exception:
            await db.rollback()
            raise

async def _resolve_counterparty_id(db: AsyncSession, counterparty_id: Optional[int]) -> Optional[int]:
    if counterparty_id is None:
        return None
    res = await db.execute(select(Contact).where(Contact.id == counterparty_id, Contact.is_active == True))
    contact = res.scalar_one_or_none()
    if not contact:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="Counterparty not found or inactive")
    return contact.id


async def create_document(
    db: AsyncSession, doc_type: DocumentType, meta: Optional[dict], user: User, counterparty_id: Optional[int] = None
) -> Document:
    counterparty_id = await _resolve_counterparty_id(db, counterparty_id)
    async with tx(db):
        doc = Document(
            type=doc_type,
            status=DocumentStatus.draft,
            created_by=user.id,
            meta=meta,
            counterparty_id=counterparty_id,
        )
        db.add(doc)
        await db.flush()
        await log_action(db, user.id, "document_create", "document", doc.id, {"type": doc_type, "meta": meta})
    await db.refresh(doc)
    return doc


async def update_document_details(
    db: AsyncSession,
    document: Document,
    counterparty_id=UNSET,
    meta=UNSET,
    user: Optional[User] = None,
) -> Document:
    async with tx(db):
        if counterparty_id is not UNSET:
            document.counterparty_id = await _resolve_counterparty_id(db, counterparty_id)
        if meta is not UNSET:
            document.meta = meta
        db.add(document)
        await log_action(
            db,
            user.id if user else document.created_by,
            "document_update",
            "document",
            document.id,
            {
                "counterparty_id": None if counterparty_id is UNSET else document.counterparty_id,
                "meta_updated": meta is not UNSET,
            },
        )
    await db.refresh(document, ["counterparty"])
    return document


async def get_production_location(db: AsyncSession) -> Location:
    stmt = select(Location).where(Location.cell_code == settings.production_location_code)
    res = await db.execute(stmt)
    loc = res.scalar_one_or_none()
    if not loc:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=f"Зона производства {settings.production_location_code} не найдена",
        )
    return loc


async def _lock_stock(
    db: AsyncSession, product_id: int, location_id: int, batch: Optional[str], expiry_date
) -> Stock:
    stmt = (
        select(Stock)
        .where(
            Stock.product_id == product_id,
            Stock.location_id == location_id,
            Stock.batch == batch,
            Stock.expiry_date == expiry_date,
        )
        .with_for_update()
    )
    res = await db.execute(stmt)
    stock = res.scalar_one_or_none()
    if not stock:
        stock = Stock(
            product_id=product_id,
            location_id=location_id,
            qty=0,
            batch=batch,
            expiry_date=expiry_date,
        )
        db.add(stock)
        await db.flush()
    return stock


async def resolve_product(db: AsyncSession, product_id: Optional[int], barcode: Optional[str]) -> Product:
    if not product_id and not barcode:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="Product not specified")
    stmt = select(Product)
    if product_id:
        stmt = stmt.where(Product.id == product_id)
    elif barcode:
        stmt = stmt.where(Product.barcode_ean == barcode)
    result = await db.execute(stmt)
    product = result.scalar_one_or_none()
    if not product:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Product not found")
    return product


async def resolve_location(
    db: AsyncSession, location_id: Optional[int], cell_code: Optional[str], zone_code: Optional[str] = None
) -> Location:
    code = cell_code or zone_code
    if not location_id and not code:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="Зона не указана")
    stmt = select(Location)
    if location_id:
        stmt = stmt.where(Location.id == location_id)
    elif code:
        stmt = stmt.where(Location.cell_code == code)
    result = await db.execute(stmt)
    location = result.scalar_one_or_none()
    if not location:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Зона не найдена")
    return location


async def add_line(
    db: AsyncSession,
    document: Document,
    product: Product,
    location: Location,
    qty_delta: float,
    batch: Optional[str],
    expiry_date,
) -> DocumentLine:
    if document.status in {DocumentStatus.done, DocumentStatus.canceled}:
        raise HTTPException(status_code=400, detail="Document is closed")
    async with tx(db):
        stmt = select(DocumentLine).where(
            DocumentLine.doc_id == document.id,
            DocumentLine.product_id == product.id,
            DocumentLine.location_id == location.id,
            DocumentLine.batch == batch,
            DocumentLine.expiry_date == expiry_date,
        )
        res = await db.execute(stmt)
        line = res.scalar_one_or_none()
        if not line:
            line = DocumentLine(
                doc_id=document.id,
                product_id=product.id,
                location_id=location.id,
                qty_fact=qty_delta,
                batch=batch,
                expiry_date=expiry_date,
            )
            db.add(line)
        else:
            line.qty_fact = float(line.qty_fact) + qty_delta
            if line.qty_fact < 0:
                line.qty_fact = 0
        await db.flush()
        await log_action(
            db,
            document.created_by,
            "document_line_add",
            "document",
            document.id,
            {"line_id": getattr(line, "id", None), "qty": qty_delta},
        )
    return line


async def finish_document(db: AsyncSession, document: Document, user: User):
    if document.status not in {DocumentStatus.in_progress, DocumentStatus.draft}:
        raise HTTPException(status_code=400, detail="Cannot finish document in this status")
    await db.refresh(document, ["lines"])
    production_location = None
    if document.type in {DocumentType.production_issue, DocumentType.production_receipt}:
        production_location = await get_production_location(db)
    async with tx(db):
        # lock lines
        await db.execute(select(DocumentLine).where(DocumentLine.doc_id == document.id).with_for_update())
        for line in document.lines:
            await apply_stock_change(db, document, line, user, production_location)
        if document.type == DocumentType.outbound:
            res_hu = await db.execute(
                select(HandlingUnit).where(HandlingUnit.reserved_doc_id == document.id).with_for_update()
            )
            for hu in res_hu.scalars():
                hu.status = HandlingUnitStatus.shipped
                hu.reserved_doc_id = None
                db.add(hu)
        document.status = DocumentStatus.done
        document.finished_at = datetime.utcnow()
        db.add(document)
    await log_action(db, user.id, "document_finish", "document", document.id, {"type": document.type})


async def start_document(db: AsyncSession, document: Document, user: User):
    if document.status not in {DocumentStatus.draft, DocumentStatus.in_progress}:
        raise HTTPException(status_code=400, detail="Cannot start document in this status")
    async with tx(db):
        document.status = DocumentStatus.in_progress
        db.add(document)
        await log_action(db, user.id, "document_start", "document", document.id, None)
    await db.refresh(document, ["counterparty"])


async def cancel_document(db: AsyncSession, document: Document, user: User):
    if document.status in {DocumentStatus.done, DocumentStatus.canceled}:
        return document
    async with tx(db):
        document.status = DocumentStatus.canceled
        document.finished_at = datetime.utcnow()
        if document.type == DocumentType.outbound:
            res_hu = await db.execute(
                select(HandlingUnit).where(HandlingUnit.reserved_doc_id == document.id).with_for_update()
            )
            for hu in res_hu.scalars():
                hu.status = HandlingUnitStatus.putaway
                hu.reserved_doc_id = None
                db.add(hu)
        db.add(document)
        await log_action(db, user.id, "document_cancel", "document", document.id, None)
    await db.refresh(document, ["counterparty"])
    return document


async def admin_set_status(db: AsyncSession, document: Document, new_status: DocumentStatus, user: User):
    async with tx(db):
        document.status = new_status
        document.finished_at = datetime.utcnow() if new_status in {DocumentStatus.done, DocumentStatus.canceled} else None
        db.add(document)
        await log_action(
            db,
            user.id,
            "document_status_override",
            "document",
            document.id,
            {"to": new_status.value if hasattr(new_status, "value") else str(new_status)},
        )
    await db.refresh(document, ["counterparty"])
    return document


async def apply_stock_change(
    db: AsyncSession,
    doc: Document,
    line: DocumentLine,
    user: User,
    production_location: Optional[Location] = None,
):
    stock = await _lock_stock(db, line.product_id, line.location_id, line.batch, line.expiry_date)
    qty = float(stock.qty)
    delta = float(line.qty_fact)

    if doc.type == DocumentType.inbound:
        qty += delta
    elif doc.type == DocumentType.outbound:
        qty -= delta
        if qty < 0 and not (
            settings.allow_outbound_negative or (settings.admin_override_negative and user.role == UserRole.admin)
        ):
            raise HTTPException(status_code=400, detail="Не хватает остатка в зоне")
    elif doc.type == DocumentType.inventory:
        # Инвентаризация действует как корректировка через движение, а не "перезапись цифры"
        qty += delta
    elif doc.type == DocumentType.move:
        qty += delta
    elif doc.type == DocumentType.production_issue:
        qty -= delta
        if qty < 0 and not (
            settings.allow_outbound_negative or (settings.admin_override_negative and user.role == UserRole.admin)
        ):
            raise HTTPException(status_code=400, detail="Не хватает сырья в зоне")
        if not production_location:
            raise HTTPException(status_code=500, detail="Зона производства не настроена")
        stock_prod = await _lock_stock(db, line.product_id, production_location.id, line.batch, line.expiry_date)
        stock_prod.qty = float(stock_prod.qty) + delta
        stock_prod.batch = line.batch
        stock_prod.expiry_date = line.expiry_date
        db.add(stock_prod)
    elif doc.type == DocumentType.production_receipt:
        qty += delta
        # выпуск без вычитания из PROD-01 (упрощённо); TODO: добавить рецептуры/списание из производства
        await log_action(
            db,
            user.id,
            "production_receipt_line",
            "document",
            doc.id,
            {"line_id": line.id, "note": "qty added, PROD-01 not decremented"},
        )
    stock.qty = qty
    stock.batch = line.batch
    stock.expiry_date = line.expiry_date
    db.add(stock)
