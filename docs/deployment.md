# FlowStock Deployment

## Overview
- Production deploy is driven by `deploy/docker-compose.yml`.
- PostgreSQL init scripts in `deploy/postgres/init/` are bootstrap-only for a brand new empty data directory.
- All schema updates after that go through versioned SQL migrations from `deploy/postgres/migrations/`.
- The recommended update path is:
  1. backup
  2. migrate schema
  3. recreate application containers
  4. verify health

## Required Files And Directories
- `deploy/.env`
  - create it from `deploy/.env.example`
- `deploy/nginx/certs/flowstock.crt`
- `deploy/nginx/certs/flowstock.key`
- `deploy/runtime/`
  - created automatically by scripts for manual backups and operator artifacts

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
- `pgbackup`
  - continuous scheduled `pg_dump -Fc` backups inside the compose stack

## First Empty Production Deploy
1. Prepare env and certificates:
```bash
cd /opt/FlowStock
cp deploy/.env.example deploy/.env
```
2. Edit `deploy/.env` with real PostgreSQL password and ports.
3. Place TLS cert/key into `deploy/nginx/certs/`.
4. Run the first deploy:
```bash
cd /opt/FlowStock
bash deploy/scripts/deploy_update.sh
```
5. Verify health:
```bash
docker compose --env-file deploy/.env -f deploy/docker-compose.yml ps
curl -fsS http://127.0.0.1:${FLOWSTOCK_PORT:-8080}/health/ready
```

### Optional One-Time Clean Bootstrap Check
Use this only on a known-empty server or a disposable test project:
```bash
docker compose --env-file deploy/.env -f deploy/docker-compose.yml down -v
bash deploy/scripts/deploy_update.sh
```

## Normal Production Update
The standard update path is:
```bash
cd /opt/FlowStock
git pull
bash deploy/scripts/deploy_update.sh
```

What `deploy_update.sh` does:
- validates `docker compose config`
- ensures `postgres` is up and healthy
- creates a manual pre-deploy dump into `deploy/runtime/backups/`
- pulls base images
- rebuilds `flowstock`
- runs the `migrator`
- recreates `flowstock`, `nginx`, and `pgbackup`
- waits for healthy `flowstock`

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

## Notes
- Migration files are applied in lexical order, so keep the `V0001__name.sql` naming scheme.
- Migration files are wrapped in a transaction by the runner. On any SQL error, the migration process exits non-zero and the file is not recorded in `schema_migrations`.
- `deploy/postgres/init/001_init.sql` is intentionally minimal. It only bootstraps `schema_migrations` for a brand new Postgres volume.
- The WPF client no longer owns production schema creation. If a database is missing migrations, the app reports that state instead of silently creating/updating schema.
