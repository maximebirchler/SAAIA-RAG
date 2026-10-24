"""Bounded loopback fixture for probe tests. Never contacts a model/provider."""
import argparse
import json
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_POST(self):
        payload = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        if not payload["stream"]:
            body = json.dumps({"model": "fixture", "choices": [{"message": {"content": "ok"}}]}).encode()
            self.send_response(200)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Connection", "close")
        self.end_headers()
        try:
            if self.path.startswith("/slow"):
                time.sleep(3)
            else:
                time.sleep(0.04)
            self.event({"model": "fixture", "choices": [{"delta": {"role": "assistant"}}]})
            self.event({"model": "fixture", "choices": [{"delta": {"content": "ok"}}]})
            time.sleep(0.10)
            if self.path.startswith("/incomplete"):
                return
            self.event({"choices": [{"delta": {}, "finish_reason": "stop"}]})
            if not self.path.startswith("/no-usage"):
                self.event({"choices": [], "usage": {"completion_tokens": 4}})
            self.wfile.write(b"data: [DONE]\n\n")
            self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError):
            pass

    def event(self, value):
        self.wfile.write(("data: " + json.dumps(value) + "\n\n").encode())
        self.wfile.flush()


parser = argparse.ArgumentParser()
parser.add_argument("--ready-file", required=True)
args = parser.parse_args()
server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
server.daemon_threads = True
Path(args.ready_file).write_text(str(server.server_port), encoding="utf-8")
server.serve_forever()
