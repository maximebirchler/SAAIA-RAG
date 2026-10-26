"""Causal probe tests use an in-memory HTTP double; never any socket/model."""
from __future__ import annotations

import copy
import importlib.util
import inspect
import math
from pathlib import Path
import unittest
from unittest.mock import patch
import urllib.error

SCRIPT = Path(__file__).resolve().parents[2] / "validation-ingestion/probe-local-vectors.py"
SPEC = importlib.util.spec_from_file_location("saaia_local_vector_probe", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
PROFILE = {"schemaVersion": "saaia-local-embedding-profile.v1", "adapter": "transformers_cpu", "modelFileSha256": "b" * 64,
    "Rag": {"EmbeddingsBaseUrl": "http://127.0.0.1:15103", "EmbeddingsModel": "intfloat/multilingual-e5-base",
            "EmbeddingsModelRevision": "a" * 40, "EmbeddingsRuntimeRevision": "synthetic-runtime-only"}}


class HttpDouble:
    def __init__(self, mode="coherent", wrong_field=None, preexisting=False):
        self.mode, self.wrong_field = mode, wrong_field
        self.collections = {"preexisting": True} if preexisting else {}
        self.preexisting = preexisting
        self.events = []

    def __call__(self, url, method="GET", body=None):
        self.events.append((url, method, body))
        if url.endswith("/info"):
            info = {"adapter": "transformers_cpu", "offline": True, "dimensions": 768,
                    "model_id": PROFILE["Rag"]["EmbeddingsModel"], "model_revision": "a" * 40,
                    "model_file_sha256": "b" * 64, "runtime_revision": "synthetic-runtime-only"}
            if self.wrong_field:
                info[self.wrong_field] = "different"
            return info
        if url.endswith("/v1/embeddings"):
            text = body["input"]
            if body["model"] == "unknown" or text == "missing prefix":
                raise urllib.error.HTTPError(url, 400, "synthetic negative", {}, None)
            if isinstance(text, str) and "limite " * 100 in text:
                raise urllib.error.HTTPError(url, 413, "synthetic negative", {}, None)
            vectors = []
            for item in text:
                vector = [0.0] * 768
                vector[0 if ("maintenance" in item or item.startswith("query:")) else (1 if "salade" in item else 2)] = 1.0
                if self.mode == "constant":
                    vector = [1.0] + [0.0] * 767
                elif self.mode == "nan":
                    vector[0] = math.nan
                elif self.mode == "small_margin":
                    vector = [0.5005, math.sqrt(1 - 0.5005**2)] + [0.0] * 766 if "maintenance" in item else (
                        [1.0] + [0.0] * 767 if item.startswith("query:") else [0.5, 0.0, math.sqrt(0.75)] + [0.0] * 765)
                vectors.append(vector)
            return {"model": body["model"], "data": [{"index": index if self.mode != "duplicate_index" else 0,
                "embedding": vector} for index, vector in enumerate(vectors)]}
        collection = url.split("/collections/", 1)[1].split("/", 1)[0]
        if method == "GET":
            if self.preexisting or collection in self.collections:
                return {"result": {"status": "green"}}
            raise urllib.error.HTTPError(url, 404, "synthetic absent", {}, None)
        if method == "PUT" and "/points" not in url:
            self.collections[collection] = []
            return {"result": True}
        if method == "PUT":
            self.collections[collection] = body["points"]
            return {"result": {"status": "completed"}}
        if method == "POST":
            points = self.collections[collection]
            scores = [(sum(a*b for a,b in zip(body["vector"], point["vector"])), point) for point in points]
            scores.sort(key=lambda pair: pair[0], reverse=True)
            return {"result": [{"id": point["id"], "score": score, "payload": point["payload"]} for score,point in scores]}
        if method == "DELETE":
            del self.collections[collection]
            return {"result": True}
        raise AssertionError("Unexpected synthetic request.")


def run_probe(server):
    # Retains the old call shape for the causal RED capture. The corrected
    # implementation must receive an explicit profile, never learn it from info.
    kwargs = {"expected_profile": PROFILE} if "expected_profile" in inspect.signature(MODULE.run).parameters else {}
    with patch.object(MODULE, "request", server):
        return MODULE.run("http://127.0.0.1:15103", "http://127.0.0.1:16341", **kwargs)


class LocalVectorProbeTests(unittest.TestCase):
    def test_coherent_synthetic_runtime_and_ranking_positive_control(self):
        result = run_probe(HttpDouble())
        self.assertTrue(result["passed"])

    def test_constant_vectors_cannot_qualify_semantic_ranking(self):
        with self.assertRaises(ValueError):
            run_probe(HttpDouble("constant"))

    def test_semantic_margin_must_exceed_one_thousandth(self):
        with self.assertRaises(ValueError):
            run_probe(HttpDouble("small_margin"))

    def test_observed_model_revision_hash_and_runtime_must_match_profile(self):
        for field in ("model_id", "model_revision", "model_file_sha256", "runtime_revision"):
            server = HttpDouble(wrong_field=field)
            with self.subTest(field=field), self.assertRaises(ValueError):
                run_probe(server)

    def test_preexisting_collection_cannot_be_overwritten_or_deleted(self):
        server = HttpDouble(preexisting=True)
        with self.assertRaises(ValueError):
            run_probe(server)
        self.assertFalse(any(method == "DELETE" for _,method,_ in server.events))

    def test_nonfinite_and_duplicate_index_vectors_are_rejected(self):
        for mode in ("nan", "duplicate_index"):
            with self.subTest(mode=mode), self.assertRaises(ValueError):
                run_probe(HttpDouble(mode))

    def test_origins_with_userinfo_or_privileged_ports_are_rejected(self):
        for url in ("http://:synthetic@127.0.0.1:15103", "http://127.0.0.1:80"):
            with self.subTest(url=url), self.assertRaises(ValueError):
                MODULE.origin(url)


if __name__ == "__main__":
    unittest.main()
