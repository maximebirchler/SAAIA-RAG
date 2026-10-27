"""Expected refusal checks for the actual API/worker/SQL/socket boundary."""
import argparse
import json
import pathlib
from prepare_cases import SOURCE27_SQL_SHA256
from verify_wire import require, sha, read_call_probes, verify_projection, utf16_length, extended_windows_path


EXPECTED = {"source-hash-forged": ("evidence_revalidation_failed", 0),
            "invented-citation": ("advanced_writer_evidence_id_invalid", 11),
            "output-limit": ("advanced_llm_output_limit", 2)}


def verify(root, cases_path, capture_path):
    root = extended_windows_path(root)
    source = capture_path.read_bytes()
    require(sha(source) == SOURCE27_SQL_SHA256, "source_capture_sha_differs")
    case_data = json.loads(cases_path.read_text(encoding="utf-8-sig"))
    require(case_data.get("sourceCaptureSha256") == sha(source), "negative_cases_source_capture_binding_differs")
    cases = case_data["cases"]
    require(isinstance(cases, list) and len(cases) > 0, "negative_cases_empty_or_invalid")
    require(len({case["caseId"] for case in cases}) == len(cases), "negative_case_ids_not_unique")
    plan = json.loads((root.parent / "plan.json").read_text(encoding="utf-8-sig"))
    require(plan.get("protocol") in ("chat-completions", "responses"), "negative_protocol_not_supported")
    worker = json.loads((root / "advanced-worker.json").read_text(encoding="utf-8-sig"))
    owner = json.loads((root / "environment.json").read_text(encoding="utf-8-sig"))
    replay = json.loads((root / "corpus-replay.json").read_text(encoding="utf-8-sig"))
    require(replay["sourceCaptureSha256"] == SOURCE27_SQL_SHA256 and replay["canonicalRowsSeeded"] is True
            and replay["documentAndRevisionIdsMapped"] is False, "negative_replayed_corpus_binding_differs")
    variant = plan["scenarioVariant"]
    require(variant in EXPECTED and worker["wireQualification"] is True
            and worker["nativeResearchApiProtocol"] == plan["protocol"]
            and owner["backendAssemblySha256"].lower() == plan["backendAssemblySha256"], "negative_scenario_or_runtime_binding_differs")
    error, posts_per_case = EXPECTED[variant]
    route = "/v1/responses" if plan["protocol"] == "responses" else "/v1/chat/completions"
    traces = [json.loads(path.read_text(encoding="utf-8-sig")) for path in (root / "wire-application-traces").glob("*.json")]
    probes = read_call_probes(root / "advanced-backend.stdout.log")
    requests = []
    http_files = sorted((root / "wire-captures").glob("*.http.json"))
    require(len(http_files) == len(traces) == len(probes) == posts_per_case * len(cases), "negative_http_trace_probe_counts_differ")
    require(len({probe["call_id"] for probe in probes}) == len(probes), "negative_call_ids_not_unique")
    for path in http_files:
        meta = json.loads(path.read_text(encoding="utf-8"))
        require(meta["path"] == route and meta["method"] == "POST" and meta["loopbackPeer"] == "127.0.0.1"
                and meta["authorizationPresent"] is False, "negative_wire_route_peer_or_credentials_invalid")
        require(pathlib.Path(meta["bodyFile"]).name == meta["bodyFile"], "negative_body_path_invalid")
        raw = (path.parent / meta["bodyFile"]).read_bytes()
        require(sha(raw) == meta["bodySha256"] and len(raw) == meta["bodyBytes"], "negative_wire_hash_or_size_differs")
        body = json.loads(raw.decode("utf-8", "strict"))
        require(body.get("stream") is False and body["model"] == "saaia-wire-fixture", "negative_wire_model_or_stream_differs")
        if route.endswith("/responses"):
            require(body.get("store") is False and "input" in body and "messages" not in body
                    and "max_output_tokens" in body and "max_tokens" not in body and "response_format" not in body,
                    "negative_responses_shape_invalid")
        else:
            require("messages" in body and "input" not in body and "store" not in body
                    and "max_output_tokens" not in body, "negative_chat_shape_invalid")
        own_trace = [trace for trace in traces if trace["requestSha256"].lower() == meta["bodySha256"]]
        own_probe = [probe for probe in probes if probe.get("request_sha256", "").lower() == meta["bodySha256"]]
        require(len(own_trace) == len(own_probe) == 1, "negative_request_not_bound_to_one_application_call")
        trace, probe = own_trace[0], own_probe[0]
        require(trace["requestJson"].encode("utf-8") == raw and trace["jobId"] == probe["job_id"]
                and trace["role"] == probe["role"] and probe["request_characters"] == utf16_length(raw.decode("utf-8")),
                "negative_application_wire_bytes_or_call_identity_differ")
        require(probe["wire_protocol"] == plan["protocol"], "negative_probe_wire_protocol_differs")
        response = json.loads(path.with_name(path.name.replace(".http.json", ".response.json")).read_text(encoding="utf-8"))
        require(response["caseId"] in {case["caseId"] for case in cases}
                and json.loads(trace["responseEnvelopeJson"]) == response["envelope"], "negative_case_or_actual_response_changed")
        for message in body.get("messages", body.get("input", [])):
            if message.get("role") == "user":
                verify_projection(json.loads(message["content"]))
        requests.append((response, trace, body))
    publication_rows = {row["docPath"]: row for row in json.loads(source)["documents"]}
    expected_canonical = {case["revisionId"]: publication_rows[case["docPath"]]["binaryArtifacts"][0]["contentHash"] for case in cases}
    summaries = []
    for case in cases:
        case_dir = root / case["caseId"]
        api = json.loads((case_dir / "api-final.json").read_text(encoding="utf-8"))
        sql = json.loads((case_dir / "sql-job.json").read_text(encoding="utf-8"))
        require(api["jobId"] == sql["jobId"] and api["status"] == sql["status"] == "failed" and api["lastErrorCode"] == sql["lastErrorCode"] == error
                and api["attemptCount"] == sql["attemptCount"] == 1 and api.get("result") is None and sql.get("result") is None,
                "expected_negative_job_refusal_not_persisted_exactly")
        require(sql["corpusDigest"] == replay["corpusDigest"]
                and {item["revisionId"]: item["sha256"] for item in sql["canonicalHashes"]} == expected_canonical,
                "negative_case_mutated_corpus_or_canonical_publication")
        own = [(response, trace, body) for response, trace, body in requests if response["caseId"] == case["caseId"]]
        require(len(own) == posts_per_case and all(trace["jobId"] == api["jobId"] for _, trace, _ in own),
                "negative_case_posts_or_job_binding_differ")
        if variant == "source-hash-forged":
            require(sql["handoff"]["researchState"]["evidenceReferences"][0]["sourceHash"] == "0" * 64
                    and sql["events"] == [], "wrong_source_was_not_rejected_before_tools_or_provider")
        elif variant == "output-limit":
            require(len(sql["events"]) == 1 and sql["events"][0]["request"]["operation"] == "search_corpus"
                    and sql["events"][0]["status"] == "succeeded", "output_limit_executed_unexpected_tool_or_repair")
            last, _, _ = own[-1]
            terminal = json.loads(last["reply"]["text"])
            require(terminal["outcome"] == "answered" and terminal["claims"], "output_limit_fixture_must_contain_parseable_answer")
            if route.endswith("/responses"):
                require(last["envelope"]["status"] == "incomplete"
                        and last["envelope"]["incomplete_details"]["reason"] == "max_output_tokens", "responses_output_limit_marker_missing")
            else:
                require(last["envelope"]["choices"][0]["finish_reason"] == "length", "chat_output_limit_marker_missing")
        else:
            terminal = [json.loads(response["reply"]["text"]) for response, _, _ in own
                        if response["reply"]["kind"] == "answer" and response["turn"] is not None]
            require(len(terminal) == 2 and all(item["claims"][0]["evidenceIds"] == ["wire-invented-evidence"] for item in terminal),
                    "invented_citation_not_kept_invalid_through_repair")
            require(not any(item.get("evidenceId") == "wire-invented-evidence" for event in sql["events"] for item in event["evidence"]),
                    "invented_citation_accidentally_exists_in_real_evidence")
        summaries.append({"caseId": case["caseId"], "providerPosts": len(own), "expectedError": error,
                          "persistedAnswerAbsent": True, "expectedRefusalObserved": True, "passed": True})
    return {"schemaVersion": "saaia-model-wire-negative-qualification-v1", "variant": variant, "cases": summaries,
        "backendEnvironment": "Development", "unsignedOwnedFixtureConfiguration": True,
        "canonicalRowsReplayedFromSource27": True, "newIngestionExecuted": False,
        "semanticAnswerApproved": False, "paidCalls": 0, "productStatus": "TESTE_NON_APPROUVE", "passed": True}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime", type=pathlib.Path, required=True)
    parser.add_argument("--cases", type=pathlib.Path, required=True)
    parser.add_argument("--capture", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    result = verify(args.runtime, args.cases, args.capture)
    with args.output.open("x", encoding="utf-8") as handle:
        json.dump(result, handle, ensure_ascii=False, indent=2)
    print(json.dumps({"passed": True, "cases": len(result["cases"]), "variant": result["variant"]}))
