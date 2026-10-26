"""Read-only verification of a captured real-worker SQL/API/Qdrant observation."""
from __future__ import annotations

import argparse
import base64
import copy
import gzip
import hashlib
import json
from pathlib import Path
import re
import sys
from typing import Any

if not __package__:
    sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
sys.path.insert(0, str(Path(__file__).resolve().parent))
from vector_point_identity import vector_point_id
from canonical_search_checks import check_search_capture
from tools.ingestion_gold.canonical_evaluator import evaluate_canonical_snapshot
from tools.ingestion_gold.contracts import select_cases, validate_manifest
from tools.ingestion_gold.critical_facts import critical_fact_issues


def _sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def verify(manifest: dict, selected_ids: list[str], observation: dict, api: dict, vectors: dict, *,
           embedding_adapter: str = "deterministic_loopback_3d", embeddings_real: bool = False,
           searches: dict | None = None, require_search: bool = False) -> dict:
    if (embedding_adapter, embeddings_real) not in {("deterministic_loopback_3d", False), ("transformers_cpu", True), ("tei_cpu", True)}:
        raise ValueError("Embedding execution mode must be explicit and internally consistent.")
    cases = select_cases(validate_manifest(manifest), selected_ids)
    if observation.get("schemaVersion") != "saaia-owned-worker-observation.v1":
        raise ValueError("Unknown SQL observation schema.")
    documents = observation.get("documents")
    jobs = observation.get("jobs")
    if not isinstance(documents, list) or not isinstance(jobs, list):
        raise ValueError("SQL documents and jobs must be explicit lists.")
    expected = {case["caseId"] + ".pdf" for case in cases}
    complete = (len(documents) == len(cases) and {item.get("docPath") for item in documents} == expected
                and len(jobs) == len(cases) and {item.get("docPath") for item in jobs} == expected
                and all(item.get("status") == "done" for item in jobs)
                and observation.get("activeJobs") == 0)
    results = []
    for case in cases:
        path = case["caseId"] + ".pdf"
        rows = [row for row in documents if row.get("docPath") == path]
        job_rows = [row for row in jobs if row.get("docPath") == path]
        issues: list[str] = []
        if len(rows) != 1 or len(job_rows) != 1:
            results.append({"caseId": case["caseId"], "ok": False, "issues": ["document_or_job_coverage"]})
            continue
        row, job = rows[0], job_rows[0]
        identity = (row.get("status") == "indexed" and row.get("indexedVersion", 0) > 0
                    and row.get("sourceSha256") == case["sourceSha256"]
                    and row.get("pageCount") == case["expectations"]["pageCount"])
        if not identity:
            issues.append("published_document_identity")
        artifacts = row.get("binaryArtifacts", [])
        if len(artifacts) != 1 or artifacts[0].get("artifactType") != "canonical_ingestion_bundle":
            results.append({"caseId": case["caseId"], "ok": False, "issues": issues + ["canonical_bundle_count"]})
            continue
        artifact = artifacts[0]
        compressed = base64.b64decode(artifact["payloadBase64"], validate=False)
        raw = gzip.decompress(compressed)
        artifact_exact = (artifact.get("compression") == "gzip" and len(raw) == artifact.get("byteSize")
                          and len(compressed) == artifact.get("storedSize")
                          and _sha(raw) == artifact.get("contentHash"))
        if not artifact_exact:
            issues.append("canonical_bundle_checksum_or_size")
        bundle = json.loads(raw.decode("utf-8"))
        document = bundle.get("document", {})
        if (bundle.get("schemaVersion") != "canonical_ingestion_bundle_v1"
                or document.get("documentId") != row.get("docId")
                or document.get("revisionId") != row.get("revisionId")
                or document.get("source", {}).get("sha256") != case["sourceSha256"]):
            issues.append("canonical_bundle_identity")
        chunks = []
        checksum_failures = 0
        for chunk in row.get("chunks", []):
            if _sha(chunk["text"].encode("utf-8")) != chunk.get("checksumSha256"):
                checksum_failures += 1
            # SQL columns are authoritative. Metadata carries anchor arrays,
            # but must never replace the text/ID/page range just checksummed.
            chunks.append({**chunk.get("metadata", {}), **chunk})
        if checksum_failures or not chunks:
            issues.append("stored_chunk_checksum_or_count")
        # The worker is the projector here. Its actual loaded DLL is recorded by
        # bootstrap. Chunking cannot be overridden through this campaign overlay;
        # the helper-only self-attestation guard is not fabricated for SQL data.
        evaluation_case = copy.deepcopy(case)
        evaluation_case["canonicalExpectations"]["requireProductionDefaults"] = False
        canonical = evaluate_canonical_snapshot({"schemaVersion": "ingestion_layout_canonical_snapshot_v1",
            "sourceSha256": row.get("sourceSha256"), "document": document, "retrievalChunks": chunks}, evaluation_case)
        if not canonical["ok"]:
            issues.extend(sorted({issue["check"] for issue in canonical["issues"]}))
        if not bundle.get("sourceAnchors") or row.get("sourceAnchorCount") != len(bundle["sourceAnchors"]):
            issues.append("source_anchor_count")
        context_pages: dict[int, list[str]] = {}
        api_identity = True
        for captured in api.get(path, []):
            page, context = captured["physicalPage"], captured["response"]
            context_identity = context.get("document", {})
            if (context.get("found") is not True or context_identity.get("docId") != row["docId"]
                    or context_identity.get("revisionId") != row["revisionId"] or context_identity.get("sourceHash") != case["sourceSha256"]):
                api_identity = False
            context_pages[page] = [item.get("text", item.get("textContent", "")) for item in context.get("items", [])]
        api_fact_issues = critical_fact_issues(case["expectations"]["criticalFacts"], context_pages)
        if not api_identity or api_fact_issues:
            issues.append("read_api_identity_or_facts")
        ids = [chunk["retrievalChunkId"] for chunk in chunks]
        points = vectors.get(path, [])
        fingerprint = row.get("activeVectorPlanFingerprint")
        job_fingerprint = job.get("payload", {}).get("resume", {}).get("vectorPlanFingerprint")
        planned = {vector_point_id(identity, fingerprint): identity for identity in ids}
        vector_exact = (isinstance(fingerprint, str) and re.fullmatch(r"[a-f0-9]{64}", fingerprint) is not None
                        and fingerprint == job_fingerprint and len(points) == len(ids) and len(set(ids)) == len(ids)
                        and {point.get("id") for point in points} == set(planned)
                        and all(point.get("payload", {}).get("chunk_id") == planned.get(point.get("id"))
                                and point.get("payload", {}).get("ingestion_plan_fingerprint") == fingerprint for point in points))
        if not vector_exact:
            issues.append("qdrant_points_identity_or_plan")
        search = None
        if require_search:
            search = check_search_capture(case, row, (searches or {}).get(path))
            absent_documents = [item.get("request", {}).get("docId") for item in
                (searches or {}).get(path, {}).get("negative", []) if item.get("kind") == "absent_document"]
            if any(value in {doc.get("docId") for doc in documents} for value in absent_documents):
                search["requestsSourceBound"] = search["passed"] = False
            if not search["passed"]:
                issues.append("canonical_search_or_search_to_read")
        results.append({"caseId": case["caseId"], "ok": not issues, "issues": issues,
            "documentId": row.get("docId"), "revisionId": row.get("revisionId"), "sourceSha256": case["sourceSha256"],
            "canonicalBundleChecksumExact": artifact_exact, "chunkCount": len(chunks), "chunkChecksumFailures": checksum_failures,
            "sourceAnchorCount": row.get("sourceAnchorCount"), "criticalPhysicalFactCount": len(case["expectations"]["criticalFacts"]),
            "canonicalChecksPassed": canonical["ok"], "readApiFactsPassed": api_identity and not api_fact_issues,
            "qdrantPointCount": len(points), "qdrantPointIdsAndPlanExact": vector_exact,
            "canonicalSearch": search})
    return {"schemaVersion": "saaia-owned-worker-publication-verification.v1",
            "passed": complete and all(item["ok"] for item in results), "caseCoverageComplete": complete,
            "caseCount": len(results), "completedJobsObserved": complete,
            "workerExecutionAndNoSeedingRequireRunnerProof": True,
            "sqlPublicationValidated": complete and all(item["ok"] for item in results),
            "vectorStorePublicationValidated": complete and all(item.get("qdrantPointIdsAndPlanExact") for item in results),
            "canonicalSearchExecuted": require_search,
            "canonicalSearchValidated": require_search and complete and all(item.get("canonicalSearch", {}).get("passed") for item in results),
            "embeddingAdapter": embedding_adapter, "embeddingsReal": embeddings_real, "vectorValidated": False,
            "results": results, "limits": ["Real Qdrant storage and point identity are distinct from semantic embedding quality.",
                "An embedding runtime label alone does not validate dense ranking; TEI and Transformers remain distinct runtimes.",
                "Read API chunks can overlap pages; independent canonical page facts must pass separately.",
                "This verifier consumes captured observations; process ownership and execution are proved by the runner."]}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("manifest", "observation", "api", "vectors", "output"):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--case-id", action="append", required=True)
    parser.add_argument("--embedding-adapter", choices=("deterministic_loopback_3d", "transformers_cpu", "tei_cpu"), default="deterministic_loopback_3d")
    parser.add_argument("--embeddings-real", action="store_true")
    parser.add_argument("--searches")
    parser.add_argument("--require-search", action="store_true")
    args = parser.parse_args()
    if args.require_search and not args.searches:
        parser.error("--require-search requires captured --searches observations.")
    load = lambda path: json.loads(Path(path).read_text(encoding="utf-8-sig"))
    proof = verify(load(args.manifest), args.case_id, load(args.observation), load(args.api), load(args.vectors),
                   embedding_adapter=args.embedding_adapter, embeddings_real=args.embeddings_real,
                   searches=load(args.searches) if args.searches else None, require_search=args.require_search)
    output = Path(args.output)
    if output.exists():
        raise FileExistsError("Never overwrite a publication proof.")
    output.write_text(json.dumps(proof, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="")
    print(json.dumps({"passed": proof["passed"], "caseCount": proof["caseCount"], "vectorValidated": False}))
    return 0 if proof["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
