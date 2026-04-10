# FlowStock Deployment

## Overview
- Production deploy is driven by `deploy/docker-compose.yml`.
- PostgreSQL init scripts in `deploy/postgres/init/` are bootstrap-only for a brand new empty data directory.
- All schema updates after that go through versioned SQL migrations from `deploy/postgres/migrations/`.
- The server is expected to keep a normal git clone of the repository.
- Manual git-driven updates are performed from GitHub on demand; there is no automatic deploy loop.
- The recommended update path is:
  1. backup
  2. migrate schema
  3. recreate application containers
  4. verify health

## URL Scheme
- Production keeps web clients on separate root URLs with no `/pc` or `/tsd` prefix.
- `https://SERVER_IP:7154/` serves the PC web client.
- `http://SERVER_IP:7153/` serves the TSD web client.
- `deploy/.env.example` uses `7153` / `7154` as the default external ports for this scheme.

## Required Files And Directories
- `deploy/.env`
  - create it from `deploy/.env.example`
- `deploy/runtime/`
  - created automatically by scripts for manual backups and operator artifacts
- `FLOWSTOCK_CA_DIR`
  - external directory for local CA materials
  - do not store the CA private key in git

## Base Image Source
- By default the server image is built from:
  - `mcr.microsoft.com/dotnet/sdk:8.0`
  - `mcr.microsoft.com/dotnet/aspnet:8.0`
- If the server has poor connectivity to `mcr.microsoft.com`, override them in `deploy/.env`:
```bash
FLOWSTOCK_DOTNET_SDK_IMAGE=registry.example.com/mirror/dotnet/sdk:8.0
FLOWSTOCK_DOTNET_ASPNET_IMAGE=registry.example.com/mirror/dotnet/aspnet:8.0
```
- This keeps the deploy workflow unchanged: `deploy_from_git.sh` and `deploy_update.sh` will use the override automatically.
- The project intentionally does not hardcode a non-official fallback registry. Use your own mirror, registry cache, or preloaded internal registry if `mcr.microsoft.com` is slow from the production server.

## Services
- `postgres`
  - primary database
  - keeps bootstrap-only init scripts mounted at `/docker-entrypoint-initdb.d`
- `migrator`
  - one-shot service
  - waits for healthy Postgres
  - applies pending SQL files in lexical order
  - writes applied entries into `schema_migrations`
- `flowstock`
  - starts only after successful `migrator`
  - exposes `/health/live` and `/health/ready`
- `nginx`
  - starts after healthy `flowstock`
  - uses `deploy/nginx/certs/flowstock.crt` and `deploy/nginx/certs/flowstock.key`
- `pgbackup`
  - continuous scheduled `pg_dump -Fc` backups inside the compose stack

## TLS Modes
- `FLOWSTOCK_TLS_MODE=local_ca`
  - recommended for internal LAN deployments
  - deploy scripts expect a local root CA in `FLOWSTOCK_CA_DIR`
  - server certificates are issued automatically into `deploy/nginx/certs/`
  - root CA private key stays outside the repository
- `FLOWSTOCK_TLS_MODE=manual`
  - deploy expects `deploy/nginx/certs/flowstock.crt` and `deploy/nginx/certs/flowstock.key` to already exist
  - use this only if certificates are managed outside the FlowStock deploy scripts

## Local CA Bootstrap
For an internal HTTPS setup, bootstrap the CA once on the server:
```bash
cd /opt/FlowStock
mkdir -p /opt/flowstock-secrets/ca
bash deploy/scripts/bootstrap_local_ca.sh
```

This creates:
- `FLOWSTOCK_CA_DIR/flowstock-root-ca.crt`
- `FLOWSTOCK_CA_DIR/flowstock-root-ca.key`

Important:
- keep the CA private key backed up securely and out of git
- do not leave the CA private key on operators' laptops unless they are responsible for certificate issuance
- client devices must trust `flowstock-root-ca.crt` once

## Client Trust
After bootstrapping the CA, install `flowstock-root-ca.crt` into trusted root certificates on:
- Windows PCs that open the PC web client
- Android TSD devices that open the PWA

