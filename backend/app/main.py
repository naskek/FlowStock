from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware
from slowapi.middleware import SlowAPIMiddleware
from slowapi.errors import RateLimitExceeded
from slowapi import _rate_limit_exceeded_handler
from .config import get_settings
from .rate_limit import limiter
from .routers import (
    auth,
    products,
    locations,
    stock,
    documents,
    inventory,
    audit,
    health,
    users,
    company,
    handling_units,
    packing_profiles,
    contacts,
)


settings = get_settings()

app = FastAPI(title="TSD Warehouse API", version="0.1.0", docs_url="/swagger", redoc_url=None)
app.state.limiter = limiter
app.add_exception_handler(RateLimitExceeded, _rate_limit_exceeded_handler)
app.add_middleware(SlowAPIMiddleware)
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

app.include_router(health.router, tags=["health"])
app.include_router(auth.router, prefix="/auth", tags=["auth"])
app.include_router(products.router, prefix="/products", tags=["products"])
app.include_router(locations.router, prefix="/locations", tags=["locations"])
app.include_router(stock.router, prefix="/stock", tags=["stock"])
app.include_router(documents.router, prefix="/docs", tags=["documents"])
app.include_router(inventory.router, prefix="/inventory", tags=["inventory"])
app.include_router(audit.router, prefix="/audit", tags=["audit"])
app.include_router(users.router, prefix="/users", tags=["users"])
app.include_router(company.router, prefix="/company", tags=["company"])
app.include_router(handling_units.router)
app.include_router(packing_profiles.router)
app.include_router(contacts.router, prefix="/contacts", tags=["contacts"])


@app.get("/")
async def root():
    return {"status": "ok"}
