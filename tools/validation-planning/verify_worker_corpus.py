"""Verify a NEW real-worker SQL observation. No model, process, SQL or network client."""
from __future__ import annotations

import argparse
import base64
import gzip
import hashlib
import io
import json
import math
from pathlib import Path
import re
import sys
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "validation-ingestion"))
from vector_point_identity import vector_point_id


def require(value, reason):
    if not value:
        raise ValueError(reason)


def path(value):
    result = Path(value).absolute()
    if sys.platform == "win32" and not str(result).startswith("\\\\?\\"):
        result = Path("\\\\?\\" + str(result))
    return result


def sha(value):
    return hashlib.sha256(value).hexdigest()


def read(value):
    return json.loads(path(value).read_text(encoding="utf-8-sig"))


def blob(artifact, compressed=True):
    raw = base64.b64decode(re.sub(r"\s", "", artifact["payloadBase64"]), validate=True)
    if compressed:
        require(artifact["compression"] == "gzip" and len(raw) == artifact["storedSize"], "worker_stored_artifact_size_changed")
        require(0 < artifact["byteSize"] <= 512 * 1024 * 1024, "worker_artifact_size_limit")
        with gzip.GzipFile(fileobj=io.BytesIO(raw)) as source:
            data = source.read(artifact["byteSize"] + 1)
        require(len(data) == artifact["byteSize"], "worker_canonical_byte_size_changed")
    else:
        data = raw
    require(sha(data) == artifact["contentHash"], "worker_artifact_hash_changed")
    return json.loads(data)


