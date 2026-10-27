"""Mutation controls for an already positive wire qualification, on copies only.

No database, process control, secrets, model endpoint, or network access. Deep
mutations deliberately rebind transport hashes so documentary checks must fail.
Run --self-test for pure checks, or --execute with an explicitly pinned verifier.
"""
import argparse
import ast
import copy
import hashlib
import importlib.util
import json
import pathlib
import re
import sys
import tempfile
import unittest
import uuid

sys.dont_write_bytecode = True
HERE = pathlib.Path(__file__).resolve().parent
REPO = HERE.parent.parent
OUTPUT_PARENT = REPO / "artifacts" / "provider-wire-qualification-20261027"
MAX_FILE_BYTES = 64 * 1024 * 1024
MAX_TOTAL_BYTES = 512 * 1024 * 1024
MAX_FILES = 512


def extended_windows_path(path):
    if sys.platform != "win32":
        return path
    absolute = str(path.resolve())
    if not absolute.startswith("\\\\?\\"):
        absolute = "\\\\?\\UNC\\" + absolute[2:] if absolute.startswith("\\\\") else "\\\\?\\" + absolute
    return pathlib.Path(absolute)


def require(condition, reason):
    if not condition:
        raise ValueError(reason)


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def json_bytes(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False).encode("utf-8")


def write_json(path, value):
    path.write_bytes(json_bytes(value))


def no_reparse(path, boundary):
    resolved = path.resolve(strict=True)
    require(resolved.is_relative_to(boundary.resolve(strict=True)), "input_path_outside_closed_scope")
    current = path
    while True:
        info = current.lstat()
        require(not current.is_symlink() and not getattr(info, "st_file_attributes", 0) & 0x400,
                "reparse_or_symlink_input_forbidden")
        if current == boundary:
            break
        require(current != current.parent, "input_boundary_not_reached")
        current = current.parent
    return resolved


def file_bytes(path):
    require(path.is_file() and path.stat().st_size <= MAX_FILE_BYTES, "input_file_missing_or_too_large")
    return path.read_bytes()


def selected_inputs(runtime, cases_path, capture_path):
    """Closed list: never recursively copy a runtime directory or its database."""
    files = {"plan.json": runtime.parent / "plan.json",
             "inputs/cases.json": cases_path,
             "inputs/source-publication.json": capture_path}
    for name in ("environment.json", "advanced-worker.json", "corpus-replay.json", "advanced-backend.stdout.log"):
        files["runtime/" + name] = runtime / name
    file_bytes(cases_path)
    cases = read_json(cases_path)["cases"]
    for case in cases:
        identity = case["caseId"]
        require(re.fullmatch(r"wire-[a-z0-9_-]{1,80}", identity), "case_identity_not_closed_relative_name")
        for name in ("api-final.json", "sql-job.json"):
            files[f"runtime/{identity}/{name}"] = runtime / identity / name
    for path in sorted((runtime / "wire-captures").glob("request-*.http.json")):
        require(re.fullmatch(r"request-[0-9]{4,8}\.http\.json", path.name), "http_capture_name_invalid")
        no_reparse(path, runtime)
        file_bytes(path)
        meta = read_json(path)
        require(re.fullmatch(r"request-[0-9]{4,8}\.body\.bin", meta["bodyFile"]), "http_body_name_invalid")
        for candidate in (path, path.with_name(path.name.replace(".http.json", ".response.json")),
                          path.parent / meta["bodyFile"]):
            files["runtime/wire-captures/" + candidate.name] = candidate
    for path in sorted((runtime / "wire-application-traces").glob("*.json")):
        require(re.fullmatch(r"[A-Za-z0-9_.-]{1,200}\.json", path.name), "application_trace_name_invalid")
        files["runtime/wire-application-traces/" + path.name] = path
    require(len(files) <= MAX_FILES, "closed_snapshot_file_count_exceeded")
    for path in files.values():
        # Explicit inputs may live elsewhere, but their own final path must not
        # be a link. Runtime descendants have the stronger closed-scope guard.
        no_reparse(path, runtime if path.is_relative_to(runtime) else path.parent)
        file_bytes(path)
    return files


