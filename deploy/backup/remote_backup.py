#!/usr/bin/env python3
"""Explicit operator-only SFTP adapter. No scheduler, API, retention or local writes."""
from __future__ import annotations

import argparse
import base64
import contextlib
import errno
import hashlib
import hmac
import io
import ipaddress
import json
import logging
import os
from pathlib import PurePosixPath
import re
import signal
import socket
import stat
import subprocess
import sys
import time
import uuid

import paramiko
from paramiko.sftp import CMD_EXTENDED

CHUNK = 32768
IDENT = re.compile(r"[0-9a-f]{32}")
SHA = re.compile(r"[0-9a-f]{64}")
COMPONENT = re.compile(r"[A-Za-z0-9_-][A-Za-z0-9_.-]{0,127}")

# Paramiko can log peer-controlled exception text. Only our finite error codes
# cross this tool's boundary; never emit raw transport/auth/path exceptions.
logging.getLogger("paramiko").disabled = True
logging.getLogger("paramiko.transport").disabled = True


class RemoteError(Exception):
    def __init__(self, code, cleanup="not_needed"):
        super().__init__(code)
        self.code = code
        self.cleanup = cleanup


def require(condition, code):
    if not condition:
        raise RemoteError(code)


def canonical(value):
    return (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode()


@contextlib.contextmanager
def deadline(seconds, code):
    # Linux worker, main thread only. A wall-clock alarm also bounds DNS,
    # channel setup and peers that keep returning small amounts of data.
    def expired(*_):
        raise RemoteError(code)
    started = time.monotonic()
    previous = signal.signal(signal.SIGALRM, expired)
    prior_timer = signal.getitimer(signal.ITIMER_REAL)[0]
    signal.setitimer(signal.ITIMER_REAL, min(seconds, prior_timer) if prior_timer else seconds)
    try:
        yield
    finally:
        signal.setitimer(signal.ITIMER_REAL, 0)
        signal.signal(signal.SIGALRM, previous)
        if prior_timer:
            signal.setitimer(signal.ITIMER_REAL, max(0.000001, prior_timer - (time.monotonic() - started)))


def open_directory(path):
    parts = PurePosixPath(path).parts
    require(isinstance(path, str) and path.startswith("/") and
            ".." not in parts and str(PurePosixPath(path)) == path, "LOCAL_UNSAFE")
    fd = os.open("/", os.O_RDONLY | os.O_DIRECTORY)
    try:
        for part in parts[1:]:
            child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=fd)
            os.close(fd)
            fd = child
        return fd
    except BaseException:
        os.close(fd)
        raise


def secure_file(path, *, secret=False):
    parent, name = str(PurePosixPath(path).parent), PurePosixPath(path).name
    fd = open_directory(parent)
    try:
        item = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=fd)
    finally:
        os.close(fd)
    info = os.fstat(item)
    if not (stat.S_ISREG(info.st_mode) and info.st_nlink == 1 and
            info.st_uid in {0, os.geteuid()} and
            stat.S_IMODE(info.st_mode) in ({0o400} if secret else {0o400, 0o600})):
        os.close(item)
        raise RemoteError("SECRET_UNSAFE" if secret else "LOCAL_UNSAFE")
    return os.fdopen(item, "rb")


def limited_json(fp):
    raw = fp.read(65537)
    require(len(raw) <= 65536, "INVALID_METADATA")
    return json.loads(raw)


