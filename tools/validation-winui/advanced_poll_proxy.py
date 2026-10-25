"""Owned loopback-only WinUI polling fault fixture; never creates or mutates jobs."""
import argparse
import hashlib
import http.client
import json
import pathlib
import re
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse


HOP_HEADERS = {"connection", "keep-alive", "proxy-authenticate", "proxy-authorization",
               "te", "trailer", "transfer-encoding", "upgrade", "content-length", "host"}
JOB_PATH = re.compile(r"^/advanced-analysis/jobs/([0-9a-fA-F-]{36})$")
MODES = {"passthrough", "http503", "regressive_revision", "session_mismatch"}


class Proxy(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    lock = threading.Lock()
    observed_revisions = {}

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
        parsed = urlparse(self.path)
        match = JOB_PATH.fullmatch(parsed.path) if self.command == "GET" else None
        query_user = parse_qs(parsed.query).get("userId", [""])
        job_id = match.group(1).lower() if match else None
        mode = "passthrough"
        if match and len(query_user) == 1 and query_user[0] == self.server.user_id:
            with self.lock:
                control = json.loads(self.server.control.read_text(encoding="utf-8-sig"))
                remaining = min(10_000, max(0, int(control.get("remaining", 0))))
                requested = control.get("mode", "passthrough")
                if requested not in MODES:
                    raise ValueError("Unknown bounded polling fixture mode")
                if remaining and control.get("targetJobId", "").lower() == job_id:
                    mode = requested
                    control["remaining"] = remaining - 1
                    temporary = self.server.control.with_suffix(".tmp")
                    temporary.write_text(json.dumps(control), encoding="utf-8")
                    temporary.replace(self.server.control)
        event = {"method": self.command, "path": parsed.path, "jobId": job_id,
                 "mode": mode, "requestSha256": hashlib.sha256(body).hexdigest(),
                 "traceparent": self.headers.get("traceparent"),
                 "turnId": self.headers.get("X-SAAIA-Turn-Id"),
                 "sessionId": self.headers.get("X-SAAIA-Session-Id")}
        self.emit(event)
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
            upstream_status = incoming.status
            status = upstream_status
            metadata = {}
            if match and upstream_status == 200:
                payload = json.loads(response)
                if payload.get("jobId", "").lower() != job_id:
                    raise ValueError("Upstream job identity did not match request")
                revision = int(payload["revision"])
                with self.lock:
                    previous = self.observed_revisions.get(job_id, revision)
                    self.observed_revisions[job_id] = max(previous, revision)
                metadata = {"upstreamJobStatus": payload.get("status"), "upstreamRevision": revision,
                            "upstreamSessionId": payload.get("sessionId")}
                if mode == "http503":
                    status = 503
                    response = json.dumps({"error": "synthetic_polling_unavailable", "fixture": True}).encode()
                elif mode == "regressive_revision":
                    payload["revision"] = max(0, previous - 1)
                    response = json.dumps(payload).encode()
                    metadata["injectedRevision"] = payload["revision"]
                elif mode == "session_mismatch":
                    payload["sessionId"] = "00000000-0000-4000-8000-000000000024"
                    response = json.dumps(payload).encode()
                    metadata["injectedSessionId"] = payload["sessionId"]
            self.send_response(status)
            for name, value in incoming.getheaders():
                if name.lower() not in HOP_HEADERS and name.lower() != "content-type":
                    self.send_header(name, value)
            self.send_header("Content-Type", incoming.getheader("Content-Type", "application/json"))
            self.send_header("Content-Length", str(len(response)))
            self.end_headers()
            self.wfile.write(response)
            self.emit({"method": self.command, "path": parsed.path, "phase": "completed",
                       "jobId": job_id, "mode": mode, "upstreamStatus": upstream_status, "status": status,
                       "responseSha256": hashlib.sha256(response).hexdigest(),
                       "traceparent": self.headers.get("traceparent"),
                       "turnId": self.headers.get("X-SAAIA-Turn-Id"), **metadata})
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            self.emit({"path": parsed.path, "phase": "client_disconnected", "mode": mode})
        except (TimeoutError, OSError, ValueError, KeyError) as error:
            self.emit({"path": parsed.path, "phase": "proxy_failed", "errorType": type(error).__name__})
            self.send_error(502)
        finally:
            connection.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--upstream-port", type=int, required=True)
    parser.add_argument("--user-id", required=True)
    parser.add_argument("--control", type=pathlib.Path, required=True)
    parser.add_argument("--events", type=pathlib.Path, required=True)
    args = parser.parse_args()
    server = ThreadingHTTPServer(("127.0.0.1", args.port), Proxy)
    server.upstream_port, server.user_id = args.upstream_port, args.user_id
    server.control, server.events = args.control, args.events
    server.serve_forever()
