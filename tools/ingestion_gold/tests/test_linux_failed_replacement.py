"""Pure invalid-replacement protocol tests: no live services or real PDFs."""
from __future__ import annotations

import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from .test_linux_lifecycle import inputs, snapshot, DOC_ID, JOB_ID

SCRIPT = Path(__file__).resolve().parents[2] / "validation-ingestion/invoke-linux-failed-replacement.py"
SPEC = importlib.util.spec_from_file_location("failed_replacement_driver_test", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
import linux_failed_replacement_checks as checks


def baseline():
    state, point = snapshot()
    row, plan = state["document"], state["plans"][0]
    row.update({"indexedVersion": 4, "ingestionVersion": 5})
    point["payload"]["ingestion_version"] = 4
    plan["generation"] = 4
    state["watermark"] = 5
    return state, point


def prior_inputs():
    owner, manifest, case, corpus_proof, config = inputs()
    plan = MODULE.validate_plan(owner, corpus_proof, case, owner_sha="0" * 64, manifest_sha="0" * 64)
    state, point = baseline()
    row = state["document"]
    summary = {"searchToReadPassed": True, **{k: row[k] for k in
        ("docId", "revisionId", "indexedVersion", "ingestionVersion", "sourceSha256")}, "planFingerprint": row["plan"]}
    summary.update(MODULE.life.validate_points(row, [point]))
    proof = {"schemaVersion": "saaia-linux-lifecycle-execution.v1", "executed": True, "passed": True,
        "lateWriteReappearanceRemoved": True, "generationsAndPhysicalPointIdsNeverReused": True,
        "paidProviderCalls": 0, "canonicalRowsSeeded": False, "plan": plan, "steps": [summary],
        "cleanup": {k: True for k in ("zeroActiveJobs", "ownedContainersStopped",
                    "dockerPublishedPortBindingsAbsent", "workingSourceRestored")}}
    return owner, manifest, case, corpus_proof, config, plan, proof


class FakeControlled:
    def __init__(self):
        self.truncated = False

    def truncate(self):
        self.truncated = True
        return {"prefixOnly": True, "truncatedByteCount": 16}


class FailureRuntime:
    def __init__(self, status="failed", *, change_attempts=False):
        self.state, self.point = baseline()
        self.status, self.change_attempts = status, change_attempts
        self.time, self.reads, self.captures = 0, 0, []

    def sql(self, query):
        if query != MODULE.life.snapshot_sql():
            raise AssertionError("Only the pre-existing static read-only lifecycle SQL is allowed")
        return json.dumps(self.state)

    def api(self, role, route, method="GET", body=None):
        if role == "qdrant":
            if method != "POST" or body["ids"] != [self.point["id"]]:
                raise AssertionError("Only exact captured current point observations")
            return {"status": "ok", "result": [copy.deepcopy(self.point)]}
        if route == "/ingest/enqueue":
            if body != {"docPath": MODULE.DOC_PATH, "action": "upsert"}:
                raise AssertionError("Wrong mutation scope")
            self.state["document"].update({"ingestionVersion": 6, "status": "error"})
            self.state["watermark"] = 6
            self.state["activeJobs"] = int(self.status in {"queued", "running", "paused"})
            return {"jobId": JOB_ID, "tenantId": self.state["document"]["tenantId"], "docId": DOC_ID,
                    "action": "upsert", "docPath": MODULE.DOC_PATH, "version": 6}
        if route.startswith("/ingestion/jobs"):
            self.reads += 1
            return {"items": [{"JobId": JOB_ID, "Action": "upsert", "DocPath": MODULE.DOC_PATH,
                "Status": self.status, "Attempts": 1 + int(self.change_attempts and self.reads > 1)}]}
        raise AssertionError("No canonical search/read against corrupt source bytes is required")

    def lifecycle(self):
        owner, _, case, _, config = inputs()
        life = MODULE.life.Lifecycle(owner, case, config, self, self.api,
            lambda name, value: self.captures.append((name, copy.deepcopy(value))), maximum_wait=2,
            clock=lambda: self.time, sleep=lambda seconds: setattr(self, "time", self.time + seconds))
        life.doc_id = DOC_ID
        return life


class FailedReplacementTests(unittest.TestCase):
    def test_prior_real_lifecycle_pass_is_required_and_binds_exact_namespace(self):
        *_, plan, proof = prior_inputs()
        self.assertEqual(4, checks.validate_lifecycle_proof(proof, plan)["indexedVersion"])
        for field in ("executed", "passed", "lateWriteReappearanceRemoved"):
            changed = copy.deepcopy(proof); changed[field] = False
            with self.subTest(field=field), self.assertRaises(ValueError):
                checks.validate_lifecycle_proof(changed, plan)
        changed = copy.deepcopy(proof); changed["plan"]["ownerManifestSha256"] = "f" * 64
        with self.assertRaises(ValueError):
            checks.validate_lifecycle_proof(changed, plan)

    def test_failed_previous_cleanup_cannot_become_a_replacement_baseline(self):
        *_, plan, proof = prior_inputs()
        proof["cleanup"]["zeroActiveJobs"] = False
        with self.assertRaises(ValueError):
            checks.validate_lifecycle_proof(proof, plan)

    def test_baseline_keeps_published_four_after_canceled_ingestion_five(self):
        *_, plan, proof = prior_inputs()
        row = baseline()[0]["document"]
        checks.bind_baseline(row, checks.validate_lifecycle_proof(proof, plan))
        changed = copy.deepcopy(row); changed["ingestionVersion"] = 4
        with self.assertRaises(ValueError):
            checks.bind_baseline(changed, proof["steps"][-1])

    def test_failed_generation_six_preserves_publication_four_without_search_on_corrupt_bytes(self):
        runtime = FailureRuntime()
        before, point = copy.deepcopy(runtime.state["document"]), copy.deepcopy(runtime.point)
        fault = FakeControlled()
        result = checks.exercise_failure(runtime.lifecycle(), fault, before, [point], stable_seconds=5)
        self.assertTrue(fault.truncated)
        self.assertEqual(("PASS", "failed", 6, 1), (result["status"], result["terminal"], result["generation"], result["attempts"]))
        self.assertEqual(4, runtime.state["document"]["indexedVersion"])
        self.assertFalse(result["currentSourceFreshnessClaimed"])
        self.assertGreaterEqual(runtime.time, 5)

    def test_success_cancellation_and_pause_do_not_count_as_expected_failed_replacement(self):
        for status in ("done", "canceled", "paused", "running"):
            runtime = FailureRuntime(status)
            with self.subTest(status=status), self.assertRaises(ValueError):
                checks.exercise_failure(runtime.lifecycle(), FakeControlled(), copy.deepcopy(runtime.state["document"]),
                                        [copy.deepcopy(runtime.point)], stable_seconds=5)
            self.assertLessEqual(runtime.time, 2)

    def test_restarting_failed_job_or_attempt_changes_are_rejected(self):
        runtime = FailureRuntime(change_attempts=True)
        with self.assertRaises(ValueError):
            checks.exercise_failure(runtime.lifecycle(), FakeControlled(), copy.deepcopy(runtime.state["document"]),
                                    [copy.deepcopy(runtime.point)], stable_seconds=5)

    def test_excess_attempts_or_active_job_prevent_terminal_acceptance(self):
        state, _ = baseline(); state["document"]["ingestionVersion"] = 6
        for attempts, active in ((0, 0), (4, 0), (1, 1)):
            state["activeJobs"] = active
            with self.subTest(attempts=attempts, active=active), self.assertRaises(ValueError):
                checks.failed_terminal({"status": "failed", "attempts": attempts}, state, 6)

    def test_unchanged_sql_does_not_hide_missing_or_modified_physical_vector(self):
        state, point = baseline(); before = copy.deepcopy(state["document"])
        state["document"]["ingestionVersion"] = 6; state["watermark"] = 6
        for points in ([], [{**point, "vector": [0.02] * 768}], [{**point, "payload": {**point["payload"], "extra": "modified"}}]):
            with self.subTest(points=len(points)), self.assertRaises(ValueError):
                checks.preserved_publication(state, before, [point], points, 6)

    def test_changed_revision_chunks_or_retired_active_plan_are_refused(self):
        state, point = baseline(); before = copy.deepcopy(state["document"])
        state["document"]["ingestionVersion"] = 6; state["watermark"] = 6
        for field in ("revisionId", "chunks", "plan"):
            changed = copy.deepcopy(state); changed["document"][field] = "changed"
            with self.subTest(field=field), self.assertRaises(ValueError):
                checks.preserved_publication(changed, before, [point], [point], 6)
        state["plans"][0]["retiredAt"] = "2026-10-26T00:00:00Z"
        with self.assertRaises(ValueError):
            checks.preserved_publication(state, before, [point], [point], 6)

    def test_only_known_working_copy_is_truncated_and_restored_exactly(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); source, target = root / "original.pdf", root / "working.pdf"
            original = b"%PDF-1.7\n" + b"synthetic fixture bytes" * 40
            source.write_bytes(original); target.write_bytes(original)
            controlled = checks.ControlledPdfCopy(source, target, hashlib.sha256(original).hexdigest(), lambda: None)
            result = controlled.truncate()
            self.assertEqual(original[:16], target.read_bytes())
            self.assertEqual(original, source.read_bytes())
            self.assertTrue(result["prefixOnly"])
            controlled.restore()
            self.assertEqual(original, target.read_bytes())

    def test_unknown_working_bytes_or_restore_source_are_never_overwritten(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); source, target = root / "original.pdf", root / "working.pdf"
            original = b"%PDF-1.7\n" + b"x" * 1024
            source.write_bytes(original); target.write_bytes(original)
            controlled = checks.ControlledPdfCopy(source, target, hashlib.sha256(original).hexdigest(), lambda: None)
            controlled.truncate(); target.write_bytes(b"unknown operator file")
            with self.assertRaises(ValueError): controlled.restore()
            self.assertEqual(b"unknown operator file", target.read_bytes())
            source.write_bytes(b"changed source")
            with self.assertRaises(ValueError): controlled.restore()

    def test_failed_temporary_write_leaves_working_source_intact_and_restorable(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); source, target = root / "original.pdf", root / "working.pdf"
            original = b"%PDF-1.7\n" + b"x" * 1024
            source.write_bytes(original); target.write_bytes(original)
            controlled = checks.ControlledPdfCopy(source, target, hashlib.sha256(original).hexdigest(), lambda: None)
            real_open = Path.open
            def fail_prepare(path, *args, **kwargs):
                if ".qualification-" in path.name:
                    raise OSError("synthetic prepare failure")
                return real_open(path, *args, **kwargs)
            with patch.object(Path, "open", fail_prepare), self.assertRaises(OSError):
                controlled.truncate()
            self.assertEqual(original, target.read_bytes())
            controlled.restore()
            self.assertEqual(original, target.read_bytes())
            self.assertEqual([], list(root.glob("*.qualification-*")))

    def test_driver_recovers_fresh_and_closes_owned_runtime_even_when_failure_probe_is_interrupted(self):
        for interrupted in (False, True):
            with self.subTest(interrupted=interrupted), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                owner, manifest, case, prior_corpus, config, plan, proof = prior_inputs()
                owner.update({"root": str(root), "documentsRoot": str(root / "documents"),
                              "configurationPath": str(root / "config.json")})
                (root / "documents").mkdir(); (root / "sources").mkdir(); (root / "evidence").mkdir()
                source, target = root / "sources" / MODULE.DOC_PATH, root / "documents" / MODULE.DOC_PATH
                original = b"%PDF-1.7\n" + b"synthetic source" * 100
                source.write_bytes(original); target.write_bytes(original)
                case["sourceSha256"] = hashlib.sha256(original).hexdigest()
                plan["sourceSha256"] = case["sourceSha256"]
                proof["plan"] = plan; proof["steps"][-1]["sourceSha256"] = case["sourceSha256"]
                config.update({"VectorGc": {"Enabled": True, "PollSeconds": 1, "RecheckSeconds": 2}})
                config["Rag"]["QdrantBaseUrl"] = owner["endpoints"]["qdrant"]
                state, point = baseline(); state["document"]["sourceSha256"] = case["sourceSha256"]
                class FakeDocker:
                    stopped = False
                    def __init__(self, *args, **kwargs): pass
                    def inspect_all(self):
                        return [], [{"Id": record["id"]} for record in owner["containers"]]
                    def command(self, command):
                        if command[:1] != ["exec"]: raise AssertionError("No unowned CLI command")
                        return "a" * 64 if "sha256sum" in command else ""
                    def stop(self):
                        self.stopped = True
                        return {"ownedContainersStopped": True, "dockerPublishedPortBindingsAbsent": True}
                class FakeLifecycle:
                    def __init__(self, *args, **kwargs):
                        self.steps, self.history_ids = [], set()
                        self.source_check = kwargs["source_check"]
                    def api(self, role, route):
                        if route != "/ready": raise AssertionError("Unexpected mock API")
                        return {"ok": True, "details": {"vector_gc_enabled": True}}
                    def publication(self, label):
                        self.source_check()
                        return copy.deepcopy(state["document"]), state["plans"][0], [copy.deepcopy(point)]
                    def unrelated_vectors(self): return {"pointCount": 0, "vectorsAndPayloadSha256": "f" * 64}
                    def snapshot(self): return copy.deepcopy(state)
                    def upsert(self, previous, label):
                        self.source_check()
                        self.assert_restored = target.read_bytes() == original
                        if not self.assert_restored: raise AssertionError("Recovery ran against invalid source")
                        if previous["ingestionVersion"] != 6: raise AssertionError("Recovery did not advance past failed generation6")
                        row = copy.deepcopy(state["document"])
                        row.update({"indexedVersion": 7, "ingestionVersion": 7, "revisionId": "new-revision", "plan": "b" * 64})
                        state["document"] = row
                        return row, {}, []
                    def cleanup_jobs(self, scope_check):
                        scope_check()
                        self.source_check = scope_check
                        return {"zeroActiveJobs": True, "onlyNewScopedJobsCanceled": True}
                def fault(lifecycle, controlled, *args, **kwargs):
                    controlled.truncate()
                    state["document"]["ingestionVersion"] = 6
                    if interrupted: raise ValueError("synthetic interrupted observation")
                    return {"status": "PASS", "generation": 6}
                args = SimpleNamespace(owner=root / "owner.json", manifest=root / "manifest.json",
                    corpus_proof=root / "corpus" / "execution-result.json", lifecycle_proof=root / "lifecycle" / "lifecycle-result.json",
                    source_directory=root / "sources", output=root / "evidence" / "replacement", api_key_file=root / "key.txt",
                    maximum_wait_seconds=30, terminal_stability_seconds=5, execute=True)
                args.api_key_file.write_text("synthetic-bootstrap-key", encoding="utf-8")
                public = {args.owner: owner, args.manifest: manifest, args.corpus_proof: prior_corpus,
                    args.lifecycle_proof: proof, Path(owner["configurationPath"]): config,
                    args.lifecycle_proof.parent / "runtime-identity.json": {"configurationSha256": "0" * 64, "backendAssemblySha256": "a" * 64},
                    args.corpus_proof.parent / "runtime-ownership.json": {"signedConfigurationSha256": "0" * 64},
                    args.corpus_proof.parent / "backend-binary-identity.json": {"assemblySha256": "a" * 64}}
                docker = FakeDocker()
                with patch.object(MODULE.corpus, "read_json", side_effect=lambda path: public[path]), \
                     patch.object(MODULE.corpus, "sha", return_value="0" * 64), \
                     patch.object(MODULE, "validate_plan", return_value=plan), \
                     patch.object(MODULE, "validate_manifest", return_value=[case]), \
                     patch.object(MODULE.corpus, "validate_disabled_llm"), \
                     patch.object(MODULE.corpus, "qualification_identity", return_value={"deploymentQualificationEligible": False}), \
                     patch.object(MODULE, "OwnedDocker", return_value=docker), \
                     patch.object(MODULE.life, "Lifecycle", FakeLifecycle), \
                     patch.object(MODULE, "exercise_failure", side_effect=fault), \
                     patch.object(MODULE.sys, "platform", "linux"):
                    result = MODULE.run(args)
                self.assertEqual(not interrupted, result["passed"])
                self.assertTrue(result["cleanup"]["workingSourceRestored"])
                self.assertTrue(docker.stopped)
                self.assertEqual(original, target.read_bytes())
                self.assertEqual(7, state["document"]["indexedVersion"])
                self.assertFalse(result["deploymentQualificationPassed"])
                if interrupted:
                    self.assertEqual("PASS", result["cleanup"]["emergencyRecovery"]["status"])

    def test_noexecute_reads_only_public_manifests_and_never_constructs_runtime_or_reads_key(self):
        owner, manifest, _, corpus_proof, _, _, proof = prior_inputs()
        args = SimpleNamespace(owner=Path("owner.json"), manifest=Path("manifest.json"), corpus_proof=Path("corpus.json"),
            lifecycle_proof=Path("lifecycle.json"), maximum_wait_seconds=30, terminal_stability_seconds=5, execute=False,
            api_key_file=Path("forbidden-key"), output=Path("forbidden-output"), source_directory=Path("forbidden-docs"))
        public = {args.owner: owner, args.manifest: manifest, args.corpus_proof: corpus_proof, args.lifecycle_proof: proof}
        with patch.object(MODULE.corpus, "read_json", side_effect=lambda path: public[path]), \
             patch.object(MODULE.corpus, "sha", return_value="0" * 64), \
             patch.object(MODULE, "OwnedDocker", side_effect=AssertionError("No runtime in NOEXEC")), \
             patch.object(Path, "read_bytes", side_effect=AssertionError("No source/key in NOEXEC")):
            result = MODULE.run(args)
        self.assertFalse(result["executeRequested"])
        self.assertEqual((4, 5), (result["previousIndexedVersion"], result["previousIngestionVersion"]))


if __name__ == "__main__":
    unittest.main()