class Destination:
    def __init__(self, path):
        with secure_file(path) as fp:
            self.data = d = limited_json(fp)
        require(isinstance(d, dict) and d.get("enabled") is True and
                d.get("schema_version") == 1, "DESTINATION_DISABLED")
        require(type(d.get("destination_revision")) is int and
                0 < d["destination_revision"] < 2**63, "INVALID_CONFIG")
        self.revision = d["destination_revision"]
        require(isinstance(d.get("host"), str) and
                re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9.:-]{0,252}", d["host"]), "INVALID_CONFIG")
        require(type(d.get("port")) is int and 0 < d["port"] < 65536 and
                isinstance(d.get("account"), str) and
                re.fullmatch(r"[a-z_][a-z0-9_-]{0,63}", d["account"]), "INVALID_CONFIG")
        addresses = d.get("allowed_addresses")
        require(isinstance(addresses, list) and 0 < len(addresses) <= 8, "INVALID_CONFIG")
        self.addresses = {str(ipaddress.ip_address(x)) for x in addresses}
        for value in self.addresses:
            address = ipaddress.ip_address(value)
            require(not address.is_unspecified and not address.is_multicast and
                    (address.is_private or address in ipaddress.ip_network("100.64.0.0/10")),
                    "HOST_NOT_ALLOWED")
        root = d.get("root")
        require(isinstance(root, str) and root.startswith("/") and root != "/" and
                str(PurePosixPath(root)) == root and
                all(COMPONENT.fullmatch(x) and x not in {".", ".."}
                    for x in root.split("/")[1:]), "REMOTE_PATH_UNSAFE")
        require(d.get("rename_policy") == "openssh-v3-hardlink" and
                d.get("exclusive_root_attested") is True and
                d.get("encrypted_at_rest_recovery_attested") is True, "ATTESTATION_REQUIRED")
        for key, maximum in (("connect_seconds", 60), ("auth_seconds", 60),
                             ("transfer_seconds", 3600)):
            require(type(d.get(key)) is int and 0 < d[key] <= maximum, "INVALID_CONFIG")
        require(type(d.get("max_archive_bytes")) is int and
                0 < d["max_archive_bytes"] <= 2**50, "INVALID_CONFIG")
        # Only separately provisioned read-only files, never key text/env/agent.
        key_path = d.get("credential_file")
        require(isinstance(key_path, str) and key_path.startswith("/run/secrets/") and
                str(PurePosixPath(key_path)) == key_path and
                ".." not in PurePosixPath(key_path).parts, "SECRET_UNSAFE")
        with secure_file(key_path, secret=True) as fp:
            raw = fp.read(16385)
        require(len(raw) <= 16384, "SECRET_UNSAFE")
        # First adapter deliberately supports only unencrypted Ed25519 keys in
        # a protected secret mount. Passphrase providers are a separate contract.
        self.key = paramiko.Ed25519Key.from_private_key(io.StringIO(raw.decode("ascii")))
        pin = d.get("host_key_sha256")
        require(isinstance(pin, str) and re.fullmatch(r"SHA256:[A-Za-z0-9+/]{43}", pin),
                "INVALID_CONFIG")
        self.identity = hashlib.sha256(canonical(d | {
            "credential_public_sha256": hashlib.sha256(self.key.asbytes()).hexdigest()
        })).hexdigest()

    def bind(self, revision, identity):
        require(revision == self.revision and isinstance(identity, str) and
                hmac.compare_digest(identity, self.identity), "DESTINATION_CHANGED")


class LocalArchive:
    def __init__(self, root, backup_id, maximum):
        require(isinstance(backup_id, str) and IDENT.fullmatch(backup_id), "INVALID_BACKUP_ID")
        self.backup_id = backup_id
        self.catalog_path = root + "/managed/catalog/" + backup_id + ".json"
        with secure_file(self.catalog_path) as fp:
            self.record = r = limited_json(fp)
        require(isinstance(r, dict) and r.get("schema_version") == 1 and
                r.get("backup_id") == backup_id and r.get("archive_key") == backup_id and
                r.get("local_state") == "LOCAL_VALID" and r.get("structural_validated") is True and
                r.get("pg_major") == 16 and isinstance(r.get("sha256"), str) and
                SHA.fullmatch(r["sha256"]) and type(r.get("size_bytes")) is int and
                0 < r["size_bytes"] <= maximum, "LOCAL_NOT_VALID")
        self.archive_path = root + "/managed/archives/" + backup_id + ".dump"
        self.fp = secure_file(self.archive_path)
        self.before = os.fstat(self.fp.fileno())
        try:
            self.verify()
        except BaseException:
            self.fp.close()
            raise

    def verify(self):
        self.fp.seek(0)
        digest, size = hashlib.sha256(), 0
        while block := self.fp.read(CHUNK):
            size += len(block)
            require(size <= self.record["size_bytes"], "LOCAL_CHANGED")
            digest.update(block)
        after = os.fstat(self.fp.fileno())
        with secure_file(self.archive_path) as current:
            current_info = os.fstat(current.fileno())
            require((current_info.st_dev, current_info.st_ino) ==
                    (self.before.st_dev, self.before.st_ino), "LOCAL_CHANGED")
        require((self.before.st_dev, self.before.st_ino, self.before.st_size,
                 self.before.st_mtime_ns, self.before.st_ctime_ns) ==
                (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns, after.st_ctime_ns) and
                size == self.record["size_bytes"] and digest.hexdigest() == self.record["sha256"],
                "LOCAL_CHANGED")
        with secure_file(self.catalog_path) as fp:
            require(limited_json(fp) == self.record, "LOCAL_CHANGED")
        self.fp.seek(0)


