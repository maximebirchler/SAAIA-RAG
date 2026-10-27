"""Independent captured-publication -> actual HTTP body -> stored-result checks.

Checks documentary strings after JSON decoding. JSON escaping is transport, not
source alteration. The separate body SHA concerns exact HTTP entity bytes.
"""
import argparse
import base64
import gzip
import hashlib
import json
import os
import pathlib
from prepare_cases import SOURCE27_SQL_SHA256, raw_lines


def require(condition, code):
    if not condition:
        raise ValueError(code)


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def utf16_slice(text, offset, length):
    return text.encode("utf-16-le")[offset * 2:(offset + length) * 2].decode("utf-16-le", "strict")


def utf16_length(text):
    return len(text.encode("utf-16-le")) // 2


def extended_windows_path(path):
    if os.name != "nt":
        return path
    absolute = str(path.resolve())
    if not absolute.startswith("\\\\?\\"):
        absolute = "\\\\?\\UNC\\" + absolute[2:] if absolute.startswith("\\\\") else "\\\\?\\" + absolute
    return pathlib.Path(absolute)


def read_call_probes(path):
    # Console prefixes may use the host's OEM code page. BackendDiagnostics
    # serializes its content-free measurement object with System.Text.Json's
    # default ASCII escaping. Parse that closed ASCII JSON subrecord only;
    # preserve the original log bytes and never replace undecodable characters.
    result = []
    for line in path.read_bytes().splitlines():
        if b"operation=llm.call " in line and b"measurements=" in line:
            result.append(json.loads(line.split(b"measurements=", 1)[1].decode("ascii", "strict")))
    return result


def prompts(body):
    for message in body.get("messages", body.get("input", [])):
        if message.get("role") == "user" and isinstance(message.get("content"), str):
            try:
                value = json.loads(message["content"])
            except ValueError:
                continue
            if isinstance(value, dict) and isinstance(value.get("evidence"), list):
                yield value


def verify_projection(payload):
    audit = payload.get("evidenceProjection")
    require(isinstance(audit, dict), "documentary_projection_audit_missing")
    collection = "evidence" if isinstance(payload.get("evidence"), list) else (
        "observations" if isinstance(payload.get("observations"), list) else None)
    if collection is None:
        require(audit.get("status") == "not_applicable" and "canonicalProjection" not in audit
                and "currentCall" not in audit, "nondocumentary_call_fabricated_projection_counts")
        return
    require(audit.get("status") == "observed", "documentary_projection_not_observed")
    canonical, current = audit["canonicalProjection"], audit["currentCall"]
    for item in (canonical, current):
        require(all(type(value) is int and value >= 0 for key, value in item.items() if key != "collection"),
                "documentary_projection_count_type_or_range_invalid")
    require(current["collection"] == collection and current["visibleCount"] == len(payload[collection]),
            "documentary_projection_visible_collection_count_differs")
    require(canonical["inputCount"] == canonical["projectedCount"] + canonical["omittedByPromptBudgetCount"]
            and current["visibleCount"] == current["fromCanonicalProjectionCount"] + current["outsideCanonicalProjectionCount"]
            and canonical["projectedCount"] == current["fromCanonicalProjectionCount"] + current["canonicalProjectedNotSelectedCount"],
            "documentary_projection_accounting_incoherent")
    require(current["truncatedContentCount"] == sum(item.get("contentTruncated") is True for item in payload[collection])
            and current["supportingWindowsOmittedCount"] == sum(item.get("supportingContentWindowsOmittedCount", 0)
                                                              for item in payload[collection]),
            "documentary_projection_truncation_or_window_omissions_differ")
    require(isinstance(audit.get("instruction"), str) and "absence" in audit["instruction"].lower(),
            "documentary_projection_absence_warning_missing")


