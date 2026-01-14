import enum
from datetime import datetime, date
from typing import Optional

from sqlalchemy import (
    String,
    Integer,
    DateTime,
    Boolean,
    Enum,
    ForeignKey,
    Numeric,
    UniqueConstraint,
    JSON,
    Index,
    Date,
    Text,
)
from sqlalchemy.orm import Mapped, mapped_column, relationship
from .db import Base


class UserRole(str, enum.Enum):
    admin = "admin"
    worker = "worker"
    viewer = "viewer"


class DocumentType(str, enum.Enum):
    inbound = "inbound"
    outbound = "outbound"
    inventory = "inventory"
    move = "move"
    production_issue = "production_issue"
    production_receipt = "production_receipt"


class DocumentStatus(str, enum.Enum):
    draft = "draft"
    in_progress = "in_progress"
    done = "done"
    canceled = "canceled"


class HandlingUnitStatus(str, enum.Enum):
    created = "created"
    putaway = "putaway"
    reserved = "reserved"
    shipped = "shipped"
    quarantine = "quarantine"


class HandlingUnitType(str, enum.Enum):
    pallet = "pallet"


class ContactType(str, enum.Enum):
    supplier = "supplier"
    customer = "customer"
    both = "both"


class User(Base):
    __tablename__ = "users"

    id: Mapped[int] = mapped_column(Integer, primary_key=True, index=True)
    login: Mapped[str] = mapped_column(String(50), unique=True, index=True)
    password_hash: Mapped[str] = mapped_column(String(255))
    role: Mapped[UserRole] = mapped_column(Enum(UserRole))
    is_active: Mapped[bool] = mapped_column(Boolean, default=True)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow)

    audit_entries: Mapped[list["AuditLog"]] = relationship(back_populates="user")


class Product(Base):
    __tablename__ = "products"
    __table_args__ = (
        UniqueConstraint("barcode_ean"),
        Index("idx_products_sku", "sku"),
        Index("idx_products_name", "name"),
    )

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    sku: Mapped[str] = mapped_column(String(64))
    name: Mapped[str] = mapped_column(String(255))
    brand: Mapped[Optional[str]] = mapped_column(String(120), nullable=True)
    barcode_ean: Mapped[str] = mapped_column(String(64))
    unit: Mapped[str] = mapped_column(String(16), default="pcs")
    pack_qty: Mapped[Optional[int]] = mapped_column(Integer, nullable=True)
    is_active: Mapped[bool] = mapped_column(Boolean, default=True)

    stock_items: Mapped[list["Stock"]] = relationship(back_populates="product")


class Location(Base):
    __tablename__ = "locations"
    __table_args__ = (UniqueConstraint("cell_code"), Index("idx_location_cell", "cell_code"),)

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    warehouse: Mapped[str] = mapped_column(String(64))
    zone: Mapped[Optional[str]] = mapped_column(String(64), nullable=True)
    cell_code: Mapped[str] = mapped_column(String(64))

    stock_items: Mapped[list["Stock"]] = relationship(back_populates="location")


class Contact(Base):
    __tablename__ = "contacts"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    name: Mapped[str] = mapped_column(String(255))
    type: Mapped[ContactType] = mapped_column(Enum(ContactType))
    phone: Mapped[Optional[str]] = mapped_column(String(64), nullable=True)
    email: Mapped[Optional[str]] = mapped_column(String(255), nullable=True)
    note: Mapped[Optional[str]] = mapped_column(Text, nullable=True)
    is_active: Mapped[bool] = mapped_column(Boolean, default=True)

    documents: Mapped[list["Document"]] = relationship(back_populates="counterparty")


class Stock(Base):
    __tablename__ = "stock"
    __table_args__ = (
        Index(
            "idx_stock_product_location_batch_expiry",
            "product_id",
            "location_id",
            "batch",
            "expiry_date",
            unique=True,
        ),
    )

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    product_id: Mapped[int] = mapped_column(ForeignKey("products.id"))
    location_id: Mapped[int] = mapped_column(ForeignKey("locations.id"))
    qty: Mapped[float] = mapped_column(Numeric(scale=2), default=0)
    batch: Mapped[Optional[str]] = mapped_column(String(64), nullable=True)
    expiry_date: Mapped[Optional[date]] = mapped_column(Date, nullable=True)
    updated_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow, onupdate=datetime.utcnow)

    product: Mapped[Product] = relationship(back_populates="stock_items")
    location: Mapped[Location] = relationship(back_populates="stock_items")


class Document(Base):
    __tablename__ = "documents"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    type: Mapped[DocumentType] = mapped_column(Enum(DocumentType))
    status: Mapped[DocumentStatus] = mapped_column(Enum(DocumentStatus), default=DocumentStatus.draft)
    created_by: Mapped[int] = mapped_column(ForeignKey("users.id"))
    counterparty_id: Mapped[Optional[int]] = mapped_column(
        ForeignKey("contacts.id", ondelete="SET NULL"), nullable=True
    )
    created_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow)
    finished_at: Mapped[Optional[datetime]] = mapped_column(DateTime, nullable=True)
    meta: Mapped[Optional[dict]] = mapped_column(JSON, nullable=True)

    lines: Mapped[list["DocumentLine"]] = relationship(back_populates="document", cascade="all, delete-orphan")
    counterparty: Mapped[Optional[Contact]] = relationship(back_populates="documents", lazy="selectin")


