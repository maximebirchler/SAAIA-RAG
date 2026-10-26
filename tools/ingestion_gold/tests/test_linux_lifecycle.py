"""Pure/mocked Linux lifecycle tests. No Docker, HTTP, PDF, key or SQL execution."""
from __future__ import annotations

import copy
import hashlib
import importlib.util
import json
from pathlib import Path
from types import SimpleNamespace
import unittest
import urllib.parse
from unittest.mock import patch

from .test_linux_runtime_guards import fixture
from .test_worker_verifier import captured

SCRIPT = Path(__file__).resolve().parents[2] / "validation-ingestion/invoke-linux-lifecycle.py"
SPEC = importlib.util.spec_from_file_location("linux_lifecycle_driver_test", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
import linux_lifecycle_checks as checks

DOC_ID = "11111111-2222-3333-4444-555555555555"
CHUNK_IDS = ["33333333-4444-5555-6666-777777777777", "44444444-5555-6666-7777-888888888888",
             "55555555-6666-7777-8888-999999999999"]
JOB_ID = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"


def inputs():
    owner, _ = fixture()
    manifest = captured()[0]
    case = next(c for c in manifest["cases"] if c["caseId"] == checks.CASE_ID)
    prior = {"schemaVersion": "saaia-linux-ingestion-execution.v1", "executed": True, "passed": True,
             "ingestionWorkerExecuted": True, "sqlPublicationValidated": True,
             "vectorStorePublicationValidated": True, "canonicalSearchValidated": True,
             "plan": {"runId": owner["runId"], "ownerManifestSha256": "0" * 64,
                      "corpusManifestSha256": "0" * 64, "caseIds": [checks.CASE_ID]}}
    config = {"Rag": {"QdrantBaseUrl": "http://qdrant:6333", "QdrantCollection": owner["qdrantCollection"],
                      "QdrantRuntimeRevision": "sha256:" + "d" * 64}}
    return owner, manifest, case, prior, config


def snapshot(generation=1):
    owner, _, case, _, config = inputs()
    plan = "abc"[generation - 1] * 64
    text = " ".join(f["text"] for f in case["expectations"]["criticalFacts"])
    row = {"tenantId": checks.TENANT, "docId": DOC_ID, "docPath": checks.DOC_PATH,
           "status": "indexed", "ingestionVersion": generation, "indexedVersion": generation,
           "plan": plan, "revisionId": str(__import__("uuid").UUID(int=generation + 1)),
           "sourceSha256": case["sourceSha256"], "chunks": [{"retrievalChunkId": CHUNK_IDS[generation - 1],
           "chunkIndex": 0, "pageStart": 1, "pageEnd": 1, "text": text,
           "checksumSha256": hashlib.sha256(text.encode("utf-8")).hexdigest()}]}
    point = {"id": MODULE.vector_point_id(CHUNK_IDS[generation - 1], plan),
             "payload": {"tenant_id": checks.TENANT, "doc_id": DOC_ID,
                         "chunk_id": CHUNK_IDS[generation - 1], "ingestion_version": generation,
                         "ingestion_plan_fingerprint": plan, "private_source_text": text}, "vector": [0.01] * 768}
    registered = {"planId": str(__import__("uuid").UUID(int=10 + generation)), "generation": generation,
                  "fingerprint": plan, "manifestSealed": True, "manifestSha256": "e" * 64,
                  "pointCount": 1, "targetEndpoint": config["Rag"]["QdrantBaseUrl"],
                  "targetCollection": owner["qdrantCollection"], "targetRevision": config["Rag"]["QdrantRuntimeRevision"],
                  "retiredAt": None, "lastSweep": None, "lastAbsence": None, "lastGcCode": None,
                  "points": [{"pointId": point["id"], "canonicalChunkId": CHUNK_IDS[generation - 1]}]}
    return {"activeJobs": 0, "document": row, "watermark": generation,
            "plans": [registered], "otherDocuments": [{"docId": "unchanged-other-publication", "generation": 1}]}, point


class MockRuntime:
    """Simulated protocol only; scheduler advances solely on fake SELECT calls."""
    def __init__(self):
        self.state, point = snapshot()
        self.store, self.job_rows = {point["id"]: point}, []
        self.pending, self.tick, self.puts, self.captures = None, 0, [], []

    def sql(self, query):
        if query != checks.snapshot_sql():
            raise AssertionError("Unexpected SQL outside the static read-only snapshot")
        if self.pending:
            self.tick += 1
            old = self.state["plans"][0]
            for point in old["points"]:
                self.store.pop(point["pointId"], None)
            old.update({"lastSweep": f"2026-10-26T00:00:{self.tick:02d}Z", "lastAbsence": f"2026-10-26T00:00:{self.tick:02d}Z",
                        "lastGcCode": "reappearance_removed" if self.pending == "late" else "absence_observed"})
            self.pending = None
        return json.dumps(self.state)

    def save(self, name, value):
        self.captures.append((name, copy.deepcopy(value)))

    def api(self, role, route, method="GET", body=None):
        if role == "qdrant" and method == "PUT":
            self.puts.append(copy.deepcopy(body))
            for point in body["points"]:
                self.store[point["id"]] = copy.deepcopy(point)
            self.pending = "late"
            return {"status": "ok", "result": {"status": "completed"}}
        if role == "qdrant":
            return {"status": "ok", "result": [copy.deepcopy(self.store[x]) for x in body["ids"] if x in self.store]}
        if route == "/ingest/enqueue":
            if body != {"docPath": checks.DOC_PATH, "action": "upsert"}:
                raise AssertionError("Unexpected mock mutation")
            current, point = snapshot(2)
            old = copy.deepcopy(self.state["plans"][0]); old["retiredAt"] = "2026-10-26T00:00:00Z"
            current["plans"].insert(0, old)
            self.state = current
            self.store[point["id"]] = point
            self.pending = "first"
            self.job_rows = [{"JobId": JOB_ID, "Action": "upsert", "DocPath": checks.DOC_PATH,
                              "Status": "done", "Attempts": 1}]
            return {"jobId": JOB_ID, "tenantId": checks.TENANT, "docId": DOC_ID,
                    "version": 2, "action": "upsert", "docPath": checks.DOC_PATH}
        if route.startswith("/ingestion/jobs"):
            return {"items": self.job_rows}
        row = self.state["document"]
        chunk = row["chunks"][0]
        if route == "/rag/search":
            empty = body["docId"] != DOC_ID or body["query"].startswith("saaianomatch")
            item = {"docId": DOC_ID, "docPath": checks.DOC_PATH, "revisionId": row["revisionId"],
                    "sourceHash": row["sourceSha256"], "chunkId": chunk["retrievalChunkId"],
                    "text": chunk["text"], "pageStart": 1, "pageEnd": 1}
            return {"query": body["query"], "items": [] if empty else [item], "metrics": {
                "degradedRetrievers": [], "returned": 0 if empty else 1,
                "exactMatchReturned": 0 if empty else 1, "sparseReturned": 0,
                "denseReturned": 0, "retrieversUsed": [] if empty else ["exact_match"]}}
        if route.startswith("/documents/context?"):
            query = urllib.parse.parse_qs(urllib.parse.urlparse(route).query)
            return {"found": True, "anchorFound": True, "document": {"docId": DOC_ID,
                "revisionId": row["revisionId"], "sourceHash": row["sourceSha256"]},
                "items": [{"chunkId": query["chunkId"][0], "text": chunk["text"]}]}
        raise AssertionError("Unexpected mocked route")


class LinuxLifecycleTests(unittest.TestCase):
    def test_configuration_changed_after_corpus_pass_is_rejected(self):
        checks.validate_configuration_continuity("a" * 64, {"signedConfigurationSha256": "a" * 64})
        for prior in ({}, {"signedConfigurationSha256": "b" * 64}):
            with self.assertRaises(ValueError): checks.validate_configuration_continuity("a" * 64, prior)

    def test_unrelated_preservation_checks_real_vectors_and_payloads_not_sql_only(self):
        state, point = snapshot(3)
        row = state["document"]
        other = {"tenantId": row["tenantId"], "docId": row["docId"], "status": "indexed",
                 "indexedVersion": row["indexedVersion"], "plan": row["plan"],
                 "chunks": [{"canonicalChunkId": c["retrievalChunkId"]} for c in row["chunks"]]}
        expected = checks.unrelated_point_manifest({"otherDocuments": [other]})
        baseline = checks.validate_unrelated_points(expected, [point])
        modified = copy.deepcopy(point); modified["vector"][0] += 0.5
        self.assertNotEqual(baseline, checks.validate_unrelated_points(expected, [modified]))
        with self.assertRaises(ValueError): checks.validate_unrelated_points(expected, [])
        modified = copy.deepcopy(point); modified["payload"]["doc_id"] = JOB_ID
        with self.assertRaises(ValueError): checks.validate_unrelated_points(expected, [modified])

    def test_cleanup_can_cancel_only_new_scoped_job_when_source_bytes_are_invalid(self):
        owner, _, case, _, config = inputs()
        mock = MockRuntime()
        rows = [{"JobId": JOB_ID, "DocPath": checks.DOC_PATH, "Action": "upsert", "Status": "running", "Attempts": 1},
                {"JobId": DOC_ID, "DocPath": "another.pdf", "Action": "upsert", "Status": "done", "Attempts": 1}]
        calls, checks_called = [], []
        def api(role, route, method="GET", body=None):
            calls.append((route, body))
            if route.startswith("/ingestion/jobs"):
                return {"items": rows}
            self.assertEqual(("/admin/jobs/cancel", {"jobId": JOB_ID}), (route, body))
            return {"canceled": True}
        def sql(query):
            self.assertEqual(checks.ACTIVE_SQL, query)
            return "0"
        mock.sql = sql
        engine = MODULE.Lifecycle(owner, case, config, mock, api, mock.save,
                                 source_check=lambda: (_ for _ in ()).throw(ValueError("PDF checksum changed")))
        engine.admitted.add(JOB_ID)
        result = engine.cleanup_jobs(lambda: checks_called.append(True))
        self.assertTrue(result["zeroActiveJobs"])
        self.assertTrue(checks_called)
        self.assertEqual([("/ingestion/jobs?limit=200", None), ("/admin/jobs/cancel", {"jobId": JOB_ID})], calls)

    def test_cleanup_does_not_cancel_a_job_that_this_runner_did_not_admit(self):
        owner, _, case, _, config = inputs()
        mock = MockRuntime()
        calls = []
        def api(role, route, method="GET", body=None):
            calls.append(route)
            return {"items": [{"JobId": JOB_ID, "DocPath": checks.DOC_PATH, "Action": "upsert", "Status": "running", "Attempts": 1}]}
        mock.sql = lambda query: "1"
        clock = iter([0, 61])
        engine = MODULE.Lifecycle(owner, case, config, mock, api, mock.save, clock=lambda: next(clock), sleep=lambda _: None)
        result = engine.cleanup_jobs(lambda: None)
        self.assertFalse(result["zeroActiveJobs"])
        self.assertEqual(["/ingestion/jobs?limit=200"], calls)

    def test_gc_cannot_target_an_external_service_or_historical_credentials(self):
        owner, _, _, _, config = inputs()
        qdrant = {"Id": next(r["id"] for r in owner["containers"] if r["role"] == "qdrant"),
                  "HostConfig": {"PortBindings": {"6333/tcp": []}},
                  "NetworkSettings": {"Networks": {owner["project"] + "_default": {"Aliases": ["qdrant"]}}}}
        config["VectorGc"] = {"Enabled": True, "PollSeconds": 1, "RecheckSeconds": 2}
        checks.validate_gc_configuration(owner, config, qdrant)
        for name, value in (("Enabled", False), ("PollSeconds", 30), ("RecheckSeconds", 3600),
                            ("ApprovedTargets", [{"Endpoint": "http://unowned:6333", "ApiKeyRef": "not-read"}])):
            changed = copy.deepcopy(config); changed["VectorGc"][name] = value
            with self.subTest(name=name), self.assertRaises(ValueError): checks.validate_gc_configuration(owner, changed, qdrant)
        for url in ("http://production:6333", "http://qdrant:9999", "http://qdrant:6333/path", "http://user:password@qdrant:6333"):
            changed = copy.deepcopy(config); changed["Rag"]["QdrantBaseUrl"] = url
            with self.subTest(url=url), self.assertRaises(ValueError): checks.validate_gc_configuration(owner, changed, qdrant)

    def test_noexec_never_reads_key_config_document_or_uses_docker_http(self):
        owner, manifest, _, prior, _ = inputs()
        args = SimpleNamespace(owner=Path("owner.json"), manifest=Path("manifest.json"),
            corpus_proof=Path("result.json"), execute=False, maximum_wait_seconds=300,
            attempt_cancel=False, api_key_file=Path("never-read-key"))
        with patch.object(MODULE.corpus, "read_json", side_effect=[owner, manifest, prior]), \
                patch.object(MODULE.corpus, "sha", return_value="0" * 64), \
                patch.object(MODULE, "OwnedDocker") as docker, \
                patch.object(Path, "open", side_effect=AssertionError("Unexpected private read")), \
                patch.object(MODULE.urllib.request, "build_opener", side_effect=AssertionError("Unexpected HTTP")):
            result = MODULE.run(args)
        docker.assert_not_called()
        self.assertFalse(result["executeRequested"])
        self.assertFalse(result["controlledLateWriteIsWorkerCancellationProof"])

    def test_real_pass_must_bind_same_namespace_manifest_and_case(self):
        owner, _, case, prior, _ = inputs()
        checks.validate_plan(owner, prior, case, owner_sha="0" * 64, manifest_sha="0" * 64)
        for name in ("executed", "passed", "ingestionWorkerExecuted", "sqlPublicationValidated",
                     "vectorStorePublicationValidated", "canonicalSearchValidated"):
            changed = copy.deepcopy(prior); changed[name] = False
            with self.subTest(name=name), self.assertRaises(ValueError):
                checks.validate_plan(owner, changed, case, owner_sha="0" * 64, manifest_sha="0" * 64)
        for name in ("runId", "ownerManifestSha256", "corpusManifestSha256", "caseIds"):
            changed = copy.deepcopy(prior); changed["plan"][name] = "other"
            with self.subTest(name=name), self.assertRaises(ValueError):
                checks.validate_plan(owner, changed, case, owner_sha="0" * 64, manifest_sha="0" * 64)

    def test_only_generated_hash_pinned_scan_is_mutable(self):
        owner, _, case, prior, _ = inputs()
        for name, value in (("caseId", "real-user-pdf"), ("generator", None), ("sourceSha256", "invalid")):
            changed = {**case, name: value}
            with self.subTest(name=name), self.assertRaises(ValueError):
                checks.validate_plan(owner, prior, changed, owner_sha="0" * 64, manifest_sha="0" * 64)

    def test_publication_requires_sealed_exact_manifest_and_stored_text_checksum(self):
        state, _ = snapshot(); source = state["document"]["sourceSha256"]
        checks.current_publication(state, source)
        changes = [("activeJobs", 1), ("watermark", 0)]
        for field, value in changes:
            altered = copy.deepcopy(state); altered[field] = value
            with self.subTest(field=field), self.assertRaises(ValueError): checks.current_publication(altered, source)
        for field, value in (("status", "pending"), ("tenantId", "foreign"), ("sourceSha256", "0" * 64)):
            altered = copy.deepcopy(state); altered["document"][field] = value
            with self.subTest(field=field), self.assertRaises(ValueError): checks.current_publication(altered, source)
        for change in ("unsealed", "checksum", "pointid", "missing", "retired"):
            altered = copy.deepcopy(state)
            if change == "unsealed": altered["plans"][0]["manifestSealed"] = False
            elif change == "checksum": altered["document"]["chunks"][0]["text"] += "tampered"
            elif change == "pointid": altered["plans"][0]["points"][0]["pointId"] = DOC_ID
            elif change == "missing": altered["plans"] = []
            else: altered["plans"][0]["retiredAt"] = "retired"
            with self.subTest(change=change), self.assertRaises(ValueError): checks.current_publication(altered, source)

    def test_full_point_capture_requires_true_vector_and_all_five_identity_fields(self):
        state, point = snapshot(); row = state["document"]
        checks.validate_points(row, [point])
        for name in ("tenant_id", "doc_id", "chunk_id", "ingestion_version", "ingestion_plan_fingerprint"):
            altered = copy.deepcopy(point); altered["payload"][name] = "foreign"
            with self.subTest(name=name), self.assertRaises(ValueError): checks.validate_points(row, [altered])
        for vector in (None, [], [0.0] * 768, [float("nan")] * 768, [True] * 768, [0.1] * 3):
            with self.subTest(vector_kind=str(type(vector))), self.assertRaises(ValueError):
                checks.validate_points(row, [{**point, "vector": vector}])
        with self.assertRaises(ValueError): checks.validate_points(row, [point, point])

    def test_physical_ids_must_be_disjoint_and_generation_must_advance(self):
        before, a = snapshot(); after, b = snapshot(2)
        checks.advancement(before["document"], after["document"], 2, {a["id"]})
        with self.assertRaises(ValueError): checks.advancement(before["document"], after["document"], 2, {b["id"]})
        with self.assertRaises(ValueError): checks.advancement(before["document"], after["document"], 1, {a["id"]})

    def test_gc_needs_sql_sweep_and_actual_qdrant_absence_not_either_alone(self):
        state, point = snapshot(); old = copy.deepcopy(state["plans"][0])
        self.assertFalse(checks.gc_observed(state, old, []))
        state["plans"][0].update({"retiredAt": "retired", "lastSweep": "2026-10-26T00:00:02Z", "lastAbsence": "2026-10-26T00:00:02Z"})
        self.assertFalse(checks.gc_observed(state, old, [point]))
        self.assertTrue(checks.gc_observed(state, old, []))
        self.assertFalse(checks.gc_observed(state, old, [], reappearance=True))
        state["plans"][0]["lastGcCode"] = "reappearance_removed"
        self.assertTrue(checks.gc_observed(state, old, [], reappearance=True, after_sweep="2026-10-26T00:00:01Z"))
        self.assertFalse(checks.gc_observed(state, old, [], reappearance=True, after_sweep="2026-10-26T00:00:02Z"))
        state["plans"][0]["pointCount"] = 0
        with self.assertRaises(ValueError): checks.gc_observed(state, old, [])

    def test_cancel_completion_race_is_non_executed_not_cancellation_pass(self):
        state, _ = snapshot(); before = state["document"]
        outcome = checks.cancellation_outcome({}, {"status": "done"}, before, before, 2)
        self.assertEqual("NON_EXECUTED", outcome["status"])
        self.assertFalse(outcome["upstreamEngineCancellationVerified"])

    def test_cancel_preserves_gap_without_claiming_current_source_freshness(self):
        state, _ = snapshot(); before = state["document"]
        now = {**before, "ingestionVersion": 2}
        outcome = checks.cancellation_outcome({"canceled": True}, {"status": "canceled"}, before, now, 2)
        self.assertEqual("PASS", outcome["status"])
        self.assertFalse(outcome["currentSourceFreshnessReverified"])
        with self.assertRaises(ValueError): checks.cancellation_outcome({"canceled": False}, {"status": "canceled"}, before, now, 2)
        with self.assertRaises(ValueError): checks.cancellation_outcome({"canceled": True}, {"status": "canceled"}, before, {**now, "plan": "b" * 64}, 2)

    def test_mocked_protocol_reuses_exact_old_vector_and_protects_current_publication(self):
        owner, _, case, _, config = inputs()
        mock = MockRuntime()
        engine = MODULE.Lifecycle(owner, case, config, mock, mock.api, mock.save, sleep=lambda _: None)
        a, plan_a, captured_a = engine.publication("A")
        engine.history_ids.update(p["id"] for p in captured_a)
        b, _, _ = engine.upsert(a, "B")
        swept = engine.wait_gc(plan_a, protected=b)
        engine.late_write(plan_a, captured_a, b, swept["lastSweep"])
        engine.publication("B-final")
        self.assertEqual([{ "points": [captured_a[0]]}], mock.puts)
        self.assertNotIn(captured_a[0]["id"], mock.store)
        self.assertEqual(2, mock.state["document"]["indexedVersion"])
        self.assertEqual("reappearance_removed", mock.state["plans"][0]["lastGcCode"])
        self.assertTrue(all(step.get("searchToReadPassed", True) for step in engine.steps))
        self.assertTrue(mock.captures)

    def test_gc_does_not_ignore_lost_current_point(self):
        owner, _, case, _, config = inputs()
        mock = MockRuntime()
        engine = MODULE.Lifecycle(owner, case, config, mock, mock.api, mock.save, sleep=lambda _: None)
        a, plan_a, points_a = engine.publication("A")
        engine.history_ids.update(p["id"] for p in points_a)
        b, _, points_b = engine.upsert(a, "B")
        mock.store.pop(points_b[0]["id"])
        with self.assertRaises(ValueError): engine.wait_gc(plan_a, protected=b)

    def test_unrelated_document_change_is_a_failed_scope_guard(self):
        owner, _, case, _, config = inputs()
        mock = MockRuntime()
        engine = MODULE.Lifecycle(owner, case, config, mock, mock.api, mock.save)
        engine.snapshot()
        mock.state["otherDocuments"][0]["generation"] = 2
        with self.assertRaises(ValueError): engine.snapshot()

    def test_only_static_select_sql_is_emitted(self):
        sql = checks.snapshot_sql()
        self.assertTrue(sql.startswith("SELECT "))
        for forbidden in ("INSERT ", "UPDATE ", "DELETE ", "TRUNCATE ", "CREATE ", "ALTER "):
            self.assertNotIn(forbidden, sql.upper())
        self.assertIn(checks.TENANT, sql)
        self.assertIn(checks.DOC_PATH, sql)


if __name__ == "__main__":
    unittest.main()
