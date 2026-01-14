from datetime import datetime
from fastapi import APIRouter, Depends, HTTPException, status, Query
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select, func
from sqlalchemy.orm import selectinload
from ..models import (
    Document,
    DocumentStatus,
    DocumentType,
    User,
    UserRole,
    DocumentLine,
    HandlingUnit,
    HandlingUnitStatus,
    HandlingUnitContent,
    PackingProfile,
)
from ..schemas import (
    DocumentBase,
    DocumentWithLines,
    DocumentCreate,
    DocumentLineCreate,
    ScanPayload,
    DocumentStatusUpdate,
    DocumentUpdate,
    DocPalletAutoCreate,
    DocPalletManualAdd,
    DocPalletItemAdd,
    HandlingUnitOutMinimal,
)
from ..deps import get_db, require_role, get_current_user
from ..services import documents as doc_service
from ..services.audit import log_action
from fastapi import Response
from ..services.pdf import render_document_pdf
from ..services.handling_units import allocate_sscc
from ..services.sscc import normalize_sscc


router = APIRouter()


@router.post("", response_model=DocumentBase)
async def create_document(
    payload: DocumentCreate,
    db: AsyncSession = Depends(get_db),
    current_user: User = Depends(require_role(UserRole.admin, UserRole.worker)),
):
    doc = await doc_service.create_document(db, payload.type, payload.meta, current_user, payload.counterparty_id)
    await db.refresh(doc, ["counterparty"])
    return doc


@router.get("", response_model=list[DocumentBase])
async def list_documents(
    db: AsyncSession = Depends(get_db),
    type: DocumentType | None = Query(None),
    status_: DocumentStatus | None = Query(None, alias="status"),
    date_from: datetime | None = Query(None),
    date_to: datetime | None = Query(None),
    current_user: User = Depends(get_current_user),
):
    stmt = select(Document).options(selectinload(Document.counterparty))
    if type:
        stmt = stmt.where(Document.type == type)
    if status_:
        stmt = stmt.where(Document.status == status_)
    if date_from:
        stmt = stmt.where(Document.created_at >= date_from)
    if date_to:
        stmt = stmt.where(Document.created_at <= date_to)
    res = await db.execute(stmt.order_by(Document.created_at.desc()))
    return res.scalars().all()


@router.get("/{doc_id}", response_model=DocumentWithLines)
async def get_document(doc_id: int, db: AsyncSession = Depends(get_db), current_user=Depends(get_current_user)):
    res = await db.execute(select(Document).where(Document.id == doc_id))
    doc = res.scalar_one_or_none()
    if not doc:
        raise HTTPException(status_code=404, detail="Document not found")
    await db.refresh(doc, ["lines", "counterparty"])
    return doc


@router.put("/{doc_id}", response_model=DocumentBase)
async def update_document(
    doc_id: int,
    payload: DocumentUpdate,
    db: AsyncSession = Depends(get_db),
    current_user: User = Depends(require_role(UserRole.admin, UserRole.worker)),
):
    res = await db.execute(select(Document).where(Document.id == doc_id))
    doc = res.scalar_one_or_none()
    if not doc:
        raise HTTPException(status_code=404, detail="Document not found")
    data = payload.model_dump(exclude_unset=True)
    counterparty_id = data["counterparty_id"] if "counterparty_id" in data else doc_service.UNSET
    if counterparty_id is not doc_service.UNSET and doc.status != DocumentStatus.draft:
        raise HTTPException(status_code=409, detail="Можно менять контрагента только в черновике")
    meta = data["meta"] if "meta" in data else doc_service.UNSET
    doc = await doc_service.update_document_details(
        db, doc, counterparty_id=counterparty_id, meta=meta, user=current_user
    )
    return doc


@router.post("/{doc_id}/lines", response_model=DocumentWithLines)
async def add_line(
    doc_id: int,
    payload: DocumentLineCreate,
    db: AsyncSession = Depends(get_db),
    current_user: User = Depends(require_role(UserRole.admin, UserRole.worker)),
):
    res = await db.execute(select(Document).where(Document.id == doc_id))
    doc = res.scalar_one_or_none()
    if not doc:
        raise HTTPException(status_code=404, detail="Document not found")
    product = await doc_service.resolve_product(db, payload.product_id, payload.barcode)
    location = await doc_service.resolve_location(db, payload.location_id, payload.cell_code, payload.zone_code)
    line = await doc_service.add_line(db, doc, product, location, payload.qty_delta, payload.batch, payload.expiry_date)
    await db.refresh(doc, ["lines"])
    return doc


