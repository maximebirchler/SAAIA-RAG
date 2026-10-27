"""Loopback HTTP wire fixture. No HTTP client, redirect, model or external service.

Only explicitly prepared, owned corpus replay requests are persisted. Raw bodies
are captured before JSON parsing; this fixture is never a production provider.
"""
import argparse
import hashlib
import json
import pathlib
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit


MAX_BODY = 2_000_000


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def current_prompt(body):
    messages = body.get("messages", body.get("input", []))
    candidates = []
    for item in messages:
        if item.get("role") != "user" or not isinstance(item.get("content"), str):
            continue
        try:
            value = json.loads(item["content"])
        except (ValueError, TypeError):
            continue
        if isinstance(value, dict) and isinstance(value.get("evidence"), list):
            candidates.append(value)
    if len(candidates) != 1:
        raise ValueError("one_current_evidence_prompt_required")
    return candidates[0]


def planner(body):
    items = body.get("messages", body.get("input", []))
    return any(item.get("role") == "system" and "research planner" in str(item.get("content", "")) for item in items)


def tool_names(body):
    return [tool.get("function", tool).get("name") for tool in body.get("tools", [])]


def strict_call(name, arguments, body, ordinal):
    if name not in tool_names(body):
        raise ValueError("scripted_operation_not_currently_offered:" + name)
    tools = {tool.get("function", tool).get("name"): tool.get("function", tool) for tool in body["tools"]}
    properties = tools[name]["parameters"]["properties"]
    # OpenAI strict schemas declare nullable optional fields required. Send null
    # only for declared fields, never invent an unsupported provider parameter.
    args = {key: arguments.get(key) for key in tools[name]["parameters"].get("required", [])}
    args.update(arguments)
    if set(args) - set(properties):
        raise ValueError("scripted_argument_not_offered")
    return {"id": "wire_call_" + str(ordinal), "name": name,
            "arguments": json.dumps(args, ensure_ascii=False, separators=(",", ":"))}


def native_inventory(prompt, scenario):
    alternative = prompt.get("alternativeNativeReadings") or {}
    for entry in alternative.get("inventories", []):
        if entry.get("physicalPage") == scenario["physicalPage"]:
            descriptors = entry.get("inventory", {}).get("observations", []) or []
            for descriptor in descriptors:
                if descriptor.get("blockId") == scenario["nativeBlockId"]:
                    return entry["sourceKey"], descriptor
    # The second raw window follows a raw response; its source-bound line
    # reference is an observed capability even when the old inventory is absent.
    for item in prompt.get("evidence", []):
        reference = (item.get("nativeObservation") or {}).get("reference", {})
        if reference.get("blockId") == scenario["nativeBlockId"]:
            return item["sourceKey"], reference
    raise ValueError("required_native_descriptor_not_visible")


def resume_native_read(body, prompt, scenario, ordinal):
    source_key, descriptor = native_inventory(prompt, scenario)
    candidates = [view for view in (prompt.get("alternativeNativeReadings") or {}).get("views", [])
                  if view.get("sourceKey") == source_key and view.get("blockId") == descriptor["blockId"]
                  and view.get("rawTextSha256") == descriptor["rawTextSha256"]
                  and view.get("physicalPage") == scenario["physicalPage"]
                  and view.get("offset") == 1 and view.get("limit") == 2]
    if len(candidates) != 1:
        raise ValueError("one_observed_partial_native_window_required")
    view = candidates[0]
    cursor = view.get("nextOffset")
    if (view.get("status") != "partial_model_budget" or type(cursor) is not int
            or view.get("returnedLineCount", 0) < 1
            or cursor != view["offset"] + view["returnedLineCount"]):
        raise ValueError("observed_partial_native_cursor_required")
    remaining = len(scenario["expectedNativeLines"]) - cursor
    if not 0 < remaining <= len(scenario["expectedNativeLines"]):
        raise ValueError("native_cursor_outside_predeclared_line_target")
    return strict_call("read_source", {"sourceKey": source_key, "pageStart": view["physicalPage"],
        "pageEnd": view["physicalPage"], "topK": 1, "nativeObservationView": "raw",
        "nativeObservationBlockId": descriptor["blockId"], "nativeObservationRawSha256": descriptor["rawTextSha256"],
        "nativeObservationOffset": cursor, "nativeObservationLimit": remaining}, body, ordinal)


