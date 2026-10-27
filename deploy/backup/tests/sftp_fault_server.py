"""Loopback SSH/SFTP fault peer. Faults are injected server-side on real I/O."""
import os
from pathlib import Path
import socket
import threading

import paramiko


class Auth(paramiko.ServerInterface):
    def __init__(self, owner):
        self.owner = owner

    def check_auth_publickey(self, username, key):
        if self.owner.fault == "auth_timeout":
            self.owner.stop.wait(4)
        if self.owner.fault == "auth" or key != self.owner.client_key:
            return paramiko.AUTH_FAILED
        return paramiko.AUTH_SUCCESSFUL

    def get_allowed_auths(self, username):
        return "publickey"

    def check_channel_request(self, kind, chanid):
        return paramiko.OPEN_SUCCEEDED if kind == "session" else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED


class Handle(paramiko.SFTPHandle):
    def __init__(self, peer, path, fd, flags):
        super().__init__(flags)
        self.peer, self.path, self.fd = peer, path, fd

    def stat(self):
        return paramiko.SFTPAttributes.from_stat(os.fstat(self.fd))

    def read(self, offset, length):
        data = os.pread(self.fd, length, offset)
        if "stage-" in self.path or self.path.endswith(".dump"):
            if self.peer.fault == "read_failure":
                return paramiko.SFTP_FAILURE
            if self.peer.fault == "read_interrupt":
                self.peer.transport.close()
                return paramiko.SFTP_FAILURE
            if self.peer.fault == "corrupt" and data:
                return bytes([data[0] ^ 1]) + data[1:]
            if self.peer.fault == "slow_read":
                self.peer.stop.wait(4)
        return data

    def write(self, offset, data):
        if "stage-" in self.path:
            if self.peer.fault == "write_interrupt":
                os.pwrite(self.fd, data[:max(1, len(data)//2)], offset)
                self.peer.transport.close()
                return paramiko.SFTP_FAILURE
            if self.peer.fault == "local_mutation":
                with open(self.peer.local_archive, "ab") as fp:
                    fp.write(b"mutation")
        os.pwrite(self.fd, data, offset)
        return paramiko.SFTP_OK

    def chattr(self, attrs):
        if attrs._flags & attrs.FLAG_PERMISSIONS:
            os.fchmod(self.fd, attrs.st_mode)
        return paramiko.SFTP_OK

    def close(self):
        os.close(self.fd)
        return paramiko.SFTP_OK


class Files(paramiko.SFTPServerInterface):
    def __init__(self, server, *, peer):
        super().__init__(server)
        self.peer = peer

    def local(self, path):
        parts = Path(path).parts
        if ".." in parts:
            raise PermissionError()
        return str(self.peer.root / path.lstrip("/"))

    def canonicalize(self, path):
        return path

    def lstat(self, path):
        try:
            return paramiko.SFTPAttributes.from_stat(os.lstat(self.local(path)))
        except OSError as exc:
            return paramiko.SFTPServer.convert_errno(exc.errno)

    stat = lstat

    def open(self, path, flags, attrs):
        if self.peer.fault == "permission":
            return paramiko.SFTP_PERMISSION_DENIED
        try:
            return Handle(self.peer, path, os.open(self.local(path), flags, 0o600), flags)
        except OSError as exc:
            return paramiko.SFTPServer.convert_errno(exc.errno)

    def remove(self, path):
        if self.peer.fault == "cleanup":
            return paramiko.SFTP_PERMISSION_DENIED
        try:
            os.unlink(self.local(path))
            return paramiko.SFTP_OK
        except OSError as exc:
            return paramiko.SFTPServer.convert_errno(exc.errno)

    def rename(self, source, target):
        if self.peer.fault == "rename_unsupported":
            return paramiko.SFTP_OP_UNSUPPORTED
        if "stage-" in source:
            if self.peer.fault == "rename_failure":
                return paramiko.SFTP_FAILURE
            if self.peer.fault == "race_conflict":
                Path(self.local(target)).write_bytes(b"foreign-race")
        try:
            if self.peer.fault == "overwrite_rename":
                os.replace(self.local(source), self.local(target))
            else:
                os.link(self.local(source), self.local(target))
                os.unlink(self.local(source))
            if "stage-" in source and self.peer.fault == "rename_lost_ack":
                self.peer.transport.close()
            if "stage-" in source and self.peer.fault == "corrupt_final":
                Path(self.local(target)).write_bytes(b"corrupt-final")
            return paramiko.SFTP_OK
        except OSError as exc:
            return paramiko.SFTPServer.convert_errno(exc.errno)


class Extensions(paramiko.SFTPServer):
    def _process(self, kind, number, message):
        if kind == paramiko.sftp.CMD_EXTENDED:
            tag = message.get_text()
            if tag == "hardlink@openssh.com":
                if self.server.peer.fault == "no_hardlink":
                    self._send_status(number, paramiko.SFTP_OP_UNSUPPORTED)
                    return
                try:
                    os.link(self.server.local(message.get_text()), self.server.local(message.get_text()))
                    self._send_status(number, paramiko.SFTP_OK)
                except OSError as exc:
                    self._send_status(number, paramiko.SFTPServer.convert_errno(exc.errno))
                return
            self._send_status(number, paramiko.SFTP_OP_UNSUPPORTED)
            return
        super()._process(kind, number, message)


class FaultPeer:
    def __init__(self, root, host_key, client_key, fault):
        self.root, self.client_key, self.fault = Path(root), client_key, fault
        self.root.mkdir(mode=0o700)
        (self.root / "destination").mkdir(mode=0o700)
        self.stop = threading.Event()
        self.sock = socket.socket()
        self.sock.bind(("127.0.0.1", 0))
        self.port = self.sock.getsockname()[1]
        self.sock.listen(1)
        self.sock.settimeout(0.1)
        self.transport = None
        self.host_key = host_key
        self.thread = threading.Thread(target=self.serve, daemon=True)
        self.thread.start()

    def serve(self):
        while not self.stop.is_set():
            try:
                conn, _ = self.sock.accept()
            except socket.timeout:
                continue
            except OSError:
                return
            transport = self.transport = paramiko.Transport(conn)
            transport.local_version = "SSH-2.0-OpenSSH_fault_fixture"
            transport.add_server_key(self.host_key)
            transport.set_subsystem_handler("sftp", Extensions, Files, peer=self)
            try:
                if self.fault == "connect_timeout":
                    self.stop.wait(4)
                transport.start_server(server=Auth(self))
                while transport.is_active() and not self.stop.wait(0.05):
                    pass
            except (EOFError, OSError, paramiko.SSHException):
                pass
            finally:
                transport.close()

    def close(self):
        self.stop.set()
        self.sock.close()
        if self.transport:
            self.transport.close()
        self.thread.join(5)
