"""Synthetic captured observations only: no worker, SQL, Qdrant or OCR runs."""
from __future__ import annotations

import base64
import copy
import gzip
import hashlib
import importlib.util
import json
from pathlib import Path
import unittest

SCRIPT = Path(__file__).resolve().parents[2] / "validation-ingestion/verify_worker_publication.py"
SPEC = importlib.util.spec_from_file_location("saaia_worker_verifier", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def captured():
    manifest_path = Path(__file__).resolve().parents[3] / "config/ingestion-acceptance-gold.v2.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    case = next(row for row in manifest["cases"] if row["caseId"] == "fr-scan-critical-v2")
    text = " ".join(fact["text"] for fact in case["expectations"]["criticalFacts"])
    source = case["sourceSha256"]
    doc_id = "11111111-2222-3333-4444-555555555555"
    revision = "22222222-3333-4444-5555-666666666666"
    chunk_id = "33333333-4444-5555-6666-777777777777"
    document = {"documentId": doc_id, "revisionId": revision, "source": {"sha256": source}, "pageCount": 1,
                "pages": [{"pageNumber": 1, "blocks": [{"blockId": "b", "blockType": "text", "readingOrder": 0,
                    "text": {"raw": text}}], "tables": [], "figures": []}]}
    bundle = {"schemaVersion": "canonical_ingestion_bundle_v1", "document": document, "sourceAnchors": [{"anchorId": "a"}]}
    raw = json.dumps(bundle).encode("utf-8")
    compressed = gzip.compress(raw, mtime=0)
    artifact = {"artifactType": "canonical_ingestion_bundle", "payloadBase64": base64.b64encode(compressed).decode(),
                "byteSize": len(raw), "storedSize": len(compressed), "compression": "gzip",
                "contentHash": hashlib.sha256(raw).hexdigest()}
    chunk = {"retrievalChunkId": chunk_id, "chunkIndex": 0, "pageStart": 1, "pageEnd": 1,
             "text": text, "checksumSha256": hashlib.sha256(text.encode()).hexdigest(), "metadata": {"canonicalBlockIds": ["b"]}}
    path = case["caseId"] + ".pdf"
    fingerprint = "f" * 64
    row = {"docPath": path, "docId": doc_id, "revisionId": revision, "status": "indexed", "indexedVersion": 1,
           "activeVectorPlanFingerprint": fingerprint,
           "sourceSha256": source, "pageCount": 1, "binaryArtifacts": [artifact], "sourceAnchorCount": 1, "chunks": [chunk]}
    observation = {"schemaVersion": "saaia-owned-worker-observation.v1", "documents": [row], "activeJobs": 0,
                   "jobs": [{"docPath": path, "status": "done", "payload": {"resume": {"vectorPlanFingerprint": fingerprint}}}]}
    api = {path: [{"physicalPage": 1, "response": {"found": True, "document": {"docId": doc_id,
            "revisionId": revision, "sourceHash": source}, "items": [{"text": text}]}}]}
    vectors = {path: [{"id": MODULE.vector_point_id(chunk_id, fingerprint),
                      "payload": {"chunk_id": chunk_id, "ingestion_plan_fingerprint": fingerprint}}]}
    return manifest, [case["caseId"]], observation, api, vectors


def search_capture(values):
    manifest, selected, observation, _, _ = values
    case = next(row for row in manifest["cases"] if row["caseId"] == selected[0])
    fact, row = case["expectations"]["criticalFacts"][0], observation["documents"][0]
    chunk = row["chunks"][0]
    request = {"query": fact["text"], "docId": row["docId"], "sourceBackedCanonical": True,
               "pageStart": fact["page"], "pageEnd": fact["page"]}
    response = {"query": fact["text"], "metrics": {"degradedRetrievers": []}, "items": [{
        "docId": row["docId"], "docPath": row["docPath"], "revisionId": row["revisionId"],
        "sourceHash": case["sourceSha256"], "chunkId": chunk["retrievalChunkId"], "text": chunk["text"],
        "pageStart": chunk["pageStart"], "pageEnd": chunk["pageEnd"]}]}
    negative = []
    for kind in ("absent_query", "absent_document"):
        req = copy.deepcopy(request)
        if kind == "absent_query":
            req["query"] = "saaianomatch" + "a" * 32
        else:
            req["docId"] = "99999999-aaaa-bbbb-cccc-dddddddddddd"
        negative.append({"kind": kind, "request": req,
                         "response": {"query": req["query"], "items": [], "metrics": {"degradedRetrievers": [],
                             "returned": 0, "exactMatchReturned": 0, "sparseReturned": 0, "denseReturned": 0, "retrieversUsed": []}}})
    read = {"request": {"docId": row["docId"], "chunkId": chunk["retrievalChunkId"]}, "response": {
        "found": True, "anchorFound": True, "document": {"docId": row["docId"], "revisionId": row["revisionId"],
            "sourceHash": case["sourceSha256"]}, "items": [{"chunkId": chunk["retrievalChunkId"], "text": chunk["text"]}]}}
    return {row["docPath"]: {"positive": {"request": request, "response": response}, "negative": negative, "read": read}}


def hybrid_nonce_capture(values):
    searches = search_capture(values)
    cap = next(iter(searches.values()))
    current = copy.deepcopy(cap["positive"]["response"]["items"][0])
    current.update({"retriever": "dense_qdrant", "embeddingBasis": "contextual_text_v3", "exactMatchHit": False})
    nonce = cap["negative"][0]["response"]
    nonce["items"] = [current]
    nonce["metrics"].update({"returned": 1, "exactMatchReturned": 0, "sparseReturned": 0,
        "denseReturned": 1, "retrieversUsed": ["dense_qdrant"]})
    cap["negative"][1]["response"]["metrics"].update({"returned": 0, "exactMatchReturned": 0,
        "sparseReturned": 0, "denseReturned": 0, "retrieversUsed": []})
    return searches


class WorkerVerifierTests(unittest.TestCase):
    def test_absent_nonce_allows_current_dense_neighbors_without_literal_matches(self):
        # CanonicalRetrieval intentionally fuses exact+sparse+dense with default
        # minScore=0. Dense neighbors for an absent nonce are not literal hits.
        values = captured()
        proof = MODULE.verify(*values, searches=hybrid_nonce_capture(values), require_search=True)
        self.assertTrue(proof["canonicalSearchValidated"], proof)
        self.assertFalse(proof["vectorValidated"])

    def test_nonce_neighbors_still_require_current_sql_identity_and_no_lexical_hits(self):
        for change in ("exact", "sparse", "wrong_scope", "stale_revision", "altered_text", "wrong_chunk", "nonempty_other_doc"):
            values = captured(); searches = hybrid_nonce_capture(values); cap = next(iter(searches.values()))
            response = cap["negative"][0]["response"]; hit = response["items"][0]
            if change == "exact": response["metrics"]["exactMatchReturned"] = 1
            elif change == "sparse": response["metrics"]["sparseReturned"] = 1
            elif change == "wrong_scope": hit["docId"] = "99999999-aaaa-bbbb-cccc-dddddddddddd"
            elif change == "stale_revision": hit["revisionId"] = "99999999-aaaa-bbbb-cccc-dddddddddddd"
            elif change == "altered_text": hit["text"] += " altered"
            elif change == "wrong_chunk": hit["chunkId"] = "99999999-aaaa-bbbb-cccc-dddddddddddd"
            else: cap["negative"][1]["response"]["items"] = [copy.deepcopy(hit)]
            with self.subTest(change=change):
                self.assertFalse(MODULE.verify(*values, searches=searches, require_search=True)["passed"])

    def test_plan_point_identity_preserves_dotnet_guid_byte_order(self):
        # Frozen formula witnesses; root separately executes the C# production tests.
        chunk = "11111111-2222-3333-4444-555555555555"
        self.assertEqual("e593078c-d8f0-8882-09cc-8a4979a48b33", MODULE.vector_point_id(chunk, "a" * 64))
        self.assertEqual("28b3c020-b07d-ba93-44df-2b053615a518", MODULE.vector_point_id(chunk, "b" * 64))

    def test_payload_cannot_hide_a_legacy_or_other_plan_point_id(self):
        for plan in (None, "a" * 64):
            values = list(captured())
            row = values[2]["documents"][0]
            point = values[4][row["docPath"]][0]
            point["id"] = (row["chunks"][0]["retrievalChunkId"] if plan is None else
                           MODULE.vector_point_id(row["chunks"][0]["retrievalChunkId"], plan))
            with self.subTest(plan=plan):
                self.assertFalse(MODULE.verify(*values)["passed"])

    def test_active_sql_plan_must_match_completed_job_and_be_full(self):
        values = list(captured())
        values[2]["documents"][0]["activeVectorPlanFingerprint"] = "a" * 64
        self.assertFalse(MODULE.verify(*values)["passed"])
        for plan in (None, "a" * 63, "A" * 64):
            values = list(captured()); values[2]["documents"][0]["activeVectorPlanFingerprint"] = plan
            with self.subTest(plan=plan), self.assertRaises(ValueError):
                MODULE.verify(*values)

    def test_optional_search_is_explicit_and_checks_source_bound_search_to_read(self):
        values = captured()
        self.assertFalse(MODULE.verify(*values)["canonicalSearchValidated"])
        self.assertFalse(MODULE.verify(*values, require_search=True)["passed"])
        proof = MODULE.verify(*values, searches=search_capture(values), require_search=True)
        self.assertTrue(proof["passed"], proof)
        self.assertTrue(proof["canonicalSearchValidated"])
        self.assertFalse(proof["vectorValidated"])

    def test_search_has_causal_identity_fact_negative_and_read_controls(self):
        for change in ("source", "chunk", "text", "negation", "negative_nonempty", "negative_missing",
                       "read", "degraded", "request_not_canonical", "negative_scope"):
            values = captured(); searches = search_capture(values); cap = next(iter(searches.values()))
            hit = cap["positive"]["response"]["items"][0]
            if change == "source": hit["sourceHash"] = "0" * 64
            elif change == "chunk": hit["chunkId"] = "99999999-aaaa-bbbb-cccc-dddddddddddd"
            elif change == "text": hit["text"] += " altered"
            elif change == "negation": hit["text"] = hit["text"].replace("Ne pas ouvrir", "Ouvrir")
            elif change == "negative_nonempty": cap["negative"][0]["response"]["items"] = [copy.deepcopy(hit)]
            elif change == "negative_missing": cap["negative"].pop()
            elif change == "read": cap["read"]["response"]["document"]["revisionId"] = "other"
            elif change == "degraded": cap["positive"]["response"]["metrics"]["degradedRetrievers"] = ["canonical_sparse_fts"]
            elif change == "request_not_canonical": cap["positive"]["request"]["sourceBackedCanonical"] = False
            elif change == "negative_scope": cap["negative"][1]["request"]["docId"] = values[2]["documents"][0]["docId"]
            with self.subTest(change=change):
                self.assertFalse(MODULE.verify(*values, searches=searches, require_search=True)["passed"])

    def test_real_model_attestation_does_not_imply_semantic_vector_quality(self):
        proof = MODULE.verify(*captured(), embedding_adapter="transformers_cpu", embeddings_real=True)
        self.assertTrue(proof["passed"])
        self.assertTrue(proof["embeddingsReal"])
        self.assertFalse(proof["vectorValidated"])
        with self.assertRaises(ValueError):
            MODULE.verify(*captured(), embedding_adapter="transformers_cpu", embeddings_real=False)

    def test_coherent_synthetic_capture_passes_only_declared_storage_contract(self):
        proof = MODULE.verify(*captured())
        self.assertTrue(proof["passed"], proof)
        self.assertTrue(proof["vectorStorePublicationValidated"])
        self.assertFalse(proof["vectorValidated"])
        self.assertTrue(proof["workerExecutionAndNoSeedingRequireRunnerProof"])

    def test_incomplete_or_active_jobs_cannot_pass(self):
        for field, value in (("documents", []), ("jobs", []), ("activeJobs", 1)):
            values = list(captured()); values[2][field] = value
            with self.subTest(field=field):
                self.assertFalse(MODULE.verify(*values)["passed"])

    def test_empty_points_wrong_id_and_stale_plan_cannot_pass(self):
        for change in ("empty", "wrong_id", "stale", "duplicate"):
            values = list(captured())
            key = next(iter(values[4])); points = values[4][key]
            if change == "empty":
                points.clear()
            elif change == "wrong_id":
                points[0]["payload"]["chunk_id"] = "other"
            elif change == "stale":
                points[0]["payload"]["ingestion_plan_fingerprint"] = "0" * 64
            else:
                points.append(copy.deepcopy(points[0]))
            with self.subTest(change=change):
                self.assertFalse(MODULE.verify(*values)["passed"])

    def test_stored_text_and_bundle_checksum_are_verified(self):
        for change in ("text", "bundle"):
            values = list(captured()); row = values[2]["documents"][0]
            if change == "text":
                row["chunks"][0]["text"] = "changed"
            else:
                row["binaryArtifacts"][0]["contentHash"] = "0" * 64
            with self.subTest(change=change):
                self.assertFalse(MODULE.verify(*values)["passed"])

    def test_metadata_cannot_replace_authoritative_checksummed_sql_text(self):
        values = list(captured())
        chunk = values[2]["documents"][0]["chunks"][0]
        faithful_text = chunk["text"]
        chunk["text"] = "The actually stored text has lost the critical operational facts."
        chunk["checksumSha256"] = hashlib.sha256(chunk["text"].encode()).hexdigest()
        chunk["metadata"]["text"] = faithful_text
        proof = MODULE.verify(*values)
        self.assertFalse(proof["passed"])
        self.assertIn("critical_retrieval_fact", proof["results"][0]["issues"])

    def test_read_api_identity_and_negation_are_required(self):
        for change in ("identity", "negation"):
            values = list(captured()); response = next(iter(values[3].values()))[0]["response"]
            if change == "identity":
                response["document"]["sourceHash"] = "0" * 64
            else:
                response["items"][0]["text"] = response["items"][0]["text"].replace("Ne pas ouvrir", "Ouvrir")
            with self.subTest(change=change):
                self.assertFalse(MODULE.verify(*values)["passed"])

    def test_missing_canonical_anchors_and_source_hash_are_rejected(self):
        for change in ("anchors", "source"):
            values = list(captured()); row = values[2]["documents"][0]
            if change == "anchors":
                row["chunks"][0]["metadata"] = {}
            else:
                row["sourceSha256"] = "0" * 64
            with self.subTest(change=change):
                self.assertFalse(MODULE.verify(*values)["passed"])


if __name__ == "__main__":
    unittest.main()