def scripted_reply(body, control, turn, ordinal):
    scenario = control["scenario"]
    wrong_raw_attempt = scenario.get("variant") == "native-raw-sha-correction" and turn == 5
    if scenario.get("variant") == "native-raw-sha-correction" and turn >= 6:
        turn -= 1
    if planner(body):
        return {"kind": "answer", "text": json.dumps({"queries": [{"query": scenario["searchQuery"],
            "documentHint": scenario["docPath"], "topK": 1}]}, ensure_ascii=False)}
    prompt = current_prompt(body)
    if not prompt["evidence"]:
        raise ValueError("real_current_evidence_required")
    if scenario.get("variant") == "output-limit":
        item = prompt["evidence"][0]
        literal = item["content"].strip()
        return {"kind": "answer", "outputLimit": True, "text": json.dumps({"outcome": "answered",
            "answerText": "Citation de contrôle interrompue : « " + literal + " » [C1]",
            "claims": [{"claimId": "C1", "selectedItem": "", "text": literal,
                        "evidenceIds": [item["evidenceId"]]}]}, ensure_ascii=False)}
    keys = {item.get("sourceKey") for item in prompt["evidence"] if item.get("sourceKey")}
    if len(keys) != 1:
        raise ValueError("one_observed_source_required")
    source_key = next(iter(keys))
    common = {"sourceKey": source_key, "pageStart": scenario["physicalPage"],
              "pageEnd": scenario["physicalPage"], "topK": 1}
    if turn == 0:
        primary = max((item for item in prompt["evidence"] if item.get("nativeObservation") is None),
                      key=lambda item: item.get("originalContentLength", 0))
        if len(primary["content"]) < 20:
            raise ValueError("visible_primary_prefix_required_for_window_offset")
        offset = len(primary["content"][:10].encode("utf-16-le")) // 2
        call = strict_call("read_evidence", {"evidenceId": primary["evidenceId"], "offset": offset,
                                            "length": 256}, body, ordinal)
    elif turn == 1:
        primary_page = scenario.get("primaryPage", scenario["physicalPage"])
        call = strict_call("read_source", common | {"pageStart": primary_page, "pageEnd": primary_page}, body, ordinal)
    elif turn == 2:
        light = scenario["lightPrimaryPage"]
        call = strict_call("read_source", common | {"pageStart": light, "pageEnd": light}, body, ordinal)
    elif turn == 3:
        call = strict_call("find_source_text", {"sourceKey": source_key, "query": scenario["findQuery"],
            "offset": 0, "topK": 1}, body, ordinal)
    elif turn == 4:
        call = strict_call("read_source", common | {"nativeObservationView": "inventory",
            "nativeObservationOffset": scenario["nativeInventoryOffset"], "nativeObservationLimit": 1}, body, ordinal)
    elif turn in (5, 6):
        observed_key, descriptor = native_inventory(prompt, scenario)
        call = strict_call("read_source", common | {"sourceKey": observed_key,
            "nativeObservationView": "raw", "nativeObservationBlockId": descriptor["blockId"],
            "nativeObservationRawSha256": ("0" * 64 if descriptor["rawTextSha256"] != "0" * 64 else "1" * 64)
                if wrong_raw_attempt else descriptor["rawTextSha256"],
            "nativeObservationOffset": 0 if turn == 5 else 1,
            "nativeObservationLimit": 1 if turn == 5 else 2}, body, ordinal)
    elif turn == 7:
        call = resume_native_read(body, prompt, scenario, ordinal)
    else:
        evidence = [item for item in prompt["evidence"] if item.get("nativeObservation") is not None
                    and item.get("content") and not item.get("contentTruncated")]
        if not evidence:
            raise ValueError("untruncated_native_line_required_for_terminal_result")
        item = max(evidence, key=lambda value: len(value["content"]))
        literal = item["content"].strip()
        return {"kind": "answer", "text": json.dumps({"outcome": "answered",
            "answerText": "Citation littérale de validation du transport : « " + literal + " » [C1]",
            "claims": [{"claimId": "C1", "selectedItem": "", "text": literal,
                        "evidenceIds": ["wire-invented-evidence" if scenario.get("variant") == "invented-citation"
                                        else item["evidenceId"]]}]}, ensure_ascii=False)}
    return {"kind": "tool", "call": call}