@router.post("/{doc_id}/scan", response_model=DocumentWithLines)
async def scan_line(
    doc_id: int,
    payload: ScanPayload,
    db: AsyncSession = Depends(get_db),
    current_user: User = Depends(require_role(UserRole.admin, UserRole.worker)),
):
    res = await db.execute(select(Document).where(Document.id == doc_id))
    doc = res.scalar_one_or_none()
    if not doc:
        raise HTTPException(status_code=404, detail="Document not found")
    product = await doc_service.resolve_product(db, None, payload.barcode)
    location = await doc_service.resolve_location(db, None, payload.cell_code, payload.zone_code)
    line = await doc_service.add_line(db, doc, product, location, payload.qty, payload.batch, payload.expiry_date)
    await db.refresh(doc, ["lines"])
    return doc


@router.post("/{doc_id}/start", response_model=DocumentBase)
async def start_doc(
    doc_id: int,
    db: AsyncSession = Depends(get_db),
    current_user: User = Depends(require_role(UserRole.admin, UserRole.worker)),
):
    res = await db.execute(select(Document).where(Document.id == doc_id))
    doc = res.scalar_one_or_none()
    if not doc:
        raise HTTPException(status_code=404, detail="Not found")
    await doc_service.start_document(db, doc, current_user)
    await db.refresh(doc, ["counterparty"])
    return doc


@router.post("/{doc_id}/finish", response_model=DocumentBase)
async def finish_doc(
    doc_id: int,
    db: AsyncSession = Depends(get_db),
    current_user: User = Depends(require_role(UserRole.admin, UserRole.worker)),
):
    res = await db.execute(select(Document).where(Document.id == doc_id))
    doc = res.scalar_one_or_none()
    if not doc:
        raise HTTPException(status_code=404, detail="Not found")
    await doc_service.finish_document(db, doc, current_user)
    await db.refresh(doc, ["counterparty"])
    return doc


@router.post("/{doc_id}/cancel", response_model=DocumentBase)
async def cancel_doc(
    doc_id: int,
    db: AsyncSession = Depends(get_db),
    current_user: User = Depends(require_role(UserRole.admin, UserRole.worker)),
):
    res = await db.execute(select(Document).where(Document.id == doc_id))
    doc = res.scalar_one_or_none()
    if not doc:
        raise HTTPException(status_code=404, detail="Not found")
    await doc_service.cancel_document(db, doc, current_user)
    await db.refresh(doc, ["counterparty"])
    return doc


@router.post("/{doc_id}/status", response_model=DocumentBase)
async def override_status(
    doc_id: int,
    payload: DocumentStatusUpdate,
    db: AsyncSession = Depends(get_db),
    current_user: User = Depends(require_role(UserRole.admin)),
):
    res = await db.execute(select(Document).where(Document.id == doc_id))
    doc = res.scalar_one_or_none()
    if not doc:
        raise HTTPException(status_code=404, detail="Not found")
    await doc_service.admin_set_status(db, doc, payload.status, current_user)
    await db.refresh(doc, ["counterparty"])
    return doc


def _ensure_pallet_doc(doc: Document):
    if doc.type not in {DocumentType.inbound, DocumentType.production_receipt, DocumentType.outbound}:
        raise HTTPException(status_code=400, detail="Паллета доступна только для приходных/отгрузочных документов")
    if doc.status in {DocumentStatus.done, DocumentStatus.canceled}:
        raise HTTPException(status_code=400, detail="Документ закрыт")


def _ensure_outbound_doc(doc: Document):
    if doc.type != DocumentType.outbound:
        raise HTTPException(status_code=400, detail="Только для отгрузочных документов")
    if doc.status in {DocumentStatus.done, DocumentStatus.canceled}:
        raise HTTPException(status_code=400, detail="Документ закрыт")


