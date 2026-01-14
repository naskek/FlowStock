from fastapi import APIRouter, Depends, HTTPException, Query, UploadFile, File
import csv
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select, or_, func, case
from ..models import Product, User, UserRole
from ..schemas import ProductBase, ProductCreate, ProductUpdate
from ..deps import get_db, get_current_user, require_role
from ..services.audit import log_action

router = APIRouter()


@router.get("", response_model=list[ProductBase])
async def list_products(
    db: AsyncSession = Depends(get_db),
    q: str | None = Query(None),
    skip: int = 0,
    limit: int = 50,
):
    stmt = select(Product)
    if q:
        stmt = stmt.where(
            or_(
                Product.sku.ilike(f"%{q}%"),
                Product.name.ilike(f"%{q}%"),
                Product.barcode_ean.ilike(f"%{q}%"),
            )
        )
    stmt = stmt.offset(skip).limit(min(limit, 200))
    res = await db.execute(stmt)
    return res.scalars().all()


@router.get("/search", response_model=list[ProductBase])
async def search_products(
    db: AsyncSession = Depends(get_db),
    q: str | None = Query(None, min_length=1),
    limit: int = Query(20, ge=1, le=50),
):
    if not q:
        return []
    q_norm = q.strip()
    q_lower = q_norm.lower()
    like_start = f"{q_lower}%"
    like_any = f"%{q_lower}%"

    # relevance: exact (barcode/sku) -> startswith (barcode/sku/name) -> contains
    exact_match = or_(func.lower(Product.barcode_ean) == q_lower, func.lower(Product.sku) == q_lower)
    starts_match = or_(
        func.lower(Product.barcode_ean).like(like_start),
        func.lower(Product.sku).like(like_start),
        func.lower(Product.name).like(like_start),
    )
    contains_match = or_(
        func.lower(Product.barcode_ean).like(like_any),
        func.lower(Product.sku).like(like_any),
        func.lower(Product.name).like(like_any),
    )

    relevance = case(
        (exact_match, 0),
        (starts_match, 1),
        (contains_match, 2),
        else_=3,
    )

    stmt = (
        select(Product, relevance.label("rel"))
        .where(or_(exact_match, starts_match, contains_match))
        .order_by(relevance, Product.name, Product.id)
        .limit(limit)
    )
    res = await db.execute(stmt)
    return [row[0] for row in res.fetchall()]


@router.post("", response_model=ProductBase)
async def create_product(
    payload: ProductCreate,
    db: AsyncSession = Depends(get_db),
    current_user: User = Depends(require_role(UserRole.admin)),
):
    product = Product(**payload.dict())
    db.add(product)
    await db.commit()
    await db.refresh(product)
    await log_action(db, current_user.id, "product_create", "product", product.id, payload.dict())
    return product


@router.put("/{product_id}", response_model=ProductBase)
async def update_product(
    product_id: int,
    payload: ProductUpdate,
    db: AsyncSession = Depends(get_db),
    current_user: User = Depends(require_role(UserRole.admin)),
):
    res = await db.execute(select(Product).where(Product.id == product_id))
    product = res.scalar_one_or_none()
    if not product:
        raise HTTPException(status_code=404, detail="Product not found")
    for k, v in payload.dict().items():
        setattr(product, k, v)
    db.add(product)
    await db.commit()
    await db.refresh(product)
    await log_action(db, current_user.id, "product_update", "product", product.id, payload.dict())
    return product


@router.delete("/{product_id}")
async def delete_product(
    product_id: int,
    db: AsyncSession = Depends(get_db),
    current_user: User = Depends(require_role(UserRole.admin)),
):
    res = await db.execute(select(Product).where(Product.id == product_id))
    product = res.scalar_one_or_none()
    if not product:
        raise HTTPException(status_code=404, detail="Not found")
    await db.delete(product)
    await db.commit()
    await log_action(db, current_user.id, "product_delete", "product", product.id, None)
    return {"detail": "deleted"}


@router.post("/import")
async def import_products(
    file: UploadFile = File(...),
    db: AsyncSession = Depends(get_db),
    current_user: User = Depends(require_role(UserRole.admin)),
):
    """CSV columns: sku,name,brand,barcode_ean,unit,pack_qty"""
    content = await file.read()
    lines = content.decode("utf-8").splitlines()
    reader = csv.DictReader(lines)
    created, updated = 0, 0
    for row in reader:
        res = await db.execute(select(Product).where(Product.barcode_ean == row.get("barcode_ean")))
        existing = res.scalar_one_or_none()
        payload = {
            "sku": row.get("sku") or "",
            "name": row.get("name") or "",
            "brand": row.get("brand"),
            "barcode_ean": row.get("barcode_ean") or "",
            "unit": row.get("unit") or "pcs",
            "pack_qty": int(row["pack_qty"]) if row.get("pack_qty") else None,
            "is_active": True,
        }
        if existing:
            for k, v in payload.items():
                setattr(existing, k, v)
            updated += 1
        else:
            db.add(Product(**payload))
            created += 1
    await db.commit()
    await log_action(db, current_user.id, "product_import", "product", None, {"created": created, "updated": updated})
    return {"created": created, "updated": updated}