def resolve_native_inventory_reference(reference, prompt, case):
    require(isinstance(reference, dict) and set(reference) == {
        "sourceKey", "docId", "revisionId", "sourceSha256", "physicalPage", "offset", "inventoryReference"},
        "native_inventory_reference_shape_invalid")
    require(reference["inventoryReference"] ==
            "current_user.alternativeNativeReadings.inventories[exact_source_page_offset].inventory"
            and reference["docId"] == case["docId"] and reference["revisionId"] == case["revisionId"]
            and reference["sourceSha256"] == case["sourceSha256"]
            and reference["physicalPage"] == case["physicalPage"], "native_inventory_reference_identity_changed")
    candidates = [item["inventory"] for item in (prompt.get("alternativeNativeReadings") or {}).get("inventories", [])
                  if item.get("sourceKey") == reference["sourceKey"]
                  and item.get("physicalPage") == reference["physicalPage"]
                  and isinstance(item.get("inventory"), dict)
                  and item["inventory"].get("offset") == reference["offset"]]
    require(len(candidates) == 1, "native_inventory_reference_current_target_not_unique")
    inventory = candidates[0]
    require(set(inventory) == {"availability", "reason", "observationCount", "offset", "nextOffset", "observations"}
            and inventory["availability"] == "observed" and inventory["reason"] is None
            and isinstance(inventory["observations"], list) and len(inventory["observations"]) > 0,
            "native_inventory_reference_target_not_complete")
    require(all(descriptor["docId"] == reference["docId"] and descriptor["revisionId"] == reference["revisionId"]
                and descriptor["sourceSha256"] == reference["sourceSha256"]
                and descriptor["physicalPage"] == reference["physicalPage"] for descriptor in inventory["observations"]),
            "native_inventory_reference_descriptor_identity_changed")
    return inventory


def object_occurrences(value, expected):
    if isinstance(value, dict):
        return int(value == expected) + sum(object_occurrences(item, expected) for item in value.values())
    if isinstance(value, list):
        return sum(object_occurrences(item, expected) for item in value)
    return 0


