from datetime import timedelta
from fastapi import APIRouter, Depends, HTTPException, status, Response, Request
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select
from ..db import get_session
from ..models import User, UserRole
from ..schemas import TokenPair, UserBase
from ..security import verify_password, create_token, decode_token
from ..config import get_settings
from ..deps import get_current_user
from ..services.audit import log_action
from ..rate_limit import limiter


router = APIRouter()
settings = get_settings()


@router.post("/login", response_model=TokenPair)
@limiter.limit(f"{settings.rate_limit_login_per_min}/minute")
async def login(request: Request, response: Response, db: AsyncSession = Depends(get_session)):
    data = await request.json()
    login = (data.get("login") or "").strip()
    password = data.get("password") or ""
    if not login:
        raise HTTPException(status_code=400, detail="Missing credentials")
    result = await db.execute(select(User).where(User.login == login))
    user = result.scalar_one_or_none()
    if not user:
        raise HTTPException(status_code=401, detail="Invalid credentials")
    if not user.is_active:
        raise HTTPException(status_code=401, detail="User inactive")
    if user.role == UserRole.admin:
        # Admin login temporarily allowed without a password; if password provided, validate it.
        if password and not verify_password(password, user.password_hash):
            raise HTTPException(status_code=401, detail="Invalid credentials")
    else:
        if not password or not verify_password(password, user.password_hash):
            raise HTTPException(status_code=401, detail="Invalid credentials")
    access_expires = timedelta(minutes=settings.jwt_access_expire_min)
    refresh_expires = timedelta(days=settings.jwt_refresh_expire_days)
    access_token = create_token({"sub": str(user.id), "type": "access"}, access_expires)
    refresh_token = create_token({"sub": str(user.id), "type": "refresh"}, refresh_expires)
    _set_auth_cookies(response, access_token, refresh_token, access_expires, refresh_expires)
    await log_action(db, user.id, "login", "user", user.id, None)
    return TokenPair(
        access_token=access_token,
        refresh_token=refresh_token,
        expires_in=int(access_expires.total_seconds()),
        refresh_expires_in=int(refresh_expires.total_seconds()),
    )


@router.post("/refresh", response_model=TokenPair)
async def refresh(request: Request, response: Response, db: AsyncSession = Depends(get_session)):
    token = request.cookies.get("refresh_token")
    if not token:
        auth = request.headers.get("Authorization")
        if auth and auth.lower().startswith("bearer "):
            token = auth.split(" ", 1)[1]
    if not token:
        raise HTTPException(status_code=401, detail="No refresh token")
    payload = decode_token(token)
    if payload.get("type") != "refresh":
        raise HTTPException(status_code=401, detail="Invalid token type")
    user_id = payload.get("sub")
    result = await db.execute(select(User).where(User.id == int(user_id)))
    user = result.scalar_one_or_none()
    if not user or not user.is_active:
        raise HTTPException(status_code=401, detail="User not found")
    access_expires = timedelta(minutes=settings.jwt_access_expire_min)
    refresh_expires = timedelta(days=settings.jwt_refresh_expire_days)
    access_token = create_token({"sub": str(user.id), "type": "access"}, access_expires)
    refresh_token = create_token({"sub": str(user.id), "type": "refresh"}, refresh_expires)
    _set_auth_cookies(response, access_token, refresh_token, access_expires, refresh_expires)
    return TokenPair(
        access_token=access_token,
        refresh_token=refresh_token,
        expires_in=int(access_expires.total_seconds()),
        refresh_expires_in=int(refresh_expires.total_seconds()),
    )


@router.post("/logout")
async def logout(response: Response, current_user: User = Depends(get_current_user), db: AsyncSession = Depends(get_session)):
    response.delete_cookie("access_token")
    response.delete_cookie("refresh_token")
    await log_action(db, current_user.id, "logout", "user", current_user.id, None)
    return {"detail": "ok"}


@router.get("/me", response_model=UserBase)
async def me(current_user: User = Depends(get_current_user)):
    return current_user


def _set_auth_cookies(response: Response, access_token: str, refresh_token: str, access_exp, refresh_exp):
    response.set_cookie(
        "access_token",
        access_token,
        httponly=True,
        secure=False,
        max_age=int(access_exp.total_seconds()),
        samesite="lax",
    )
    response.set_cookie(
        "refresh_token",
        refresh_token,
        httponly=True,
        secure=False,
        max_age=int(refresh_exp.total_seconds()),
        samesite="lax",
    )
