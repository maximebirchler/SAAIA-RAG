"""Failure-path orchestration with synthetic files and HTTP; no live services."""
import contextlib
import copy
import hashlib
import json
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch
import urllib.parse
import uuid

sys.path.insert(0, str(Path(__file__).parent))
import invoke_owned_linux_planning_ingestion as ingestion
import linux_owned_runtime as runtime


class LinuxIngestionCleanupTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        for relative in ("documents", "private", "evidence", "deploy", "models"):
            (self.root / relative).mkdir()
        (self.root / "private/api-key.txt").write_text("synthetic-owned-key", encoding="utf-8")
        (self.root / "evidence/planning-bootstrap-origin.json").write_text("{}", encoding="utf-8")
        config_path = self.root / "deploy/deployment.config.json"
        config_path.write_text('{"Rag":{"EmbeddingsModel":"synthetic","EmbeddingsModelRevision":"synthetic","EmbeddingsRuntimeRevision":"synthetic"}}', encoding="utf-8")
        model = self.root / "models/model.bin"
        model.write_bytes(b"synthetic-model")
        self.profile = {"Rag": json.loads(config_path.read_text())["Rag"], "modelFileRelativePath": "model.bin",
                        "modelArtifacts": {"model.bin": ingestion.digest(model)}}
        self.args = SimpleNamespace(output=self.root / "output", source_directory=self.root / "sources",
            maximum_wait_seconds=60, source_manifest=self.root / "manifest.json", embedding_profile=self.root / "profile.json",
            backend_source_revision="1" * 64, backend_sha256="2" * 64, backend_image="sha256:" + "3" * 64)
        self.args.source_manifest.write_text("{}", encoding="utf-8")
        self.args.embedding_profile.write_text(json.dumps(self.profile), encoding="utf-8")
        self.owner = {"root": str(self.root), "runId": str(uuid.uuid4()), "documentsRoot": str(self.root / "documents"),
            "configurationPath": str(config_path), "qdrantCollection": "synthetic-owned-collection",
            "containers": [{"role": "backend", "id": "4" * 64}, {"role": "tei", "id": "5" * 64}],
            "endpoints": {role: f"http://127.0.0.1:{22521+i}" for i, role in enumerate(("backend", "docling", "tei", "qdrant"))},
            "bootstrapPreparation": {"files": {"deploy/deployment.config.json": ingestion.digest(config_path)},
                                     "backendSourceRevision": self.args.backend_source_revision}, "backendResourcePolicy": {}}
        self.backend = {"Id": "4" * 64, "Image": self.args.backend_image}
        self.docker = Mock()
        self.docker.inspect_all.return_value = ([], [self.backend, {"Id": "5" * 64, "Image": "sha256:" + "6" * 64, "Config": {"Cmd": []}}])
        self.docker.command.return_value = self.args.backend_sha256 + " /app/SAAIA.Backend.dll"
        self.docker.sql.return_value = json.dumps({name: 0 for name in ("documents", "jobs", "advancedJobs", "revisions", "chunks", "bundles")})
        self.docker.stop.return_value = {"ownedContainersStopped": True, "containerCount": 5, "volumesDeleted": False, "imagesDeleted": False}
        self.request_paths = []
        self.job_id = str(uuid.uuid4())
        self.stack = contextlib.ExitStack()
        self.addCleanup(self.stack.close)
        self.stack.enter_context(patch.object(ingestion, "OwnedDocker", return_value=self.docker))
        self.stack.enter_context(patch.object(ingestion.urllib.request, "build_opener", return_value=SimpleNamespace(open=self.open)))
        for name in ("validate_backend_resources", "validate_backend_resource_phase", "validate_disabled_llm"):
            self.stack.enter_context(patch.object(ingestion.helpers, name, return_value={}))
        self.stack.enter_context(patch.object(ingestion.helpers, "qualification_identity", return_value={"deploymentQualificationEligible": True}))
        for name in ("attest_info", "attest_tei_container"):
            self.stack.enter_context(patch.object(ingestion, name))
        self.stack.enter_context(patch.object(ingestion, "tei_model_binding", return_value={
            "hostModelRoot": str(self.root / "models"), "containerModelRoot": "/synthetic-models", "modelRevision": "synthetic"}))
        self.stack.enter_context(patch.object(ingestion, "mounted_source", return_value=str(model)))
        self.dependency_dead = False
        self.cancel_error = None

    def open(self, request, *, timeout):
        path = urllib.parse.urlparse(request.full_url).path
        self.request_paths.append(path)
        if path.startswith("/collections/"):
            if self.dependency_dead:
                self.docker.inspect_all.side_effect = ValueError("Every owned runtime container must be running before an observation.")
            raise ValueError("synthetic_original_ingestion_failure")
        if path == "/admin/jobs/cancel" and self.cancel_error:
            raise self.cancel_error
        values = {
            "/ready": {"ok": True, "details": {"canonical_provenance_ready": True, "canonical_artifacts_enabled": True, "tei_dim": 768}},
            "/info": {}, "/health": {"status": "ok", "upstream": True, "regionalTextRepair": {"enabled": True}},
            "/ingestion/jobs": {"items": [{"jobId": self.job_id, "action": "upsert", "docPath": "synthetic.pdf", "status": "running", "attempts": 1}]},
            "/admin/jobs/cancel": {"ok": True},
        }
        response = Mock()
        response.__enter__ = Mock(return_value=response)
        response.__exit__ = Mock(return_value=False)
        response.read.return_value = json.dumps(values[path]).encode()
        return response

    def fail_run(self):
        with self.assertRaisesRegex(ValueError, "real_linux_ingestion_not_accepted_inspect_private_receipt"):
            ingestion.run(self.args, self.owner, self.profile, {"cases": []})
        record = json.loads((self.args.output / "result.json").read_text(encoding="utf-8"))
        self.assertFalse(record["passed"])
        self.assertEqual(record["failureCode"], "synthetic_original_ingestion_failure")
        self.assert_diagnostic(record, "synthetic_original_ingestion_failure")
        return record

    def assert_diagnostic(self, failure, expected_text):
        path = Path(failure["privateDiagnostic"]["path"])
        raw = path.read_bytes()
        self.assertIn(expected_text.encode(), raw)
        self.assertEqual(failure["privateDiagnostic"]["sha256"], hashlib.sha256(raw).hexdigest())

    def test_dead_dependency_still_stops_owned_runtime_and_retains_both_failures(self):
        self.dependency_dead = True
        record = self.fail_run()
        self.docker.stop.assert_called_once_with()
        self.assertTrue(record["cleanup"]["ownedContainersStopped"])
        cancellation = record["cleanup"]["jobCancellation"]
        self.assertTrue(cancellation["attempted"])
        self.assert_diagnostic(cancellation, "Every owned runtime container must be running")
        self.assertNotIn("/ingestion/jobs", self.request_paths)  # Strict admission still refuses the HTTP observation.

    def test_cancel_request_error_does_not_skip_stop_or_replace_ingestion_failure(self):
        self.cancel_error = RuntimeError("synthetic private cancellation detail")
        record = self.fail_run()
        self.docker.stop.assert_called_once_with()
        self.assertIn("/admin/jobs/cancel", self.request_paths)
        self.assertEqual(record["cleanup"]["jobCancellation"]["failureCode"], "real_ingestion_failed")
        self.assert_diagnostic(record["cleanup"]["jobCancellation"], "synthetic private cancellation detail")
        self.assertNotIn("synthetic private cancellation detail", json.dumps(record))

    def test_stop_ownership_refusal_is_recorded_separately_and_does_not_mask_origin(self):
        self.dependency_dead = True
        self.docker.stop.side_effect = ValueError("Container ownership/image/project/service labels do not match.")
        record = self.fail_run()
        self.docker.stop.assert_called_once_with()
        self.assertNotIn("ownedContainersStopped", record["cleanup"])
        self.assert_diagnostic(record["cleanup"]["containerStop"], "Container ownership/image/project/service labels do not match")
        self.assert_diagnostic(record["cleanup"]["jobCancellation"], "Every owned runtime container must be running")

    def test_successful_cancellation_and_stop_remain_visible(self):
        record = self.fail_run()
        self.docker.stop.assert_called_once_with()
        self.assertEqual(record["cleanup"]["jobCancellation"], {"attempted": True, "activeJobCount": 1, "cancelRequestsCompleted": 1})
        self.assertTrue(record["cleanup"]["ownedContainersStopped"])
        self.assertFalse(record["cleanup"]["volumesDeleted"])
        self.assertFalse(record["cleanup"]["imagesDeleted"])

    def test_failure_before_baseline_never_cancels_jobs_but_attempts_safe_stop(self):
        self.docker.inspect_all.side_effect = ValueError("synthetic_original_ingestion_failure")
        record = self.fail_run()
        self.docker.stop.assert_called_once_with()
        self.assertEqual(record["cleanup"]["jobCancellation"], {"attempted": False})
        self.assertEqual(self.request_paths, [])

    def test_cleanup_diagnostic_write_failure_cannot_skip_stop_or_mask_origin(self):
        self.dependency_dead = True
        capture_failure = ingestion.capture_failure
        def failed_cleanup_write(error, output, **kwargs):
            if kwargs:
                raise PermissionError("synthetic private diagnostic write denied")
            return capture_failure(error, output)
        with patch.object(ingestion, "capture_failure", side_effect=failed_cleanup_write):
            record = self.fail_run()
        self.docker.stop.assert_called_once_with()
        self.assertTrue(record["cleanup"]["ownedContainersStopped"])
        cancellation = record["cleanup"]["jobCancellation"]
        self.assertEqual(cancellation["failureType"], "ValueError")
        self.assertEqual(cancellation["privateDiagnosticFailureType"], "PermissionError")
        self.assertNotIn("synthetic private diagnostic write denied", json.dumps(record))


