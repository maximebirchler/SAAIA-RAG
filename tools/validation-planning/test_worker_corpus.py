"""Synthetic observation mutations; these never claim to execute ingestion."""
import base64
import copy
import gzip
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
import uuid

sys.path.insert(0, str(Path(__file__).parent))
from verify_worker_corpus import evaluate, validate_vectors


def digest(data):
    return hashlib.sha256(data).hexdigest()


def fixture(root):
    cases, documents, chunks, units = [], [], [], []
    for number in range(4):
        source = root / f"synthetic-{number}.pdf"
        source.write_bytes(f"synthetic-not-a-real-pdf-{number}".encode())
        source_sha = digest(source.read_bytes())
        doc, revision, chunk, job = (str(uuid.uuid4()) for _ in range(4))
        text, plan = f"synthetic exact text {number}", str(number) * 64
        anchor = {"anchorId": f"anchor-{number}", "documentId": doc, "revisionId": revision,
                  "sourceSha256": source_sha, "projectionType": "retrieval_chunk", "projectionId": chunk}
        bundle = {"document": {"documentId": doc, "revisionId": revision, "source": {"sha256": source_sha},
                               "pageCount": 1, "pages": [{"pageNumber": 1}]}, "sourceAnchors": [anchor]}
        data = json.dumps(bundle).encode(); packed = gzip.compress(data)
        artifact = {"artifactType": "canonical_ingestion_bundle", "compression": "gzip", "byteSize": len(data),
                    "storedSize": len(packed), "contentHash": digest(data), "payloadBase64": base64.b64encode(packed).decode()}
        catalog_data = b'{}'
        documents.append({"docPath": f"source-{number}.pdf", "docId": doc, "revisionId": revision,
            "sourceSha256": source_sha, "physicalPdfPath": str(source), "pageCount": 1, "status": "indexed",
            "ingestionVersion": 1, "indexedVersion": 1, "activeVectorPlanFingerprint": plan,
            "ingestionJob": {"job_id": job, "doc_path": f"source-{number}.pdf", "status": "done", "action": "upsert",
                "attempts": 1, "started_at": "2026-10-27T00:00:00Z", "finished_at": "2026-10-27T00:01:00Z",
                "payload": {"docId": doc, "version": 1, "resume": {"vectorPlanFingerprint": plan}}},
            "canonicalArtifact": artifact,
            "sourceAnchors": [{"anchorId": anchor["anchorId"], "projectionId": chunk, "projectionType": "retrieval_chunk",
                "pageStart": 1, "pageEnd": 1, "canonicalAnchor": anchor}],
            "nativeRawCatalog": {"mode": "inline_fallback", "reason": "synthetic_fixture", "revision_id": revision,
                "canonicalBundleHash": artifact["contentHash"], "sourceHash": source_sha,
                "contentHash": digest(catalog_data), "payloadBase64": base64.b64encode(catalog_data).decode()}, "nativeRawPages": []})
        cases.append({"caseId": f"source-{number}", "physicalPdfPath": str(source), "sourceSha256": source_sha,
                      "expectations": {"pageCount": 1}})
        chunks.append({"retrievalChunkId": chunk, "revisionId": revision, "chunkIndex": 0, "pageStart": 1, "pageEnd": 1,
                       "text": text, "checksumSha256": digest(text.encode())})
        units.append({"revisionId": revision, "text": text, "checksumSha256": digest(text.encode())})
    return {"schemaVersion": "saaia-planning-worker-corpus-observation.v1", "canonicalRowsSeeded": False,
            "sourceManifestSha256": "a" * 64, "advancedJobCount": 0, "activeJobCount": 0,
            "documents": documents, "retrievalChunks": chunks, "units": units, "sections": [], "contentCards": []}, {"cases": cases}


class WorkerCorpusTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.observation, self.sources = fixture(Path(self.temporary.name))

    def evaluate(self, data=None):
        return evaluate(data or self.observation, self.sources, "a" * 64)

    def reject(self, mutate, reason):
        altered = copy.deepcopy(self.observation)
        mutate(altered)
        with self.assertRaisesRegex(ValueError, "^" + reason + "$" ):
            self.evaluate(altered)

    def test_synthetic_complete_mapping_preserves_actual_guids(self):
        documents, points = self.evaluate()
        self.assertEqual(len(documents), 4)
        self.assertEqual({p["canonicalChunkId"] for group in points.values() for p in group},
                         {p["retrievalChunkId"] for p in self.observation["retrievalChunks"]})

    def test_seed_and_missing_worker_are_rejected(self):
        self.reject(lambda x: x.update(canonicalRowsSeeded=True), "worker_publication_boundary_invalid")
        self.reject(lambda x: x["documents"][0]["ingestionJob"].update(attempts=0), "worker_actual_completed_job_binding_invalid")

    def test_exact_stored_literal_mutation_rejected(self):
        self.reject(lambda x: x["retrievalChunks"][0].update(text="modified"), "worker_exact_chunk_text_or_page_changed")

    def test_false_chunk_guid_rejected(self):
        self.reject(lambda x: x["retrievalChunks"][0].update(retrievalChunkId=str(uuid.uuid4())), "worker_anchor_chunk_binding_invalid")

    def test_canonical_and_sql_anchor_disagreement_rejected(self):
        self.reject(lambda x: x["documents"][0]["sourceAnchors"][0]["canonicalAnchor"].update(sourceSha256="f" * 64), "worker_sql_anchor_differs_from_canonical")

    def test_plan_and_publication_versions_rejected(self):
        self.reject(lambda x: x["documents"][0].update(activeVectorPlanFingerprint="f" * 64), "worker_job_and_document_vector_plan_changed")
        self.reject(lambda x: x["documents"][0].update(ingestionVersion=2), "worker_original_or_publication_identity_changed")

    def test_vector_requires_real_dimensions_and_active_plan(self):
        _, points = self.evaluate()
        vectors = {name: [{"id": p["pointId"], "payload": {"chunk_id": p["canonicalChunkId"],
            "ingestion_plan_fingerprint": p["planFingerprint"]}, "vector": [0.01] * 768} for p in group] for name, group in points.items()}
        validate_vectors(points, vectors)
        vectors[next(iter(vectors))][0]["vector"][3] = float("nan")
        with self.assertRaisesRegex(ValueError, "worker_real_embedding_vector_shape_invalid"):
            validate_vectors(points, vectors)


if __name__ == "__main__":
    unittest.main()