def response_envelope(reply, responses, ordinal):
    if responses:
        if reply["kind"] == "tool":
            call = reply["call"]
            output = [{"id": "wire_reasoning_" + str(ordinal), "type": "reasoning", "summary": [],
                       "encrypted_content": "opaque-fixture-reasoning-" + str(ordinal)},
                      {"id": "wire_function_" + str(ordinal), "type": "function_call", "call_id": call["id"],
                       "name": call["name"], "arguments": call["arguments"], "status": "completed"}]
        else:
            output = [{"type": "message", "role": "assistant", "status": "completed",
                       "content": [{"type": "output_text", "text": reply["text"]}]}]
        result = {"id": "wire_response_" + str(ordinal), "status": "incomplete" if reply.get("outputLimit") else "completed", "output": output,
                "usage": {"input_tokens": 100, "output_tokens": 30, "input_tokens_details": {"cached_tokens": 0}}}
        if reply.get("outputLimit"):
            result["incomplete_details"] = {"reason": "max_output_tokens"}
        return result
    message = {"role": "assistant", "content": reply.get("text")}
    if reply["kind"] == "tool":
        call = reply["call"]
        message["tool_calls"] = [{"id": call["id"], "type": "function",
                                  "function": {"name": call["name"], "arguments": call["arguments"]}}]
    return {"id": "wire_completion_" + str(ordinal), "model": "saaia-wire-fixture",
            "choices": [{"index": 0, "message": message,
                         "finish_reason": "length" if reply.get("outputLimit") else "tool_calls" if reply["kind"] == "tool" else "stop"}],
            "usage": {"prompt_tokens": 100, "completion_tokens": 30, "total_tokens": 130}}


class Fixture(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    lock = threading.Lock()
    ordinal = 0
    turns = {}

    def log_message(self, *args):
        pass

    def send(self, value, status=200):
        raw = json.dumps(value, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def do_GET(self):
        path = urlsplit(self.path).path
        if path == "/fixture/status":
            return self.send({"fixture": True, "externalCalls": 0, "rawRequests": type(self).ordinal})
        if path in ("/health", "/healthz", "/"):
            return self.send({"status": "ok", "fixture": True})
        if path in ("/v1/models", "/models"):
            return self.send({"data": [{"id": "saaia-wire-fixture"}]})
        return self.send({"error": "wire_route_not_implemented"}, 404)

    def do_POST(self):
        try:
            path = urlsplit(self.path).path
            if path not in ("/v1/chat/completions", "/v1/responses"):
                return self.send({"error": "wire_route_not_implemented"}, 404)
            if self.headers.get("Authorization") or self.headers.get("api-key"):
                return self.send({"error": "credentials_forbidden_in_wire_fixture"}, 400)
            if self.headers.get("Transfer-Encoding"):
                return self.send({"error": "transfer_encoding_not_supported"}, 400)
            length = int(self.headers.get("Content-Length", "0"))
            if not 0 < length <= MAX_BODY:
                return self.send({"error": "wire_request_size_invalid"}, 413)
            raw = self.rfile.read(length)
            if len(raw) != length:
                raise ValueError("wire_body_incomplete")
            with self.lock:
                type(self).ordinal += 1
                ordinal = type(self).ordinal
                stem = f"request-{ordinal:04d}"
                raw_path = self.server.capture / (stem + ".body.bin")
                with raw_path.open("xb") as handle:
                    handle.write(raw)
                metadata = {"schemaVersion": "saaia-wire-http-request-v1", "ordinal": ordinal,
                            "method": self.command, "path": path, "bodyBytes": len(raw), "bodySha256": sha(raw),
                            "authorizationPresent": False, "loopbackPeer": self.client_address[0],
                            "bodyFile": raw_path.name}
                # Capture is committed before attempting any interpretation.
                (self.server.capture / (stem + ".http.json")).write_text(json.dumps(metadata, indent=2), encoding="utf-8")
            body = json.loads(raw.decode("utf-8", "strict"))
            control = json.loads(self.server.control.read_text(encoding="utf-8-sig"))
            case_id = control["caseId"]
            with self.lock:
                turn = type(self).turns.get(case_id, 0)
                if not planner(body):
                    type(self).turns[case_id] = turn + 1
            reply = scripted_reply(body, control, turn, ordinal)
            envelope = response_envelope(reply, path.endswith("/responses"), ordinal)
            (self.server.capture / (stem + ".response.json")).write_text(
                json.dumps({"caseId": case_id, "turn": None if planner(body) else turn, "reply": reply,
                            "envelope": envelope}, ensure_ascii=False, indent=2), encoding="utf-8")
            self.send(envelope)
        except (ValueError, KeyError, TypeError) as error:
            self.send({"error": {"type": "wire_fixture_script_rejected", "message": str(error)}}, 400)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--control", type=pathlib.Path, required=True)
    parser.add_argument("--events", type=pathlib.Path, required=True)
    parser.add_argument("--capture", type=pathlib.Path, required=True)
    args = parser.parse_args()
    if not 1024 <= args.port <= 65535 or args.port == 5432 or args.capture.exists():
        raise SystemExit("A new capture directory and unprivileged fixture port are required.")
    args.capture.mkdir(parents=True)
    server = ThreadingHTTPServer(("127.0.0.1", args.port), Fixture)
    server.control, server.capture = args.control, args.capture
    server.serve_forever()
