"""Loopback-only deterministic protocol fixture; not a language model or vector index.

No request content is persisted. The control file is local synthetic test data.
Unknown HTTP routes fail closed instead of contacting another service.
"""
import argparse
import hashlib
import json
import pathlib
import threading
import time
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse


def advanced_answered_content(body):
    """One synthetic claim, citing an ID actually supplied by the real writer.

    This is deliberately not a general answer generator. Unknown evidence,
    layouts or requested counts fail instead of fabricating a source identity.
    """
    inputs = []
    for message in body.get("messages", []):
        if message.get("role") != "user" or not isinstance(message.get("content"), str):
            continue
        try:
            candidate = json.loads(message["content"])
        except (ValueError, TypeError):
            continue
        if isinstance(candidate, dict) and isinstance(candidate.get("evidence"), list):
            inputs.append(candidate)
    if len(inputs) != 1:
        raise ValueError("advanced_answered_fixture_requires_one_writer_input")
    writer = inputs[0]
    load = writer.get("load", {})
    count = load.get("answerUnitCount")
    if type(count) is not int or count != 1 or load.get("structuredLayout") is not False or writer.get("claimCoordinates", []):
        raise ValueError("advanced_answered_fixture_requires_one_unstructured_claim")
    marker = "Code de validation ORION : 4826."
    candidates = [item for item in writer["evidence"]
                  if isinstance(item, dict) and isinstance(item.get("content"), str)
                  and marker in item["content"]]
    if len(candidates) != 1:
        raise ValueError("advanced_answered_fixture_requires_unique_canonical_orion_evidence")
    evidence_id = candidates[0].get("evidenceId")
    if not isinstance(evidence_id, str) or not evidence_id.strip() or len(evidence_id) > 128:
        raise ValueError("advanced_answered_fixture_evidence_id_missing")
    return json.dumps({
        "outcome": "answered",
        "answerText": "Résultat contrôlé du parcours avancé : le code ORION est 4826. [C1]",
        "claims": [{"claimId": "C1", "selectedItem": "", "text": "Le code de validation ORION est 4826.",
                    "evidenceIds": [evidence_id]}]
    }, ensure_ascii=False)


def advanced_fixture_content(body, mode):
    system = " ".join(str(m.get("content", "")) for m in body.get("messages", []) if m.get("role") == "system")
    if "research planner" in system:
        return json.dumps({"queries": [{"query": "ORION", "topK": 5}]})
    if mode == "advanced_answered":
        return advanced_answered_content(body)
    return json.dumps({"outcome": "insufficient_documentation", "answerText":
        "Validation synthétique du parcours avancé : le worker a terminé ses opérations. Ce résultat contrôlé ne constitue pas une validation sémantique du modèle.", "claims": []}, ensure_ascii=False)