def evaluate(observation, sources, source_sha, source_directory=None):
    require(observation["schemaVersion"] == "saaia-planning-worker-corpus-observation.v1"
            and observation["canonicalRowsSeeded"] is False
            and observation["sourceManifestSha256"] == source_sha
            and observation["advancedJobCount"] == observation["activeJobCount"] == 0,
            "worker_publication_boundary_invalid")
    require(len(observation["documents"]) == len(sources["cases"]) == 4, "worker_four_originals_required")
    cases = {case["caseId"] + ".pdf": case for case in sources["cases"]}
    require(len(cases) == 4 and set(cases) == {doc["docPath"] for doc in observation["documents"]}, "worker_original_inventory_changed")
    chunks = {chunk["retrievalChunkId"]: chunk for chunk in observation["retrievalChunks"]}
    require(len(chunks) == len(observation["retrievalChunks"]) > 0, "worker_duplicate_chunk_identity")
    seen_chunks, seen_revisions, output, points = set(), set(), [], {}
    for doc in observation["documents"]:
        case = cases[doc["docPath"]]
        uuid.UUID(doc["docId"]); uuid.UUID(doc["revisionId"])
        require(doc["revisionId"] not in seen_revisions, "worker_duplicate_revision")
        seen_revisions.add(doc["revisionId"])
        original_path = Path(source_directory) / (case["caseId"] + ".pdf") if source_directory else path(case["physicalPdfPath"])
        with original_path.open("rb") as source:
            original_sha = hashlib.file_digest(source, "sha256").hexdigest()
        require(doc["physicalPdfPath"] == case["physicalPdfPath"] and original_sha == case["sourceSha256"] == doc["sourceSha256"]
                and doc["pageCount"] == case["expectations"]["pageCount"] and doc["status"] == "indexed"
                and doc["ingestionVersion"] == doc["indexedVersion"] > 0, "worker_original_or_publication_identity_changed")
        job = doc["ingestionJob"]
        require(job["status"] == "done" and job["action"] == "upsert" and job["doc_path"] == doc["docPath"]
                and job["attempts"] >= 1 and job["started_at"] and job["finished_at"]
                and job["payload"]["docId"] == doc["docId"] and job["payload"]["version"] == doc["ingestionVersion"],
                "worker_actual_completed_job_binding_invalid")
        plan = doc["activeVectorPlanFingerprint"]
        require(re.fullmatch("[a-f0-9]{64}", plan or "") and job["payload"]["resume"]["vectorPlanFingerprint"] == plan,
                "worker_job_and_document_vector_plan_changed")
        artifact = doc["canonicalArtifact"]
        require(artifact["artifactType"] == "canonical_ingestion_bundle", "worker_canonical_bundle_required")
        bundle = blob(artifact)
        canonical = bundle["document"]
        require(canonical["documentId"] == doc["docId"] and canonical["revisionId"] == doc["revisionId"]
                and canonical["source"]["sha256"] == doc["sourceSha256"]
                and canonical["pageCount"] == doc["pageCount"]
                and [p["pageNumber"] for p in canonical["pages"]] == list(range(1, doc["pageCount"] + 1)),
                "worker_canonical_full_pages_or_identity_changed")
        expected_anchors = {a["anchorId"]: a for a in bundle["sourceAnchors"]}
        sql_anchors = {a["anchorId"]: a for a in doc["sourceAnchors"]}
        require(len(sql_anchors) == len(doc["sourceAnchors"]) == len(expected_anchors)
                and set(sql_anchors) == set(expected_anchors), "worker_published_anchor_inventory_changed")
        doc_points = []
        for anchor_id, entry in sql_anchors.items():
            anchor = entry["canonicalAnchor"]
            require(anchor == expected_anchors[anchor_id] and anchor["documentId"] == doc["docId"]
                    and anchor["revisionId"] == doc["revisionId"] and anchor["sourceSha256"] == doc["sourceSha256"]
                    and entry["projectionType"] == anchor["projectionType"] and entry["projectionId"] == anchor["projectionId"],
                    "worker_sql_anchor_differs_from_canonical")
            if entry["projectionType"] != "retrieval_chunk":
                continue
            chunk_id = str(uuid.UUID(entry["projectionId"]))
            require(chunk_id in chunks and chunk_id not in seen_chunks, "worker_anchor_chunk_binding_invalid")
            chunk = chunks[chunk_id]
            require(chunk["revisionId"] == doc["revisionId"] and sha(chunk["text"].encode("utf-8")) == chunk["checksumSha256"]
                    and chunk["pageStart"] == entry["pageStart"] and chunk["pageEnd"] == entry["pageEnd"],
                    "worker_exact_chunk_text_or_page_changed")
            seen_chunks.add(chunk_id)
            doc_points.append({"canonicalChunkId": chunk_id, "pointId": vector_point_id(chunk_id, plan), "planFingerprint": plan})
        require(doc_points, "worker_document_has_no_retrieval_chunks")
        for collection in ("units", "sections", "contentCards"):
            require(all(row["revisionId"] in seen_revisions or row["revisionId"] in {d["revisionId"] for d in observation["documents"]}
                        for row in observation[collection]), "worker_projection_has_unregistered_revision")
        catalog = doc["nativeRawCatalog"]
        require(catalog and catalog["canonicalBundleHash"] == artifact["contentHash"]
                and catalog["sourceHash"] == doc["sourceSha256"] and catalog["revision_id"] == doc["revisionId"],
                "worker_raw_catalog_root_binding_changed")
        blob(catalog, compressed=False)
        if catalog["mode"] == "raw_pages":
            require([p["page_number"] for p in doc["nativeRawPages"]] == list(range(1, doc["pageCount"] + 1)), "worker_raw_page_inventory_changed")
            for page in doc["nativeRawPages"]:
                require(page["generationId"] == catalog["generationId"] and page["sourceHash"] == doc["sourceSha256"], "worker_raw_page_generation_changed")
                blob({**page, "byteSize": page["byte_size"], "storedSize": page["stored_size"]})
        else:
            require(catalog["mode"] == "inline_fallback" and catalog["reason"] and not doc["nativeRawPages"], "worker_raw_availability_not_explicit")
        points[doc["docPath"]] = sorted(doc_points, key=lambda p: p["canonicalChunkId"])
        output.append({key: doc[key] for key in ("docPath", "docId", "revisionId", "sourceSha256", "physicalPdfPath", "pageCount",
            "ingestionVersion", "indexedVersion", "activeVectorPlanFingerprint")} | {
                "canonicalHash": artifact["contentHash"], "retrievalChunkCount": len(doc_points), "sourceAnchorCount": len(sql_anchors),
                "completeCanonicalBundle": True, "ingestionJobId": job["job_id"], "jobStatus": job["status"],
                "attempts": job["attempts"], "startedAtUtc": job["started_at"], "finishedAtUtc": job["finished_at"]})
    require(seen_chunks == set(chunks), "worker_unanchored_chunk_inventory")
    for row in observation["units"]:
        require(sha(row["text"].encode("utf-8")) == row["checksumSha256"], "worker_unit_checksum_changed")
    return output, points