You only need to do this once per device while the same root CA remains in use.

## First Empty Production Deploy
1. Prepare env:
```bash
cd /opt/FlowStock
cp deploy/.env.example deploy/.env
```
2. Edit `deploy/.env` with real PostgreSQL password and ports.
3. Bootstrap the local CA once:
```bash
mkdir -p /opt/flowstock-secrets/ca
bash deploy/scripts/bootstrap_local_ca.sh
```
4. Install the generated root CA cert on client devices.
5. Run the first deploy:
```bash
cd /opt/FlowStock
bash deploy/scripts/deploy_update.sh
```
6. Verify health:
```bash
docker compose --env-file deploy/.env -f deploy/docker-compose.yml ps
curl -fsS http://127.0.0.1:${FLOWSTOCK_PORT:-8080}/health/ready
```

7. Check the recorded release state:
```bash
bash deploy/scripts/release_status.sh
```

### Optional One-Time Clean Bootstrap Check
Use this only on a known-empty server or a disposable test project:
```bash
docker compose --env-file deploy/.env -f deploy/docker-compose.yml down -v
bash deploy/scripts/deploy_update.sh
```

## Normal Production Update
The recommended update path from GitHub is:
```bash
cd /opt/FlowStock
bash deploy/scripts/deploy_from_git.sh
```

By default `deploy_from_git.sh`:
- fetches `origin`
- resolves `origin/main`
- refuses to continue if the tracked worktree has local modifications
- records the deployment attempt metadata
- fast-forwards the local `main`
- runs the standard deployment flow
- records the successful release metadata

Deploy a specific tag or commit instead of `origin/main`:
```bash
cd /opt/FlowStock
bash deploy/scripts/deploy_from_git.sh v2026.04.10-1
```

The lower-level `deploy_update.sh` remains available for cases where the repository is already on the desired revision and you only need to rebuild/restart:
```bash
cd /opt/FlowStock
bash deploy/scripts/deploy_update.sh
```

What `deploy_update.sh` does:
- ensures TLS assets are present
- in `local_ca` mode reissues the server certificate if it is missing, mismatched, or close to expiry
- validates `docker compose config`
- ensures `postgres` is up and healthy
- creates a manual pre-deploy dump into `deploy/runtime/backups/`
- pulls base images
- rebuilds `flowstock`
- runs the `migrator`
- recreates `flowstock`, `nginx`, and `pgbackup`
- waits for healthy `flowstock`

## Server Verification For Git-Driven Deploy
Run this once after the server clone is prepared:
```bash
cd /opt/FlowStock
git checkout main
git pull --ff-only origin main
bash deploy/scripts/release_status.sh
bash deploy/scripts/deploy_from_git.sh
```

If the server uses a mirror for base images, set `FLOWSTOCK_DOTNET_SDK_IMAGE` and `FLOWSTOCK_DOTNET_ASPNET_IMAGE` in `deploy/.env` before the first run.

Quick post-deploy verification:
```bash
cd /opt/FlowStock
bash deploy/scripts/release_status.sh
docker compose --env-file deploy/.env -f deploy/docker-compose.yml ps
curl -fsS http://127.0.0.1:${FLOWSTOCK_PORT:-8080}/health/ready
```

## Manual Backup
Create a manual dump:
```bash
cd /opt/FlowStock
bash deploy/scripts/backup_now.sh
```

Create a dump at a specific path:
```bash
bash deploy/scripts/backup_now.sh /opt/flowstock-backups/pre_release.dump
```

## Manual Migration Run
Run pending migrations without recreating app containers:
```bash
cd /opt/FlowStock
bash deploy/scripts/migrate.sh
```

Check applied migrations:
```bash
docker compose --env-file deploy/.env -f deploy/docker-compose.yml exec -T postgres \
  sh -lc 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "SELECT version, filename, applied_at FROM schema_migrations ORDER BY version;"'
```