@router.get("/{doc_id}/pallets", response_model=list[HandlingUnitOutMinimal])
async def list_doc_pallets(
    doc_id: int,
    db: AsyncSession = Depends(get_db),
    current_user: User = Depends(require_role(UserRole.admin, UserRole.worker)),
):
    res = await db.execute(select(Document).where(Document.id == doc_id))
    doc = res.scalar_one_or_none()
    if not doc:
        raise HTTPException(status_code=404, detail="Document not found")

    stmt = (
        select(
            HandlingUnit.id,
            HandlingUnit.sscc,
            HandlingUnit.location_id,
            HandlingUnit.created_at,
            func.coalesce(func.sum(HandlingUnitContent.qty), 0).label("total_qty"),
        )
        .where(HandlingUnit.source_doc_id == doc_id)
        .outerjoin(HandlingUnitContent, HandlingUnitContent.hu_id == HandlingUnit.id)
        .group_by(HandlingUnit.id)
        .order_by(HandlingUnit.created_at, HandlingUnit.id)
    )
    rows = (await db.execute(stmt)).all()
    return [
        HandlingUnitOutMinimal(
            id=row.id,
            sscc=row.sscc,
            location_id=row.location_id,
            created_at=row.created_at,
            total_qty=float(row.total_qty) if row.total_qty is not None else None,
        )
        for row in rows
    ]


@router.post("/{doc_id}/pallets/auto")
async def create_pallet_auto(
    doc_id: int,
    payload: DocPalletAutoCreate,
    db: AsyncSession = Depends(get_db),
    current_user: User = Depends(require_role(UserRole.admin, UserRole.worker)),
):
    res = await db.execute(select(Document).where(Document.id == doc_id))
    doc = res.scalar_one_or_none()
    if not doc:
        raise HTTPException(status_code=404, detail="Not found")
    _ensure_pallet_doc(doc)
    if not payload.cell_code and not payload.location_id:
        raise HTTPException(status_code=400, detail="Не указана ячейка")
    location = await doc_service.resolve_location(db, payload.location_id, payload.cell_code)
    count = max(1, payload.count or 1)
    sscc_list: list[str] = []
    async with doc_service.tx(db):
        for _ in range(count):
            sscc = await allocate_sscc(db)
            hu = HandlingUnit(
                sscc=sscc,
                status=HandlingUnitStatus.putaway,
                location_id=location.id,
                source_doc_id=doc.id,
            )
            db.add(hu)
            await db.flush()
            sscc_list.append(sscc)
    return {"sscc": sscc_list}


@router.post("/{doc_id}/pallets/{sscc}/add-item", response_model=DocumentWithLines)
async def pallet_add_item(
    doc_id: int,
    sscc: str,
    payload: DocPalletItemAdd,
    db: AsyncSession = Depends(get_db),
    current_user: User = Depends(require_role(UserRole.admin, UserRole.worker)),
):
    res = await db.execute(select(Document).where(Document.id == doc_id))
    doc = res.scalar_one_or_none()
    if not doc:
        raise HTTPException(status_code=404, detail="Not found")
    _ensure_pallet_doc(doc)

    sscc_norm = normalize_sscc(sscc)
    stmt_hu = select(HandlingUnit).where(HandlingUnit.sscc == sscc_norm).with_for_update()
    res_hu = await db.execute(stmt_hu)
    hu = res_hu.scalar_one_or_none()
    if not hu:
        raise HTTPException(status_code=404, detail="Паллета не найдена")
    if hu.source_doc_id and hu.source_doc_id != doc.id:
        raise HTTPException(status_code=400, detail="Паллета привязана к другому документу")
    if not hu.source_doc_id:
        hu.source_doc_id = doc.id
    if not hu.location_id:
        meta_cell = doc.meta.get("cell_code") if isinstance(doc.meta, dict) else None
        if meta_cell:
            loc = await doc_service.resolve_location(db, None, meta_cell)
            hu.location_id = loc.id
        else:
            raise HTTPException(status_code=400, detail="У паллеты не задана ячейка")

    product = await doc_service.resolve_product(db, payload.product_id, None)
    async with doc_service.tx(db):
        # upsert content
        stmt_content = (
            select(HandlingUnitContent)
            .where(
                HandlingUnitContent.hu_id == hu.id,
                HandlingUnitContent.product_id == product.id,
                HandlingUnitContent.batch == payload.batch,
                HandlingUnitContent.expiry_date == payload.expiry_date,
            )
            .with_for_update()
        )
        res_content = await db.execute(stmt_content)
        content = res_content.scalar_one_or_none()
        if not content:
            content = HandlingUnitContent(
                hu_id=hu.id,
                product_id=product.id,
                qty=payload.qty,
                batch=payload.batch,
                expiry_date=payload.expiry_date,
            )
            db.add(content)
        else:
            content.qty = float(content.qty) + float(payload.qty)
            db.add(content)

        # also add to document lines to reflect in stock on finish
        location = await doc_service.resolve_location(db, hu.location_id, None)
        await doc_service.add_line(
            db,
            doc,
            product,
            location,
            qty_delta=float(payload.qty),
            batch=payload.batch,
            expiry_date=payload.expiry_date,
        )
        db.add(hu)
    await db.refresh(doc, ["lines"])
    return doc


