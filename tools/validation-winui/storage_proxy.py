"""Loopback storage fault proxy for the owned synthetic WinUI backend only.

The production backend is unchanged. Controls can delay/reject user-message
POSTs, reject message/readiness GETs, or delay a bounded number of message GETs
for one explicit session UUID. Nothing is transmitted outside loopback.
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


def message_session_id(method, request_path):
    """Only a single explicit session UUID can target a GET delay."""
    if method != "GET":
        return None
    parsed = urlparse(request_path)
    if parsed.path == "/chat/messages":
        values = parse_qs(parsed.query).get("sessionId", [])
        if len(values) != 1:
            return None
        value = values[0]
    else:
        pieces = parsed.path.split("/")
        if len(pieces) != 5 or pieces[1:3] != ["chat", "sessions"] or pieces[4] != "messages":
            return None
        value = pieces[3]
    try:
        return str(uuid.UUID(value))
    except (ValueError, AttributeError):
        return None


def consume_message_get_delay(control, method, request_path):
    """Mutate only the bounded matching counter; caller owns the file lock."""
    session = message_session_id(method, request_path)
    try:
        target = str(uuid.UUID(control.get("delayGetMessagesSessionId", "")))
        remaining = min(10_000, max(0, int(control.get("delayGetMessagesRemaining", 0))))
        delay = min(30, max(0, float(control.get("delayGetMessagesSeconds", 0))))
    except (ValueError, TypeError, AttributeError):
        return 0
    if not session or session != target or remaining == 0 or delay == 0:
        return 0
    control["delayGetMessagesRemaining"] = remaining - 1
    return delay


class Proxy(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    lock = threading.Lock()

    def log_message(self, *args):
        pass

    def emit(self, event):
        event["atUtc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
        with self.lock:
            with self.server.events.open("a", encoding="utf-8") as output:
                output.write(json.dumps(event) + "\n")

    def do_GET(self):
        self.forward()

    def do_POST(self):
        self.forward()

    def do_PATCH(self):
        self.forward()

    def do_DELETE(self):
        self.forward()

    def do_PUT(self):
        self.forward()

    def forward(self):
        length = int(self.headers.get("Content-Length", "0"))
        if length > 2_000_000:
            return self.send_error(413)
        body = self.rfile.read(length) if length else b""
        path = urlparse(self.path).path
        message_endpoint = path == "/chat/messages" or (
            path.startswith("/chat/sessions/") and path.endswith("/messages"))
        matches = False
        if self.command == "POST" and message_endpoint:
            try:
                matches = json.loads(body).get("role") == "user"
            except (ValueError, AttributeError):
                pass
        control = {}
        action = "forward"
        delay = 0
        failure_counter = None
        failure_status = 500
        if matches:
            failure_counter = "failUserMessagesRemaining"
        elif self.command == "GET" and message_endpoint:
            failure_counter = "failGetMessagesRemaining"
        elif self.command == "GET" and path == "/ready":
            failure_counter = "failReadyRemaining"
            failure_status = 503
        if failure_counter:
            with self.lock:
                control = json.loads(self.server.control.read_text(encoding="utf-8-sig"))
                failures = min(10_000, max(0, int(control.get(failure_counter, 0))))
                if failures:
                    action = {"failUserMessagesRemaining": "synthetic_storage_failure",
                              "failGetMessagesRemaining": "synthetic_message_load_failure",
                              "failReadyRemaining": "synthetic_readiness_failure"}[failure_counter]
                    control[failure_counter] = failures - 1
                    temporary = self.server.control.with_suffix(".tmp")
                    temporary.write_text(json.dumps(control), encoding="utf-8")
                    temporary.replace(self.server.control)
                elif matches:
                    delay = min(120, max(0, float(control.get("delayUserMessagesSeconds", 0))))
                    if delay:
                        action = "synthetic_storage_delay"
                else:
                    delay = consume_message_get_delay(control, self.command, self.path)
                    if delay:
                        action = "synthetic_message_load_delay"
                        temporary = self.server.control.with_suffix(".tmp")
                        temporary.write_text(json.dumps(control), encoding="utf-8")
                        temporary.replace(self.server.control)
        event = {"method": self.command, "path": path, "userMessageMatched": matches,
                 "action": action, "delaySeconds": delay,
                 "requestSha256": hashlib.sha256(body).hexdigest(),
                 "traceparent": self.headers.get("traceparent"),
                 "turnId": self.headers.get("X-SAAIA-Turn-Id"),
                 "sessionId": self.headers.get("X-SAAIA-Session-Id"),
                 "messageGetSessionId": message_session_id(self.command, self.path)}
        self.emit(event)
        try:
            if action in ("synthetic_storage_failure", "synthetic_message_load_failure", "synthetic_readiness_failure"):
                response = json.dumps({"error": action, "fixture": True}).encode()
                self.send_response(failure_status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(response)))
                self.end_headers()
                self.wfile.write(response)
                return
            if delay:
                time.sleep(delay)
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
                self.send_response(incoming.status)
                for name, value in incoming.getheaders():
                    if name.lower() not in HOP_HEADERS:
                        self.send_header(name, value)
                self.send_header("Content-Length", str(len(response)))
                self.end_headers()
                self.wfile.write(response)
                self.emit({"method": self.command, "path": path, "phase": "completed",
                           "userMessageMatched": matches, "status": incoming.status,
                           "action": action, "turnId": self.headers.get("X-SAAIA-Turn-Id")})
            finally:
                connection.close()
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            self.emit({"method": self.command, "path": path, "phase": "client_disconnected", "action": action})
        except (TimeoutError, OSError, ValueError) as error:
            self.emit({"method": self.command, "path": path, "phase": "proxy_failed", "errorType": type(error).__name__})
            self.send_error(502)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--upstream-port", type=int, required=True)
    parser.add_argument("--control", type=pathlib.Path, required=True)
    parser.add_argument("--events", type=pathlib.Path, required=True)
    args = parser.parse_args()
    server = ThreadingHTTPServer(("127.0.0.1", args.port), Proxy)
    server.upstream_port = args.upstream_port
    server.control = args.control
    server.events = args.events
    server.serve_forever()
