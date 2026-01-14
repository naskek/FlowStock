import asyncio
from sqlalchemy import select
from app.db import AsyncSessionLocal
from app.models import User, UserRole, Product, Location, Stock
from app.security import hash_password


async def run():
    async with AsyncSessionLocal() as session:
        await seed_users(session)
        await seed_locations(session)
        await seed_products(session)
        await session.commit()


async def seed_users(session):
    users = [
        ("admin", "admin123", UserRole.admin),
        ("worker", "worker123", UserRole.worker),
        ("viewer", "viewer123", UserRole.viewer),
    ]
    for login, pwd, role in users:
        res = await session.execute(select(User).where(User.login == login))
        if res.scalar_one_or_none():
            continue
        session.add(User(login=login, password_hash=hash_password(pwd), role=role, is_active=True))


async def seed_products(session):
    sample = [
        ("SKU-001", "Товар 1", "BrandA", "100000000001"),
        ("SKU-002", "Товар 2", "BrandB", "100000000002"),
        ("SKU-003", "Товар 3", "BrandC", "100000000003"),
        ("SKU-004", "Товар 4", "BrandD", "100000000004"),
        ("SKU-005", "Товар 5", "BrandE", "100000000005"),
    ]
    for sku, name, brand, barcode in sample:
        res = await session.execute(select(Product).where(Product.barcode_ean == barcode))
        if res.scalar_one_or_none():
            continue
        session.add(
            Product(
                sku=sku,
                name=name,
                brand=brand,
                barcode_ean=barcode,
                unit="pcs",
                pack_qty=1,
                is_active=True,
            )
        )


async def seed_locations(session):
    zones = [
        ("Склад сырья", "Сухой склад", "DRY-01"),
        ("Склад сырья", "Холодильник 1", "FRIDGE-01"),
        ("Склад сырья", "Холодильник 2", "FRIDGE-02"),
        ("Склад сырья", "Морозилка", "FREEZER-01"),
        ("Склад ГП", "Готовая продукция", "FG-01"),
        ("Производство", "В производстве", "PROD-01"),
    ]
    for warehouse, zone, code in zones:
        res = await session.execute(select(Location).where(Location.cell_code == code))
        if res.scalar_one_or_none():
            continue
        session.add(Location(warehouse=warehouse, zone=zone, cell_code=code))
    # optional initial stock for demo
    await session.flush()
    res_prod = await session.execute(select(Product).limit(3))
    products = res_prod.scalars().all()
    res_loc = await session.execute(select(Location).where(Location.cell_code.in_(["DRY-01", "FG-01"])))
    locations = {loc.cell_code: loc for loc in res_loc.scalars().all()}
    if products and locations and len(locations) >= 2:
        res_stock = await session.execute(select(Stock))
        if not res_stock.scalars().first():
            session.add(Stock(product_id=products[0].id, location_id=locations["DRY-01"].id, qty=50))
            session.add(Stock(product_id=products[1].id, location_id=locations["DRY-01"].id, qty=30))
            session.add(Stock(product_id=products[2].id, location_id=locations["FG-01"].id, qty=15))


if __name__ == "__main__":
    asyncio.run(run())