## Restore From Dump
Restore a `.dump` file:
```bash
cd /opt/FlowStock
bash deploy/scripts/restore_dump.sh /opt/flowstock-backups/pre_release.dump
```

What the restore script does:
- verifies compose config
- ensures Postgres is healthy
- creates a pre-restore safety backup
- stops `flowstock`, `nginx`, and `pgbackup`
- recreates the target database
- restores the supplied dump with `pg_restore`
- starts `pgbackup` again
- intentionally leaves `flowstock`/`nginx` stopped for explicit validation

## Release Status
Show the current git checkout plus recorded release metadata:
```bash
cd /opt/FlowStock
bash deploy/scripts/release_status.sh
```

The script prints:
- current git branch / detached state
- current checked out commit
- latest successful release metadata
- previous successful release metadata
- last attempted deployment metadata
- current `docker compose ps` output when Docker is available

## Rollback Path
If an update fails after backup or after app recreation:
1. Inspect status and logs:
```bash
docker compose --env-file deploy/.env -f deploy/docker-compose.yml ps
docker compose --env-file deploy/.env -f deploy/docker-compose.yml logs --tail=100 migrator flowstock nginx postgres
```
2. If the issue is schema/app incompatibility, return to the previous app revision:
```bash
git checkout <previous-good-commit-or-tag>
```
3. Restore the pre-deploy dump created by `deploy_update.sh`:
```bash
bash deploy/scripts/restore_dump.sh /opt/FlowStock/deploy/runtime/backups/FlowStock_<timestamp>.dump
```
4. Start the previous-good application revision:
```bash
docker compose --env-file deploy/.env -f deploy/docker-compose.yml up -d --build flowstock nginx pgbackup
```

For the common case there is also a dedicated rollback helper:
```bash
cd /opt/FlowStock
bash deploy/scripts/rollback_release.sh
```

By default `rollback_release.sh`:
- checks out the previously recorded successful release revision
- restores the last recorded pre-deploy dump of the current release
- starts `flowstock`, `nginx`, and `pgbackup`
- records the rollback as the new latest successful release

Rollback only the application code without restoring the database:
```bash
bash deploy/scripts/rollback_release.sh --no-restore
```

Rollback to an explicit revision and dump:
```bash
bash deploy/scripts/rollback_release.sh <git-ref> /opt/flowstock-backups/pre_release.dump
```

## Health Checks
- Liveness:
```bash
curl -fsS http://127.0.0.1:${FLOWSTOCK_PORT:-8080}/health/live
```
- Readiness:
```bash
curl -fsS http://127.0.0.1:${FLOWSTOCK_PORT:-8080}/health/ready
```

Readiness returns success only if the app can open a PostgreSQL connection and the migration history is present in `schema_migrations`.

## Manual Certificate Operations
Force reissue the server certificate from the local CA:
```bash
cd /opt/FlowStock
bash deploy/scripts/renew_server_cert.sh --force
docker compose --env-file deploy/.env -f deploy/docker-compose.yml restart nginx
```

Issue the current server certificate immediately:
```bash
cd /opt/FlowStock
bash deploy/scripts/issue_server_cert.sh
docker compose --env-file deploy/.env -f deploy/docker-compose.yml restart nginx
```

## Notes
- Migration files are applied in lexical order, so keep the `V0001__name.sql` naming scheme.
- Migration files are wrapped in a transaction by the runner. On any SQL error, the migration process exits non-zero and the file is not recorded in `schema_migrations`.
- `deploy/postgres/init/001_init.sql` is intentionally minimal. It only bootstraps `schema_migrations` for a brand new Postgres volume.
- The WPF client no longer owns production schema creation. If a database is missing migrations, the app reports that state instead of silently creating/updating schema.
- Keep the server-side repository clone clean. `deploy_from_git.sh` aborts when tracked files on the server were edited manually.
- The recorded release metadata is stored under `deploy/runtime/releases/`.
- `deploy/nginx/gen_cert.sh` remains available only as a quick self-signed fallback; the recommended internal production mode is `FLOWSTOCK_TLS_MODE=local_ca`.