@router.post("/{doc_id}/pallets/{sscc_scan}/manual", response_model=DocumentWithLines)
async def pallet_add_content_manual(
    doc_id: int,
    sscc_scan: str,
    payload: DocPalletManualAdd,
    db: AsyncSession = Depends(get_db),
    current_user: User = Depends(require_role(UserRole.admin, UserRole.worker)),
):
    res = await db.execute(select(Document).where(Document.id == doc_id))
    doc = res.scalar_one_or_none()
    if not doc:
        raise HTTPException(status_code=404, detail="Not found")
    _ensure_pallet_doc(doc)

    sscc = normalize_sscc(sscc_scan)
    stmt_hu = select(HandlingUnit).where(HandlingUnit.sscc == sscc).with_for_update()
    res_hu = await db.execute(stmt_hu)
    hu = res_hu.scalar_one_or_none()
    if not hu:
        raise HTTPException(status_code=404, detail="Паллета не найдена")
    if hu.source_doc_id and hu.source_doc_id != doc.id:
        raise HTTPException(status_code=404, detail="Паллета привязана к другому документу")
    if not hu.source_doc_id:
        hu.source_doc_id = doc.id
        db.add(hu)
    # попытка проставить ячейку, если не было
    if not hu.location_id:
        meta_cell = doc.meta.get("cell_code") if isinstance(doc.meta, dict) else None
        if meta_cell:
            location = await doc_service.resolve_location(db, None, meta_cell)
            hu.location_id = location.id
            db.add(hu)
        else:
            raise HTTPException(status_code=400, detail="У паллеты не задана ячейка")
    product = await doc_service.resolve_product(db, payload.product_id, payload.barcode)
    # find packing profile
    stmt_profile = select(PackingProfile).where(
        PackingProfile.product_id == product.id,
        PackingProfile.pack_type == payload.pack_type,
        PackingProfile.is_active.is_(True),
    )
    res_profile = await db.execute(stmt_profile)
    profile = res_profile.scalar_one_or_none()
    if not profile:
        raise HTTPException(status_code=404, detail="Профиль упаковки не найден")

    qty = float(payload.pack_count) * float(profile.qty_per_pack)

    async with doc_service.tx(db):
        # upsert content
        stmt_content = select(HandlingUnitContent).where(
            HandlingUnitContent.hu_id == hu.id,
            HandlingUnitContent.product_id == product.id,
            HandlingUnitContent.batch == payload.batch,
            HandlingUnitContent.expiry_date == payload.expiry_date,
        ).with_for_update()
        res_content = await db.execute(stmt_content)
        content = res_content.scalar_one_or_none()
        if not content:
            content = HandlingUnitContent(
                hu_id=hu.id,
                product_id=product.id,
                qty=qty,
                batch=payload.batch,
                expiry_date=payload.expiry_date,
            )
            db.add(content)
        else:
            content.qty = float(content.qty) + qty
            db.add(content)

        location = await doc_service.resolve_location(db, hu.location_id, None)
        await doc_service.add_line(
            db,
            doc,
            product,
            location,
            qty_delta=qty,
            batch=payload.batch,
            expiry_date=payload.expiry_date,
        )
    await db.refresh(doc, ["lines"])
    return doc