def snapshot(files):
    return {name: {"sha256": sha(file_bytes(path)), "bytes": path.stat().st_size}
            for name, path in files.items()}


def copy_selected(files, destination):
    require(not destination.exists(), "mutation_copy_destination_already_exists")
    destination.mkdir(parents=True)
    for relative, source in files.items():
        target = destination / relative
        require(target.resolve().is_relative_to(destination.resolve()), "copy_target_outside_owned_scratch")
        target.parent.mkdir(parents=True, exist_ok=True)
        with target.open("xb") as stream:
            stream.write(file_bytes(source))


def records(runtime):
    for path in sorted((runtime / "wire-captures").glob("request-*.http.json")):
        meta = read_json(path)
        yield path, meta, json.loads((path.parent / meta["bodyFile"]).read_bytes())


def current_prompt(body):
    for message in body.get("messages", body.get("input", [])):
        if message.get("role") == "user" and isinstance(message.get("content"), str):
            prompt = json.loads(message["content"])
            if isinstance(prompt.get("evidence"), list):
                return message, prompt
    return None, None


def rewrite_probe_bytes(log, old_sha, new_sha, characters):
    result, matches = [], 0
    for line in log.splitlines(keepends=True):
        if b"operation=llm.call " in line and b"measurements=" in line:
            prefix, raw = line.split(b"measurements=", 1)
            probe = json.loads(raw.decode("ascii", "strict"))
            if probe.get("request_sha256", "").lower() == old_sha:
                matches += 1
                probe["request_sha256"] = new_sha.upper()
                probe["request_characters"] = characters
                ending = b"\r\n" if line.endswith(b"\r\n") else b"\n" if line.endswith(b"\n") else b""
                line = prefix + b"measurements=" + json.dumps(probe, ensure_ascii=True,
                    separators=(",", ":"), allow_nan=False).encode("ascii") + ending
        result.append(line)
    require(matches == 1, "probe_rebind_requires_one_exact_match")
    return b"".join(result)


