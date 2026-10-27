"""Read-only inspection of actual local preflight envelopes before paid execution."""
import argparse
import hashlib
import json
import pathlib
import sys
from decimal import Decimal, ROUND_HALF_UP

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "validation-wire"))
from verify_wire import extended_windows_path, read_call_probes, require, utf16_length, verify_projection


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def inspect(profile_path, runtime):
    profile_path = extended_windows_path(profile_path)
    profile = json.loads(profile_path.read_bytes())
    require(profile["schemaVersion"] == "saaia-local-openai-planning-campaign.v1", "planning_profile_required")
    runtime = extended_windows_path(runtime)
    submission = json.loads((runtime / "runtime/planning-submission.private.json").read_bytes())
    terminal = json.loads((runtime / "runtime/planning-terminal.private.json").read_bytes())
    sql = json.loads((runtime / "runtime/planning-sql-job.private.json").read_bytes())
    load = submission["handoff"]["load"]
    case = profile["case"]
    require(submission["handoff"]["transferStage"] == "before_retrieval"
            and submission["handoff"]["requestText"] == case["question"]
            and submission["handoff"]["researchState"]["evidenceReferences"] == []
            and load["answerUnitCount"] == load["atomicEvidenceCount"] == len(case["rowLabels"]) * 4
            and load["rowLabels"] == case["rowLabels"] and load["columns"] == case["columns"]
            and load["structuredLayout"] is True and load["selectionPolicy"] == "distinct_structured_layout",
            "planning_handoff_grid_or_initial_evidence_invalid")
    require(terminal["jobId"] == sql["jobId"] and terminal["status"] == sql["status"] == "failed"
            and terminal.get("result") is None and sql.get("result") is None,
            "preflight_must_stop_without_a_model_answer")
    config = json.loads((runtime / "runtime/paid-worker/deployment.config.json").read_bytes())["AdvancedAnalysis"]
    require(config["LlmBaseUrl"] == f'http://127.0.0.1:{profile["execution"]["preflightProviderPort"]}/v1'
            and config["LlmApiKeyRef"] is None and config["LlmLocation"] == "internal"
            and config["ReasoningEffort"] == "low"
            and config["NativeObservationReadEnabled"] is True and config["NativeResearchToolsEnabled"] is True
            and config["NativeResearchApiProtocol"] == "responses" and config["NativeResearchTopology"] == "agent",
            "preflight_must_use_uncredentialed_loopback_and_real_native_options")
    for key, source in [("MaximumEvidencePromptCharacters", "maximumEvidencePromptCharacters"),
                        ("MaximumEvidenceCharactersTotal", "maximumEvidenceCharactersTotal"),
                        ("MaximumEvidenceCharactersPerItem", "maximumEvidenceCharactersPerItem"),
                        ("NativeResearchMaximumHistoryCharacters", "maximumHistoryCharacters"),
                        ("ExternalMaximumCallsPerJob", "maximumCallsPerJob"), ("MaximumToolCalls", "maximumToolCalls"),
                        ("WriterMaxTokens", "writerMaxTokens")]:
        require(config[key] == profile["execution"][source], "preflight_config_limit_differs_from_profile")
    traces = [json.loads(p.read_bytes()) for p in (runtime / "runtime/private-traces").glob("*.json")]
    probes = read_call_probes(runtime / "runtime/paid-backend.stdout.log")
    files = sorted((runtime / "runtime/planning-wire-captures").glob("*.http.json"))
    require(len(files) == len(traces) == len(probes) == 2, "planner_and_first_writer_exactly_required")
    summaries = []
    writer_tools = False
    price = profile["pricing"]
    for metadata in files:
        meta = json.loads(metadata.read_bytes())
        require(meta["path"] == "/v1/responses" and meta["loopbackPeer"] == "127.0.0.1"
                and meta["authorizationPresent"] is False, "preflight_route_peer_or_credentials_invalid")
        raw = (metadata.parent / meta["bodyFile"]).read_bytes()
        body = json.loads(raw)
        request_hash = hashlib.sha256(raw).hexdigest()
        require(request_hash == meta["bodySha256"] and len(raw) == meta["bodyBytes"], "preflight_wire_bytes_changed")
        own_traces = [trace for trace in traces if trace["requestSha256"].lower() == request_hash]
        own_probes = [probe for probe in probes if probe.get("request_sha256", "").lower() == request_hash]
        require(len(own_traces) == len(own_probes) == 1, "preflight_not_bound_to_one_trace_and_call")
        trace, probe = own_traces[0], own_probes[0]
        require(trace["requestJson"].encode("utf-8") == raw and str(trace["jobId"]) == terminal["jobId"]
                and probe["job_id"] == terminal["jobId"] and probe["request_characters"] == utf16_length(raw.decode("utf-8"))
                and probe["wire_protocol"] == "responses", "preflight_application_wire_identity_changed")
        require(probe["diagnostic_dropped_measurements"] == probe["diagnostic_truncated_values"] == 0,
                "preflight_diagnostic_measurements_lost")
        require(body["model"] == "gpt-5.6-luna" and body.get("store") is False and body.get("stream") is False
                and "input" in body and "messages" not in body and "reasoning" not in body,
                "preflight_provider_protocol_or_reasoning_changed")
        users = [json.loads(item["content"]) for item in body["input"] if item.get("role") == "user"]
        require(len(users) == 1, "preflight_current_user_payload_not_unique")
        verify_projection(users[0])
        tools = {item.get("name") for item in body.get("tools", [])}
        if trace["role"].startswith("writer"):
            actual_load = users[0]["load"]
            require(actual_load["answerUnitCount"] == actual_load["atomicEvidenceCount"] == case["answerUnitCount"]
                    and actual_load["rowCount"] == len(case["rowLabels"]) and actual_load["columnCount"] == 4
                    and actual_load["rowLabels"] == case["rowLabels"] and actual_load["columns"] == case["columns"]
                    and actual_load["structuredLayout"] is True and actual_load["selectionPolicy"] == "distinct_structured_layout",
                    "actual_writer_grid_contract_changed_before_provider")
            require({"search_corpus", "read_source", "find_source_text", "read_evidence"} <= tools,
                    "planning_writer_native_tools_missing")
            reader = next(item for item in body["tools"] if item.get("name") == "read_source")
            require({"nativeObservationView", "nativeObservationOffset", "nativeObservationLimit",
                     "nativeObservationBlockId", "nativeObservationRawSha256"} <= set(reader["parameters"]["properties"]),
                    "planning_writer_native_raw_schema_missing")
            writer_tools = True
        # The product reserves all unknown uncached input at cache-write rate
        # when that rate is higher. Match that observed guard, not chars/4.
        input_upper_rate = max(Decimal(str(price["inputUsdPerMillion"])), Decimal(str(price["cacheWriteUsdPerMillion"])))
        reserved_input = Decimal(len(raw) + 256) * input_upper_rate
        reserved_output = Decimal(body["max_output_tokens"]) * Decimal(str(price["outputUsdPerMillion"]))
        upper_reservation = ((reserved_input + reserved_output) / Decimal(1000000)).quantize(
            Decimal("0.00000001"), rounding=ROUND_HALF_UP)
        summaries.append({"role": trace["role"], "requestSha256": request_hash, "utf8Bytes": len(raw),
                          "utf16Characters": utf16_length(raw.decode("utf-8")), "maximumOutputTokens": body["max_output_tokens"],
                          "conservativeInputTokenReservation": len(raw) + 256,
                          "conservativeUpperReservationUsd": str(upper_reservation), "toolNames": sorted(tools)})
    require(writer_tools, "planning_writer_envelope_not_observed")
    require(sum(Decimal(item["conservativeUpperReservationUsd"]) for item in summaries)
            + Decimal(str(profile["budget"]["baselineCostUsd"])) <= Decimal("0.50"),
            "initial_envelopes_do_not_fit_cumulative_budget")
    return {"schemaVersion": "saaia-planning-envelope-preflight.v1", "profileSha256": digest(profile_path),
            "corpusManifestSha256": profile["corpus"]["manifestSha256"], "writerToolsObserved": True,
            "exactRequestParity": True, "externalProviderUsed": False, "secretUsed": False,
            "preflightPlannerQueryIsScripted": True, "preflightPlannerQuery": "recette",
            "paidPlannerQueriesPreselectedByHarness": False,
            "envelopes": summaries, "pricingMode": price["pricingMode"],
            "preflightProviderDifferences": ["Loopback customer-server/internal instead of external OpenAI HTTPS.",
                "reasoning.effort is emitted only for openai-dev; local wire omits it. Profile/config low must be confirmed in the first paid application capture.",
                "The local sink has no provider key and deliberately rejects the first Writer request after recording it."],
            "paidWireReasoningLowQualifiedByThisPreflight": False,
            "futureRequestsGuaranteedBelow272kTokens": False, "semanticPlanningApproved": False,
            "functionalFiveDayAccepted": False, "productStatus": "TESTE_NON_APPROUVE", "passed": True}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--profile", type=pathlib.Path, required=True)
    parser.add_argument("--runtime", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    result = inspect(args.profile, args.runtime)
    with extended_windows_path(args.output).open("x", encoding="utf-8") as handle:
        json.dump(result, handle, indent=2)
    print(json.dumps({"passed": True, "externalProviderUsed": False, "envelopes": len(result["envelopes"])}))
