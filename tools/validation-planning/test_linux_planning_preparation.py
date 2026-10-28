"""Offline bootstrap/NOEXEC boundaries, using copies and synthetic file contents."""
import contextlib
import ast
import hashlib
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch, Mock
import uuid
from types import SimpleNamespace
import copy

sys.path.insert(0, str(Path(__file__).parent))
import new_owned_linux_ingestion as bootstrap
import invoke_owned_linux_planning_ingestion as ingestion
import linux_planning_contracts as contracts

class LinuxPlanningPreparationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()

    def fixture_inputs(self):
        run = uuid.uuid4(); root = "/home/maxime/saaia-ingestion-qualification/" + str(run)
        owner = {"schemaVersion": "saaia-linux-ingestion-runtime.v1", "runId": str(run), "root": root,
            "project": "saaiaqual_" + run.hex[:12], "documentsRoot": root + "/documents", "configurationPath": root + "/deploy/deployment.config.json",
            "database": "saaia_qual_" + run.hex[:12], "databaseUser": "saaia_qual_" + run.hex[:12], "qdrantCollection": "ingestion_" + run.hex,
            "endpoints": {role: f"http://127.0.0.1:{22521+i}" for i, role in enumerate(("backend", "docling", "tei", "qdrant"))},
            "containers": [{"role": role, "service": role, "id": str(i + 1) * 64, "imageId": "sha256:" + str(i + 1) * 64}
                           for i, role in enumerate(("postgres", "qdrant", "tei", "docling", "backend"))],
            "bootstrapPreparation": {"backendSourceRevision": "0" * 64, "doclingSourceRevision": "2" * 64,
                "dependencyImages": {role: "sha256:" + str(i + 1) * 64
                                     for i, role in enumerate(("postgres", "qdrant", "tei", "docling"))}}}
        profile = {"schemaVersion": "saaia-tei-embedding-profile.v1", "adapter": "tei_cpu", "dimensions": 768,
            "imageId": "sha256:" + "3" * 64, "modelFileSha256": "b" * 64, "truncationPolicy": "reject",
            "expectedInfo": {"model_id": "intfloat/multilingual-e5-base", "version": "synthetic", "model_sha": "a" * 40, "max_input_length": 512},
            "Rag": {"EmbeddingsBaseUrl": owner["endpoints"]["tei"], "EmbeddingsModel": "intfloat/multilingual-e5-base",
                    "EmbeddingsModelRevision": "a" * 40, "EmbeddingsRuntimeRevision": "sha256:" + "3" * 64}}
        manifest = {"schemaVersion": "ingestion_real_acceptance_v1", "newWorkerObservationSeenDuringRegistration": False,
                    "cases": [{"caseId": f"synthetic-{i}"} for i in range(4)]}
        result = []
        for name, value in (("owner", owner), ("profile", profile), ("manifest", manifest)):
            target = self.root / (name + ".json"); target.write_text(json.dumps(value), encoding="utf-8"); result.append(target)
        return result

    def test_every_actual_runner_helper_reference_resolves_to_a_callable(self):
        tree = ast.parse(Path(ingestion.__file__).read_text(encoding="utf-8"))
        names = {node.attr for node in ast.walk(tree) if isinstance(node, ast.Attribute)
                 and isinstance(node.value, ast.Name) and node.value.id == "helpers"}
        self.assertEqual(names, {"parse_job_listing", "validate_disabled_llm", "qualification_identity", "synthetic_admin_headers", "validate_full_document_timeouts",
                                "validate_backend_resource_phase", "validate_backend_resources"})
        for name in names:
            self.assertTrue(callable(getattr(contracts, name, None)), name)

    def test_default900_keeps_the_pinned_bootstrap_constants_unchanged(self):
        module=SimpleNamespace(ENV={"docling":["CPU=3","DOCLING_SERVE_MAX_SYNC_WAIT=900","SAAIA_DOCLING_UPSTREAM_TIMEOUT_SECONDS=900"]},REGIONAL_CONFIG={"NumThreads":3})
        before=copy.deepcopy(vars(module));policy=bootstrap.configure_full_document_timeout(module,900)
        self.assertEqual(vars(module),before);self.assertTrue(policy["default900Unchanged"])

    def test_actual_hash_pinned_v6_policy_accepts_only_the_declared_signed_timeout(self):
        source=Path(__file__).resolve().parents[1]/"validation-ingestion/prepare_owned_linux_bootstrap_v6.py"
        self.assertTrue(source.is_file(), "The public byte-pinned bootstrap must ship in a fresh checkout.")
        module=bootstrap.load_module(source,"23f201c39c4d447979d44ae223a1447d382e997ab396b9bf0f816cd0f37046a0","pure_v6_timeout_test")
        bootstrap.configure_full_document_timeout(module,3600)
        config={"DocumentIntelligence":{**module.REGIONAL_CONFIG,"DeploymentRevision":"synthetic"},
                "Ingestion":{"WorkerEnabled":True,"ScannerEnabled":False,"WatcherEnabled":False,"WorkerConcurrency":1},
                "AdvancedAnalysis":{"VisualSourceCitationsEnabled":True,"NativeObservationReadEnabled":True,"WorkerEnabled":False,"Provider":"disabled","AllowExternalProviderContent":False,"AllowExternalProviderMetadata":False},
                "License":{"AdvancedAnalysisEnabled":False},"RuntimeGovernance":{"CapabilityBWorkerEnabled":False}}
        module.validate_v6_config(config,"synthetic")
        self.assertEqual(module.LIMITS["docling"],("3","7g"))
        config["DocumentIntelligence"]["TimeoutSeconds"]=900
        with self.assertRaisesRegex(ValueError,"v6_docling_signed_settings_changed"):module.validate_v6_config(config,"synthetic")

    def test_full_document_overlay_changes_only_three_reviewed_constants(self):
        module=SimpleNamespace(ENV={"docling":["CPU=3","DOCLING_SERVE_MAX_SYNC_WAIT=900","SAAIA_DOCLING_UPSTREAM_TIMEOUT_SECONDS=900","QUALITY=true"]},REGIONAL_CONFIG={"NumThreads":3,"RegionalTextRepairTotalTimeoutSeconds":60})
        policy=bootstrap.configure_full_document_timeout(module,3600)
        self.assertEqual(module.ENV["docling"],["CPU=3","DOCLING_SERVE_MAX_SYNC_WAIT=3900","SAAIA_DOCLING_UPSTREAM_TIMEOUT_SECONDS=3900","QUALITY=true"])
        self.assertEqual(module.REGIONAL_CONFIG,{"NumThreads":3,"RegionalTextRepairTotalTimeoutSeconds":60,"TimeoutSeconds":3600})
        self.assertEqual(policy["proxyMarginSeconds"],300)
        for invalid in (0,901,7200,True,"3600"):
            with self.assertRaisesRegex(ValueError,"reviewed_full_document_timeout"):bootstrap.configure_full_document_timeout(module,invalid)

    def test_unexpected_or_duplicate_original_timeout_never_gets_silently_rewritten(self):
        for entries in (["DOCLING_SERVE_MAX_SYNC_WAIT=100","SAAIA_DOCLING_UPSTREAM_TIMEOUT_SECONDS=900"],
                        ["DOCLING_SERVE_MAX_SYNC_WAIT=900","DOCLING_SERVE_MAX_SYNC_WAIT=900","SAAIA_DOCLING_UPSTREAM_TIMEOUT_SECONDS=900"]):
            module=SimpleNamespace(ENV={"docling":entries},REGIONAL_CONFIG={})
            with self.assertRaisesRegex(ValueError,"pinned_v6_timeout_constants_changed"):bootstrap.configure_full_document_timeout(module,3600)

    def test_all_three_real_runtime_timeout_values_are_required_and_cross_checked(self):
        policy={"backendFullDocumentTimeoutSeconds":3600,"doclingUpstreamTimeoutSeconds":3900,"doclingMaximumSyncWaitSeconds":3900}
        config={"DocumentIntelligence":{"TimeoutSeconds":3600}}
        container={"Config":{"Env":["DOCLING_SERVE_MAX_SYNC_WAIT=3900","SAAIA_DOCLING_UPSTREAM_TIMEOUT_SECONDS=3900"]}}
        contracts.validate_full_document_timeouts(policy,config,container)
        bad=copy.deepcopy(config);bad["DocumentIntelligence"]["TimeoutSeconds"]=900
        with self.assertRaisesRegex(ValueError,"timeout_contract_changed"):contracts.validate_full_document_timeouts(policy,bad,container)
        for index in (0,1):
            bad=copy.deepcopy(container);bad["Config"]["Env"][index]=bad["Config"]["Env"][index].replace("3900","900")
            with self.assertRaisesRegex(ValueError,"timeout_contract_changed"):contracts.validate_full_document_timeouts(policy,config,bad)

    def test_synthetic_admin_key_is_sent_in_both_required_headers(self):
        self.assertEqual(contracts.synthetic_admin_headers("synthetic-owned-key"),
                         {"X-Api-Key": "synthetic-owned-key", "X-Admin-Key": "synthetic-owned-key"})

    def test_private_failure_preserves_original_traceback_but_public_code_is_bounded(self):
        try:
            raise AttributeError("Synthetic absent helper")
        except AttributeError as error:
            record = ingestion.capture_failure(error, self.root)
        raw = (self.root / "failure-traceback.private.txt").read_bytes()
        self.assertIn(b"AttributeError: Synthetic absent helper", raw)
        self.assertIn(b"Traceback (most recent call last)", raw)
        self.assertEqual(record["failureCode"], "real_ingestion_failed")
        self.assertEqual(record["privateDiagnostic"]["sha256"], hashlib.sha256(raw).hexdigest())
        with self.assertRaises(FileExistsError):
            ingestion.capture_failure(ValueError("do_not_overwrite"), self.root)

    def test_public_failure_does_not_expose_arbitrary_exception_text(self):
        record = ingestion.capture_failure(ValueError("a private path with spaces"), self.root)
        self.assertEqual(record["failureCode"], "real_ingestion_failed")

    def test_fresh_scope_preserves_complete_uuid(self):
        target = self.root / str(uuid.uuid4())
        with patch.object(bootstrap, "PARENT", self.root):
            owner = bootstrap.fresh_owner(target, 22521, "10.238.29.0/24")
        self.assertEqual(owner["qdrantCollection"], "ingestion_" + uuid.UUID(target.name).hex)
        self.assertEqual(owner["containers"], [])
        self.assertFalse(target.exists())

    def test_existing_root_and_standard_postgres_port_are_rejected(self):
        target = self.root / str(uuid.uuid4()); target.mkdir()
        with patch.object(bootstrap, "PARENT", self.root):
            with self.assertRaises(ValueError):
                bootstrap.fresh_owner(target, 22521, "10.238.29.0/24")
            with self.assertRaises(ValueError):
                bootstrap.fresh_owner(self.root / str(uuid.uuid4()), 5431, "10.238.29.0/24")

    def test_cache_copy_requires_both_models_and_detects_one_byte_change(self):
        (self.root / "tei").mkdir(); (self.root / "docling").mkdir()
        (self.root / "tei/model").write_bytes(b"synthetic-e5")
        (self.root / "docling/model").write_bytes(b"synthetic-docling")
        first = bootstrap.cache_inventory(self.root)
        (self.root / "docling/model").write_bytes(b"synthetic-doclinG")
        self.assertNotEqual(first, bootstrap.cache_inventory(self.root))

    def test_unpinned_library_is_not_imported(self):
        source = self.root / "never_import.py"
        source.write_text("raise RuntimeError('import executed')", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "pinned_bootstrap_or_cache_library_changed"):
            bootstrap.load_module(source, "0" * 64, "not_executed")

    def test_bootstrap_noexecute_never_imports_pinned_executable_libraries(self):
        source = self.root / "library.py"
        source.write_text("raise RuntimeError('must not execute library')", encoding="utf-8")
        sha = hashlib.sha256(source.read_bytes()).hexdigest()
        owner_path, profile, _ = self.fixture_inputs()
        args = ["bootstrap", "--phase", "backend", "--root", "/home/maxime/saaia-ingestion-qualification/" + str(uuid.uuid4()),
            "--source-owner", str(owner_path), "--source-owner-sha256", hashlib.sha256(owner_path.read_bytes()).hexdigest(),
            "--bootstrap", str(source), "--bootstrap-sha256", sha, "--cache-library", str(source), "--cache-library-sha256", sha,
            "--source-embedding-profile", str(profile), "--source-embedding-profile-sha256", hashlib.sha256(profile.read_bytes()).hexdigest(),
            "--backend-image", "sha256:" + "9" * 64, "--backend-source-revision", "8" * 64]
        for options, expected_memory in (([], 2), (["--backend-memory-gib", "3"], 3),
                                         (["--docling-image", "sha256:" + "a" * 64, "--docling-source-revision", "b" * 64], 2)):
            output = io.StringIO()
            with patch.object(sys, "argv", args + options), patch.object(bootstrap, "load_module", side_effect=AssertionError("runtime gate crossed")), contextlib.redirect_stdout(output):
                bootstrap.main()
            result = json.loads(output.getvalue())
            self.assertFalse(result["dockerCalled"])
            self.assertEqual(result["backendResourcePolicy"], contracts.backend_resource_policy(expected_memory))
            self.assertEqual(result["fullDocumentTimeoutSeconds"], 900)
            self.assertEqual(result["doclingImage"], "sha256:" + ("a" if "--docling-image" in options else "4") * 64)
            self.assertFalse(result["doclingImageVerified"])
        for partial in (["--docling-image", "sha256:" + "a" * 64], ["--docling-source-revision", "b" * 64]):
            for execution in ([], ["--execute"]):
                with patch.object(sys, "argv", args + partial + execution), \
                     patch.object(bootstrap, "load_module", side_effect=AssertionError("runtime gate crossed")), \
                     patch.object(bootstrap, "OwnedDocker", side_effect=AssertionError("Docker gate crossed")), \
                     self.assertRaisesRegex(ValueError, "pair_required"):
                    bootstrap.main()

    def test_docling_override_requires_a_complete_immutable_pair(self):
        source = json.loads(self.fixture_inputs()[0].read_text())
        for image, revision in (("sha256:" + "a" * 64, None), (None, "b" * 64)):
            with self.subTest(image=image, revision=revision), self.assertRaisesRegex(ValueError, "pair_required"):
                bootstrap.execute(SimpleNamespace(docling_image=image, docling_source_revision=revision), source, b"unused")
        for image, revision in (("docling:latest", "b" * 64), ("sha256:" + "a" * 64, "commit-not-source-hash")):
            with self.subTest(image=image), self.assertRaisesRegex(ValueError, "immutable_docling"):
                bootstrap.select_docling_image(SimpleNamespace(docling_image=image, docling_source_revision=revision), source)
        selected = bootstrap.select_docling_image(SimpleNamespace(), source)
        self.assertEqual(selected, {"imageId": "sha256:" + "4" * 64, "sourceRevision": "2" * 64, "selection": "source_owner"})

    def test_docling_phase_rejects_rebinding_and_keeps_legacy_default_origins(self):
        source = json.loads(self.fixture_inputs()[0].read_text())
        selected = bootstrap.select_docling_image(SimpleNamespace(docling_image="sha256:" + "a" * 64, docling_source_revision="b" * 64), source)
        origin = bootstrap.dependency_image_origin(source, selected)
        owner = {"bootstrapPreparation": {"dependencyImages": copy.deepcopy(origin["dependencyImages"]), "doclingSourceRevision": "b" * 64}}
        self.assertNotIn("docling", origin["dependencyImagesReused"])
        self.assertEqual(origin["sourceDependencyImages"]["docling"], "sha256:" + "4" * 64)
        bootstrap.validate_docling_image_phase(owner, origin, source, selected)
        for key in ("dependencyImages", "doclingSourceRevision"):
            changed = copy.deepcopy(owner)
            changed["bootstrapPreparation"][key] = source["bootstrapPreparation"][key]
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, "preparation_identity_changed"):
                bootstrap.validate_docling_image_phase(changed, origin, source, selected)
        changed = copy.deepcopy(origin); changed["dependencyImagesReused"]["docling"] = source["bootstrapPreparation"]["dependencyImages"]["docling"]
        with self.assertRaisesRegex(ValueError, "origin_changed"):
            bootstrap.validate_docling_image_phase(owner, changed, source, selected)
        with self.assertRaisesRegex(ValueError, "preparation_identity_changed"):
            bootstrap.validate_docling_image_phase(owner, origin, source, bootstrap.select_docling_image(SimpleNamespace(), source))
        legacy_selected = bootstrap.select_docling_image(SimpleNamespace(), source)
        legacy_owner = {"bootstrapPreparation": copy.deepcopy(source["bootstrapPreparation"])}
        legacy_origin = {"dependencyImagesReused": source["bootstrapPreparation"]["dependencyImages"]}
        bootstrap.validate_docling_image_phase(legacy_owner, legacy_origin, source, legacy_selected)
        changed_source = copy.deepcopy(source); changed_source["bootstrapPreparation"]["dependencyImages"]["docling"] = "sha256:" + "f" * 64
        with self.assertRaisesRegex(ValueError, "source_dependency_images"):
            bootstrap.dependency_image_origin(changed_source, selected)

    def test_prepare_verifies_actual_docling_label_before_copy_and_passes_new_identity(self):
        source_owner_path, profile, _ = self.fixture_inputs()
        source = json.loads(source_owner_path.read_text())
        module_path = Path(__file__).resolve().parents[1] / "validation-ingestion/prepare_owned_linux_bootstrap_v6.py"
        module_hash = "23f201c39c4d447979d44ae223a1447d382e997ab396b9bf0f816cd0f37046a0"
        for matches in (False, True):
            with self.subTest(labelMatches=matches):
                module = bootstrap.load_module(module_path, module_hash, "synthetic_docling_image_admission")
                target = self.root / str(uuid.uuid4())
                args = SimpleNamespace(root=target, phase="prepare", bootstrap=module_path, bootstrap_sha256=module_hash,
                    full_document_timeout_seconds=900, backend_memory_gib=2, docling_memory_profile="legacy",
                    port_base=22561, network_subnet="10.238.33.0/24", source_owner=source_owner_path, source_owner_sha256="c" * 64,
                    source_embedding_profile=profile, source_embedding_profile_sha256="d" * 64,
                    backend_image="sha256:" + "9" * 64, backend_source_revision="8" * 64,
                    docling_image="sha256:" + "a" * 64, docling_source_revision="b" * 64,
                    cache_library=Path("synthetic-cache-library"), cache_library_sha256="e" * 64)
                events = []
                def image_inspect(*arguments):
                    self.assertEqual(arguments[:2], ("image", "inspect"))
                    image = arguments[2]; role = "docling" if image == args.docling_image else "backend"
                    events.append("inspect-" + role)
                    revision = (args.docling_source_revision if matches else "f" * 64) if role == "docling" else args.backend_source_revision
                    return json.dumps([{"Id": image, "Config": {"Labels": {module.IMAGE_LABELS[role]: revision}}}])
                docker = SimpleNamespace(call=Mock(side_effect=image_inspect))
                cache = SimpleNamespace(copy_cache=Mock(side_effect=lambda image: events.append("copy-cache")))
                def prepare_runtime(passed):
                    self.assertEqual(passed.docling_image_id, args.docling_image)
                    self.assertEqual(passed.expected_docling_source_revision, args.docling_source_revision)
                    events.append("prepare")
                    return {"prepared": True}
                module.run = Mock(side_effect=prepare_runtime)
                module.Docker = Mock(return_value=docker)
                inventory = {"tei/model": {"bytes": 1}, "docling/model": {"bytes": 1}}
                with patch.object(bootstrap, "PARENT", self.root), patch.object(bootstrap, "load_module", side_effect=[module, cache]), \
                     patch.object(bootstrap, "OwnedDocker"), patch.object(bootstrap, "assert_source_unchanged"), \
                     patch.object(bootstrap, "cache_inventory", return_value=inventory), \
                     patch.object(bootstrap.shutil, "disk_usage", return_value=SimpleNamespace(free=10 * 1024**3)), \
                     patch.object(bootstrap.socket, "socket"):
                    if matches:
                        self.assertEqual(bootstrap.execute(args, source, source_owner_path.read_bytes()), {"prepared": True})
                    else:
                        with self.assertRaisesRegex(ValueError, "image_source_revision_mismatch"):
                            bootstrap.execute(args, source, source_owner_path.read_bytes())
                if matches:
                    self.assertEqual(events, ["inspect-backend", "inspect-docling", "copy-cache", "prepare"])
                    origin = json.loads((target / "evidence/planning-bootstrap-origin.json").read_text())
                    self.assertEqual(origin["dependencyImages"]["docling"], args.docling_image)
                    self.assertEqual(origin["sourceDependencyImages"]["docling"], "sha256:" + "4" * 64)
                    self.assertNotIn("docling", origin["dependencyImagesReused"])
                else:
                    cache.copy_cache.assert_not_called(); module.run.assert_not_called()

    def test_ingestion_noexecute_reads_no_key_or_pdf_and_constructs_no_docker(self):
        owner_path, profile, manifest = self.fixture_inputs()
        args = ["ingestion", "--owner", str(owner_path),
            "--embedding-profile", str(profile), "--source-manifest", str(manifest),
            "--source-directory", "/absent/private-or-pdf", "--output", "/absent/output", "--backend-image", "sha256:" + "9" * 64,
            "--backend-sha256", "8" * 64, "--backend-source-revision", "7" * 64]
        output = io.StringIO()
        with patch.object(sys, "argv", args), patch.object(ingestion, "OwnedDocker", side_effect=AssertionError("runtime gate crossed")), contextlib.redirect_stdout(output):
            ingestion.main()
        value = json.loads(output.getvalue())
        self.assertFalse(value["privateFilesRead"])
        self.assertFalse(value["dockerCalled"])

    def test_conflicting_job_wire_aliases_are_rejected(self):
        with self.assertRaisesRegex(ValueError, "missing or ambiguous"):
            contracts.parse_job_listing({"items": [], "Items": []})

    def test_actual_attempt_field_cannot_be_a_numeric_string(self):
        with self.assertRaisesRegex(ValueError, "attempts is invalid"):
            contracts.parse_job_listing({"items": [{"jobId": str(uuid.uuid4()), "action": "upsert", "docPath": "synthetic.pdf",
                "status": "done", "attempts": "1"}]})

    def test_enabling_advanced_license_is_rejected_before_job(self):
        config = {"AdvancedAnalysis": {"WorkerEnabled": False, "Provider": "disabled", "AllowExternalProviderContent": False,
            "AllowExternalProviderMetadata": False}, "License": {"AdvancedAnalysisEnabled": False},
            "RuntimeGovernance": {"CapabilityBWorkerEnabled": False}, "Ingestion": {"ScannerEnabled": False, "WatcherEnabled": False}}
        contracts.validate_disabled_llm(config, {"Config": {"Env": []}})
        config["License"]["AdvancedAnalysisEnabled"] = True
        with self.assertRaisesRegex(ValueError, "disable advanced/provider"):
            contracts.validate_disabled_llm(config, {"Config": {"Env": []}})


if __name__ == "__main__":
    unittest.main()
