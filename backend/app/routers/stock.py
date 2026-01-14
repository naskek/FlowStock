from fastapi import APIRouter, Depends, Query
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select
from ..models import Stock, Location
from ..schemas import StockItem
from ..deps import get_db, get_current_user

router = APIRouter()


@router.get("", response_model=list[StockItem])
async def list_stock(
    db: AsyncSession = Depends(get_db),
    warehouse: str | None = Query(None),
    zone: str | None = Query(None),
    cell_code: str | None = Query(None),
    product_id: int | None = Query(None),
    current_user=Depends(get_current_user),
):
    stmt = select(Stock).join(Location, Stock.location_id == Location.id)
    if warehouse:
        stmt = stmt.where(Location.warehouse == warehouse)
    if zone:
        stmt = stmt.where(Location.zone == zone)
    if cell_code:
        stmt = stmt.where(Location.cell_code == cell_code)
    if product_id:
        stmt = stmt.where(Stock.product_id == product_id)
    res = await db.execute(stmt)
    return res.scalars().all()