class Fixture(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    lock = threading.Lock()
    requests = 0
    chat_requests = 0

    def log_message(self, *args):
        pass

    def payload(self):
        length = int(self.headers.get("Content-Length", "0"))
        if length > 2_000_000:
            raise ValueError("fixture request too large")
        return json.loads(self.rfile.read(length)) if length else {}

    def send_json(self, value, status=200):
        raw = json.dumps(value, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def do_GET(self):
        path = urlparse(self.path).path
        if path == "/fixture/status":
            return self.send_json({"fixture": True, "externalCalls": 0,
                                   "requests": self.requests, "chatRequests": self.chat_requests})
        if path in ("/health", "/healthz", "/"):
            return self.send_json({"status": "ok", "fixture": True})
        if path in ("/v1/models", "/models"):
            return self.send_json({"object": "list", "data": [{"id": "saaia-winui-fixture", "object": "model"}]})
        if path.startswith("/collections/"):
            return self.send_json({"status": "ok", "result": {"status": "green", "points_count": 0,
                "config": {"params": {"vectors": {"size": 3, "distance": "Cosine"}}}}})
        return self.send_json({"error": "fixture_route_not_implemented"}, 404)

    def do_PUT(self):
        self.payload()
        if urlparse(self.path).path.startswith("/collections/"):
            return self.send_json({"status": "ok", "result": True})
        return self.send_json({"error": "fixture_route_not_implemented"}, 404)

    def do_POST(self):
        try:
            body = self.payload()
            path = urlparse(self.path).path
            with self.lock:
                type(self).requests += 1
            if path == "/v1/embeddings":
                inputs = body.get("input", [])
                if isinstance(inputs, str):
                    inputs = [inputs]
                return self.send_json({"object": "list", "data": [
                    {"object": "embedding", "index": i, "embedding": [1.0, 0.0, 0.0]}
                    for i, _ in enumerate(inputs)]})
            if path.startswith("/collections/"):
                return self.send_json({"status": "ok", "result": []})
            if path != "/v1/chat/completions":
                return self.send_json({"error": "fixture_route_not_implemented"}, 404)
            with self.lock:
                type(self).chat_requests += 1
                ordinal = type(self).chat_requests
            control = json.loads(self.server.control.read_text(encoding="utf-8-sig"))
            time.sleep(min(120, max(0, float(control.get("delaySeconds", 0)))))
            mode = control.get("mode", "success")
            event = {"atUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                     "ordinal": ordinal, "mode": mode, "stream": bool(body.get("stream")),
                     "requestSha256": hashlib.sha256(json.dumps(body, sort_keys=True).encode()).hexdigest(),
                     "traceparent": self.headers.get("traceparent"),
                     "turnId": self.headers.get("X-SAAIA-Turn-Id"),
                     "sessionId": self.headers.get("X-SAAIA-Session-Id"),
                     "toolNames": [t.get("function", {}).get("name") for t in body.get("tools", [])]}
            with self.lock:
                with self.server.events.open("a", encoding="utf-8") as handle:
                    handle.write(json.dumps(event) + "\n")
            if mode == "error":
                return self.send_json({"error": {"message": "Erreur synthétique de validation WinUI", "type": "fixture_failure"}}, 503)
            content = control.get("response", "Réponse synthétique de validation WinUI. Aucun modèle payant utilisé.")
            if mode in ("advanced", "advanced_answered"):
                content = advanced_fixture_content(body, mode)
            message = {"role": "assistant", "content": content}
            tools = body.get("tools", [])
            if tools and mode != "plain":
                names = [t.get("function", {}).get("name", "") for t in tools]
                selected = control.get("toolName", "submit_operational_route")
                if selected not in names:
                    selected = names[0]
                tool = next(t for t in tools if t.get("function", {}).get("name") == selected)
                parameters = tool.get("function", {}).get("parameters", {})
                args = control.get("toolArguments")
                if args is None:
                    args = {} if not parameters.get("properties") else {
                        "intent": "chat.general", "toolName": "none", "toolArgs": {}}
                message = {"role": "assistant", "content": None, "tool_calls": [{
                    "id": "call_" + uuid.uuid4().hex, "type": "function",
                    "function": {"name": selected, "arguments": json.dumps(args)}}]}
            usage = {"prompt_tokens": 32, "completion_tokens": 12, "total_tokens": 44}
            if not body.get("stream"):
                return self.send_json({"id": "fixture_" + uuid.uuid4().hex, "object": "chat.completion",
                    "model": "saaia-winui-fixture", "choices": [{"index": 0, "message": message,
                    "finish_reason": "tool_calls" if message.get("tool_calls") else "stop"}], "usage": usage})
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.send_header("Connection", "close")
            self.end_headers()
            chunks = []
            if message.get("tool_calls"):
                delta = message.copy()
                delta.pop("content", None)
                delta["tool_calls"][0]["index"] = 0
                chunks.append({"choices": [{"index": 0, "delta": delta, "finish_reason": None}]})
            else:
                for word in content.split(" "):
                    chunks.append({"choices": [{"index": 0, "delta": {"content": word + " "}, "finish_reason": None}]})
            chunks.append({"choices": [{"index": 0, "delta": {}, "finish_reason": "stop"}], "usage": usage})
            for chunk in chunks:
                self.wfile.write(("data: " + json.dumps(chunk, ensure_ascii=False) + "\n\n").encode())
                self.wfile.flush()
                time.sleep(min(1, max(0, float(control.get("chunkDelaySeconds", 0.02)))))
            self.wfile.write(b"data: [DONE]\n\n")
            self.wfile.flush()
            self.close_connection = True
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass  # The client deliberately cancelled or closed its connection.
        except (ValueError, KeyError) as error:
            self.send_json({"error": type(error).__name__}, 400)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--control", type=pathlib.Path, required=True)
    parser.add_argument("--events", type=pathlib.Path, required=True)
    arguments = parser.parse_args()
    server = ThreadingHTTPServer(("127.0.0.1", arguments.port), Fixture)
    server.control = arguments.control
    server.events = arguments.events
    server.serve_forever()
