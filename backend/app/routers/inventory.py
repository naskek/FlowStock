from datetime import datetime
from fastapi import APIRouter, Depends, HTTPException
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select
from ..models import InventoryTask, InventoryTaskLine, User, UserRole, Document, DocumentStatus, DocumentType, DocumentLine
from ..schemas import InventoryTaskCreate, InventoryTaskBase, InventoryScan, DocumentBase
from ..deps import get_db, require_role
from ..services import documents as doc_service
from ..services.audit import log_action


router = APIRouter()


@router.post("/tasks", response_model=InventoryTaskBase)
async def create_task(
    payload: InventoryTaskCreate,
    db: AsyncSession = Depends(get_db),
    current_user: User = Depends(require_role(UserRole.admin, UserRole.worker)),
):
    task = InventoryTask(scope=payload.scope, status="in_progress", created_by=current_user.id)
    db.add(task)
    await db.commit()
    await db.refresh(task)
    await log_action(db, current_user.id, "inventory_task_create", "inventory_task", task.id, payload.scope)
    return task


@router.post("/tasks/{task_id}/scan", response_model=InventoryTaskBase)
async def scan_task(
    task_id: int,
    payload: InventoryScan,
    db: AsyncSession = Depends(get_db),
    current_user: User = Depends(require_role(UserRole.admin, UserRole.worker)),
):
    res = await db.execute(select(InventoryTask).where(InventoryTask.id == task_id))
    task = res.scalar_one_or_none()
    if not task:
        raise HTTPException(status_code=404, detail="Task not found")
    if task.status != "in_progress":
        raise HTTPException(status_code=400, detail="Task finished")
    product = await doc_service.resolve_product(db, payload.product_id, payload.barcode)
    if not payload.cell_code and not payload.zone_code:
        raise HTTPException(status_code=400, detail="Зона обязательна")
    location = await doc_service.resolve_location(db, None, payload.cell_code, payload.zone_code)
    stmt = select(InventoryTaskLine).where(
        InventoryTaskLine.task_id == task.id,
        InventoryTaskLine.product_id == product.id,
        InventoryTaskLine.location_id == location.id,
        InventoryTaskLine.batch == payload.batch,
        InventoryTaskLine.expiry_date == payload.expiry_date,
    )
    line_res = await db.execute(stmt)
    line = line_res.scalar_one_or_none()
    if not line:
        line = InventoryTaskLine(
            task_id=task.id,
            product_id=product.id,
            location_id=location.id,
            qty_fact=payload.qty,
            batch=payload.batch,
            expiry_date=payload.expiry_date,
        )
        db.add(line)
    else:
        line.qty_fact = float(line.qty_fact) + payload.qty
    await db.commit()
    await db.refresh(task, ["lines"])
    await log_action(db, current_user.id, "inventory_scan", "inventory_task", task.id, {"qty": payload.qty})
    return task


@router.post("/tasks/{task_id}/finish", response_model=DocumentBase)
async def finish_task(
    task_id: int,
    db: AsyncSession = Depends(get_db),
    current_user: User = Depends(require_role(UserRole.admin, UserRole.worker)),
):
    res = await db.execute(select(InventoryTask).where(InventoryTask.id == task_id))
    task = res.scalar_one_or_none()
    if not task:
        raise HTTPException(status_code=404, detail="Task not found")
    await db.refresh(task, ["lines"])
    doc = Document(
        type=DocumentType.inventory,
        status=DocumentStatus.in_progress,
        created_by=current_user.id,
        meta={"task_id": task.id},
    )
    db.add(doc)
    await db.flush()
    for line in task.lines:
        db.add(
            DocumentLine(
                doc_id=doc.id,
                product_id=line.product_id,
                location_id=line.location_id,
                qty_fact=line.qty_fact,
                batch=line.batch,
                expiry_date=line.expiry_date,
            )
        )
    await db.commit()
    await doc_service.finish_document(db, doc, current_user)
    task.status = "done"
    task.finished_at = datetime.utcnow()
    db.add(task)
    await db.commit()
    await log_action(db, current_user.id, "inventory_finish", "inventory_task", task.id, {"doc_id": doc.id})
    await db.refresh(doc)
    return doc
