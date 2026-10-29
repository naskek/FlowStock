#!/usr/bin/env python3
"""Disposable PG16 + OpenSSH and wire-level SFTP fault integration suite."""
import base64
import contextlib
import hashlib
import json
import os
from pathlib import Path
import pwd
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import managed_backup as managed
import remote_backup as adapter
from sftp_fault_server import FaultPeer


def run(*args, **kwargs):
    return subprocess.run(args, check=True, stdout=subprocess.DEVNULL,
                          stderr=subprocess.DEVNULL, timeout=30, **kwargs)


def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


class RemoteIntegration(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory(prefix="remote-integration-")
        cls.base = Path(cls.tmp.name)
        cls.secret_dir = Path("/run/secrets") / cls.base.name
        cls.secret_dir.mkdir(mode=0o700)
        cls.key_path = cls.secret_dir / "identity"
        cls.host_path = cls.base / "host"
        for path in (cls.key_path, cls.host_path):
            run("ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", str(path))
        cls.key_path.chmod(0o400)
        cls.key = adapter.paramiko.Ed25519Key.from_private_key_file(str(cls.key_path))
        cls.host_key = adapter.paramiko.Ed25519Key.from_private_key_file(str(cls.host_path))
        cls.pin = "SHA256:" + base64.b64encode(hashlib.sha256(cls.host_key.asbytes()).digest()).decode().rstrip("=")
        cls.account = pwd.getpwnam("remote_fixture")
        cls.destination = Path(cls.account.pw_dir) / ("store-" + cls.base.name)
        cls.destination.mkdir(mode=0o700)
        os.chown(cls.destination, cls.account.pw_uid, cls.account.pw_gid)
        cls.authorized = Path(cls.account.pw_dir) / ("authorized-" + cls.base.name)
        shutil.copyfile(str(cls.key_path) + ".pub", cls.authorized)
        cls.authorized.chmod(0o644)
        cls.base.chmod(0o755)  # sshd reads only public authorized_keys here
        cls.port = free_port()
        cls.ssh_config = cls.base / "sshd_config"
        cls.ssh_config.write_text(f"""ListenAddress 127.0.0.1
Port {cls.port}
HostKey {cls.host_path}
PidFile {cls.base}/sshd.pid
AuthorizedKeysFile {cls.authorized}
StrictModes yes
PasswordAuthentication no
KbdInteractiveAuthentication no
UsePAM no
PermitRootLogin no
AllowUsers remote_fixture
AllowTcpForwarding no
X11Forwarding no
PermitTTY no
Subsystem sftp internal-sftp
ForceCommand internal-sftp
""")
        cls.ssh = subprocess.Popen(["/usr/sbin/sshd", "-D", "-e", "-f", str(cls.ssh_config)],
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        for _ in range(100):
            try:
                with socket.create_connection(("127.0.0.1", cls.port), 0.1):
                    break
            except OSError:
                time.sleep(0.03)
        else:
            raise RuntimeError("isolated sshd did not start")
        # Produce one real pg_dump through the unchanged managed LOCAL_VALID
        # primitive. No production data, credentials, ports or volumes involved.
        cls.pg = cls.base / "pg"
        cls.pg.mkdir(mode=0o700)
        postgres = pwd.getpwnam("postgres")
        os.chown(cls.pg, postgres.pw_uid, postgres.pw_gid)
        run("runuser", "-u", "postgres", "--", "initdb", "-D", str(cls.pg), "-A", "trust", "--no-locale")
        cls.pg_port = free_port()
        run("runuser", "-u", "postgres", "--", "pg_ctl", "-D", str(cls.pg),
            "-o", f"-h 127.0.0.1 -p {cls.pg_port}", "-l", str(cls.pg / "log"), "-w", "start")
        os.environ.update(PGHOST="127.0.0.1", PGPORT=str(cls.pg_port), PGUSER="postgres", PGDATABASE="postgres")
        run("psql", "-c", "CREATE TABLE fixture AS SELECT generate_series(1,10000) AS id")
        cls.local_root = cls.base / "local"
        cls.local_root.mkdir(mode=0o700)
        cls.store = managed.Store(str(cls.local_root))
        with cls.store.lock(2):
            cls.record = cls.store.create("manual", 1024)
        cls.ident = cls.record["backup_id"]
        cls.archive_path = cls.store.archives / (cls.ident + ".dump")
        cls.original = cls.archive_path.read_bytes()

    @classmethod
    def tearDownClass(cls):
        cls.ssh.terminate()
        cls.ssh.wait(5)
        run("runuser", "-u", "postgres", "--", "pg_ctl", "-D", str(cls.pg), "-m", "fast", "-w", "stop")
        # Only directories created by this fixture, inside a disposable container.
        shutil.rmtree(cls.destination)
        cls.authorized.unlink()
        shutil.rmtree(cls.secret_dir)
        cls.tmp.cleanup()

    def setUp(self):
        self.case = self.base / ("case-" + adapter.uuid.uuid4().hex)
        self.case.mkdir(mode=0o700)
        self.config_path = self.case / "destination.json"
        self.data = {"schema_version": 1, "enabled": True, "destination_revision": 7,
            "host": "127.0.0.1", "port": self.port, "account": "remote_fixture",
            "allowed_addresses": ["127.0.0.1"], "root": str(self.destination),
            "host_key_sha256": self.pin, "credential_file": str(self.key_path),
            "rename_policy": "openssh-v3-hardlink", "exclusive_root_attested": True,
            "encrypted_at_rest_recovery_attested": True, "connect_seconds": 2,
            "auth_seconds": 2, "transfer_seconds": 5, "max_archive_bytes": 1024*1024}

    def tearDown(self):
        # Every remote failure must preserve the valid local archive/catalog.
        self.assertEqual(self.archive_path.read_bytes(), self.original)
        self.assertEqual(managed._read(self.store.catalog / (self.ident + ".json")), self.record)

    def config(self, **changes):
        self.data.update(changes)
        self.config_path.write_bytes(adapter.canonical(self.data))
        self.config_path.chmod(0o600)
        return adapter.Destination(str(self.config_path))

    def call(self, config, operation="upload", **kwargs):
        return adapter.execute(config, operation, config.revision, config.identity,
                               root=str(self.local_root), backup_id=self.ident, **kwargs)

    def peer(self, fault):
        peer = FaultPeer(self.case / "peer", self.host_key, self.key, fault)
        self.addCleanup(peer.close)
        return peer, self.config(port=peer.port, root="/destination")

    @contextlib.contextmanager
    def scenario(self, fault):
        previous_case, previous_config = self.case, self.config_path
        try:
            self.case = previous_case / fault
            self.case.mkdir()
            self.config_path = self.case / "destination.json"
            yield self.peer(fault)
        finally:
            self.case, self.config_path = previous_case, previous_config

    def expect_failure(self, config, codes=None, **kwargs):
        with self.assertRaises(adapter.RemoteError) as raised:
            self.call(config, **kwargs)
        if codes:
            self.assertIn(raised.exception.code, codes)
        return raised.exception

    def test_openssh_probe_upload_idempotency_and_conflict(self):
        config = self.config()
        foreign = self.destination / "foreign"
        foreign.write_bytes(b"do not change")
        self.addCleanup(foreign.unlink)
        self.assertEqual(self.call(config, "probe")["state"], "PROBE_OK")
        self.assertEqual(list(self.destination.iterdir()), [foreign])
        self.assertEqual(self.call(config)["state"], "VERIFIED")
        final = self.destination / (self.ident + "-r7.dump")
        meta = self.destination / (self.ident + "-r7.json")
        self.addCleanup(final.unlink)
        self.addCleanup(meta.unlink)
        before = final.stat()
        self.assertEqual(self.call(config)["state"], "VERIFIED")
        self.assertEqual(before.st_ino, final.stat().st_ino)
        self.assertEqual(final.read_bytes(), self.original)
        # The remotely retrieved bytes are a PostgreSQL 16 archive too.
        run("pg_restore", "--file=/dev/null", str(final))
        final.write_bytes(b"foreign conflict")
        self.expect_failure(config, {"REMOTE_MISMATCH"})
        self.assertEqual(final.read_bytes(), b"foreign conflict")
        self.assertEqual(foreign.read_bytes(), b"do not change")

    def test_missing_manifest_needs_explicit_reconciliation(self):
        peer, config = self.peer("none")
        self.assertEqual(self.call(config)["state"], "VERIFIED")
        meta = peer.root / "destination" / (self.ident + "-r7.json")
        meta.unlink()
        self.expect_failure(config, {"REMOTE_UNKNOWN"})
        self.assertEqual(self.call(config, reconcile=True)["state"], "VERIFIED")

    def test_host_pin_mismatch(self):
        config = self.config(host_key_sha256="SHA256:" + "A"*43)
        self.expect_failure(config, {"HOST_KEY_MISMATCH"})

    def test_auth_failure(self):
        _, config = self.peer("auth")
        self.expect_failure(config, {"AUTH_FAILED"})

    def test_connectivity_failure(self):
        config = self.config(port=free_port())
        self.expect_failure(config, {"REMOTE_IO_FAILED"})

    def test_connect_and_auth_deadlines(self):
        for fault, code in (("connect_timeout", "CONNECT_TIMEOUT"), ("auth_timeout", "AUTH_TIMEOUT")):
            with self.subTest(fault=fault), self.scenario(fault) as (_, config):
                started = time.monotonic()
                self.expect_failure(config, {code})
                self.assertLess(time.monotonic() - started, 3.5)

    def test_allowlist_rejection_before_connect(self):
        config = self.config(allowed_addresses=["127.0.0.2"])
        self.expect_failure(config, {"HOST_NOT_ALLOWED"})

    def test_revision_and_credential_rotation_are_bound(self):
        config = self.config()
        with self.assertRaises(adapter.RemoteError) as raised:
            adapter.execute(config, "probe", 6, config.identity)
        self.assertEqual(raised.exception.code, "DESTINATION_CHANGED")
        rotated = self.secret_dir / ("rotated-" + adapter.uuid.uuid4().hex)
        run("ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", str(rotated))
        rotated.chmod(0o400)
        old = config.identity
        new = self.config(credential_file=str(rotated))
        with self.assertRaises(adapter.RemoteError):
            adapter.execute(new, "probe", 7, old)

    def test_secret_reference_and_attestation_guards(self):
        with self.assertRaises(adapter.RemoteError):
            self.config(encrypted_at_rest_recovery_attested=False)
        self.data["encrypted_at_rest_recovery_attested"] = True
        with self.assertRaises(adapter.RemoteError):
            self.config(credential_file="/tmp/key")
        self.data["credential_file"] = str(self.key_path)
        self.key_path.chmod(0o600)
        try:
            with self.assertRaises(adapter.RemoteError):
                self.config()
        finally:
            self.key_path.chmod(0o400)

    def test_foreign_manifest_blocks_archive_upload(self):
        peer, config = self.peer("none")
        manifest = peer.root / "destination" / (self.ident + "-r7.json")
        manifest.write_bytes(b"foreign")
        self.expect_failure(config, {"REMOTE_MISMATCH"})
        self.assertEqual(manifest.read_bytes(), b"foreign")
        self.assertEqual(list((peer.root / "destination").glob("*.dump")), [])

    def test_path_traversal_root_and_final_symlinks(self):
        for root in ("/", "/destination/../other", "/destination//other", "/destination/*", "/destination;id"):
            with self.subTest(root=root), self.assertRaises(adapter.RemoteError):
                self.config(root=root)
        peer, config = self.peer("none")
        final = peer.root / "destination" / (self.ident + "-r7.dump")
        final.symlink_to(self.archive_path)
        self.expect_failure(config, {"REMOTE_PATH_UNSAFE"})
        final.unlink()
        (peer.root / "alias").symlink_to(peer.root / "destination")
        self.expect_failure(self.config(root="/alias"), {"REMOTE_PATH_UNSAFE"})

    def test_permission_failure_and_probe_cleanup(self):
        peer, config = self.peer("permission")
        self.expect_failure(config, {"RENAME_UNSUPPORTED"})
        self.assertEqual(list((peer.root / "destination").iterdir()), [])

    def test_probe_cleanup_failure_is_reported(self):
        _, config = self.peer("cleanup")
        error = self.expect_failure(config, {"PROBE_CLEANUP_FAILED"})
        self.assertEqual(error.cleanup, "failed")

    def test_lost_probe_ack_never_claims_complete_cleanup(self):
        for fault in ("open_lost_ack", "hardlink_lost_ack", "probe_rename_lost_ack"):
            with self.subTest(fault=fault), self.scenario(fault) as (peer, config):
                error = self.expect_failure(config, {"PROBE_CLEANUP_FAILED"}, operation="probe")
                self.assertEqual(error.cleanup, "failed")
                self.assertTrue(list((peer.root / "destination").iterdir()))

    def test_rename_deadline_is_unknown_even_when_final_exists(self):
        peer, config = self.peer("rename_delayed_ack")
        self.expect_failure(config, {"REMOTE_UNKNOWN"})
        self.assertEqual((peer.root / "destination" / (self.ident + "-r7.dump")).read_bytes(), self.original)
        self.assertEqual(list((peer.root / "destination").glob("*.json")), [])

    def test_wire_faults_never_report_replication(self):
        for fault in ("write_interrupt", "read_interrupt", "read_failure", "corrupt", "corrupt_final",
                      "rename_failure", "rename_lost_ack", "race_conflict", "slow_read"):
            with self.subTest(fault=fault), self.scenario(fault) as (peer, config):
                error = self.expect_failure(config)
                self.assertNotEqual(error.code, "INVALID_CONFIG")
                objects = list((peer.root / "destination").glob("*.json"))
                self.assertEqual(objects, [])
                if fault == "race_conflict":
                    self.assertEqual((peer.root / "destination" / (self.ident + "-r7.dump")).read_bytes(), b"foreign-race")

    def test_rename_capability_rejected(self):
        for fault in ("rename_unsupported", "overwrite_rename", "no_hardlink"):
            with self.subTest(fault=fault), self.scenario(fault) as (peer, config):
                self.expect_failure(config)
                self.assertEqual(list((peer.root / "destination").iterdir()), [])

    def test_local_metadata_mutation_and_symlink_rejected(self):
        config = self.config()
        for change in ({"local_state": "FAILED"}, {"sha256": "0"*64}, {"archive_key": "../escape"},
                       {"destination_revision": 6}):
            catalog = self.store.catalog / (self.ident + ".json")
            try:
                catalog.write_bytes(adapter.canonical(self.record | change))
                self.expect_failure(config)
            finally:
                catalog.write_bytes(adapter.canonical(self.record))
        root_alias = self.case / "local_alias"
        root_alias.symlink_to(self.local_root)
        with self.assertRaises((OSError, adapter.RemoteError)):
            adapter.LocalArchive(str(root_alias), self.ident, 1024*1024)

    def test_local_mutation_during_upload_prevents_publish(self):
        peer, config = self.peer("local_mutation")
        peer.local_archive = self.archive_path
        try:
            self.expect_failure(config, {"LOCAL_CHANGED"})
            self.assertEqual(list((peer.root / "destination").glob("*-r7.dump")), [])
        finally:
            self.archive_path.write_bytes(self.original)

    def test_cli_reports_only_finite_codes(self):
        config = self.config(host_key_sha256="SHA256:" + "A"*43)
        result = subprocess.run([sys.executable, str(Path(adapter.__file__)), "--config", str(self.config_path),
            "probe", "--destination-revision", "7", "--destination-identity", config.identity],
            capture_output=True, text=True, timeout=15)
        self.assertEqual(result.returncode, 1)
        self.assertEqual(json.loads(result.stderr)["error_code"], "HOST_KEY_MISMATCH")
        for secret in (str(self.key_path), str(self.destination), "PRIVATE KEY", self.data["host"]):
            self.assertNotIn(secret, result.stderr + result.stdout)


if __name__ == "__main__":
    unittest.main(verbosity=2)