def validate_vectors(points, vectors):
    require(set(points) == set(vectors), "worker_qdrant_document_inventory_changed")
    for doc_path, expected in points.items():
        actual = {str(uuid.UUID(p["id"])): p for p in vectors[doc_path]}
        require(len(actual) == len(vectors[doc_path]) == len(expected), "worker_qdrant_point_count_changed")
        for point in expected:
            item = actual.get(point["pointId"])
            require(item and item["payload"]["chunk_id"] == point["canonicalChunkId"]
                    and item["payload"]["ingestion_plan_fingerprint"] == point["planFingerprint"], "worker_qdrant_active_plan_binding_changed")
            require(isinstance(item["vector"], list) and len(item["vector"]) == 768
                    and all(isinstance(value, (float, int)) and not isinstance(value, bool) and math.isfinite(value) for value in item["vector"]),
                    "worker_real_embedding_vector_shape_invalid")


def write_new(value, destination):
    with path(destination).open("x", encoding="utf-8", newline="") as target:
        target.write(json.dumps(value, ensure_ascii=False, indent=2) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--observation", required=True)
    parser.add_argument("--source-manifest", required=True)
    parser.add_argument("--source-directory", help="Explicit owned Linux copy; original Windows provenance path remains unchanged.")
    parser.add_argument("--point-output")
    parser.add_argument("--vectors")
    parser.add_argument("--manifest-output")
    parser.add_argument("--output")
    args = parser.parse_args()
    require(bool(args.point_output) != bool(args.manifest_output), "select_point_preparation_or_final_verification")
    observation, sources = read(args.observation), read(args.source_manifest)
    documents, points = evaluate(observation, sources, sha(path(args.source_manifest).read_bytes()), args.source_directory)
    if args.point_output:
        write_new(points, args.point_output)
        return
    require(args.vectors and args.output, "final_verification_requires_real_vector_capture")
    validate_vectors(points, read(args.vectors))
    manifest = {"schemaVersion": "saaia-planning-complete-corpus.v1", "completeDocuments": True, "exactSqlReadback": True,
        "origin": "real_ingestion_worker", "observationPath": str(Path(args.observation).absolute()),
        "observationSha256": sha(path(args.observation).read_bytes()), "sourceManifestSha256": observation["sourceManifestSha256"],
        "documents": documents, "documentCount": len(documents), "physicalPageCount": sum(d["pageCount"] for d in documents),
        "boundaries": {"nativeFixture": False, "realWorker": True, "realDocling": True, "vectorPublication": True,
                       "semanticRecipeOwnershipAccepted": False, "modelAnswerAccepted": False, "winUiRenderingValidated": False}}
    write_new(manifest, args.manifest_output)
    write_new({"schemaVersion": "saaia-planning-worker-publication-verification.v1", "passed": True,
               "documentCount": len(documents), "chunkCount": sum(map(len, points.values())), "vectorPublicationValidated": True,
               "semanticOwnershipAcceptance": "requires_independent_pdf_review", "providerCalls": 0,
               "observationSha256": manifest["observationSha256"], "manifestSha256": sha(path(args.manifest_output).read_bytes())}, args.output)


if __name__ == "__main__":
    main()
