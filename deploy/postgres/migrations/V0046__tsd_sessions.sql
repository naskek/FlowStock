-- TSD bearer credentials, never public device_id. Session deletion cascades with account.
CREATE TABLE tsd_sessions (
    token_hash BYTEA PRIMARY KEY,
    account_id BIGINT NOT NULL REFERENCES tsd_devices(id) ON DELETE CASCADE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at TIMESTAMPTZ NOT NULL,
    revoked_at TIMESTAMPTZ NULL,
    CONSTRAINT ck_tsd_sessions_expiry CHECK (expires_at > created_at)
);
CREATE INDEX ix_tsd_sessions_account ON tsd_sessions(account_id);
