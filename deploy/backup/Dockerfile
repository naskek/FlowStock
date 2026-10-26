FROM postgres:16-alpine

# Python 3 executes the existing atomic PostgreSQL 16 backup primitive.
RUN apk add --no-cache python3
COPY deploy/backup/managed_backup.py /usr/local/bin/managed_backup.py
COPY deploy/backup/pg_backup.sh /usr/local/bin/pg_backup.sh
RUN chmod 0555 /usr/local/bin/managed_backup.py /usr/local/bin/pg_backup.sh
