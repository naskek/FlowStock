import base64
import io
from datetime import datetime
from typing import Optional, Tuple
from pathlib import Path

from fpdf import FPDF
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import text

from ..models import Document, DocumentType, DocumentLine

# Unicode-шрифт (Arial) кладём в образ и используем для русских символов.
FONT_PATH = Path(__file__).resolve().parent.parent / "static" / "fonts" / "Arial.ttf"


async def ensure_font():
    """
    Возвращает путь к ttf-шрифту.
    Работает только с локальным файлом внутри образа (без сети).
    """
    if FONT_PATH.exists() and FONT_PATH.stat().st_size > 50_000:
        return str(FONT_PATH)
    raise RuntimeError(
        f"Font not found or corrupted at {FONT_PATH}. "
        "Положите Arial.ttf в backend/app/static/fonts."
    )


def detect_image_type(data: bytes) -> Optional[str]:
    if data.startswith(b"\x89PNG\r\n\x1a\n"):
        return "png"
    if data.startswith(b"\xff\xd8"):
        return "jpg"
    return None


def parse_logo_data(value: str) -> Optional[Tuple[bytes, str]]:
    if not value:
        return None
    if value.startswith("data:"):
        header, b64 = value.split(",", 1)
        if ";base64" not in header:
            return None
        mime = header[5:].split(";", 1)[0].lower()
        image_type = mime.split("/")[-1] if "/" in mime else mime
        if image_type == "jpeg":
            image_type = "jpg"
        try:
            data = base64.b64decode(b64)
        except Exception:
            return None
        if image_type not in ("png", "jpg"):
            image_type = detect_image_type(data) or image_type
        if image_type not in ("png", "jpg"):
            return None
        return data, image_type
    try:
        data = base64.b64decode(value)
    except Exception:
        return None
    image_type = detect_image_type(data)
    if not image_type:
        return None
    return data, image_type


def get_image_size(data: bytes, image_type: str) -> Optional[Tuple[int, int]]:
    if image_type == "png":
        if len(data) < 24:
            return None
        width = int.from_bytes(data[16:20], "big")
        height = int.from_bytes(data[20:24], "big")
        return width, height
    if image_type == "jpg":
        idx = 2
        while idx + 9 < len(data):
            if data[idx] != 0xFF:
                return None
            marker = data[idx + 1]
            if marker in (0xC0, 0xC2):
                height = int.from_bytes(data[idx + 5 : idx + 7], "big")
                width = int.from_bytes(data[idx + 7 : idx + 9], "big")
                return width, height
            if marker == 0xDA:
                return None
            if idx + 4 >= len(data):
                return None
            segment_len = int.from_bytes(data[idx + 2 : idx + 4], "big")
            if segment_len < 2:
                return None
            idx += 2 + segment_len
    return None


def format_doc_number(doc: Document):
    prefix = {
        DocumentType.inbound: "ПР",
        DocumentType.outbound: "ОТ",
        DocumentType.production_issue: "СП",
        DocumentType.production_receipt: "ГП",
        DocumentType.inventory: "ИНВ",
    }.get(doc.type, "DOC")
    dt = doc.created_at or datetime.utcnow()
    year = dt.year
    month = str(dt.month).zfill(2)
    num = str(doc.id).zfill(6)
    return f"{prefix}-{year}{month}-{num}"


def status_label(status: str) -> str:
    return {
        "draft": "Черновик",
        "in_progress": "В процессе",
        "done": "Завершён",
        "canceled": "Удалён",
    }.get(status, status)


async def load_company(db: AsyncSession) -> dict:
    await db.execute(
        text(
            """CREATE TABLE IF NOT EXISTS company_settings (
                id SERIAL PRIMARY KEY,
                payload JSONB,
                created_at TIMESTAMPTZ DEFAULT NOW()
            );"""
        )
    )
    await db.execute(
        text(
            """CREATE TABLE IF NOT EXISTS company_settings (
                id SERIAL PRIMARY KEY,
                payload JSONB,
                is_active BOOLEAN DEFAULT FALSE,
                created_at TIMESTAMPTZ DEFAULT NOW()
            );"""
        )
    )
    res = await db.execute(
        text("SELECT payload FROM company_settings WHERE is_active = TRUE ORDER BY id DESC LIMIT 1")
    )
    row = res.first()
    if row and row[0]:
        return row[0]
    res = await db.execute(text("SELECT payload FROM company_settings ORDER BY id DESC LIMIT 1"))
    row = res.first()
    return row[0] if row and row[0] else {}


