"""Pure tests of real TEI support; no socket, service, model or Docker."""
from __future__ import annotations
import copy
import unittest
from unittest.mock import patch
import urllib.error
from .test_local_vector_probe import MODULE as PROBE, HttpDouble
from .test_worker_verifier import MODULE as VERIFIER, captured
from embedding_attestation import attest_tei_container, tei_model_binding

PROFILE = {"schemaVersion": "saaia-tei-embedding-profile.v1", "adapter": "tei_cpu",
    "modelFileSha256": "b" * 64, "dimensions": 768,
    "imageId": "sha256:" + "c" * 64,
    "truncationPolicy": "reject",
    "expectedInfo": {"model_id": "intfloat/multilingual-e5-base", "model_sha": "a" * 40,
                     "version": "1.6.0", "max_input_length": 512},
    "Rag": {"EmbeddingsBaseUrl": "http://127.0.0.1:15103", "EmbeddingsModel": "intfloat/multilingual-e5-base",
            "EmbeddingsModelRevision": "a" * 40, "EmbeddingsRuntimeRevision": "sha256:" + "c" * 64}}
INFO = copy.deepcopy(PROFILE["expectedInfo"])


class TeiHttpDouble(HttpDouble):
    """Native info, generic TEI behavior; explicitly not a real HTTP service."""
    def __init__(self, policy="reject", mode="coherent"):
        super().__init__(mode)
        self.policy = policy

    def __call__(self, url, method="GET", body=None):
        if url.endswith("/info"):
            return copy.deepcopy(INFO)
        if url.endswith("/v1/embeddings"):
            if "input" not in body:
                raise urllib.error.HTTPError(url, 422, "mocked native validation", {}, None)
            if isinstance(body["input"], str):
                if self.policy == "reject":
                    raise urllib.error.HTTPError(url, 413, "mocked native length", {}, None)
                return {"model": body["model"], "data": [{"index": 0, "embedding": [1.0] + [0.0] * 767}]}
        return super().__call__(url, method, body)

class TeiRuntimeContractTests(unittest.TestCase):
    def test_native_tei_probe_uses_explicit_length_policy_not_adapter_prefix_errors(self):
        for policy in ("reject", "auto_truncate"):
            profile = copy.deepcopy(PROFILE); profile["truncationPolicy"] = policy
            with self.subTest(policy=policy), patch.object(PROBE, "request", TeiHttpDouble(policy)):
                result = PROBE.run("http://127.0.0.1:15103", "http://127.0.0.1:16341", expected_profile=profile)
            self.assertTrue(result["passed"])
            self.assertEqual("tei_cpu", result["embeddingAdapter"])
            self.assertFalse(result["ingestionValidated"])

    def test_native_tei_constant_outputs_still_cannot_qualify_semantic_ranking(self):
        with patch.object(PROBE, "request", TeiHttpDouble(mode="constant")), self.assertRaises(ValueError):
            PROBE.run("http://127.0.0.1:15103", "http://127.0.0.1:16341", expected_profile=PROFILE)

    def test_native_tei_info_requires_no_fabricated_transformers_fields(self):
        PROBE.attest(INFO, PROFILE, "http://127.0.0.1:15103")

    def test_real_tei_publication_is_explicit_without_claiming_semantic_quality(self):
        proof = VERIFIER.verify(*captured(), embedding_adapter="tei_cpu", embeddings_real=True)
        self.assertTrue(proof["passed"])
        self.assertEqual("tei_cpu", proof["embeddingAdapter"])
        self.assertFalse(proof["vectorValidated"])

    def test_native_tei_identity_is_not_learned_from_its_endpoint(self):
        for field in ("model_id", "model_sha", "version", "max_input_length"):
            info = copy.deepcopy(INFO)
            info[field] = "wrong"
            with self.subTest(field=field), self.assertRaises(ValueError):
                PROBE.attest(info, PROFILE, "http://127.0.0.1:15103")

    def test_native_tei_without_explicit_truncation_policy_is_rejected(self):
        profile = copy.deepcopy(PROFILE)
        del profile["truncationPolicy"]
        with self.assertRaises(ValueError):
            PROBE.attest(INFO, profile, "http://127.0.0.1:15103")

    def test_tei_mode_cannot_be_claimed_for_synthetic_vectors(self):
        with self.assertRaises(ValueError):
            VERIFIER.verify(*captured(), embedding_adapter="tei_cpu", embeddings_real=False)

    def test_real_null_model_sha_is_preserved_with_separate_command_and_weight_evidence(self):
        profile = copy.deepcopy(PROFILE)
        profile["expectedInfo"]["model_sha"] = None
        info = copy.deepcopy(profile["expectedInfo"])
        with self.assertRaises(ValueError):
            PROBE.attest(info, profile, "http://127.0.0.1:15103")
        profile["modelRevisionAttestation"] = "owned_command_and_weights"
        PROBE.attest(info, profile, "http://127.0.0.1:15103")
        observation = {"imageId": profile["imageId"], "modelFileSha256": "b" * 64,
                       "modelRevision": "a" * 40, "autoTruncate": False, "ownedContainerVerified": True}
        attest_tei_container(profile, observation)
        for field, wrong in [("imageId", "sha256:" + "d" * 64), ("modelFileSha256", "d" * 64),
                             ("modelRevision", "d" * 40), ("autoTruncate", True), ("ownedContainerVerified", False)]:
            altered = copy.deepcopy(observation); altered[field] = wrong
            with self.subTest(field=field), self.assertRaises(ValueError):
                attest_tei_container(profile, altered)

    def test_weight_witness_must_resolve_through_actual_tei_cache_bind(self):
        profile = copy.deepcopy(PROFILE)
        profile["containerModelRoot"] = "/data/models--intfloat--multilingual-e5-base/snapshots/" + "a" * 40
        profile["modelFileRelativePath"] = "onnx/model.onnx"
        container = {"Config": {"Cmd": ["--model-id", profile["Rag"]["EmbeddingsModel"], "--revision", "a" * 40,
                                            "--huggingface-hub-cache", "/data"]},
                     "Mounts": [{"Type": "bind", "Source": "/owned/cache", "Destination": "/data"}]}
        binding = tei_model_binding(profile, container)
        self.assertEqual("/owned/cache/models--intfloat--multilingual-e5-base/snapshots/" + "a" * 40, binding["hostModelRoot"])
        for mutation in ("unmounted", "other_revision", "other_cache"):
            broken = copy.deepcopy(container)
            if mutation == "unmounted": broken["Mounts"] = []
            elif mutation == "other_revision": broken["Config"]["Cmd"][3] = "d" * 40
            else: broken["Config"]["Cmd"][-1] = "/unmounted"
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                tei_model_binding(profile, broken)
