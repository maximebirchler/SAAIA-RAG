"""Linux driver dry-run and admission policies: no service, process, key or HTTP."""
from __future__ import annotations
import copy
import importlib.util
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch
from .test_linux_runtime_guards import fixture
from .test_tei_runtime_contracts import PROFILE
from .test_worker_verifier import captured

SCRIPT = Path(__file__).resolve().parents[2] / "validation-ingestion/invoke-linux-ingestion.py"
SPEC = importlib.util.spec_from_file_location("saaia_linux_ingestion", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def inputs():
    owner, observed = fixture()
    profile = copy.deepcopy(PROFILE)
    profile["Rag"]["EmbeddingsBaseUrl"] = owner["endpoints"]["tei"]
    profile["modelFileRelativePath"] = "onnx/model.onnx"
    profile["modelArtifacts"] = {path: "b" * 64 for path in (
        "onnx/model.onnx", "config.json", "tokenizer.json", "tokenizer_config.json", "1_Pooling/config.json")}
    return owner, profile, captured()[0], observed


def disabled_config():
    return {"License": {"AdvancedAnalysisEnabled": False},
            "AdvancedAnalysis": {"WorkerEnabled": False, "Provider": "disabled",
                "AllowExternalProviderContent": False, "AllowExternalProviderMetadata": False},
            "RuntimeGovernance": {"CapabilityBWorkerEnabled": False},
            "Ingestion": {"WatcherEnabled": False, "ScannerEnabled": False}}


class LinuxIngestionPlanTests(unittest.TestCase):
    def test_actual_dapper_job_shape_and_camel_dto_have_the_same_typed_semantics(self):
        dapper = {"JobId": "11111111-2222-4333-8444-555555555555", "Action": "upsert",
                  "DocPath": "fr-scan-critical-v2.pdf", "Status": "done", "Attempts": 1,
                  "LockedBy": None, "Payload": {"fixture": True}}
        parsed = MODULE.parse_job_listing({"items": [dapper]})
        camel = {name[0].lower() + name[1:]: value for name, value in dapper.items()}
        self.assertEqual(parsed, MODULE.parse_job_listing({"items": [camel]}))
        self.assertEqual("done", parsed[0]["status"])
        self.assertEqual(1, parsed[0]["attempts"])

    def test_job_wire_missing_status_or_ambiguous_alias_fails_immediately(self):
        item = {"JobId": "11111111-2222-4333-8444-555555555555", "Action": "upsert",
                "DocPath": "fixture.pdf", "Status": "running", "Attempts": 1}
        for change in ("missing", "alias", "status_type", "attempts_type", "duplicate"):
            rows = [copy.deepcopy(item)]
            if change == "missing": del rows[0]["Status"]
            elif change == "alias": rows[0]["status"] = "done"
            elif change == "status_type": rows[0]["Status"] = None
            elif change == "attempts_type": rows[0]["Attempts"] = True
            else: rows.append(copy.deepcopy(item))
            with self.subTest(change=change), self.assertRaises(ValueError):
                MODULE.parse_job_listing({"items": rows})
        with self.assertRaises(ValueError): MODULE.parse_job_listing({"items": [], "Items": []})
    def test_isolated_admin_routes_receive_both_bootstrap_headers(self):
        key = "synthetic-bootstrap-admin-not-a-provider-key"
        def simulated_admin_route(headers):
            return 200 if headers.get("X-Api-Key") == key and headers.get("X-Admin-Key") == key else 401
        self.assertEqual(401, simulated_admin_route({"X-Api-Key": key}))
        self.assertEqual(200, simulated_admin_route(MODULE.synthetic_admin_headers(key)))
        self.assertEqual(401, simulated_admin_route(MODULE.synthetic_admin_headers("other-synthetic-key")))
    def test_native_tei_snapshot_does_not_invent_unused_tokenizer_configuration(self):
        owner, profile, manifest, _ = inputs()
        del profile["modelArtifacts"]["tokenizer_config.json"]
        profile["modelArtifacts"]["sentence_bert_config.json"] = "d" * 64
        cases, _ = MODULE.validate_inputs(owner, profile, manifest, ["fr-scan-critical-v2"])
        self.assertEqual(1, len(cases))
    def test_bound_readiness_must_match_profile_and_actual_qdrant_image_identity_type(self):
        _, profile, _, _ = inputs()
        image_id = "sha256:" + "d" * 64
        digest = "sha256:" + "e" * 64
        config = {"Rag": {"QdrantRuntimeRevision": digest}}
        ready = {"details": {"embeddings_model_revision": profile["Rag"]["EmbeddingsModelRevision"],
            "embeddings_runtime_revision": profile["Rag"]["EmbeddingsRuntimeRevision"], "qdrant_runtime_revision": digest}}
        image = {"Id": image_id, "RepoDigests": ["qdrant/qdrant@" + digest]}
        proof = MODULE.readiness_provenance(profile, config, ready, image_id, image)
        self.assertEqual("repository_manifest_digest", proof["qdrantRevisionType"])
        for field in ("embeddings_model_revision", "embeddings_runtime_revision", "qdrant_runtime_revision"):
            stale = copy.deepcopy(ready); stale["details"][field] = "stale"
            with self.subTest(field=field), self.assertRaises(ValueError):
                MODULE.readiness_provenance(profile, config, stale, image_id, image)
        with self.assertRaises(ValueError): MODULE.readiness_provenance(profile, config, ready, image_id, {"Id": image_id, "RepoDigests": []})
    def test_unsigned_development_never_masquerades_as_production_qualification(self):
        owner, _, _, _ = inputs()
        backend = {"Config": {"Env": ["ASPNETCORE_ENVIRONMENT=Development"]}}
        readiness = {"details": {"config_signature_verified": False, "config_signature_mode": "unsigned-dev"}}
        with self.assertRaises(ValueError): MODULE.qualification_identity(owner, backend, readiness)
        owner["qualificationMode"] = "development_diagnostic"
        identity = MODULE.qualification_identity(owner, backend, readiness)
        self.assertFalse(identity["deploymentQualificationEligible"])
        self.assertEqual("Development", identity["environmentObservedFromContainer"])
        self.assertFalse(identity["configurationSignatureVerified"])

    def test_verified_production_identity_is_explicit_and_conflicting_environments_fail(self):
        owner, _, _, _ = inputs()
        backend = {"Config": {"Env": ["ASPNETCORE_ENVIRONMENT=Production"]}}
        readiness = {"details": {"config_signature_verified": True, "config_signature_mode": "signed"}}
        self.assertTrue(MODULE.qualification_identity(owner, backend, readiness)["deploymentQualificationEligible"])
        backend["Config"]["Env"].append("DOTNET_ENVIRONMENT=Development")
        with self.assertRaises(ValueError): MODULE.qualification_identity(owner, backend, readiness)
    def test_no_pass_when_even_one_required_cleanup_observation_is_false_or_missing(self):
        cleanup = {"zeroActiveJobs": True, "ownedContainersStopped": True, "dockerPublishedPortBindingsAbsent": True}
        self.assertTrue(MODULE.cleanup_success(cleanup))
        for name in cleanup:
            altered = copy.deepcopy(cleanup); altered[name] = False
            with self.subTest(name=name): self.assertFalse(MODULE.cleanup_success(altered))
            del altered[name]
            self.assertFalse(MODULE.cleanup_success(altered))
    def test_native_tei_plan_preserves_real_execution_boundaries(self):
        owner, profile, manifest, _ = inputs()
        cases, plan = MODULE.validate_inputs(owner, profile, manifest, ["fr-scan-critical-v2"])
        self.assertEqual(1, len(cases))
        self.assertFalse(plan["executeRequested"])
        self.assertFalse(plan["canonicalRowsSeeded"])
        self.assertFalse(plan["bootstrapPerformedByRunner"])
        self.assertEqual("tei_cpu", plan["embeddingAdapter"])

    def test_no_key_weight_docker_http_or_output_access_before_execute(self):
        owner, profile, manifest, _ = inputs()
        args = SimpleNamespace(owner=Path("owner.json"), embedding_profile=Path("profile.json"), manifest=Path("manifest.json"),
                               case_id=["fr-scan-critical-v2"], maximum_wait_seconds=1800, execute=False,
                               api_key_file=Path("never-read-private-key"), model_file=Path("never-read-weights"))
        with patch.object(MODULE, "read_json", side_effect=[owner, profile, manifest]), patch.object(MODULE, "sha", return_value="0" * 64), \
                patch.object(MODULE, "OwnedDocker") as docker, patch.object(Path, "open", side_effect=AssertionError("Unexpected file access")), \
                patch.object(MODULE.urllib.request, "build_opener", side_effect=AssertionError("Unexpected HTTP")):
            proof = MODULE.run(args)
            docker.assert_not_called()
        self.assertFalse(proof["executeRequested"])

    def test_missing_model_artifacts_or_unknown_corpus_cannot_reach_execution(self):
        owner, profile, manifest, _ = inputs()
        del profile["modelArtifacts"]["tokenizer.json"]
        with self.assertRaises(ValueError): MODULE.validate_inputs(owner, profile, manifest, ["fr-scan-critical-v2"])
        owner, profile, manifest, _ = inputs()
        with self.assertRaises(ValueError): MODULE.validate_inputs(owner, profile, manifest, ["not-in-corpus"])

    def test_signed_config_can_override_ambient_environment_but_must_disable_every_llm_path(self):
        config = disabled_config()
        backend = {"Config": {"Env": ["AdvancedAnalysis__WorkerEnabled=true", "RuntimeGovernance__CapabilityBWorkerEnabled=true"]}}
        MODULE.validate_disabled_llm(config, backend)
        for section, field, value in [("AdvancedAnalysis", "WorkerEnabled", True),
                                      ("AdvancedAnalysis", "AllowExternalProviderContent", True),
                                      ("License", "AdvancedAnalysisEnabled", True),
                                      ("RuntimeGovernance", "CapabilityBWorkerEnabled", True),
                                      ("Ingestion", "WatcherEnabled", True)]:
            altered = copy.deepcopy(config); altered[section][field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                MODULE.validate_disabled_llm(altered, backend)

    def test_unspecified_automatic_worker_is_not_silently_treated_as_disabled(self):
        config = disabled_config(); del config["RuntimeGovernance"]
        with self.assertRaises(ValueError): MODULE.validate_disabled_llm(config, {"Config": {"Env": []}})