class OwnedDockerCleanupAdmissionTests(unittest.TestCase):
    """Exercise the existing stop/ownership code through a fake Docker transport."""
    def setUp(self):
        run = uuid.uuid4()
        root = "/home/maxime/saaia-ingestion-qualification/" + str(run)
        self.owner = {"schemaVersion": "saaia-linux-ingestion-runtime.v1", "runId": str(run), "root": root,
            "project": "saaiaqual_" + run.hex[:12], "documentsRoot": root + "/documents", "configurationPath": root + "/deploy/deployment.config.json",
            "database": "saaia_qual_" + run.hex[:12], "databaseUser": "saaia_qual_" + run.hex[:12], "qdrantCollection": "ingestion_" + run.hex,
            "endpoints": {role: f"http://127.0.0.1:{22521+i}" for i, role in enumerate(("backend", "docling", "tei", "qdrant"))},
            "containers": [{"role": role, "service": role, "id": str(i + 1) * 64, "imageId": "sha256:" + str(i + 1) * 64}
                           for i, role in enumerate(("postgres", "qdrant", "tei", "docling", "backend"))]}
        self.network = self.owner["project"] + "_validation"
        self.observed = {}
        for record in self.owner["containers"]:
            role = record["role"]
            mounts = []
            if role == "backend":
                mounts = [{"Type": "bind", "Source": self.owner[key], "Destination": destination}
                          for key, destination in (("documentsRoot", "/app/documents"), ("configurationPath", "/app/deployment.config.json"))]
            port_bindings = {} if role == "postgres" else {"8080/tcp": [{"HostIp": "127.0.0.1", "HostPort": str(urllib.parse.urlparse(self.owner["endpoints"][role]).port)}]}
            self.observed[record["id"]] = {"Id": record["id"], "Image": record["imageId"],
                "Config": {"Labels": {"com.docker.compose.project": self.owner["project"], "com.docker.compose.service": record["service"],
                                       runtime.RUN_LABEL: self.owner["runId"]}, "Entrypoint": ["dotnet", "SAAIA.Backend.dll"], "Cmd": [], "Env": []},
                "HostConfig": {"Memory": 1024**3, "NanoCpus": 1_000_000_000, "PortBindings": port_bindings},
                "Mounts": mounts, "NetworkSettings": {"Networks": {self.network: {}}}, "State": {"Running": role != "docling"}}
        self.stopped = []
        self.docker = runtime.OwnedDocker(self.owner, execute=True)
        self.transport = patch.object(self.docker, "command", side_effect=self.command)
        self.transport.start()
        self.addCleanup(self.transport.stop)
        # POSIX host mounts are synthetic; retain every Docker identity, resource,
        # label, network and loopback check while avoiding Windows path resolution.
        paths = patch.object(runtime, "assert_owned_path", side_effect=lambda path, root: path)
        paths.start()
        self.addCleanup(paths.stop)

    def command(self, arguments, **kwargs):
        if arguments[0] == "inspect":
            return json.dumps([copy.deepcopy(self.observed[value]) for value in arguments[1:]])
        if arguments[:2] == ["network", "inspect"]:
            return json.dumps([{"Name": self.network, "Internal": True, "Labels": {"com.docker.compose.project": self.owner["project"]}}])
        if arguments[0] == "stop":
            self.assertEqual(arguments[:3], ["stop", "--time", "45"])
            self.stopped.append(arguments[3])
            self.observed[arguments[3]]["State"]["Running"] = False
            return arguments[3]
        if arguments[:2] == ["ps", "--quiet"]:
            return ""
        raise AssertionError("Unexpected synthetic Docker operation: " + repr(arguments))

    def test_existing_stop_accepts_dead_docling_but_normal_observation_remains_strict(self):
        with self.assertRaisesRegex(ValueError, "Every owned runtime container must be running"):
            self.docker.inspect_all()
        result = self.docker.stop()
        ids = {record["role"]: record["id"] for record in self.owner["containers"]}
        self.assertEqual(self.stopped, [ids[role] for role in ("backend", "tei", "qdrant", "postgres")])
        self.assertTrue(result["ownedContainersStopped"])
        self.assertTrue(result["dockerPublishedPortBindingsAbsent"])
        self.assertFalse(result["volumesDeleted"])
        self.assertFalse(result["imagesDeleted"])

    def test_existing_stop_rejects_wrong_ownership_before_any_stop_command(self):
        docling = next(record for record in self.owner["containers"] if record["role"] == "docling")
        self.observed[docling["id"]]["Image"] = "sha256:" + "9" * 64
        with self.assertRaisesRegex(ValueError, "Container ownership/image/project/service labels do not match"):
            self.docker.stop()
        self.assertEqual(self.stopped, [])


if __name__ == "__main__":
    unittest.main()
