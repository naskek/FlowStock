ALTER TABLE tsd_devices
    ADD COLUMN IF NOT EXISTS access_role TEXT NOT NULL DEFAULT 'OPERATOR';

ALTER TABLE tsd_devices
    DROP CONSTRAINT IF EXISTS ck_tsd_devices_access_role;

ALTER TABLE tsd_devices
    ADD CONSTRAINT ck_tsd_devices_access_role
    CHECK (access_role IN ('OPERATOR', 'ADMIN'));

CREATE TABLE IF NOT EXISTS pc_web_sessions (
    id BIGSERIAL PRIMARY KEY,
    account_id BIGINT NOT NULL REFERENCES tsd_devices(id) ON DELETE CASCADE,
    token_hash TEXT NOT NULL UNIQUE,
    created_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    revoked_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS ix_pc_web_sessions_account
    ON pc_web_sessions(account_id);

CREATE INDEX IF NOT EXISTS ix_pc_web_sessions_expires
    ON pc_web_sessions(expires_at);