def rebind_body(runtime, meta_path, body):
    """Keep byte/tracing bindings coherent so a deep oracle is actually tested."""
    meta = read_json(meta_path)
    old_sha = meta["bodySha256"].lower()
    raw = json_bytes(body)
    digest = sha(raw)
    traces = [(path, read_json(path)) for path in (runtime / "wire-application-traces").glob("*.json")]
    matched = [(path, trace) for path, trace in traces if trace["requestSha256"].lower() == old_sha]
    require(len(matched) == 1, "trace_rebind_requires_one_exact_match")
    trace_path, trace = matched[0]
    trace["requestSha256"], trace["requestJson"] = digest.upper(), raw.decode("utf-8")
    meta["bodyBytes"], meta["bodySha256"] = len(raw), digest
    log_path = runtime / "advanced-backend.stdout.log"
    log = rewrite_probe_bytes(log_path.read_bytes(), old_sha, digest, len(raw.decode("utf-8").encode("utf-16-le")) // 2)
    (meta_path.parent / meta["bodyFile"]).write_bytes(raw)
    write_json(meta_path, meta)
    write_json(trace_path, trace)
    log_path.write_bytes(log)


def first_native(runtime):
    for path, _, body in records(runtime):
        message, prompt = current_prompt(body)
        if prompt:
            for item in prompt["evidence"]:
                if item.get("nativeObservation") is not None:
                    return path, body, message, prompt, item
    raise ValueError("positive_fixture_has_no_native_model_evidence")


def alter_body_bytes(runtime):
    path, meta, _ = next(records(runtime))
    body = path.parent / meta["bodyFile"]
    body.write_bytes(body.read_bytes() + b" ")
    return "actual_wire_body_hash_or_size_differs", False


def mislabel_protocol(runtime):
    path = runtime.parent / "plan.json"
    plan = read_json(path)
    protocol = "responses" if plan["protocol"] == "chat-completions" else "chat-completions"
    plan["protocol"] = protocol
    worker_path = runtime / "advanced-worker.json"
    worker = read_json(worker_path)
    worker["nativeResearchApiProtocol"] = protocol
    write_json(path, plan)
    write_json(worker_path, worker)
    return "request_did_not_use_planned_wire_protocol", False


def alter_native_quality(runtime):
    path, body, message, prompt, item = first_native(runtime)
    item["nativeObservation"]["qualitySignals"] = ["mutation_removed_actual_warnings"]
    message["content"] = json_bytes(prompt).decode("utf-8")
    rebind_body(runtime, path, body)
    return "native_uncertainty_or_quality_lost", True


def alter_native_reference(runtime):
    path, body, message, prompt, item = first_native(runtime)
    item["nativeObservation"]["reference"]["lineId"] = "native_line_" + "0" * 64
    message["content"] = json_bytes(prompt).decode("utf-8")
    rebind_body(runtime, path, body)
    return "native_reference_changed_before_model", True


def alter_application_trace(runtime):
    _, meta, _ = next(records(runtime))
    matched = [(path, read_json(path)) for path in (runtime / "wire-application-traces").glob("*.json")
               if read_json(path)["requestSha256"].lower() == meta["bodySha256"]]
    require(len(matched) == 1, "trace_mutation_requires_one_match")
    path, trace = matched[0]
    trace["requestJson"] += " "
    write_json(path, trace)
    return "application_serialized_request_and_received_bytes_differ", False


def alter_terminal_citation(runtime):
    case_path = sorted(runtime.glob("wire-*/api-final.json"))[0]
    api, sql = read_json(case_path), read_json(case_path.with_name("sql-job.json"))
    for value in (api, sql):
        value["result"]["evidence"][0]["nativeObservation"]["blockId"] = "mutated-terminal-block"
    write_json(case_path, api)
    write_json(case_path.with_name("sql-job.json"), sql)
    return "terminal_citation_identity_changed", False


def alter_linked_arguments(runtime):
    protocol = read_json(runtime.parent / "plan.json")["protocol"]
    latest_calls = {}
    for path, _, body in records(runtime):
        response = read_json(path.with_name(path.name.replace(".http.json", ".response.json")))
        previous = latest_calls.get(response["caseId"])
        messages = body.get("messages", body.get("input", []))
        if previous:
            candidates = ([item for item in messages if item.get("type") == "function_call"
                           and item.get("call_id") == previous["id"]]
                if protocol == "responses" else [call["function"] for message in messages
                    if message.get("role") == "assistant" for call in message.get("tool_calls", [])
                    if call.get("id") == previous["id"]])
            require(len(candidates) == 1, "linked_argument_mutation_requires_one_current_original_call")
            arguments = json.loads(candidates[0]["arguments"])
            arguments["mutationExtraArgument"] = "changed_after_original_provider_call"
            candidates[0]["arguments"] = json_bytes(arguments).decode("utf-8")
            rebind_body(runtime, path, body)
            return ("responses_tool_origin_or_unique_result_lost" if protocol == "responses"
                    else "chat_tool_origin_or_unique_result_lost"), True
        if response["reply"]["kind"] == "tool":
            latest_calls[response["caseId"]] = response["reply"]["call"]
    raise ValueError("positive_fixture_has_no_linked_native_tool_call")


MUTATIONS = (("body-bytes", alter_body_bytes), ("protocol-label", mislabel_protocol),
             ("native-quality", alter_native_quality), ("native-reference", alter_native_reference),
             ("application-trace", alter_application_trace), ("terminal-citation", alter_terminal_citation),
             ("linked-tool-arguments", alter_linked_arguments))


def execute(args):
    input_paths = tuple(extended_windows_path(path) for path in (args.runtime, args.cases, args.capture))
    for path in input_paths:
        no_reparse(path.absolute(), pathlib.Path(path.absolute().anchor))
    runtime, cases_path, capture_path = (path.resolve(strict=True) for path in input_paths)
    scratch = extended_windows_path(args.scratch).resolve()
    output_parent = extended_windows_path(OUTPUT_PARENT).resolve()
    require(scratch.is_relative_to(output_parent) and scratch != output_parent
            and scratch.name.startswith("verifier-mutations-") and not scratch.exists(), "new_owned_mutation_scratch_required")
    verifier_path = HERE / "verify_wire.py"
    prepare_path = HERE / "prepare_cases.py"
    require(re.fullmatch(r"[0-9a-f]{64}", args.verifier_sha256 or "")
            and sha(verifier_path.read_bytes()) == args.verifier_sha256, "verifier_pin_differs_or_missing")
    require(re.fullmatch(r"[0-9a-f]{64}", args.prepare_cases_sha256 or "")
            and sha(prepare_path.read_bytes()) == args.prepare_cases_sha256, "prepare_cases_dependency_pin_differs_or_missing")
    files = selected_inputs(runtime, cases_path, capture_path)
    before = snapshot(files)
    runtime_bytes = sum(item["bytes"] for name, item in before.items() if not name.startswith("inputs/"))
    require(sum(item["bytes"] for item in before.values()) + runtime_bytes * len(MUTATIONS) <= MAX_TOTAL_BYTES,
            "mutation_copy_byte_budget_exceeded")
    sys.path.insert(0, str(HERE))
    spec = importlib.util.spec_from_file_location("wire_verifier_under_mutation_review", verifier_path)
    verifier = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(verifier)
    # This read-only gate must pass on the actual evidence before any scratch
    # mutation is created. Failed or merely planned campaigns are not controls.
    require(verifier.verify(runtime, cases_path, capture_path).get("passed") is True, "original_campaign_not_positive")
    require(snapshot(files) == before, "original_evidence_changed_during_positive_gate")
    scratch.mkdir(parents=True)
    write_json(scratch / "owner.json", {"schema": "saaia-wire-verifier-mutations-v1", "id": str(uuid.uuid4()),
        "noNetwork": True, "noDatabase": True, "noProcessControl": True,
        "verifierSha256": args.verifier_sha256, "prepareCasesSha256": args.prepare_cases_sha256})
    baseline = scratch / "baseline"
    copy_selected(files, baseline)
    copied_cases, copied_capture = baseline / "inputs/cases.json", baseline / "inputs/source-publication.json"
    require(verifier.verify(baseline / "runtime", copied_cases, copied_capture).get("passed") is True,
            "closed_copy_did_not_reproduce_positive_control")
    runtime_sources = {name: baseline / name for name in before if not name.startswith("inputs/")}
    results = []
    for name, mutate in MUTATIONS:
        folder = scratch / name
        copy_selected(runtime_sources, folder)
        mutated_root = folder / "runtime"
        try:
            expected, rebound = mutate(mutated_root)
        except Exception as error:
            results.append({"mutation": name, "passed": False, "setupError": type(error).__name__ + ": " + str(error)})
            continue
        observed = None
        try:
            verifier.verify(mutated_root, copied_cases, copied_capture)
        except ValueError as error:
            observed = str(error)
        except Exception as error:
            observed = "UNEXPECTED_EXCEPTION:" + type(error).__name__ + ":" + str(error)
        results.append({"mutation": name, "deepTransportBindingsRecomputed": rebound,
                        "expectedReason": expected, "observedReason": observed, "passed": observed == expected})
    unchanged = snapshot(files) == before
    result = {"schema": "saaia-wire-verifier-mutation-result-v1", "originalPositive": True,
        "copiedPositive": True, "originalFilesUnchanged": unchanged, "selectedInputManifest": before,
        "verifierSha256": args.verifier_sha256, "prepareCasesSha256": args.prepare_cases_sha256, "mutations": results,
        "paidCalls": 0, "networkCalls": 0, "passed": unchanged and all(item["passed"] for item in results)}
    write_json(scratch / "mutation-results.json", result)
    print(json.dumps({"passed": result["passed"], "mutations": len(results), "output": str(scratch / "mutation-results.json")}))
    return 0 if result["passed"] else 1


class PureMutationHelperTests(unittest.TestCase):
    @unittest.skipUnless(sys.platform == "win32", "Windows path boundary")
    def test_closed_snapshot_reads_and_copies_real_paths_beyond_max_path(self):
        # Only a newly owned temporary subtree under ignored artifacts is used.
        # TemporaryDirectory receives the extended root so cleanup also works.
        with tempfile.TemporaryDirectory(prefix="verifier-mutations-selftest-",
                dir=str(extended_windows_path(OUTPUT_PARENT))) as temporary:
            root = pathlib.Path(temporary)
            relative = "runtime/" + "long-directory-" + "a" * 90 + "/" + "trace-" + "b" * 155 + ".json"
            source = root / "source" / relative
            source.parent.mkdir(parents=True)
            write_json(source, {"literal": "exact 😀\r\n", "safeFixture": True})
            self.assertGreater(len(str(source)), 260)
            no_reparse(source, root)
            before = snapshot({relative: source})
            destination = root / "copied"
            copy_selected({relative: source}, destination)
            self.assertEqual(before, snapshot({relative: destination / relative}))
            self.assertEqual({"literal": "exact 😀\r\n", "safeFixture": True}, read_json(destination / relative))
            self.assertEqual(source, extended_windows_path(source))

    def test_probe_rebind_keeps_non_utf8_console_prefix_and_exact_single_link(self):
        prefix = b"\x82host operation=llm.call measurements="
        original = prefix + b'{"request_sha256":"old","request_characters":12}\r\n'
        rewritten = rewrite_probe_bytes(original, "old", "f" * 64, 19)
        self.assertTrue(rewritten.startswith(prefix) and rewritten.endswith(b"\r\n"))
        self.assertEqual(19, json.loads(rewritten.split(b"measurements=", 1)[1])["request_characters"])

    def test_probe_rebind_refuses_zero_or_multiple_links(self):
        line = b'operation=llm.call measurements={"request_sha256":"old"}\n'
        for value in (b"", line + line):
            with self.assertRaisesRegex(ValueError, "one_exact_match"):
                rewrite_probe_bytes(value, "old", "new", 1)

    def test_transport_reencoding_preserves_documentary_unicode_and_crlf(self):
        value = {"messages": [{"role": "user", "content": "  𝛼 🧪\r\n½ litre  "}]}
        self.assertEqual(value, json.loads(json_bytes(value)))

    def test_native_edit_changes_only_the_selected_reference(self):
        original = {"nativeObservation": {"reference": {"lineId": "original", "physicalPage": 40}}, "content": "literal\r\n"}
        changed = copy.deepcopy(original)
        changed["nativeObservation"]["reference"]["lineId"] = "native_line_" + "0" * 64
        self.assertEqual(original["content"], changed["content"])
        self.assertEqual(40, changed["nativeObservation"]["reference"]["physicalPage"])
        self.assertEqual("original", original["nativeObservation"]["reference"]["lineId"])

    def test_mutation_registry_has_seven_distinct_targets(self):
        self.assertEqual(7, len(MUTATIONS))
        self.assertEqual(7, len({name for name, _ in MUTATIONS}))

    def test_source_has_no_network_process_or_database_import(self):
        tree = ast.parse(pathlib.Path(__file__).read_text(encoding="utf-8-sig"))
        imported = {node.module.split(".")[0] for node in ast.walk(tree) if isinstance(node, ast.ImportFrom) and node.module}
        imported.update(alias.name.split(".")[0] for node in ast.walk(tree) if isinstance(node, ast.Import) for alias in node.names)
        self.assertFalse(imported & {"socket", "subprocess", "requests", "urllib", "psycopg", "sqlite3"})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime", type=pathlib.Path)
    parser.add_argument("--cases", type=pathlib.Path)
    parser.add_argument("--capture", type=pathlib.Path)
    parser.add_argument("--scratch", type=pathlib.Path)
    parser.add_argument("--verifier-sha256")
    parser.add_argument("--prepare-cases-sha256")
    parser.add_argument("--execute", action="store_true")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        require(not args.execute, "self_test_and_actual_execution_are_distinct")
        suite = unittest.defaultTestLoader.loadTestsFromTestCase(PureMutationHelperTests)
        return 0 if unittest.TextTestRunner(verbosity=2).run(suite).wasSuccessful() else 1
    if not args.execute:
        print(json.dumps({"state": "NOT_EXECUTED", "mutations": [name for name, _ in MUTATIONS], "networkCalls": 0}))
        return 0
    require(all((args.runtime, args.cases, args.capture, args.scratch)), "explicit_positive_inputs_and_new_scratch_required")
    return execute(args)


if __name__ == "__main__":
    raise SystemExit(main())
