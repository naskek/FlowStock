FROM postgres:16-alpine

# Python 3 executes the existing atomic PostgreSQL 16 backup primitive.
RUN apk add --no-cache python3 py3-paramiko py3-pip \
    && python3 -m venv --system-site-packages /opt/remote
COPY deploy/backup/requirements-remote.txt /tmp/requirements-remote.txt
RUN /opt/remote/bin/pip install --no-cache-dir --no-deps -r /tmp/requirements-remote.txt
ENV PATH="/opt/remote/bin:${PATH}"
COPY deploy/backup/managed_backup.py /usr/local/bin/managed_backup.py
COPY deploy/backup/remote_backup.py /usr/local/bin/remote_backup.py
COPY deploy/backup/pg_backup.sh /usr/local/bin/pg_backup.sh
RUN chmod 0555 /usr/local/bin/managed_backup.py /usr/local/bin/pg_backup.sh
RUN chmod 0555 /usr/local/bin/remote_backup.py