class Session:
    def __init__(self, destination):
        self.config = destination
        self.sftp = self.transport = self.sock = None
        self.owned = set()
        self.uncertain = False

    def __enter__(self):
        d = self.config.data
        try:
            with deadline(d["connect_seconds"], "CONNECT_TIMEOUT"):
                try:
                    # getaddrinfo in libc may not return promptly on SIGALRM.
                    # A disposable child makes slow DNS killable, without a
                    # persistent resolver thread or shell interpretation.
                    output = subprocess.run([sys.executable, "-I", "-c",
                        "import json,socket,sys; print(json.dumps(sorted({x[4][0] for x in "
                        "socket.getaddrinfo(sys.argv[1],int(sys.argv[2]),type=socket.SOCK_STREAM)})))",
                        d["host"], str(d["port"])], capture_output=True,
                        timeout=d["connect_seconds"], check=True)
                except subprocess.TimeoutExpired:
                    raise RemoteError("CONNECT_TIMEOUT") from None
                except subprocess.SubprocessError:
                    raise RemoteError("REMOTE_IO_FAILED") from None
                require(len(output.stdout) <= 4096, "HOST_NOT_ALLOWED")
                resolved = set(json.loads(output.stdout))
                require(resolved and resolved <= self.config.addresses, "HOST_NOT_ALLOWED")
                address = sorted(resolved)[0]
                self.sock = socket.create_connection((address, d["port"]), d["connect_seconds"])
                self.transport = paramiko.Transport(self.sock)
                self.transport.banner_timeout = d["connect_seconds"]
                self.transport.start_client(timeout=d["connect_seconds"])
                key = self.transport.get_remote_server_key()
                pin = "SHA256:" + base64.b64encode(hashlib.sha256(key.asbytes()).digest()).decode().rstrip("=")
                require(key.get_name() == "ssh-ed25519" and
                        hmac.compare_digest(pin, d["host_key_sha256"]), "HOST_KEY_MISMATCH")
                require(self.transport.remote_version.startswith("SSH-2.0-OpenSSH_"),
                        "RENAME_UNSUPPORTED")
            with deadline(d["auth_seconds"], "AUTH_TIMEOUT"):
                self.transport.auth_timeout = d["auth_seconds"]
                self.transport.auth_publickey(d["account"], self.config.key)
                require(self.transport.is_authenticated(), "AUTH_FAILED")
            with deadline(d["transfer_seconds"], "TRANSFER_TIMEOUT"):
                self.transport.channel_timeout = min(30, d["transfer_seconds"])
                self.sftp = paramiko.SFTPClient.from_transport(self.transport)
                self.sftp.get_channel().settimeout(min(30, d["transfer_seconds"]))
                self.check_root()
            return self
        except BaseException:
            self.__exit__(None, None, None)
            raise

    def __exit__(self, *_):
        # Closing the transport first never waits for a remote CLOSE ack.
        if self.transport:
            self.transport.close()
        if self.sock:
            self.sock.close()

    def check_root(self):
        current = ""
        for part in self.config.data["root"].split("/")[1:]:
            current += "/" + part
            attrs = self.sftp.lstat(current)
            require(stat.S_ISDIR(attrs.st_mode) and not attrs.st_mode & 0o022,
                    "REMOTE_PATH_UNSAFE")
        require(self.sftp.normalize(current) == current, "REMOTE_PATH_UNSAFE")

    def path(self, name):
        require(COMPONENT.fullmatch(name), "REMOTE_PATH_UNSAFE")
        self.check_root()
        return self.config.data["root"] + "/" + name

    def exists(self, name):
        try:
            attrs = self.sftp.lstat(self.path(name))
            require(stat.S_ISREG(attrs.st_mode), "REMOTE_PATH_UNSAFE")
            return True
        except OSError as exc:
            if exc.errno == errno.ENOENT:
                return False
            raise

    def verify(self, name, size, digest):
        path = self.path(name)
        require(self.exists(name), "REMOTE_MISSING")
        before = self.sftp.lstat(path)
        require(before.st_size == size, "REMOTE_MISMATCH")
        actual, count = hashlib.sha256(), 0
        with self.sftp.open(path, "rb", bufsize=0) as fp:
            require(stat.S_ISREG(fp.stat().st_mode), "REMOTE_PATH_UNSAFE")
            while block := fp.read(min(CHUNK, size - count + 1)):
                count += len(block)
                require(count <= size, "REMOTE_MISMATCH")
                actual.update(block)
        after = self.sftp.lstat(path)
        require(stat.S_ISREG(after.st_mode) and after.st_size == size and
                before.st_mtime == after.st_mtime and count == size and
                hmac.compare_digest(actual.hexdigest(), digest), "REMOTE_MISMATCH")

    def write(self, name, source, size, digest):
        path = self.path(name)
        # Paramiko's x alone doesn't set WRITE; wx maps to WRITE|CREAT|EXCL.
        with self.sftp.open(path, "wx", bufsize=0) as fp:
            self.owned.add(name)
            fp.chmod(0o600)
            count, actual = 0, hashlib.sha256()
            while block := source.read(CHUNK):
                count += len(block)
                require(count <= size, "LOCAL_CHANGED")
                actual.update(block)
                fp.write(block)  # synchronous, bounded packets; no prefetch/pipeline
            fp.flush()
            require(count == size and actual.hexdigest() == digest, "LOCAL_CHANGED")
        self.verify(name, size, digest)

    def rename(self, source, target):
        # SSH_FXP_RENAME only. Never posix_rename (which replaces targets).
        try:
            self.sftp.rename(self.path(source), self.path(target))
        except (OSError, EOFError, paramiko.SSHException):
            self.uncertain = True
            raise RemoteError("REMOTE_UNKNOWN", "retained") from None
        self.owned.discard(source)

    def cleanup(self):
        try:
            with deadline(5, "CLEANUP_FAILED"):
                for name in list(self.owned):
                    if self.exists(name):
                        self.sftp.remove(self.path(name))
                    self.owned.discard(name)
            return "complete"
        except Exception:
            return "failed"

    def prove_rename(self):
        # OpenSSH regular-file RENAME uses link/unlink. Prove hardlinks work on
        # this filesystem too: otherwise OpenSSH has a racy stat+rename fallback.
        names = ["probe-" + uuid.uuid4().hex + suffix for suffix in ("-a", "-b", "-c")]
        a, b, c = names
        try:
            for name, data in ((a, b"source"), (b, b"occupied")):
                self.write(name, io.BytesIO(data), len(data), hashlib.sha256(data).hexdigest())
            # Paramiko 4.x has no public hardlink wrapper. Keep the OpenSSH
            # extension in one place; integration tests exercise real OpenSSH.
            self.sftp._request(CMD_EXTENDED, "hardlink@openssh.com", self.path(a), self.path(c))
            self.owned.add(c)
            self.verify(c, 6, hashlib.sha256(b"source").hexdigest())
            self.sftp.remove(self.path(c))
            self.owned.remove(c)
            try:
                self.sftp.rename(self.path(a), self.path(b))
            except OSError:
                pass
            else:
                raise RemoteError("RENAME_UNSUPPORTED")
            self.verify(a, 6, hashlib.sha256(b"source").hexdigest())
            self.verify(b, 8, hashlib.sha256(b"occupied").hexdigest())
            self.rename(a, c)
            self.owned.add(c)
            require(not self.exists(a), "RENAME_UNSUPPORTED")
            self.verify(c, 6, hashlib.sha256(b"source").hexdigest())
        except (OSError, paramiko.SSHException, EOFError):
            raise RemoteError("RENAME_UNSUPPORTED") from None
        finally:
            cleanup = self.cleanup()
            if cleanup != "complete":
                raise RemoteError("PROBE_CLEANUP_FAILED", cleanup)


