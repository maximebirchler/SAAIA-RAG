"""One-shot post-delete inventory failure for an owned loopback WinUI fixture.

Nothing fails until an explicitly armed, user/session-scoped DELETE succeeds.
Only the next GET /chat/sessions for that same user receives a synthetic 500.
The DELETE itself and every other request are forwarded to the real backend.
"""
import argparse
import hashlib
import http.client
import json
import pathlib
import threading
import time
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse


HOP_HEADERS = {"connection", "keep-alive", "proxy-authenticate", "proxy-authorization",
               "te", "trailer", "transfer-encoding", "upgrade", "content-length", "host"}


class DeleteRefreshControl:
    def __init__(self, path):
        self.path = pathlib.Path(path)
        self.lock = threading.Lock()

    def _read(self):
        control = json.loads(self.path.read_text(encoding="utf-8-sig"))
        if control.get("schemaVersion") != "saaia-winui-delete-refresh-control.v1":
            raise ValueError("Unknown delete-refresh control schema")
        return control

    def _write(self, control):
        temporary = self.path.with_suffix(".tmp")
        temporary.write_text(json.dumps(control), encoding="utf-8")
        temporary.replace(self.path)

    @staticmethod
    def _matches_scope(control, request_path, is_delete):
        if control.get("enabled") is not True:
            return False
        try:
            target_session = str(uuid.UUID(control.get("targetSessionId", "")))
            target_user = str(uuid.UUID(control.get("targetUserId", "")))
        except (ValueError, TypeError, AttributeError):
            return False
        request = urlparse(request_path)
        users = parse_qs(request.query).get("userId", [])
        if len(users) != 1 or users[0].lower() != target_user:
            return False
        expected = "/chat/sessions/" + target_session if is_delete else "/chat/sessions"
        return request.path.lower() == expected

    def observe_delete_result(self, method, request_path, status):
        if method != "DELETE" or not 200 <= status < 300:
            return False
        with self.lock:
            control = self._read()
            if control.get("state") != "waiting_for_delete_ack" or not self._matches_scope(control, request_path, True):
                return False
            control["state"] = "delete_acknowledged"
            control["deleteStatus"] = status
            self._write(control)
            return True

    def consume_inventory_failure(self, method, request_path):
        if method != "GET":
            return False
        with self.lock:
            control = self._read()
            if control.get("state") != "delete_acknowledged" or not self._matches_scope(control, request_path, False):
                return False
            control["state"] = "consumed"
            control["enabled"] = False
            control["inventoryFailuresInjected"] = 1
            self._write(control)
            return True


class Proxy(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def emit(self, **event):
        event["atUtc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
        with self.server.event_lock:
            with self.server.events.open("a", encoding="utf-8") as output:
                output.write(json.dumps(event) + "\n")

    def send_body(self, status, body, headers=()):
        self.send_response(status)
        for name, value in headers:
            if name.lower() not in HOP_HEADERS:
                self.send_header(name, value)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def forward(self):
        length = int(self.headers.get("Content-Length", "0"))
        if length < 0 or length > 2_000_000:
            return self.send_error(413)
        body = self.rfile.read(length) if length else b""
        path = urlparse(self.path).path
        self.emit(method=self.command, path=path, phase="request",
                  requestSha256=hashlib.sha256(body).hexdigest())
        try:
            inject = self.server.control.consume_inventory_failure(self.command, self.path)
        except (OSError, ValueError, TypeError):
            inject = False
            self.emit(method=self.command, path=path, phase="control_invalid_pass_through")
        if inject:
            response = json.dumps({"error": "synthetic_inventory_failure_after_delete_ack", "fixture": True}).encode()
            self.send_body(500, response, [("Content-Type", "application/json")])
            self.emit(method=self.command, path=path, phase="completed", status=500,
                      action="synthetic_inventory_failure_after_delete_ack")
            return
        connection = http.client.HTTPConnection("127.0.0.1", self.server.upstream_port, timeout=90)
        try:
            headers = {name: value for name, value in self.headers.items() if name.lower() not in HOP_HEADERS}
            headers["Host"] = f"127.0.0.1:{self.server.upstream_port}"
            if body:
                headers["Content-Length"] = str(len(body))
            connection.request(self.command, self.path, body=body, headers=headers)
            incoming = connection.getresponse()
            response = incoming.read(2_000_001)
            if len(response) > 2_000_000:
                raise ValueError("fixture upstream response limit")
            try:
                acknowledged = self.server.control.observe_delete_result(self.command, self.path, incoming.status)
            except (OSError, ValueError, TypeError):
                acknowledged = False
                self.emit(method=self.command, path=path, phase="control_invalid_pass_through")
            self.send_body(incoming.status, response, incoming.getheaders())
            self.emit(method=self.command, path=path, phase="completed", status=incoming.status,
                      action="forward", targetDeleteAcknowledged=acknowledged)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            self.emit(method=self.command, path=path, phase="client_disconnected")
        except (TimeoutError, OSError, ValueError) as error:
            self.emit(method=self.command, path=path, phase="proxy_failed", errorType=type(error).__name__)
            self.send_error(502)
        finally:
            connection.close()

    do_GET = forward
    do_POST = forward
    do_PATCH = forward
    do_DELETE = forward
    do_PUT = forward


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--upstream-port", type=int, required=True)
    parser.add_argument("--control", type=pathlib.Path, required=True)
    parser.add_argument("--events", type=pathlib.Path, required=True)
    args = parser.parse_args()
    if not 1024 <= args.port <= 65535 or not 1024 <= args.upstream_port <= 65535 or args.port == args.upstream_port:
        parser.error("Distinct unprivileged loopback ports are required")
    server = ThreadingHTTPServer(("127.0.0.1", args.port), Proxy)
    server.upstream_port = args.upstream_port
    server.control = DeleteRefreshControl(args.control)
    server.events = args.events
    server.event_lock = threading.Lock()
    server.serve_forever()
