import logging

from sqlalchemy.ext.asyncio import create_async_engine, async_sessionmaker, AsyncSession
from sqlalchemy.orm import DeclarativeBase
from .config import get_settings


logger = logging.getLogger(__name__)

settings = get_settings()

def _create_engine():
    try:
        return create_async_engine(settings.database_url, echo=False, future=True)
    except ModuleNotFoundError as e:
        if "asyncpg" in str(e):
            # Fallback for environments without asyncpg (e.g., local tests)
            fallback_url = "sqlite+aiosqlite:///:memory:"
            logger.warning("asyncpg not installed, falling back to %s", fallback_url)
            return create_async_engine(fallback_url, echo=False, future=True)
        raise

engine = _create_engine()
AsyncSessionLocal = async_sessionmaker(engine, expire_on_commit=False, class_=AsyncSession)


class Base(DeclarativeBase):
    pass


async def get_session() -> AsyncSession:
    async with AsyncSessionLocal() as session:
        yield session