def execute(config, operation, revision, identity, *, root=None, backup_id=None, reconcile=False):
    config.bind(revision, identity)
    require(operation in {"probe", "upload"}, "INVALID_OPERATION")
    archive = None
    remote = None
    try:
        with deadline(config.data["transfer_seconds"], "LOCAL_TIMEOUT"):
            if operation == "upload":
                archive = LocalArchive(root, backup_id, config.data["max_archive_bytes"])
                catalog_revision = archive.record.get("destination_revision")
                require(catalog_revision is None or
                        (type(catalog_revision) is int and catalog_revision == revision),
                        "DESTINATION_CHANGED")
        with Session(config) as remote:
            with deadline(config.data["transfer_seconds"], "TRANSFER_TIMEOUT"):
                remote.prove_rename()  # also the write/read/delete connection probe
                result = {"state": "PROBE_OK", "cleanup": "complete",
                          "destination_revision": revision, "destination_identity": identity}
                if operation == "probe":
                    return result
                record = archive.record
                manifest = canonical({"schema_version": 1, "backup_id": backup_id,
                    "sha256": record["sha256"], "size_bytes": record["size_bytes"],
                    "destination_revision": revision, "destination_identity": identity})
                prefix = backup_id + "-r" + str(revision)
                final, meta = prefix + ".dump", prefix + ".json"
                meta_hash = hashlib.sha256(manifest).hexdigest()
                present, has_meta = remote.exists(final), remote.exists(meta)
                if has_meta:
                    remote.verify(meta, len(manifest), meta_hash)
                if present:
                    remote.verify(final, record["size_bytes"], record["sha256"])
                    require(has_meta or reconcile, "REMOTE_UNKNOWN")
                else:
                    require(not has_meta, "REMOTE_UNKNOWN")
                    stage = "stage-" + uuid.uuid4().hex + ".dump"
                    remote.write(stage, archive.fp, record["size_bytes"], record["sha256"])
                    archive.verify()  # source/catalog must still be unchanged before publish
                    remote.rename(stage, final)
                    remote.verify(final, record["size_bytes"], record["sha256"])
                if not has_meta:
                    stage = "stage-" + uuid.uuid4().hex + ".json"
                    remote.write(stage, io.BytesIO(manifest), len(manifest), meta_hash)
                    remote.rename(stage, meta)
                # Success evidence is the complete final pair, never rename ack.
                remote.verify(meta, len(manifest), meta_hash)
                remote.verify(final, record["size_bytes"], record["sha256"])
                archive.verify()
                return result | {"state": "VERIFIED", "backup_id": backup_id}
    except RemoteError as exc:
        if remote and remote.owned and exc.cleanup == "not_needed":
            exc.cleanup = "retained"
        raise
    except paramiko.AuthenticationException:
        raise RemoteError("AUTH_FAILED") from None
    except (OSError, EOFError, paramiko.SSHException):
        raise RemoteError("REMOTE_IO_FAILED", "retained" if archive else "not_needed") from None
    finally:
        if archive:
            archive.fp.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True)
    parser.add_argument("operation", choices=("describe", "probe", "upload"))
    parser.add_argument("--destination-revision", type=int)
    parser.add_argument("--destination-identity")
    parser.add_argument("--root")
    parser.add_argument("--backup-id")
    parser.add_argument("--reconcile", action="store_true")
    args = parser.parse_args()
    try:
        config = Destination(args.config)
        if args.operation == "describe":
            result = {"destination_revision": config.revision, "destination_identity": config.identity}
        else:
            result = execute(config, args.operation, args.destination_revision,
                             args.destination_identity, root=args.root,
                             backup_id=args.backup_id, reconcile=args.reconcile)
        print(json.dumps(result, sort_keys=True))
        return 0
    except Exception as exc:
        print(json.dumps({"state": "FAILED", "error_code": exc.code if isinstance(exc, RemoteError)
                          else "INVALID_CONFIG", "cleanup": exc.cleanup if isinstance(exc, RemoteError)
                          else "not_needed"}), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
