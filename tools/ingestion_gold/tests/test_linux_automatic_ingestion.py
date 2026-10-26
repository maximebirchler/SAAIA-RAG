"""Pure guards, simulated observations and temporary fake-byte file copies only."""
from __future__ import annotations

import base64
import copy
import gzip
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import urllib.parse
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import uuid

REPO = next(p for p in Path(__file__).resolve().parents if (p / "tools/validation-ingestion").is_dir())
sys.path.insert(0, str(REPO))
sys.path.insert(0, str(REPO / "tools/validation-ingestion"))
sys.path.insert(0, str(Path(__file__).resolve().parent))
SCRIPT = Path(__file__).with_name("invoke-linux-automatic-ingestion.py")
if not SCRIPT.is_file():
    SCRIPT = REPO / "tools/validation-ingestion/invoke-linux-automatic-ingestion.py"
SPEC = importlib.util.spec_from_file_location("automatic_driver_test", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
import linux_automatic_ingestion_checks as CHECK
from tools.ingestion_gold.tests.test_linux_runtime_guards import fixture
from tools.ingestion_gold.tests.test_worker_verifier import captured
from tools.ingestion_gold.tests.test_linux_ingestion_plan import inputs as plan_inputs


def inputs(profile="scanner"):
    owner, observed = fixture()
    owner["admissionProfile"] = profile
    manifest = captured()[0]
    cases, _ = CHECK.validate_inputs(owner, manifest, profile, owner_sha="0" * 64, manifest_sha="0" * 64)
    config = {"Ingestion": {**CHECK.TIMINGS, "WorkerEnabled": True,
        "ScannerEnabled": CHECK.PROFILES[profile][0], "WatcherEnabled": CHECK.PROFILES[profile][1]},
        "AdvancedAnalysis": {"WorkerEnabled": False, "Provider": "disabled",
            "AllowExternalProviderContent": False, "AllowExternalProviderMetadata": False},
        "License": {"AdvancedAnalysisEnabled": False}, "RuntimeGovernance": {"CapabilityBWorkerEnabled": False},
        "Rag": {"QdrantBaseUrl": "http://qdrant:80", "QdrantCollection": owner["qdrantCollection"],
            "QdrantRuntimeRevision": "sha256:" + "a" * 64}}
    backend = next(c for c in observed if c["Id"] == next(r["id"] for r in owner["containers"] if r["role"] == "backend"))
    return owner, cases, config, backend, manifest


def publication(index=0, generation=1):
    owner, cases, config, _, _ = inputs()
    case = cases[index]
    path = CHECK.TARGETS[index]
    doc, revision, chunk = [str(uuid.UUID(int=100 + index * 100 + n + generation * 10)) for n in range(3)]
    text = " ".join(f["text"] for f in case["expectations"]["criticalFacts"])
    pages = [{"pageNumber": p, "blocks": [{"blockId": f"b{p}", "blockType": "text", "readingOrder": 0,
        "text": {"raw": " ".join(f["text"] for f in case["expectations"]["criticalFacts"] if f["page"] == p)}}], "tables": []}
        for p in range(1, case["expectations"]["pageCount"] + 1)]
    raw = json.dumps({"schemaVersion": "canonical_ingestion_bundle_v1", "document": {
        "documentId": doc, "revisionId": revision, "source": {"sha256": case["sourceSha256"]}, "pages": pages}}).encode()
    compressed = gzip.compress(raw, mtime=0)
    row = {"docPath": path, "docId": doc, "revisionId": revision, "status": "indexed", "indexedVersion": generation,
        "sourceSha256": case["sourceSha256"], "pageCount": len(pages), "activeVectorPlanFingerprint": "f" * 64,
        "chunks": [{"retrievalChunkId": chunk, "chunkIndex": 0, "pageStart": 1, "pageEnd": len(pages),
            "text": text, "checksumSha256": hashlib.sha256(text.encode()).hexdigest()}],
        "binaryArtifacts": [{"artifactType": "canonical_ingestion_bundle", "compression": "gzip", "byteSize": len(raw),
            "storedSize": len(compressed), "contentHash": hashlib.sha256(raw).hexdigest(),
            "payloadBase64": base64.b64encode(compressed).decode()}]}
    state = {"tenantId": CHECK.TENANT, "docId": doc, "docPath": path, "status": "indexed", "ingestionVersion": generation,
        "indexedVersion": generation, "missingSince": None, "plan": "f" * 64, "watermark": generation}
    physical = MODULE.vector_point_id(chunk, "f" * 64)
    plan = {"tenantId": CHECK.TENANT, "docId": doc, "planId": str(uuid.UUID(int=900 + generation)),
        "generation": generation, "fingerprint": "f" * 64, "manifestSealed": True, "retiredAt": None,
        "manifestSha256": "a" * 64, "pointCount": 1, "points": [{"pointId": physical, "canonicalChunkId": chunk}],
        "targetCollection": owner["qdrantCollection"], "targetEndpoint": config["Rag"]["QdrantBaseUrl"],
        "targetRevision": config["Rag"]["QdrantRuntimeRevision"]}
    return {"publication": {"schemaVersion": "saaia-owned-worker-observation.v1", "activeJobs": 0,
        "documents": [row], "jobs": []}, "documentStates": [state], "jobs": [], "plans": [plan]}


def job(index=0, *, status="done", consumer=0, source="scanner", action="upsert"):
    owner = "instance-w" + str(consumer) + ":" + str(index + 1) * 32
    return {"tenantId": CHECK.TENANT, "jobId": str(uuid.UUID(int=1000 + index)), "docPath": CHECK.TARGETS[0],
        "status": status, "action": action, "attempts": 1, "lockedBy": owner,
        "startedAt": "2026-10-26T12:00:10Z", "lastError": None,
        "payload": {"source": source, "version": 2, "executionLeaseOwner": owner}}


class SimulatedAutomaticRuntime:
    """In-memory scheduler only. These observations are never live evidence."""
    def __init__(self, profile):
        self.profile = profile
        self.owner, self.cases, self.config, _, _ = inputs(profile)
        self.rows, self.states, self.plans, self.jobs, self.store = {}, {}, [], [], {}
        self.expected, self.now, self.events = {}, 0.0, []
        if profile == "defaults":
            self.replace(CHECK.TARGETS[0], CHECK.SOURCE_IDS[0], admission="scanner")

    def sql(self, query):
        if query != CHECK.snapshot_sql():
            raise AssertionError("Only the static automatic snapshot is permitted")
        return json.dumps({"documentStates": list(self.states.values()), "jobs": self.jobs, "plans": self.plans,
            "publication": {"schemaVersion": "saaia-owned-worker-observation.v1", "activeJobs": 0,
                "documents": list(self.rows.values()), "jobs": self.jobs}})

    def verify(self):
        return None

    def _retire(self, path):
        old = self.states.get(path, {})
        for plan in self.plans:
            if plan["docId"] == old.get("docId") and plan.get("retiredAt") is None:
                plan.update(retiredAt="2026-10-26T12:00:00Z", lastSweep="2026-10-26T12:00:01Z",
                            lastAbsence="2026-10-26T12:00:01Z", lastGcCode="absence_observed")
                for point in plan["points"]:
                    self.store.pop(point["pointId"], None)

    def replace(self, path, source_case, *, admission=None):
        index = CHECK.SOURCE_IDS.index(source_case)
        target_index = CHECK.TARGETS.index(path)
        generation = self.states.get(path, {}).get("ingestionVersion", 0) + 1
        self._retire(path)
        snap = publication(index, generation)
        row, state, plan = snap["publication"]["documents"][0], snap["documentStates"][0], snap["plans"][0]
        old_doc = row["docId"]
        doc_id = str(uuid.UUID(int=10000 + target_index))
        for value in (row, state):
            value.update(docId=doc_id, docPath=path)
        plan.update(docId=doc_id, planId=str(uuid.UUID(int=20000 + len(self.plans))))
        # Keep the full canonical document bound to the stable target identity.
        artifact = row["binaryArtifacts"][0]
        raw = gzip.decompress(base64.b64decode(artifact["payloadBase64"])).replace(old_doc.encode(), doc_id.encode())
        compressed = gzip.compress(raw, mtime=0)
        artifact.update(byteSize=len(raw), storedSize=len(compressed), contentHash=hashlib.sha256(raw).hexdigest(),
                        payloadBase64=base64.b64encode(compressed).decode())
        # The real plan changes for every generation, including the same source.
        fingerprint = hashlib.sha256((path + str(generation)).encode()).hexdigest()
        row["activeVectorPlanFingerprint"] = state["plan"] = plan["fingerprint"] = fingerprint
        physical = MODULE.vector_point_id(row["chunks"][0]["retrievalChunkId"], fingerprint)
        plan["points"][0]["pointId"] = physical
        self.store[physical] = {"id": physical, "vector": [0.001 * generation] * 768,
            "payload": {"tenant_id": CHECK.TENANT, "doc_id": doc_id,
                "chunk_id": row["chunks"][0]["retrievalChunkId"], "ingestion_version": generation,
                "ingestion_plan_fingerprint": fingerprint}}
        self.rows[path], self.states[path] = row, state
        self.plans.append(plan)
        completed = job(len(self.jobs), source=admission or ("scanner" if self.profile == "scanner" else "watcher"))
        completed["docPath"] = path
        completed["payload"]["version"] = generation
        self.jobs.append(completed)
        self.expected[path] = self.cases[index]["sourceSha256"]
        self.events.append(("replace", path, source_case))
        return "2026-10-26T12:00:00Z"

    def remove(self, path):
        self.expected.pop(path)
        if self.profile == "watcher":
            self.states[path].update(status="missing", missingSince="2026-10-26T12:00:00Z")
            self.rows[path]["status"] = "missing"
        else:
            self._retire(path)
            self.states[path].update(status="deleted", indexedVersion=0, plan=None)
            self.states[path]["ingestionVersion"] += 1
            self.states[path]["watermark"] = self.states[path]["ingestionVersion"]
            self.rows[path].update(status="deleted", indexedVersion=0, activeVectorPlanFingerprint=None)
            completed = job(len(self.jobs), action="delete", source="scanner_missing")
            completed.update(docPath=path)
            completed["payload"].update(version=self.states[path]["ingestionVersion"], requiresSourceAbsence=True)
            self.jobs.append(completed)
        self.events.append(("remove", path))
        return "2026-10-26T12:00:00Z"

    def api(self, role, route, method="GET", body=None):
        if role == "qdrant" and method == "POST":
            return {"status": "ok", "result": [copy.deepcopy(self.store[i]) for i in body["ids"] if i in self.store]}
        if role != "backend":
            raise AssertionError("No provider or document conversion call is permitted in the protocol fake")
        if route == "/rag/search" and method == "POST":
            row = next(r for r in self.rows.values() if r["docId"] == body["docId"])
            items = []
            if self.states[row["docPath"]]["status"] != "deleted":
                chunk = row["chunks"][0]
                items = [{"docId": row["docId"], "docPath": row["docPath"], "revisionId": row["revisionId"],
                    "sourceHash": row["sourceSha256"], "chunkId": chunk["retrievalChunkId"], "text": chunk["text"],
                    "pageStart": chunk["pageStart"], "pageEnd": chunk["pageEnd"]}]
            return {"query": body["query"], "items": items, "metrics": {"degradedRetrievers": []}}
        if route.startswith("/documents/context?") and method == "GET":
            query = urllib.parse.parse_qs(route.split("?", 1)[1])
            row = next(r for r in self.rows.values() if r["docId"] == query["docId"][0])
            chunk = row["chunks"][0]
            return {"found": True, "anchorFound": True, "document": {"docId": row["docId"],
                "revisionId": row["revisionId"], "sourceHash": row["sourceSha256"]},
                "items": [{"chunkId": chunk["retrievalChunkId"], "text": chunk["text"]}]}
        raise AssertionError("No API enqueue, cancel or other HTTP route is allowed in the functional protocol")

    def sleep(self, seconds):
        self.now += seconds


class AutomaticGuards(unittest.TestCase):
    def test_automatic_search_read_accepts_real_wire_omission_of_healthy_nullable_diagnostics(self):
        runtime=SimulatedAutomaticRuntime("scanner")
        runtime.replace(CHECK.TARGETS[0],CHECK.SOURCE_IDS[0])
        original=runtime.api
        def wire(role,route,method="GET",body=None):
            response=original(role,route,method,body)
            if "metrics" in response:response["metrics"].pop("degradedRetrievers",None)
            return json.loads(json.dumps(response))
        probe=MODULE.AutomaticProbe(runtime.owner,runtime.cases,runtime.config,"scanner",runtime,runtime,
            wire,lambda *_:None,clock=lambda:runtime.now,sleep=runtime.sleep)
        row=CHECK.current_row(probe.snapshot(),CHECK.TARGETS[0],runtime.cases[0])
        try:result=probe.search_read(row,runtime.cases[0])
        except ValueError:result={"passed":False}
        self.assertTrue(result["passed"])

    def test_scanner_only_refuses_observed_watcher_admission(self):
        runtime = SimulatedAutomaticRuntime("scanner")
        runtime.replace(CHECK.TARGETS[0], CHECK.SOURCE_IDS[0], admission="watcher")
        probe = MODULE.AutomaticProbe(runtime.owner, runtime.cases, runtime.config, "scanner", runtime, runtime,
            runtime.api, lambda *_: None, clock=lambda: runtime.now, sleep=runtime.sleep)
        with self.assertRaises(ValueError): probe.snapshot()

    def test_watcher_only_refuses_observed_scanner_admission(self):
        runtime = SimulatedAutomaticRuntime("watcher")
        runtime.replace(CHECK.TARGETS[0], CHECK.SOURCE_IDS[0], admission="scanner")
        probe = MODULE.AutomaticProbe(runtime.owner, runtime.cases, runtime.config, "watcher", runtime, runtime,
            runtime.api, lambda *_: None, clock=lambda: runtime.now, sleep=runtime.sleep)
        with self.assertRaises(ValueError): probe.snapshot()

    def test_bound_readiness_must_report_two_workers_not_only_the_configuration(self):
        ready = {"ok": True, "details": {"canonical_provenance_ready": True,
            "canonical_artifacts_enabled": True, "tei_dim": 768, "ingestion_worker_concurrency": 1}}
        for value in (None, 1, 3, True, "2", 2.0):
            ready["details"]["ingestion_worker_concurrency"] = value
            with self.subTest(value=value), self.assertRaises(ValueError): CHECK.validate_bound_readiness(ready)
        ready["details"]["ingestion_worker_concurrency"] = 2
        self.assertEqual({"boundWorkerConcurrency": 2}, CHECK.validate_bound_readiness(ready))

    def test_scanner_sources_allow_conditional_delete_and_safe_return_but_watcher_profile_does_not(self):
        for source in ("scanner", "scanner_missing", "scanner_source_returned"):
            snapshot = {"jobs": [{"payload": {"source": source}}]}
            CHECK.validate_job_profile(snapshot, "scanner")
            CHECK.validate_job_profile(snapshot, "defaults")
            with self.assertRaises(ValueError): CHECK.validate_job_profile(snapshot, "watcher")

    def test_backend_identity_compares_dll_and_image_independently_not_tags_or_windows_builds(self):
        expected = {"assemblySha256": "a" * 64, "imageId": "sha256:" + "b" * 64}
        CHECK.validate_backend_identity(expected, "a" * 64, "sha256:" + "b" * 64)
        for assembly, image in (("c" * 64, "sha256:" + "b" * 64), ("a" * 64, "sha256:" + "c" * 64),
                                ("not-a-hash", "image:latest")):
            with self.subTest(assembly=assembly, image=image), self.assertRaises(ValueError):
                CHECK.validate_backend_identity(expected, assembly, image)

    def test_production_requires_observed_environment_and_verified_signature_without_forcing_development(self):
        owner, _, _, backend, _ = inputs()
        owner["qualificationMode"] = "production"
        backend["Config"]["Env"] = ["ASPNETCORE_ENVIRONMENT=Production"]
        ready = {"details": {"config_signature_verified": True, "config_signature_mode": "enforced"}}
        result = MODULE.corpus.qualification_identity(owner, backend, ready)
        self.assertTrue(result["deploymentQualificationEligible"])
        self.assertEqual("Production", result["environmentObservedFromContainer"])
        ready["details"]["config_signature_verified"] = False
        with self.assertRaises(ValueError): MODULE.corpus.qualification_identity(owner, backend, ready)

    def test_unsigned_development_remains_explicitly_ineligible_for_production_qualification(self):
        owner, _, _, backend, _ = inputs()
        owner["qualificationMode"] = "development_diagnostic"
        backend["Config"]["Env"] = ["ASPNETCORE_ENVIRONMENT=Development"]
        result = MODULE.corpus.qualification_identity(owner, backend, {"details": {"config_signature_verified": False}})
        self.assertFalse(result["deploymentQualificationEligible"])
        self.assertEqual("Development", result["environmentObservedFromContainer"])

    def test_simulated_functional_scanner_and_watcher_protocols_keep_distinct_deletion_contracts(self):
        for profile in ("scanner", "watcher"):
            runtime = SimulatedAutomaticRuntime(profile)
            probe = MODULE.AutomaticProbe(runtime.owner, runtime.cases, runtime.config, profile, runtime, runtime,
                runtime.api, lambda *_: None, clock=lambda: runtime.now, sleep=runtime.sleep)
            result = probe.execute()
            with self.subTest(profile=profile):
                self.assertTrue(result["passed"])
                self.assertTrue(result["otherPublicationsAndFullVectorsPreserved"])
                phases = [s["phase"] for s in result["steps"]]
                self.assertIn("watcher-missing" if profile == "watcher" else "scanner-deleted", phases)
                self.assertNotIn("scanner-deleted" if profile == "watcher" else "watcher-missing", phases)
                self.assertEqual("NON_EXECUTED", result["twoWorkers"]["status"])

    def test_simulated_combined_completion_wins_never_claims_race_or_two_worker_pass(self):
        runtime = SimulatedAutomaticRuntime("defaults")
        probe = MODULE.AutomaticProbe(runtime.owner, runtime.cases, runtime.config, "defaults", runtime, runtime,
            runtime.api, lambda *_: None, clock=lambda: runtime.now, sleep=runtime.sleep)
        result = probe.execute()
        self.assertTrue(result["functionalPassed"])
        self.assertFalse(result["passed"])
        self.assertEqual("NON_EXECUTED", result["conditionalDeleteReturn"]["status"])
        self.assertEqual("NON_EXECUTED", result["twoWorkers"]["status"])

    def test_three_distinct_generated_sources_are_required(self):
        owner, _, _, _, manifest = inputs()
        cases, plan = CHECK.validate_inputs(owner, manifest, "scanner", owner_sha="0"*64, manifest_sha="0"*64)
        self.assertEqual(3, len({c["sourceSha256"] for c in cases}))
        self.assertFalse(plan["apiJobEnqueueAllowed"])
        changed = copy.deepcopy(manifest)
        chosen = [c for c in changed["cases"] if c["caseId"] in CHECK.SOURCE_IDS]
        chosen[1]["sourceSha256"] = chosen[0]["sourceSha256"]
        with self.assertRaises(ValueError): CHECK.validate_inputs(owner, changed, "scanner", owner_sha="0"*64, manifest_sha="0"*64)

    def test_profile_or_foreign_owner_mismatch_refused(self):
        owner, _, _, _, manifest = inputs()
        with self.assertRaises(ValueError): CHECK.validate_inputs(owner, manifest, "watcher", owner_sha="0"*64, manifest_sha="0"*64)
        owner["root"] = "/opt/saaia"
        with self.assertRaises(ValueError): CHECK.validate_inputs(owner, manifest, "scanner", owner_sha="0"*64, manifest_sha="0"*64)

    def test_all_profiles_retain_strict_llm_guard_with_only_measured_trigger_exception(self):
        for profile in CHECK.PROFILES:
            _, _, config, backend, _ = inputs(profile)
            result = CHECK.validate_configuration(config, backend, profile, MODULE.corpus.validate_disabled_llm)
            self.assertTrue(result["llmWorkersDisabled"])
            config["AdvancedAnalysis"]["WorkerEnabled"] = True
            with self.assertRaises(ValueError): CHECK.validate_configuration(config, backend, profile, MODULE.corpus.validate_disabled_llm)

    def test_timing_worker_and_override_drift_refused(self):
        for key in (*CHECK.TIMINGS, "WorkerEnabled", "WatcherEnabled", "ScannerEnabled"):
            _, _, config, backend, _ = inputs()
            config["Ingestion"][key] = not config["Ingestion"][key] if type(config["Ingestion"][key]) is bool else 999
            with self.subTest(key=key), self.assertRaises(ValueError):
                CHECK.validate_configuration(config, backend, "scanner", MODULE.corpus.validate_disabled_llm)
        _, _, config, backend, _ = inputs()
        for name in ("Ingestion__ScannerEnabled", "ingestion__scannerenabled", "Ingestion__workerconcurrency", "INGESTION:WORKERENABLED"):
            backend["Config"]["Env"] = [name + "=false"]
            with self.subTest(environmentName=name), self.assertRaises(ValueError):
                CHECK.validate_configuration(config, backend, "scanner", MODULE.corpus.validate_disabled_llm)

    def test_sql_is_static_select_and_history_has_deterministic_tie_break(self):
        sql = CHECK.snapshot_sql()
        self.assertTrue(sql.startswith("SELECT"))
        self.assertNotRegex(sql, r"(?i)\b(?:UPDATE|DELETE|INSERT|CREATE|DROP|ALTER)\s")
        self.assertIn("ORDER BY doc_path,job_id", sql)
        self.assertIn("'missingSince'", sql)
        self.assertIn("'startedAt'", sql)

    def test_scope_rejects_foreign_tenant_path_and_nonautomatic_job(self):
        for field, value in (("tenantId", str(uuid.uuid4())), ("docPath", "other.pdf")):
            snap = publication(); snap["documentStates"][0][field] = value
            with self.subTest(field=field), self.assertRaises(ValueError): CHECK.scope(snap)
        snap = publication(); snap["jobs"] = [job(source="api")]
        with self.assertRaises(ValueError): CHECK.scope(snap)

    def test_scope_rejects_unconditional_deletion_and_unexplained_active_job(self):
        snap = publication(); snap["jobs"] = [job(action="delete", source="scanner_missing")]
        with self.assertRaises(ValueError): CHECK.scope(snap)
        snap["jobs"][0]["payload"]["requiresSourceAbsence"] = True
        CHECK.scope(snap)
        snap["publication"]["activeJobs"] = 1
        with self.assertRaises(ValueError): CHECK.scope(snap)

    def test_failed_jobs_are_failure_but_do_not_block_owned_cleanup_scope(self):
        snap = publication(); snap["jobs"] = [job(status="failed")]
        with self.assertRaises(ValueError): CHECK.scope(snap)
        CHECK.scope(snap, allow_failed=True)

    def test_two_workers_requires_simultaneous_running_matching_distinct_leases(self):
        snap = publication(); self.assertEqual("NON_EXECUTED", CHECK.worker_overlap(snap)["status"])
        snap["jobs"] = [job(0, status="running"), job(1, status="running", consumer=1)]
        self.assertEqual("PASS", CHECK.worker_overlap(snap)["status"])
        snap["jobs"][1]["status"] = "done"
        self.assertEqual("NON_EXECUTED", CHECK.worker_overlap(snap)["status"])
        snap["jobs"][1] = job(1, status="running", consumer=0)
        self.assertEqual("NON_EXECUTED", CHECK.worker_overlap(snap)["status"])
        snap["jobs"][1] = job(1, status="running", consumer=1)
        snap["jobs"][1]["payload"]["executionLeaseOwner"] = "foreign"
        self.assertEqual("NON_EXECUTED", CHECK.worker_overlap(snap)["status"])

    def test_current_publication_uses_independent_physical_page_facts(self):
        _, cases, _, _, _ = inputs()
        snap = publication()
        row = CHECK.current_row(snap, CHECK.TARGETS[0], cases[0])
        self.assertTrue(row["_verifiedPhysicalFacts"])
        artifact = snap["publication"]["documents"][0]["binaryArtifacts"][0]
        bundle = json.loads(gzip.decompress(base64.b64decode(artifact["payloadBase64"])))
        bundle["document"]["pages"][0]["blocks"] = []
        raw = json.dumps(bundle).encode(); compressed = gzip.compress(raw, mtime=0)
        artifact.update(byteSize=len(raw), storedSize=len(compressed), contentHash=hashlib.sha256(raw).hexdigest(),
            payloadBase64=base64.b64encode(compressed).decode())
        with self.assertRaises(ValueError): CHECK.current_row(snap, CHECK.TARGETS[0], cases[0])

    def test_current_plan_checks_physical_uuid_not_only_canonical_payload_id(self):
        owner, cases, config, _, _ = inputs(); snap = publication()
        row = CHECK.current_row(snap, CHECK.TARGETS[0], cases[0])
        CHECK.active_plan(snap, row, owner, config)
        snap["plans"][0]["points"][0]["pointId"] = row["chunks"][0]["retrievalChunkId"]
        with self.assertRaises(ValueError): CHECK.active_plan(snap, row, owner, config)

    def test_watcher_missing_preserves_publication_but_scanner_requires_deleted(self):
        snap = publication(); before = copy.deepcopy(snap["publication"]["documents"][0])
        snap["documentStates"][0].update(status="missing", missingSince="2026-10-26T12:00:00Z")
        self.assertTrue(CHECK.missing_state(snap, CHECK.TARGETS[0], before, watcher_only=True))
        self.assertFalse(CHECK.missing_state(snap, CHECK.TARGETS[0], before, watcher_only=False))
        snap["publication"]["documents"][0]["chunks"][0]["text"] += " changed"
        self.assertFalse(CHECK.missing_state(snap, CHECK.TARGETS[0], before, watcher_only=True))
        snap["documentStates"][0].update(status="deleted", indexedVersion=0, plan=None)
        self.assertTrue(CHECK.missing_state(snap, CHECK.TARGETS[0], before, watcher_only=False))

    def test_automatic_publication_must_advance_without_reusing_old_physical_points(self):
        before = {"docId": "id", "ingestionVersion": 3, "revisionId": "old", "plan": "a"*64}
        after = {"docId": "id", "indexedVersion": 5, "revisionId": "new", "plan": "b"*64,
            "chunks": [{"retrievalChunkId": str(uuid.UUID(int=4))}]}
        CHECK.advanced(before, after, set())
        old_id = MODULE.vector_point_id(after["chunks"][0]["retrievalChunkId"], after["plan"])
        with self.assertRaises(ValueError): CHECK.advanced(before, after, {old_id})
        after["indexedVersion"] = 3
        with self.assertRaises(ValueError): CHECK.advanced(before, after, set())

    def test_conditional_delete_needs_timestamp_window_and_safe_terminal(self):
        snap = publication(); before = {"indexedVersion": 1}
        item = job(status="canceled", action="delete", source="scanner_missing")
        item.update(lastError="scanner_source_returned_before_delete")
        item["payload"]["requiresSourceAbsence"] = True
        snap["jobs"] = [item]
        result = CHECK.returned_delete(snap, item["jobId"], restored_at="2026-10-26T12:00:05Z", before=before)
        self.assertEqual("PASS", result["status"])
        result = CHECK.returned_delete(snap, item["jobId"], restored_at="2026-10-26T12:00:15Z", before=before)
        self.assertEqual("NON_EXECUTED", result["status"])
        item["status"] = "done"
        with self.assertRaises(ValueError): CHECK.returned_delete(snap, item["jobId"], restored_at="2026-10-26T12:00:05Z", before=before)

    def test_requested_trigger_requires_completed_provenance_not_configuration_alone(self):
        snap = publication(); snap["jobs"] = [job(status="queued")]
        with self.assertRaises(ValueError): CHECK.source_attribution(snap, CHECK.TARGETS[0], "scanner")
        snap["jobs"][0]["status"] = "done"
        self.assertEqual(["scanner"], CHECK.source_attribution(snap, CHECK.TARGETS[0], "scanner")["sources"])
        with self.assertRaises(ValueError): CHECK.source_attribution(snap, CHECK.TARGETS[0], "watcher")

    def test_noexecute_never_reads_key_pdf_config_or_constructs_docker(self):
        owner, _, _, _, manifest = inputs()
        _, profile, _, _ = plan_inputs()
        profile["Rag"]["EmbeddingsBaseUrl"] = owner["endpoints"]["tei"]
        args = SimpleNamespace(owner=Path("owner"), manifest=Path("manifest"), embedding_profile=Path("profile"),
            admission_profile="scanner", maximum_wait_seconds=300, execute=False)
        with patch.object(MODULE.corpus, "read_json", side_effect=[owner, manifest, profile]), \
                patch.object(MODULE.corpus, "sha", return_value="0"*64), \
                patch.object(MODULE, "OwnedDocker", side_effect=AssertionError("No Docker")), \
                patch.object(Path, "open", side_effect=AssertionError("No private/source read")):
            self.assertFalse(MODULE.run(args)["executeRequested"])

    def test_transport_rejects_scan_enqueue_vector_write_before_inspection(self):
        owner = inputs()[0]
        docker = SimpleNamespace(inspect_all=lambda: self.fail("No inspection for forbidden route"))
        for role, route, method in (("backend", "/ingestion/scan", "POST"), ("backend", "/ingest/enqueue", "POST"),
            ("qdrant", "/collections/" + owner["qdrantCollection"] + "/points", "PUT"),
            ("backend", "/admin/jobs/cancel", "POST")):
            with self.subTest(route=route), self.assertRaises(ValueError):
                MODULE.owned_http(docker, owner, {}, role, route, method, {}, 100)

    def test_transport_deadline_rechecked_after_slow_ownership_inspection(self):
        owner = inputs()[0]; now = [0.0]
        docker = SimpleNamespace(inspect_all=lambda: now.__setitem__(0, 31.0))
        with patch.object(MODULE.time, "monotonic", side_effect=lambda: now[0]), \
                patch.object(MODULE.urllib.request, "build_opener", side_effect=AssertionError("No HTTP")):
            with self.assertRaises(ValueError): MODULE.owned_http(docker, owner, {}, "backend", "/ready", "GET", None, 30)

    def test_atomic_known_copies_originals_and_removal_scope(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder).resolve(); documents = root / "documents"; sources = root / "sources"
            documents.mkdir(); sources.mkdir(); cases = []
            for i, name in enumerate(CHECK.SOURCE_IDS):
                data = ("fake-source-bytes-" + str(i)).encode()
                (sources / (name + ".pdf")).write_bytes(data)
                cases.append({"caseId": name, "sourceSha256": hashlib.sha256(data).hexdigest()})
            files = MODULE.ControlledFiles(root, documents, sources, cases, lambda: None)
            files.replace(CHECK.TARGETS[0], CHECK.SOURCE_IDS[0]); files.verify()
            files.replace(CHECK.TARGETS[0], CHECK.SOURCE_IDS[1]); files.verify()
            self.assertEqual(1, len(list(documents.iterdir())))
            with self.assertRaises(ValueError): files.remove("other.pdf")
            files.remove(CHECK.TARGETS[0]); files.verify()
            self.assertTrue(all((sources / (c["caseId"] + ".pdf")).is_file() for c in cases))

    def test_partial_preparation_never_overwrites_existing_working_pdf(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder).resolve(); documents = root / "documents"; sources = root / "sources"
            documents.mkdir(); sources.mkdir(); name = CHECK.SOURCE_IDS[0]
            original = b"original-known-bytes"
            (sources / (name + ".pdf")).write_bytes(original)
            cases = [{"caseId": name, "sourceSha256": hashlib.sha256(original).hexdigest()}]
            files = MODULE.ControlledFiles(root, documents, sources, cases, lambda: None)
            files.replace(CHECK.TARGETS[0], name)
            with patch.object(MODULE.shutil, "copyfileobj", side_effect=OSError("synthetic write failure")):
                with self.assertRaises(OSError): files.replace(CHECK.TARGETS[0], name)
            self.assertEqual(original, (documents / CHECK.TARGETS[0]).read_bytes())
            self.assertEqual(1, len(list(documents.iterdir())))

    def test_unknown_working_file_is_never_deleted_or_overwritten(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder).resolve(); documents = root / "documents"; sources = root / "sources"
            documents.mkdir(); sources.mkdir(); name = CHECK.SOURCE_IDS[0]
            (sources / (name + ".pdf")).write_bytes(b"source")
            files = MODULE.ControlledFiles(root, documents, sources,
                [{"caseId": name, "sourceSha256": hashlib.sha256(b"source").hexdigest()}], lambda: None)
            target = documents / CHECK.TARGETS[0]; target.write_bytes(b"unknown")
            with self.assertRaises(ValueError): files.replace(CHECK.TARGETS[0], name)
            with self.assertRaises(ValueError): files.remove(CHECK.TARGETS[0])
            self.assertEqual(b"unknown", target.read_bytes())


if __name__ == "__main__":
    unittest.main()
