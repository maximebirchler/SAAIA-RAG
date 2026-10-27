import pathlib
import sys
import hashlib
import json
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / 'validation-wire'))
from planning_preflight import preflight_reply
from verify_planning_inputs import inspect
from verify_wire import utf16_length


class PlanningPreflightPolicyTests(unittest.TestCase):
    def prepare_inspection(self, root, kind="one-day-4"):
        days = ["lundi"] if kind == "one-day-4" else ["lundi", "mardi", "mercredi", "jeudi", "vendredi"]
        case = {"kind": kind, "question": "planning", "answerUnitCount": len(days) * 4, "rowLabels": days,
                "columns": ["petit-déjeuner", "déjeuner", "collation", "souper"],
                "selectionPolicy": "distinct_structured_layout" if kind == "one-day-4" else "structured_layout"}
        load = dict(case, atomicEvidenceCount=case["answerUnitCount"], rowCount=len(days), columnCount=4, structuredLayout=True)
        limits = {"maximumEvidencePromptCharacters": 65536, "maximumEvidenceCharactersTotal": 96000,
                  "maximumEvidenceCharactersPerItem": 14000, "maximumHistoryCharacters": 131072,
                  "maximumCallsPerJob": 64, "maximumToolCalls": 128, "writerMaxTokens": 8192,
                  "preflightProviderPort": 22504}
        profile = {"schemaVersion": "saaia-local-openai-planning-campaign.v1", "case": case,
                   "execution": limits, "corpus": {"manifestSha256": "a" * 64},
                   "budget": {"baselineCostUsd": 0.00678686},
                   "pricing": {"pricingMode": "conservative_long_context_upper_bound", "inputUsdPerMillion": 0.40,
                               "cacheWriteUsdPerMillion": 0.50, "outputUsdPerMillion": 1.80}}
        profile_path = root / "profile.json"
        profile_path.write_text(json.dumps(profile), encoding="utf-8")
        runtime = root / "owned"
        for name in ("runtime/private-traces", "runtime/planning-wire-captures", "runtime/paid-worker"):
            (runtime / name).mkdir(parents=True)
        handoff = {"transferStage": "before_retrieval", "requestText": "planning", "load": load,
                   "researchState": {"evidenceReferences": []}}
        (runtime / "runtime/planning-submission.private.json").write_text(json.dumps({"handoff": handoff}), encoding="utf-8")
        terminal = {"jobId": "job", "status": "failed", "result": None}
        for name in ("planning-terminal.private.json", "planning-sql-job.private.json"):
            (runtime / "runtime" / name).write_text(json.dumps(terminal), encoding="utf-8")
        config = {"LlmBaseUrl": "http://127.0.0.1:22504/v1", "LlmApiKeyRef": None, "LlmLocation": "internal",
                  "ReasoningEffort": "low", "NativeObservationReadEnabled": True, "NativeResearchToolsEnabled": True,
                  "NativeResearchApiProtocol": "responses", "NativeResearchTopology": "agent"}
        mapping = {"MaximumEvidencePromptCharacters": "maximumEvidencePromptCharacters", "MaximumEvidenceCharactersTotal": "maximumEvidenceCharactersTotal",
                   "MaximumEvidenceCharactersPerItem": "maximumEvidenceCharactersPerItem", "NativeResearchMaximumHistoryCharacters": "maximumHistoryCharacters",
                   "ExternalMaximumCallsPerJob": "maximumCallsPerJob", "MaximumToolCalls": "maximumToolCalls", "WriterMaxTokens": "writerMaxTokens"}
        config.update({key: limits[value] for key, value in mapping.items()})
        (runtime / "runtime/paid-worker/deployment.config.json").write_text(json.dumps({"AdvancedAnalysis": config}), encoding="utf-8")
        properties = {key: {} for key in ("nativeObservationView", "nativeObservationOffset", "nativeObservationLimit",
                                         "nativeObservationBlockId", "nativeObservationRawSha256")}
        tools = [{"type": "function", "name": name, "parameters": {"properties": properties}}
                 for name in ("search_corpus", "read_source", "find_source_text", "read_evidence")]
        logs = []
        for ordinal, role in enumerate(("planner", "writer"), 1):
            payload = {"load": load, "evidenceProjection": {"status": "not_applicable"}}
            if role == "writer":
                payload.update(evidence=[], evidenceProjection={"status": "observed", "canonicalProjection": {
                    "inputCount": 0, "projectedCount": 0, "omittedByPromptBudgetCount": 0}, "currentCall": {
                    "collection": "evidence", "visibleCount": 0, "fromCanonicalProjectionCount": 0,
                    "outsideCanonicalProjectionCount": 0, "canonicalProjectedNotSelectedCount": 0,
                    "truncatedContentCount": 0, "supportingWindowsOmittedCount": 0}, "instruction": "not corpus absence"})
            body = {"model": "gpt-5.6-luna", "store": False, "stream": False, "max_output_tokens": 8192,
                    "input": [{"role": "user", "content": json.dumps(payload)}], "tools": tools if role == "writer" else []}
            raw = json.dumps(body).encode("utf-8")
            request_hash = hashlib.sha256(raw).hexdigest()
            stem = f"request-{ordinal:04d}"
            folder = runtime / "runtime/planning-wire-captures"
            (folder / (stem + ".body.bin")).write_bytes(raw)
            (folder / (stem + ".http.json")).write_text(json.dumps({"path": "/v1/responses", "loopbackPeer": "127.0.0.1",
                "authorizationPresent": False, "bodyFile": stem + ".body.bin", "bodySha256": request_hash, "bodyBytes": len(raw)}), encoding="utf-8")
            (runtime / "runtime/private-traces" / (role + ".json")).write_text(json.dumps({"requestSha256": request_hash,
                "requestJson": raw.decode("utf-8"), "jobId": "job", "role": role}), encoding="utf-8")
            logs.append('operation=llm.call outcome=failed measurements=' + json.dumps({"request_sha256": request_hash,
                "job_id": "job", "request_characters": utf16_length(raw.decode("utf-8")), "wire_protocol": "responses",
                "diagnostic_dropped_measurements": 0, "diagnostic_truncated_values": 0}))
        (runtime / "runtime/paid-backend.stdout.log").write_bytes(("\n".join(logs)).encode("ascii"))
        return profile_path, runtime

    def test_actual_envelope_inspector_links_bytes_traces_probes_grid_and_tools(self):
        with tempfile.TemporaryDirectory() as folder:
            profile, runtime = self.prepare_inspection(pathlib.Path(folder))
            result = inspect(profile, runtime)
            self.assertTrue(result["passed"])
            self.assertEqual(2, len(result["envelopes"]))
            self.assertFalse(result["paidWireReasoningLowQualifiedByThisPreflight"])

    def test_inspector_rejects_single_item_submission_disguised_as_planning(self):
        with tempfile.TemporaryDirectory() as folder:
            profile, runtime = self.prepare_inspection(pathlib.Path(folder))
            path = runtime / "runtime/planning-submission.private.json"
            value = json.loads(path.read_bytes())
            value["handoff"]["load"]["answerUnitCount"] = 1
            path.write_text(json.dumps(value), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "planning_handoff_grid_or_initial_evidence_invalid"):
                inspect(profile, runtime)

    def test_historical_twenty_cell_preflight_preserves_repeatable_policy(self):
        with tempfile.TemporaryDirectory() as folder:
            profile, runtime = self.prepare_inspection(pathlib.Path(folder), "five-day-20")
            self.assertEqual("structured_layout", inspect(profile, runtime)["selectionPolicy"])

    def test_writer_policy_drift_is_rejected_after_wire_trace_and_probe_hashes_are_rebound(self):
        with tempfile.TemporaryDirectory() as folder:
            profile, runtime = self.prepare_inspection(pathlib.Path(folder), "five-day-20")
            captures = runtime / "runtime/planning-wire-captures"
            metadata_path = captures / "request-0002.http.json"
            metadata = json.loads(metadata_path.read_bytes())
            body_path = captures / metadata["bodyFile"]
            body = json.loads(body_path.read_bytes())
            payload = json.loads(body["input"][0]["content"])
            payload["load"]["selectionPolicy"] = "distinct_structured_layout"
            body["input"][0]["content"] = json.dumps(payload)
            raw = json.dumps(body).encode("utf-8")
            request_sha = hashlib.sha256(raw).hexdigest()
            body_path.write_bytes(raw)
            metadata.update(bodySha256=request_sha, bodyBytes=len(raw))
            metadata_path.write_text(json.dumps(metadata), encoding="utf-8")
            trace_path = runtime / "runtime/private-traces/writer.json"
            trace = json.loads(trace_path.read_bytes())
            trace.update(requestSha256=request_sha, requestJson=raw.decode("utf-8"))
            trace_path.write_text(json.dumps(trace), encoding="utf-8")
            log_path = runtime / "runtime/paid-backend.stdout.log"
            lines = log_path.read_text(encoding="ascii").splitlines()
            prefix, measurement = lines[1].split("measurements=", 1)
            probe = json.loads(measurement)
            probe.update(request_sha256=request_sha, request_characters=utf16_length(raw.decode("utf-8")))
            lines[1] = prefix + "measurements=" + json.dumps(probe)
            log_path.write_bytes(("\n".join(lines)).encode("ascii"))
            with self.assertRaisesRegex(ValueError, "actual_writer_grid_contract_changed_before_provider"):
                inspect(profile, runtime)

    def test_writer_stops_after_capture_instead_of_faking_a_planning_answer(self):
        with self.assertRaisesRegex(ValueError, 'planning_preflight_stop_after_writer_capture'):
            preflight_reply({'tools': [{'type': 'function', 'name': 'read_source'}]}, {}, 0, 2)

    def test_preflight_has_no_http_client_or_secret_loader(self):
        text = pathlib.Path(__file__).with_name('planning_preflight.py').read_text(encoding='utf-8')
        for forbidden in ('urllib.request', 'requests.', 'http.client', 'ProtectedData', 'OpenAiApiKeyProtected'):
            self.assertNotIn(forbidden, text)


if __name__ == '__main__':
    unittest.main()