def verify(root, cases_path, capture_path):
    root = extended_windows_path(root)
    capture_bytes = capture_path.read_bytes()
    require(sha(capture_bytes) == SOURCE27_SQL_SHA256, "source_capture_sha_differs")
    rows = {row["docPath"]: row for row in json.loads(capture_bytes)["documents"]}
    replay = json.loads((root / "corpus-replay.json").read_text(encoding="utf-8-sig"))
    require(replay["sourceCaptureSha256"] == sha(capture_bytes) and replay["canonicalRowsSeeded"] is True
            and replay["documentAndRevisionIdsMapped"] is False, "corpus_replay_receipt_binding_differs")
    advanced = json.loads((root / "advanced-worker.json").read_text(encoding="utf-8-sig"))
    plan = json.loads((root.parent / "plan.json").read_text(encoding="utf-8-sig"))
    owner = json.loads((root / "environment.json").read_text(encoding="utf-8-sig"))
    require(owner["backendAssemblySha256"].lower() == plan["backendAssemblySha256"], "planned_and_started_backend_assembly_differ")
    protocol = plan["protocol"]
    variant = plan.get("scenarioVariant", "positive")
    require(variant in ("positive", "native-raw-sha-correction"), "positive_or_corrected_native_scenario_required")
    require(protocol in ("chat-completions", "responses") and advanced["wireQualification"] is True
            and advanced["nativeResearchApiProtocol"] == protocol, "planned_and_actual_worker_protocol_differ")
    expected_route = "/v1/responses" if protocol == "responses" else "/v1/chat/completions"
    case_data = json.loads(cases_path.read_text(encoding="utf-8-sig"))
    require(case_data["sourceCaptureSha256"] == sha(capture_bytes), "case_source_binding_differs")
    all_http = sorted((root / "wire-captures").glob("request-*.http.json"))
    require(all_http, "no_actual_http_capture")
    traces = [json.loads(path.read_text(encoding="utf-8-sig")) for path in (root / "wire-application-traces").glob("*.json")]
    require(len(traces) == len(all_http), "actual_http_and_application_trace_count_differ")
    probes = read_call_probes(root / "advanced-backend.stdout.log")
    require(len(probes) == len(all_http), "actual_http_and_llm_call_probe_count_differ")
    require(len({probe["call_id"] for probe in probes}) == len(probes), "application_call_id_not_unique")
    requests = []
    for path in all_http:
        meta = json.loads(path.read_text(encoding="utf-8"))
        require(meta["loopbackPeer"] == "127.0.0.1" and meta["authorizationPresent"] is False,
                "wire_peer_or_credentials_invalid")
        require(meta["path"] in ("/v1/chat/completions", "/v1/responses") and meta["method"] == "POST", "wire_route_invalid")
        require(meta["path"] == expected_route, "request_did_not_use_planned_wire_protocol")
        body_file = path.parent / meta["bodyFile"]
        require(body_file.parent == path.parent and body_file.name == meta["bodyFile"], "wire_body_path_invalid")
        raw = body_file.read_bytes()
        require(len(raw) == meta["bodyBytes"] and sha(raw) == meta["bodySha256"], "actual_wire_body_hash_or_size_differs")
        body = json.loads(raw.decode("utf-8", "strict"))
        user_payloads = []
        for message in body.get("messages", body.get("input", [])):
            if message.get("role") == "user" and isinstance(message.get("content"), str):
                user_payloads.append(json.loads(message["content"]))
        require(len(user_payloads) == 1, "one_actual_current_user_payload_required")
        verify_projection(user_payloads[0])
        require(body.get("stream") is False, "nonstreaming_wire_mode_required")
        if protocol == "responses":
            require(isinstance(body.get("input"), list) and "messages" not in body and body.get("store") is False
                    and "max_output_tokens" in body and "max_tokens" not in body and "response_format" not in body,
                    "responses_request_shape_invalid")
        else:
            require(isinstance(body.get("messages"), list) and "input" not in body and "store" not in body
                    and "max_output_tokens" not in body, "chat_request_shape_invalid")
        matched_traces = [trace for trace in traces if trace["requestSha256"].lower() == meta["bodySha256"]]
        matched_probes = [probe for probe in probes if probe.get("request_sha256", "").lower() == meta["bodySha256"]]
        require(len(matched_traces) == len(matched_probes) == 1, "wire_bytes_not_bound_to_one_application_call")
        application, probe = matched_traces[0], matched_probes[0]
        require(application["requestJson"].encode("utf-8") == raw, "application_serialized_request_and_received_bytes_differ")
        require(str(application["jobId"]) == str(probe["job_id"])
                and application["role"] == probe["role"] and probe["request_characters"] == utf16_length(raw.decode("utf-8")),
                "application_call_job_role_or_size_binding_differs")
        require(probe["wire_protocol"] == ("responses" if meta["path"].endswith("/responses") else "chat-completions"),
                "application_probe_wire_protocol_differs")
        response_file = path.with_name(path.name.replace(".http.json", ".response.json"))
        require(response_file.is_file(), "fixture_response_or_script_failed")
        response = json.loads(response_file.read_text(encoding="utf-8"))
        require(response["caseId"] in {case["caseId"] for case in case_data["cases"]}, "wire_request_has_unprepared_case")
        require(json.loads(application["responseEnvelopeJson"]) == response["envelope"], "fixture_and_application_response_envelopes_differ")
        meta["applicationJobId"] = application["jobId"]
        meta["applicationCallId"] = probe["call_id"]
        requests.append((meta, body, response))
    summaries, digests = [], set()
    for case in case_data["cases"]:
        row = rows[case["docPath"]]
        artifact, = row["binaryArtifacts"]
        bundle = json.loads(gzip.decompress(base64.b64decode(artifact["payloadBase64"])))
        doc = bundle["document"]
        chunks = {chunk["retrievalChunkId"]: chunk for chunk in row["chunks"]}
        blocks = {block["blockId"]: (page, block) for page in doc["pages"] for block in page["blocks"]}
        case_dir = root / case["caseId"]
        api = json.loads((case_dir / "api-final.json").read_text(encoding="utf-8"))
        sql = json.loads((case_dir / "sql-job.json").read_text(encoding="utf-8"))
        require(api["jobId"] == sql["jobId"] and api["status"] == sql["status"] == "succeeded" and api["attemptCount"] == sql["attemptCount"] == 1,
                "real_job_not_successful_once")
        require(api["result"] == sql["result"], "api_and_persisted_result_differ")
        require(api["result"]["outcome"] == "answered", "terminal_controlled_answer_missing")
        digests.add(sql["corpusDigest"])
        expected_hashes = {item["revisionId"]: rows[item["docPath"]]["binaryArtifacts"][0]["contentHash"]
                           for item in case_data["cases"]}
        actual_hashes = {item["revisionId"]: item["sha256"] for item in sql["canonicalHashes"]}
        require(actual_hashes == expected_hashes and len(sql["canonicalHashes"]) == len(expected_hashes),
                "canonical_publications_changed_during_wire_run")
        refs = {item["evidenceId"]: item for item in sql["handoff"]["researchState"]["evidenceReferences"]}
        refused_reads = [event for event in sql["events"] if event["status"] == "failed"]
        require(len(refused_reads) == 1, "exactly_one_bounded_primary_read_refusal_expected")
        refused = refused_reads[0]
        require(refused["errorCode"] == "canonical_read_window_result_limit_exceeded"
                and refused["request"]["operation"] == "read_source" and refused["request"]["topK"] == 1
                and refused["request"]["pageStart"] == refused["request"]["pageEnd"] == case["physicalPage"]
                and refused["request"].get("nativeObservationView") is None and refused["evidence"] == [],
                "bounded_primary_read_refusal_wrong_reason_or_false_admission")
        light_reads = [event for event in sql["events"] if event["status"] == "succeeded"
                       and event["request"]["operation"] == "read_source"
                       and event["request"]["pageStart"] == event["request"]["pageEnd"] == case["lightPrimaryPage"]
                       and event["request"].get("nativeObservationView") is None]
        require(len(light_reads) == 1 and len(light_reads[0]["evidence"]) == 1
                and light_reads[0]["evidence"][0]["chunkId"] == case["lightPrimaryChunkId"],
                "bounded_light_primary_read_not_successful_with_exact_chunk")
        for event in sql["events"]:
            require(event is refused or (event["status"] == "succeeded" and event["errorCode"] is None), "unexpected_real_tool_failure")
            for item in event["evidence"]:
                require(item["docId"] == case["docId"] and item["revisionId"] == case["revisionId"]
                        and item["sourceHash"] == case["sourceSha256"] and item["docPath"] == case["docPath"],
                        "tool_returned_wrong_source_identity")
                refs[item["evidenceId"]] = item
        operations = [event["request"]["operation"] for event in sql["events"]]
        require("search_corpus" in operations and "read_source" in operations and "find_source_text" in operations,
                "real_search_read_find_not_all_executed")
        require(any(event["request"].get("nativeObservationView") == "inventory" for event in sql["events"])
                and sum(event["request"].get("nativeObservationView") == "raw" for event in sql["events"]) == 3,
                "real_native_inventory_and_three_raw_windows_required")
        own = [(meta, body, response) for meta, body, response in requests if response["caseId"] == case["caseId"]]
        require(len(own) == (11 if variant == "native-raw-sha-correction" else 10), "planned_tool_turns_and_synthesis_count_differs")
        observed_lines, primary_count, native_count, primary_windows = set(), 0, 0, 0
        validated_native = {}
        stable_source_key = None
        light_primary_seen = False
        bounded_refusal_feedback_seen = False
        inventory_reference_count = 0
        native_partial_cursor_recovery_count = 0
        rejected_raw_locator_feedback_count = 0
        calls = []
        for meta, body, response in own:
            require(str(meta["applicationJobId"]) == api["jobId"], "wire_request_bound_to_another_persisted_job")
            require(body["model"] == "saaia-wire-fixture" and not body.get("stream"), "wire_model_or_mode_differs")
            for prompt in prompts(body):
                for item in prompt["evidence"]:
                    if stable_source_key is None:
                        stable_source_key = item["sourceKey"]
                    require(item["sourceKey"] == stable_source_key, "source_key_changed_for_one_observed_revision")
                    reference = refs.get(item["evidenceId"])
                    require(reference is not None, "model_evidence_id_not_from_real_tools")
                    require(reference["docId"] == case["docId"] and reference["revisionId"] == case["revisionId"]
                            and reference["sourceHash"] == case["sourceSha256"] and reference["docPath"] == case["docPath"],
                            "model_reference_wrong_document_revision_source_or_path")
                    native = item.get("nativeObservation")
                    if native is not None:
                        line = native["reference"]
                        require(reference.get("nativeObservation") == line, "native_reference_changed_before_model")
                        page, block = blocks[line["blockId"]]
                        raw_text = block["text"]["raw"]
                        lines = raw_lines(raw_text)
                        ordinal = line["lineOrdinal"]
                        expected = lines[ordinal]
                        start = sum(len(value) for value in lines[:ordinal])
                        end = start + len(expected)
                        identity = [case["docId"], case["revisionId"], case["sourceSha256"], doc["manifestSha256"],
                            page["pageNumber"], block["blockId"], sha(raw_text.encode("utf-8")), ordinal,
                            start, end, sha(expected.encode("utf-8"))]
                        line_id = "native_line_" + sha(json.dumps(identity, separators=(",", ":")).encode("utf-8"))
                        require(item["content"] == expected and item["contentTruncated"] is False
                                and item["originalContentLength"] == utf16_length(expected), "native_literal_or_length_changed")
                        require(line["lineId"] == line_id and line["lineTextSha256"] == sha(expected.encode("utf-8"))
                                and line["unicodeScalarStart"] == start and line["unicodeScalarEndExclusive"] == end,
                                "native_line_identity_or_scalar_bounds_changed")
                        require(line["rawTextSha256"] == sha(raw_text.encode("utf-8"))
                                and line["physicalPage"] == page["pageNumber"], "native_source_or_page_changed")
                        require(item["physicalPageStart"] == item["physicalPageEnd"] == reference["pageStart"]
                                == reference["pageEnd"] == page["pageNumber"], "native_physical_pages_changed")
                        expected_signals = list(dict.fromkeys(page.get("qualityFlags", []) + block.get("qualityFlags", [])
                                                               + ["canonical_native_audit_literal_not_primary"]))
                        require(native["qualitySignals"] == expected_signals and native["role"] == "secondary_native_audit"
                                and native["semanticAccuracy"] == "not_verified" and native["sourceFidelityApproved"] is False,
                                "native_uncertainty_or_quality_lost")
                        require(item.get("sourceQuality", {}).get("semanticAccuracy") == "not_verified"
                                and item["sourceQuality"]["signals"] == expected_signals, "native_source_quality_lost")
                        views = [view for view in (prompt.get("alternativeNativeReadings") or {}).get("views", [])
                                 if view.get("sourceKey") == item["sourceKey"] and view.get("blockId") == line["blockId"]
                                 and any(value.get("evidenceId") == item["evidenceId"] for value in view.get("lines", []))]
                        require(len(views) == 1 and native["nextLineOffset"] == views[0]["nextOffset"]
                                and native["lineOffset"] == views[0]["offset"] and native["lineLimit"] == views[0]["limit"]
                                and native["viewLineCount"] == views[0]["rawLineCount"],
                                "native_evidence_and_visible_window_cursors_contradict")
                        observed_lines.add((line["blockId"], ordinal))
                        validated_native[item["evidenceId"]] = (line, expected)
                        native_count += 1
                    else:
                        chunk = chunks.get(reference.get("chunkId"))
                        require(chunk is not None, "primary_evidence_not_a_captured_chunk")
                        require(item["physicalPageStart"] == reference["pageStart"] == chunk["pageStart"]
                                and item["physicalPageEnd"] == reference["pageEnd"] == chunk["pageEnd"],
                                "primary_physical_pages_changed")
                        content = item["content"]
                        offset = item.get("contentOffset", 0)
                        require(item["originalContentLength"] == utf16_length(chunk["text"])
                                and content == utf16_slice(chunk["text"], offset, utf16_length(content)),
                                "primary_literal_or_original_length_changed")
                        truncated = offset > 0 or utf16_length(content) < utf16_length(chunk["text"])
                        require(item["contentTruncated"] == truncated, "primary_truncation_not_announced")
                        if offset > 0:
                            primary_windows += 1
                        for window in item.get("supportingContentWindows", []):
                            require(window["content"] == utf16_slice(chunk["text"], window["contentOffset"],
                                                                    utf16_length(window["content"])), "support_window_literal_changed")
                        metadata = chunk["metadata"]
                        quality = item.get("sourceQuality")
                        require(quality is not None and quality["semanticAccuracy"] == "not_verified"
                                and quality["textStatus"] == metadata.get("extractionTextStatus")
                                and quality["ocrCandidate"] == metadata.get("extractionOcrCandidate")
                                and quality["signals"] == metadata.get("extractionQualitySignals", []),
                                "primary_quality_metadata_changed")
                        primary_count += 1
                        if reference.get("chunkId") == case["lightPrimaryChunkId"]:
                            light_primary_seen = True
                for inventory in (prompt.get("alternativeNativeReadings") or {}).get("inventories", []):
                    for descriptor in inventory.get("inventory", {}).get("observations") or []:
                        page, block = blocks[descriptor["blockId"]]
                        provenance = block["provenance"]
                        require(descriptor["docId"] == case["docId"] and descriptor["revisionId"] == case["revisionId"]
                                and descriptor["sourceSha256"] == case["sourceSha256"]
                                and descriptor["manifestSha256"] == doc["manifestSha256"]
                                and descriptor["rawTextSha256"] == sha(block["text"]["raw"].encode("utf-8"))
                                and descriptor["physicalPage"] == page["pageNumber"]
                                and all(descriptor[name] == provenance[name] for name in ("engine", "engineVersion", "method", "stageId"))
                                and descriptor["blockType"] == block["blockType"]
                                and descriptor["rawLineCount"] == len(raw_lines(block["text"]["raw"]))
                                and descriptor["observedPolygon"] == block.get("polygon")
                                and descriptor["qualitySignals"] == list(dict.fromkeys(
                                    page.get("qualityFlags", []) + block.get("qualityFlags", []))),
                                "native_descriptor_provenance_changed")
            messages = body.get("messages", body.get("input", []))
            if calls:
                latest = calls[-1]
                if meta["path"].endswith("/responses"):
                    results = [message for message in messages if message.get("type") == "function_call_output"
                               and message.get("call_id") == latest["id"]]
                    originals = [message for message in messages if message.get("type") == "function_call"
                                 and message.get("call_id") == latest["id"]]
                    reasoning = [message for message in messages if message.get("type") == "reasoning"
                                 and message.get("id") == latest["reasoning"]["id"]]
                    require(len(results) == 1 and originals == [latest["original"]], "responses_tool_origin_or_unique_result_lost")
                    require(reasoning == [latest["reasoning"]], "responses_opaque_reasoning_lost")
                    result_content = results[0]["output"]
                else:
                    results = [message for message in messages if message.get("role") == "tool"
                               and message.get("tool_call_id") == latest["id"]]
                    originals = [call for message in messages if message.get("role") == "assistant"
                                 for call in message.get("tool_calls", []) if call.get("id") == latest["id"]]
                    require(len(results) == 1 and originals == [latest["original"]], "chat_tool_origin_or_unique_result_lost")
                    result_content = results[0]["content"]
                result_payload = json.loads(result_content)
                require(isinstance(result_payload, dict), "tool_result_must_remain_valid_json")
                arguments = json.loads(latest["arguments"])
                if latest["name"] == "read_source" and arguments.get("nativeObservationView") == "inventory":
                    current_prompt = next(prompts(body))
                    catalogue = resolve_native_inventory_reference(result_payload.get("nativeInventory"), current_prompt, case)
                    alternatives = (result_payload.get("alternativeNativeReadings") or {}).get("inventories", [])
                    require(result_payload["nativeInventory"]["sourceKey"] == arguments["sourceKey"] == stable_source_key,
                            "native_inventory_reference_not_bound_to_current_call_source")
                    require(len(alternatives) <= 1
                            and all(resolve_native_inventory_reference(item, current_prompt, case) == catalogue
                                    for item in alternatives),
                            "native_tool_inventory_copy_not_replaced_with_exact_current_reference")
                    page = next(page for page in doc["pages"] if page["pageNumber"] == case["physicalPage"])
                    native_blocks = [block for block in page["blocks"] if block["blockType"] in
                                     ("native_text_recovery", "native_layout_recovery", "native_table_recovery")]
                    require(catalogue["offset"] == arguments["nativeObservationOffset"] == case["nativeInventoryOffset"]
                            and catalogue["observationCount"] == len(native_blocks)
                            and len(catalogue["observations"]) == 1
                            and catalogue["observations"][0]["blockId"] == case["nativeBlockId"]
                            and catalogue["nextOffset"] == (catalogue["offset"] + 1
                                if catalogue["offset"] + 1 < len(native_blocks) else None),
                            "native_inventory_pagination_or_selected_descriptor_changed")
                    require(object_occurrences(current_prompt, catalogue) + object_occurrences(result_payload, catalogue) == 1,
                            "native_inventory_complete_surface_duplicated_or_missing")
                    inventory_reference_count += 1
                if latest["name"] == "read_source" and arguments.get("pageStart") == case["physicalPage"] \
                        and arguments.get("nativeObservationView") is None:
                    require(result_payload["status"] == "resource_limit_before_evidence_admission"
                            and result_payload["returnedEvidenceCount"] == 0 and result_payload["visibleEvidenceIds"] == []
                            and result_payload["resourceLimit"]["reasonCode"] == "canonical_read_window_result_limit_exceeded"
                            and result_payload["resourceLimit"]["stopsResearch"] is False,
                            "bounded_read_refusal_not_correctly_reported_to_model")
                    bounded_refusal_feedback_seen = True
                if latest["name"] == "read_source" and arguments.get("nativeObservationView") == "raw" \
                        and arguments.get("nativeObservationOffset") == 1 and arguments.get("nativeObservationLimit") == 2:
                    current_prompt = next(prompts(body))
                    views = [view for view in (current_prompt.get("alternativeNativeReadings") or {}).get("views", [])
                             if view.get("blockId") == case["nativeBlockId"] and view.get("sourceKey") == arguments["sourceKey"]
                             and view.get("offset") == arguments["nativeObservationOffset"]
                             and view.get("limit") == arguments["nativeObservationLimit"]]
                    require(len(views) == 1 and views[0]["status"] == "partial_model_budget"
                            and views[0]["returnedLineCount"] == len(views[0]["lines"]) == 1
                            and views[0]["nextOffset"] == views[0]["offset"] + views[0]["returnedLineCount"],
                            "partial_native_window_or_resume_cursor_not_explicit")
                    require(response["reply"]["kind"] == "tool" and response["reply"]["call"]["name"] == "read_source",
                            "fake_provider_did_not_request_observed_native_cursor")
                    recovery = json.loads(response["reply"]["call"]["arguments"])
                    require(recovery["sourceKey"] == arguments["sourceKey"]
                            and recovery["nativeObservationBlockId"] == case["nativeBlockId"]
                            and recovery["nativeObservationRawSha256"] == views[0]["rawTextSha256"]
                            and recovery["nativeObservationOffset"] == views[0]["nextOffset"]
                            and recovery["nativeObservationLimit"] == len(case["expectedNativeLines"]) - views[0]["nextOffset"],
                            "native_recovery_not_derived_from_current_observed_cursor")
                    native_partial_cursor_recovery_count += 1
                if latest["name"] == "read_source" and arguments.get("nativeObservationView") == "raw" \
                        and arguments.get("nativeObservationRawSha256") != case["nativeRawSha256"]:
                    require(variant == "native-raw-sha-correction"
                            and result_payload["status"] == "batch_rejected_before_execution"
                            and result_payload["visibleEvidenceIds"] == []
                            and list(prompts(body))[0]["evidence"] == latest["currentEvidence"],
                            "wrong_raw_locator_was_not_rejected_before_state_or_evidence_changed")
                    require(not any(event["request"].get("nativeObservationRawSha256") == arguments["nativeObservationRawSha256"]
                                    for event in sql["events"]), "wrong_raw_locator_reached_real_reader")
                    rejected_raw_locator_feedback_count += 1
            if response["reply"]["kind"] == "tool":
                native = meta["path"].endswith("/responses")
                calls.append({"id": response["reply"]["call"]["id"],
                    "name": response["reply"]["call"]["name"], "arguments": response["reply"]["call"]["arguments"],
                    "currentEvidence": list(prompts(body))[0]["evidence"],
                    "original": response["envelope"]["output"][1] if native
                        else response["envelope"]["choices"][0]["message"]["tool_calls"][0],
                    "reasoning": response["envelope"]["output"][0] if native else None})
        require(all((case["nativeBlockId"], index) in observed_lines for index in range(3)),
                "three_expected_raw_lines_not_all_received_by_model")
        require(primary_windows > 0, "real_nonprefix_read_evidence_window_not_received_by_model")
        require(light_primary_seen and bounded_refusal_feedback_seen, "successful_light_read_and_explicit_bounded_refusal_both_required")
        require(inventory_reference_count == 1, "one_exact_current_native_inventory_reference_required")
        require(native_partial_cursor_recovery_count == 1, "one_observed_native_partial_cursor_recovery_required")
        require(rejected_raw_locator_feedback_count == (1 if variant == "native-raw-sha-correction" else 0),
                "wrong_raw_locator_expected_rejection_count_differs")
        result = api["result"]
        require(len(result["claims"]) == 1 and len(result["evidence"]) == 1, "one_controlled_cited_result_required")
        claim, cited = result["claims"][0], result["evidence"][0]
        require(claim["evidenceIds"] == [cited["evidenceId"]] and cited["docId"] == case["docId"]
                and cited["revisionId"] == case["revisionId"] and cited["sourceHash"] == case["sourceSha256"]
                and cited["nativeObservation"]["blockId"] == case["nativeBlockId"], "terminal_citation_identity_changed")
        terminal = json.loads(own[-1][2]["reply"]["text"])
        require(own[-1][2]["reply"]["kind"] == "answer" and terminal["outcome"] == result["outcome"]
                and terminal["answerText"] == result["answerText"], "actual_provider_answer_changed_before_api_result")
        require(all(claim.get(key) == terminal["claims"][0][key] for key in ("claimId", "text", "evidenceIds")),
                "actual_provider_claim_changed_before_api_result")
        validated = validated_native.get(cited["evidenceId"])
        require(validated is not None and cited["nativeObservation"] == validated[0]
                and cited["pageStart"] == cited["pageEnd"] == case["physicalPage"]
                and cited["docPath"] == case["docPath"] and claim["text"] == validated[1].strip()
                and claim["text"] in result["answerText"], "terminal_citation_or_claim_not_equal_original_line")
        summaries.append({"caseId": case["caseId"], "wireRequests": len(own), "realToolEvents": len(sql["events"]),
            "primaryExcerptsCompared": primary_count, "nativeLineOccurrencesCompared": native_count,
            "nonzeroOffsetPrimaryWindowsCompared": primary_windows,
            "boundedPrimaryReadsRefusedWithFeedback": 1, "lightCanonicalPageReadsSucceeded": 1,
            "invalidRawLocatorsCorrectedBeforeReader": rejected_raw_locator_feedback_count,
            "completeNativeInventoriesWithExactToolReferences": inventory_reference_count,
            "partialNativeWindowsRecoveredFromObservedCursor": native_partial_cursor_recovery_count,
            "expectedDistinctNativeLines": 3, "actualBodyHashesVerified": True,
            "applicationRequestProbeAndExactTraceBytesEqual": True,
            "qualityAndProvenanceChecked": True, "sqlAndApiResultEqual": True, "passed": True})
    require(digests == {replay["corpusDigest"]}, "read_tools_mutated_replayed_corpus")
    require(len(requests) == sum(item["wireRequests"] for item in summaries), "wire_requests_outside_qualified_cases")
    return {"schemaVersion": "saaia-model-wire-qualification-v1", "cases": summaries,
        "fakeProvider": True, "realApiWorkerStoreToolsAndHttpSocket": True, "paidCalls": 0,
        "backendEnvironment": "Development", "unsignedOwnedFixtureConfiguration": True,
        "canonicalRowsReplayedFromSource27": True, "newIngestionExecuted": False,
        "teiAndQdrantDeterministicEmptyFixtures": True, "denseRetrievalQualityQualified": False,
        "realRetrievalScope": "Production gateway and SQL sparse/canonical paths; vector adapter is a fixture",
        "unseenTailRecoveryQualifiedByThisRun": False,
        "semanticModelQualityApproved": False, "productStatus": "TESTE_NON_APPROUVE", "passed": True}


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
    print(json.dumps({"passed": result["passed"], "cases": len(result["cases"])}))