class DocumentLine(Base):
    __tablename__ = "document_lines"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    doc_id: Mapped[int] = mapped_column(ForeignKey("documents.id"))
    product_id: Mapped[int] = mapped_column(ForeignKey("products.id"))
    location_id: Mapped[int] = mapped_column(ForeignKey("locations.id"))
    qty_expected: Mapped[Optional[float]] = mapped_column(Numeric(scale=2), nullable=True)
    qty_fact: Mapped[float] = mapped_column(Numeric(scale=2), default=0)
    batch: Mapped[Optional[str]] = mapped_column(String(64), nullable=True)
    expiry_date: Mapped[Optional[date]] = mapped_column(Date, nullable=True)

    document: Mapped[Document] = relationship(back_populates="lines")
    product: Mapped[Product] = relationship()
    location: Mapped[Location] = relationship()


class AuditLog(Base):
    __tablename__ = "audit_log"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    user_id: Mapped[Optional[int]] = mapped_column(ForeignKey("users.id"), nullable=True)
    action: Mapped[str] = mapped_column(String(120))
    entity_type: Mapped[str] = mapped_column(String(120))
    entity_id: Mapped[Optional[int]] = mapped_column(Integer, nullable=True)
    payload_json: Mapped[Optional[dict]] = mapped_column(JSON, nullable=True)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow, index=True)

    user: Mapped[Optional[User]] = relationship(back_populates="audit_entries")


class InventoryTask(Base):
    __tablename__ = "inventory_tasks"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    status: Mapped[str] = mapped_column(String(32), default="in_progress")
    scope: Mapped[Optional[dict]] = mapped_column(JSON, nullable=True)
    created_by: Mapped[int] = mapped_column(ForeignKey("users.id"))
    created_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow)
    finished_at: Mapped[Optional[datetime]] = mapped_column(DateTime, nullable=True)

    lines: Mapped[list["InventoryTaskLine"]] = relationship(
        back_populates="task", cascade="all, delete-orphan"
    )


class InventoryTaskLine(Base):
    __tablename__ = "inventory_task_lines"
    __table_args__ = (
        UniqueConstraint("task_id", "product_id", "location_id", name="uq_inv_task_line"),
    )

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    task_id: Mapped[int] = mapped_column(ForeignKey("inventory_tasks.id"))
    product_id: Mapped[int] = mapped_column(ForeignKey("products.id"))
    location_id: Mapped[int] = mapped_column(ForeignKey("locations.id"))
    qty_fact: Mapped[float] = mapped_column(Numeric(scale=2), default=0)
    batch: Mapped[Optional[str]] = mapped_column(String(64), nullable=True)
    expiry_date: Mapped[Optional[date]] = mapped_column(Date, nullable=True)

    task: Mapped[InventoryTask] = relationship(back_populates="lines")
    product: Mapped[Product] = relationship()
    location: Mapped[Location] = relationship()


class HandlingUnit(Base):
    __tablename__ = "handling_units"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    sscc: Mapped[str] = mapped_column(String(18), unique=True, index=True)
    type: Mapped[HandlingUnitType] = mapped_column(Enum(HandlingUnitType), default=HandlingUnitType.pallet)
    status: Mapped[HandlingUnitStatus] = mapped_column(Enum(HandlingUnitStatus), default=HandlingUnitStatus.created)
    location_id: Mapped[Optional[int]] = mapped_column(ForeignKey("locations.id"), nullable=True)
    source_doc_id: Mapped[Optional[int]] = mapped_column(ForeignKey("documents.id"), nullable=True)
    reserved_doc_id: Mapped[Optional[int]] = mapped_column(ForeignKey("documents.id"), nullable=True)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow)

    contents: Mapped[list["HandlingUnitContent"]] = relationship(
        back_populates="handling_unit", cascade="all, delete-orphan"
    )
    location: Mapped[Optional[Location]] = relationship()


class HandlingUnitContent(Base):
    __tablename__ = "handling_unit_contents"
    __table_args__ = (
        UniqueConstraint("hu_id", "product_id", "batch", "expiry_date", name="uq_handling_unit_content"),
    )

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    hu_id: Mapped[int] = mapped_column(ForeignKey("handling_units.id"))
    product_id: Mapped[int] = mapped_column(ForeignKey("products.id"))
    qty: Mapped[float] = mapped_column(Numeric(scale=2), default=0)
    batch: Mapped[Optional[str]] = mapped_column(String(64), nullable=True)
    expiry_date: Mapped[Optional[date]] = mapped_column(Date, nullable=True)

    handling_unit: Mapped[HandlingUnit] = relationship(back_populates="contents")
    product: Mapped[Product] = relationship()


class SSCCSequence(Base):
    __tablename__ = "sscc_sequences"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    company_prefix: Mapped[str] = mapped_column(String(32), unique=True)
    extension_digit: Mapped[str] = mapped_column(String(1))
    serial_length: Mapped[int] = mapped_column(Integer)
    next_serial: Mapped[int] = mapped_column(Integer, default=1)
    updated_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow, onupdate=datetime.utcnow)


class PackingProfile(Base):
    __tablename__ = "packing_profiles"
    __table_args__ = (
        UniqueConstraint("product_id", "pack_type", name="uq_packing_profile_product_type"),
    )

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    product_id: Mapped[int] = mapped_column(ForeignKey("products.id"))
    pack_type: Mapped[str] = mapped_column(String(64))
    qty_per_pack: Mapped[float] = mapped_column(Numeric(scale=3))
    is_active: Mapped[bool] = mapped_column(Boolean, default=True)

    product: Mapped[Product] = relationship()