async def render_document_pdf(db: AsyncSession, doc: Document) -> bytes:
    font_path = await ensure_font()
    company = await load_company(db)

    pdf = FPDF()
    pdf.add_page()
    try:
        pdf.add_font("Arial", "", font_path, uni=True)
        pdf.add_font("Arial", "B", font_path, uni=True)  # используем тот же файл для bold
    except Exception:
        try:
            FONT_PATH.unlink(missing_ok=True)  # type: ignore[attr-defined]
        except Exception:
            pass
        font_path = await ensure_font()
        pdf.add_font("Arial", "", font_path, uni=True)
        pdf.add_font("Arial", "B", font_path, uni=True)
    pdf.set_font("Arial", "", 12)
    start_x = pdf.get_x()
    start_y = pdf.get_y()
    logo_data = company.get("logo")
    if not isinstance(logo_data, str):
        logo_data = ""
    logo_w = 0.0
    logo_h = 0.0
    if logo_data:
        parsed = parse_logo_data(logo_data)
        if parsed:
            logo_bytes, logo_type = parsed
            size = get_image_size(logo_bytes, logo_type)
            max_w, max_h = 30.0, 18.0
            if size:
                width_px, height_px = size
                if width_px > 0 and height_px > 0:
                    scale = min(max_w / width_px, max_h / height_px)
                    logo_w = max(1.0, width_px * scale)
                    logo_h = max(1.0, height_px * scale)
            if logo_w == 0 or logo_h == 0:
                logo_w, logo_h = max_w, max_h
            try:
                pdf.image(io.BytesIO(logo_bytes), x=start_x, y=start_y, w=logo_w, h=logo_h, type=logo_type)
            except Exception:
                logo_w = 0
                logo_h = 0

    text_x = start_x + logo_w + 4 if logo_w else start_x
    pdf.set_xy(text_x, start_y)
    pdf.cell(0, 10, company.get("name", "Компания"), ln=1)
    requisites = []
    if company.get("inn"):
        requisites.append(f"ИНН: {company.get('inn')}")
    if company.get("kpp"):
        requisites.append(f"КПП: {company.get('kpp')}")
    if company.get("address"):
        requisites.append(company.get("address"))
    if company.get("phone"):
        requisites.append(f"Тел.: {company.get('phone')}")
    if company.get("email"):
        requisites.append(company.get("email"))
    for line in requisites:
        pdf.set_x(text_x)
        pdf.cell(0, 8, line, ln=1)
    content_bottom = pdf.get_y()
    logo_bottom = start_y + logo_h
    pdf.set_y(max(content_bottom, logo_bottom) + 4)
    pdf.set_font("Arial", "B", 14)
    pdf.cell(0, 10, f"Документ {format_doc_number(doc)}", ln=1)
    pdf.set_font("Arial", "", 12)
    pdf.cell(0, 8, f"Тип: {doc.type}", ln=1)
    pdf.cell(
        0,
        8,
        f"Дата: {doc.created_at.strftime('%d.%m.%Y %H:%M:%S') if doc.created_at else '-'} · Статус: {status_label(doc.status)}",
        ln=1,
    )
    if getattr(doc, "counterparty", None):
        name = getattr(doc.counterparty, "name", None) or ""
        pdf.cell(0, 8, f"Контрагент: {name}", ln=1)
    if doc.meta and doc.meta.get("cell_code"):
        pdf.cell(0, 8, f"Склад/зона: {doc.meta.get('cell_code')}", ln=1)

    pdf.ln(4)
    pdf.set_font("Arial", "B", 12)
    pdf.cell(60, 8, "Товар", border=1)
    pdf.cell(40, 8, "Зона", border=1)
    pdf.cell(30, 8, "Кол-во", border=1)
    pdf.cell(30, 8, "Партия", border=1)
    pdf.cell(30, 8, "Годен до", border=1, ln=1)
    pdf.set_font("Arial", "", 11)
    for line in doc.lines or []:
        prod_name = getattr(line, "product", None)
        prod_val = ""
        if prod_name:
            prod_val = prod_name.name or prod_name.sku or str(prod_name.id)
        else:
            prod_val = str(line.product_id)
        loc = getattr(line, "location", None)
        loc_val = loc.cell_code if loc else str(line.location_id)
        pdf.cell(60, 8, prod_val[:28], border=1)
        pdf.cell(40, 8, loc_val[:18], border=1)
        pdf.cell(30, 8, str(line.qty_fact), border=1)
        pdf.cell(30, 8, (line.batch or "")[:12], border=1)
        exp = line.expiry_date.strftime("%d.%m.%Y") if line.expiry_date else ""
        pdf.cell(30, 8, exp, border=1, ln=1)

    buffer = io.BytesIO()
    pdf.output(buffer, dest="S")
    return buffer.getvalue()
