-- Только additive backup security. Нет implicit grants и изменения legacy PC/TSD прав.
ALTER TABLE tsd_devices ADD COLUMN backup_auth_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE pc_web_sessions ADD COLUMN backup_auth_version BIGINT;

CREATE TABLE backup_grants (
    account_id BIGINT NOT NULL REFERENCES tsd_devices(id) ON DELETE CASCADE,
    permission TEXT NOT NULL CHECK (permission IN
        ('backup.read','backup.create','backup.configure','backup.probe','backup.replicate')),
    PRIMARY KEY (account_id, permission)
);

CREATE TABLE backup_credential_refs (
    credential_ref UUID PRIMARY KEY,
    revision UUID NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('MISSING','CONFIGURED','REVOKED')),
    expires_at TIMESTAMPTZ NOT NULL
);

CREATE TABLE backup_security_events (
    id BIGSERIAL PRIMARY KEY,
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    account_id BIGINT,
    action TEXT NOT NULL CHECK (action IN ('GRANTS_REPLACED','GRANTS_INVALIDATED','REFERENCE_CHANGED'))
);

-- Legacy machine account administration must never become backup impersonation:
-- changing credentials/authorization removes backup grants, even if later reverted.
CREATE FUNCTION invalidate_backup_authorization() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF ROW(OLD.password_salt, OLD.password_hash, OLD.password_iterations,
           OLD.is_active, OLD.platform, OLD.access_role)
       IS DISTINCT FROM
       ROW(NEW.password_salt, NEW.password_hash, NEW.password_iterations,
           NEW.is_active, NEW.platform, NEW.access_role) THEN
        NEW.backup_auth_version := OLD.backup_auth_version + 1;
        IF EXISTS (SELECT 1 FROM backup_grants WHERE account_id = OLD.id) THEN
            DELETE FROM backup_grants WHERE account_id = OLD.id;
            INSERT INTO backup_security_events(account_id, action) VALUES(OLD.id, 'GRANTS_INVALIDATED');
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER tsd_devices_backup_authorization BEFORE UPDATE ON tsd_devices
    FOR EACH ROW EXECUTE FUNCTION invalidate_backup_authorization();
