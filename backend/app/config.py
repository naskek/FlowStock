from pydantic_settings import BaseSettings
from pydantic import Field
from functools import lru_cache


class Settings(BaseSettings):
    app_env: str = Field("dev", alias="APP_ENV")
    db_host: str = Field("localhost", alias="DB_HOST")
    db_port: int = Field(5432, alias="DB_PORT")
    db_name: str = Field("tsd", alias="DB_NAME")
    db_user: str = Field("tsd", alias="DB_USER")
    db_password: str = Field("tsdpass", alias="DB_PASSWORD")
    jwt_secret: str = Field("devsecret", alias="JWT_SECRET")
    jwt_access_expire_min: int = Field(15, alias="JWT_ACCESS_EXPIRE_MIN")
    jwt_refresh_expire_days: int = Field(7, alias="JWT_REFRESH_EXPIRE_DAYS")
    rate_limit_login_per_min: int = Field(8, alias="RATE_LIMIT_LOGIN_PER_MIN")
    allow_outbound_negative: bool = Field(False, alias="ALLOW_OUTBOUND_NEGATIVE")
    admin_override_negative: bool = Field(True, alias="ADMIN_OVERRIDE_NEGATIVE")
    production_location_code: str = Field("PROD-01", alias="PRODUCTION_LOCATION_CODE")
    gs1_company_prefix: str = Field("460704615", alias="GS1_COMPANY_PREFIX")
    sscc_extension_digit: str = Field("0", alias="SSCC_EXTENSION_DIGIT")
    sscc_serial_length: int = Field(7, alias="SSCC_SERIAL_LENGTH")
    sscc_validate_check_digit: bool = Field(True, alias="SSCC_VALIDATE_CHECK_DIGIT")

    class Config:
        env_file = ".env"
        case_sensitive = False

    @property
    def database_url(self) -> str:
        return (
            f"postgresql+asyncpg://{self.db_user}:{self.db_password}"
            f"@{self.db_host}:{self.db_port}/{self.db_name}"
        )


@lru_cache
def get_settings() -> Settings:
    return Settings()  # type: ignore[arg-type]
