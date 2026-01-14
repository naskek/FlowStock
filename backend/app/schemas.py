from datetime import datetime, date, timedelta
from typing import Optional, List
from pydantic import BaseModel, Field
from .models import DocumentType, DocumentStatus, UserRole, HandlingUnitStatus, HandlingUnitType, ContactType


class TokenPair(BaseModel):
    access_token: str
    refresh_token: str
    token_type: str = "bearer"
    expires_in: int
    refresh_expires_in: int


class UserBase(BaseModel):
    id: int
    login: str
    role: UserRole
    is_active: bool
    created_at: datetime

    class Config:
        from_attributes = True


class UserCreate(BaseModel):
    login: str
    password: str
    role: UserRole = UserRole.worker
    is_active: bool = True


class UserUpdate(BaseModel):
    role: Optional[UserRole] = None
    is_active: Optional[bool] = None


class PasswordChange(BaseModel):
    password: str


class ProductBase(BaseModel):
    id: int
    sku: str
    name: str
    brand: Optional[str]
    barcode_ean: str
    unit: str
    pack_qty: Optional[int]
    is_active: bool

    class Config:
        from_attributes = True


class ProductCreate(BaseModel):
    sku: str
    name: str
    brand: Optional[str] = None
    barcode_ean: str
    unit: str = "pcs"
    pack_qty: Optional[int] = None
    is_active: bool = True


class ProductUpdate(ProductCreate):
    pass


class LocationBase(BaseModel):
    id: int
    warehouse: str
    zone: Optional[str]
    cell_code: str

    class Config:
        from_attributes = True


class LocationCreate(BaseModel):
    warehouse: str
    zone: Optional[str] = None
    cell_code: str


class LocationUpdate(LocationCreate):
    pass


class ContactBase(BaseModel):
    id: int
    name: str
    type: ContactType
    phone: Optional[str]
    email: Optional[str]
    note: Optional[str]
    is_active: bool

    class Config:
        from_attributes = True


class ContactCreate(BaseModel):
    name: str
    type: ContactType
    phone: Optional[str] = None
    email: Optional[str] = None
    note: Optional[str] = None
    is_active: bool = True


class ContactUpdate(BaseModel):
    name: Optional[str] = None
    type: Optional[ContactType] = None
    phone: Optional[str] = None
    email: Optional[str] = None
    note: Optional[str] = None
    is_active: Optional[bool] = None


class StockItem(BaseModel):
    id: int
    product_id: int
    location_id: int
    qty: float
    batch: Optional[str]
    expiry_date: Optional[date]
    updated_at: datetime

    class Config:
        from_attributes = True


class DocumentLineBase(BaseModel):
    id: int
    doc_id: int
    product_id: int
    location_id: int
    qty_expected: Optional[float]
    qty_fact: float
    batch: Optional[str]
    expiry_date: Optional[date]

    class Config:
        from_attributes = True


class DocumentBase(BaseModel):
    id: int
    type: DocumentType
    status: DocumentStatus
    created_by: int
    counterparty_id: Optional[int]
    counterparty: Optional[ContactBase] = None
    created_at: datetime
    finished_at: Optional[datetime]
    meta: Optional[dict]

    class Config:
        from_attributes = True


class DocumentWithLines(DocumentBase):
    lines: List[DocumentLineBase] = []


class DocumentCreate(BaseModel):
    type: DocumentType
    meta: Optional[dict] = None
    counterparty_id: Optional[int] = None


class DocumentUpdate(BaseModel):
    meta: Optional[dict] = None
    counterparty_id: Optional[int] = None


class DocumentStatusUpdate(BaseModel):
    status: DocumentStatus


class DocumentLineCreate(BaseModel):
    product_id: Optional[int] = None
    barcode: Optional[str] = None
    location_id: Optional[int] = None
    qty_delta: float = 1
    batch: Optional[str] = None
    expiry_date: Optional[date] = None
    cell_code: Optional[str] = None
    zone_code: Optional[str] = None


class ScanPayload(BaseModel):
    barcode: str
    cell_code: Optional[str] = None
    zone_code: Optional[str] = None
    qty: float = 1
    batch: Optional[str] = None
    expiry_date: Optional[date] = None


class InventoryTaskBase(BaseModel):
    id: int
    status: str
    scope: Optional[dict]
    created_by: int
    created_at: datetime
    finished_at: Optional[datetime]

    class Config:
        from_attributes = True


class InventoryTaskCreate(BaseModel):
    scope: Optional[dict] = None


class InventoryScan(BaseModel):
    product_id: Optional[int] = None
    barcode: Optional[str] = None
    cell_code: Optional[str] = None
    zone_code: Optional[str] = None
    qty: float = 1
    batch: Optional[str] = None
    expiry_date: Optional[date] = None


class CompanyInfo(BaseModel):
    name: Optional[str] = None
    inn: Optional[str] = None
    kpp: Optional[str] = None
    address: Optional[str] = None
    phone: Optional[str] = None
    email: Optional[str] = None
    logo: Optional[str] = None
    bank: Optional[str] = None
    account: Optional[str] = None
    warehouse_code: Optional[str] = None


class CompanyEntry(CompanyInfo):
    id: int
    is_active: Optional[bool] = False


class AuditEntry(BaseModel):
    id: int
    user_id: Optional[int]
    action: str
    entity_type: str
    entity_id: Optional[int]
    payload_json: Optional[dict]
    created_at: datetime

    class Config:
        from_attributes = True


class HandlingUnitCreateAuto(BaseModel):
    location_id: Optional[int] = None
    cell_code: Optional[str] = None


class HandlingUnitCreateManual(HandlingUnitCreateAuto):
    sscc_scan: str


class HandlingUnitContentOut(BaseModel):
    product_id: int
    qty: float
    batch: Optional[str]
    expiry_date: Optional[date]

    class Config:
        from_attributes = True


class HandlingUnitOut(BaseModel):
    id: int
    sscc: str
    status: HandlingUnitStatus
    type: HandlingUnitType
    location: Optional[LocationBase] = None

    class Config:
        from_attributes = True


class HandlingUnitDetail(HandlingUnitOut):
    contents: List[HandlingUnitContentOut] = []


class HandlingUnitConsumePayload(BaseModel):
    doc_id: Optional[int] = None
    product_id: int
    qty: float
    batch: Optional[str] = None
    expiry_date: Optional[date] = None


class HandlingUnitOutMinimal(BaseModel):
    id: int
    sscc: str
    location_id: Optional[int] = None
    created_at: Optional[datetime] = None
    total_qty: Optional[float] = None

class PackingProfileBase(BaseModel):
    id: int
    product_id: int
    pack_type: str
    qty_per_pack: float
    is_active: bool = True

    class Config:
        from_attributes = True


class PackingProfileCreate(BaseModel):
    product_id: int
    pack_type: str
    qty_per_pack: float
    is_active: bool = True


class PackingProfileUpdate(BaseModel):
    pack_type: Optional[str] = None
    qty_per_pack: Optional[float] = None
    is_active: Optional[bool] = None


class DocPalletAutoCreate(BaseModel):
    cell_code: Optional[str] = None
    location_id: Optional[int] = None
    count: int = 1


class DocPalletItemAdd(BaseModel):
    product_id: int
    qty: float
    batch: Optional[str] = None
    expiry_date: Optional[date] = None


class DocPalletManualAdd(BaseModel):
    barcode: Optional[str] = None
    product_id: Optional[int] = None
    pack_type: str
    pack_count: float
    batch: Optional[str] = None
    expiry_date: Optional[date] = None