@router.post("/{doc_id}/pick-sscc", response_model=DocumentWithLines)
async def pick_sscc(
    doc_id: int,
    payload: dict,
    db: AsyncSession = Depends(get_db),
    current_user: User = Depends(require_role(UserRole.admin, UserRole.worker)),
):
    sscc_scan = payload.get("sscc_scan")
    if not sscc_scan:
        raise HTTPException(status_code=400, detail="sscc_scan is required")
    res = await db.execute(select(Document).where(Document.id == doc_id))
    doc = res.scalar_one_or_none()
    if not doc:
        raise HTTPException(status_code=404, detail="Not found")
    _ensure_outbound_doc(doc)

    sscc = normalize_sscc(sscc_scan)
    stmt_hu = (
        select(HandlingUnit)
        .where(HandlingUnit.sscc == sscc)
        .options(
            selectinload(HandlingUnit.contents),
            selectinload(HandlingUnit.location),
        )
        .with_for_update()
    )
    res_hu = await db.execute(stmt_hu)
    hu = res_hu.scalar_one_or_none()
    if not hu:
        raise HTTPException(status_code=404, detail="Паллета не найдена")
    if hu.status != HandlingUnitStatus.putaway or hu.reserved_doc_id:
        raise HTTPException(status_code=status.HTTP_409_CONFLICT, detail="Паллета уже зарезервирована или недоступна")
    if not hu.location_id:
        raise HTTPException(status_code=400, detail="У паллеты не задана ячейка")

    location = await doc_service.resolve_location(db, hu.location_id, None)
    async with doc_service.tx(db):
        for content in hu.contents:
            product = await doc_service.resolve_product(db, content.product_id, None)
            await doc_service.add_line(
                db,
                doc,
                product,
                location,
                qty_delta=float(content.qty),
                batch=content.batch,
                expiry_date=content.expiry_date,
            )
        hu.status = HandlingUnitStatus.reserved
        hu.reserved_doc_id = doc.id
        db.add(hu)
    await db.refresh(doc, ["lines"])
    return doc


@router.post("/{doc_id}/unpick-sscc", response_model=DocumentWithLines)
async def unpick_sscc(
    doc_id: int,
    payload: dict,
    db: AsyncSession = Depends(get_db),
    current_user: User = Depends(require_role(UserRole.admin, UserRole.worker)),
):
    sscc_scan = payload.get("sscc_scan")
    if not sscc_scan:
        raise HTTPException(status_code=400, detail="sscc_scan is required")
    res = await db.execute(select(Document).where(Document.id == doc_id))
    doc = res.scalar_one_or_none()
    if not doc:
        raise HTTPException(status_code=404, detail="Not found")
    _ensure_outbound_doc(doc)

    sscc = normalize_sscc(sscc_scan)
    stmt_hu = (
        select(HandlingUnit)
        .where(HandlingUnit.sscc == sscc)
        .options(selectinload(HandlingUnit.contents), selectinload(HandlingUnit.location))
        .with_for_update()
    )
    res_hu = await db.execute(stmt_hu)
    hu = res_hu.scalar_one_or_none()
    if not hu:
        raise HTTPException(status_code=404, detail="Паллета не найдена")
    if hu.reserved_doc_id != doc.id:
        raise HTTPException(status_code=status.HTTP_409_CONFLICT, detail="Паллета не принадлежит документу")
    if not hu.location_id:
        raise HTTPException(status_code=400, detail="У паллеты не задана ячейка")

    location = await doc_service.resolve_location(db, hu.location_id, None)
    async with doc_service.tx(db):
        for content in hu.contents:
            product = await doc_service.resolve_product(db, content.product_id, None)
            await doc_service.add_line(
                db,
                doc,
                product,
                location,
                qty_delta=-float(content.qty),
                batch=content.batch,
                expiry_date=content.expiry_date,
            )
        hu.status = HandlingUnitStatus.putaway
        hu.reserved_doc_id = None
        db.add(hu)
    await db.refresh(doc, ["lines"])
    return doc


@router.get("/{doc_id}/pdf")
async def document_pdf(
    doc_id: int,
    db: AsyncSession = Depends(get_db),
    current_user: User = Depends(require_role(UserRole.admin, UserRole.worker)),
):
    res = await db.execute(
        select(Document)
        .where(Document.id == doc_id)
        .options(
            selectinload(Document.lines).selectinload(DocumentLine.product),
            selectinload(Document.lines).selectinload(DocumentLine.location),
            selectinload(Document.counterparty),
        )
    )
    doc = res.scalar_one_or_none()
    if not doc:
        raise HTTPException(status_code=404, detail="Not found")
    pdf_bytes = await render_document_pdf(db, doc)
    await log_action(db, current_user.id, "document_pdf_generated", "document", doc.id, {"status": doc.status})
    return Response(content=pdf_bytes, media_type="application/pdf", headers={"Content-Disposition": f'inline; filename="doc_{doc_id}.pdf"'})
