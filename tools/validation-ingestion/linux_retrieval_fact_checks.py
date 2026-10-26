"""Pure, independent-annotation retrieval checks for current canonical publications."""
from __future__ import annotations

import base64
import copy
import datetime
import gzip
import hashlib
import io
import json
import re
import uuid

from linux_owned_runtime import validate_owner
from linux_lifecycle_checks import digest, require
from canonical_search_checks import check_search_capture, _current_items, retrieval_metrics_healthy, fact_in_current_item
from tools.ingestion_gold.contracts import validate_manifest
from tools.ingestion_gold.critical_facts import critical_fact_issues, exact_text
from tools.ingestion_gold.canonical_evaluator import _text_variant, _table_text
from tools.ingestion_gold.structured_fact_surfaces import has_structured_chunks, document_source_windows, build_retrieval_windows


def validate_inputs(owner, prior, manifest, *, owner_sha, manifest_sha):
    validate_owner(owner)
    cases = validate_manifest(manifest)
    facts = sum(len(c["expectations"]["criticalFacts"]) for c in cases)
    require(0 < facts <= 106 and all(f.get("retrievalRequired", True) is True
            for c in cases for f in c["expectations"]["criticalFacts"]), "Select the complete bounded existing fact annotations, without silent exclusions.")
    require(prior.get("schemaVersion") == "saaia-linux-ingestion-execution.v1"
            and all(prior.get(k) is True for k in ("executed", "passed", "ingestionWorkerExecuted", "sqlPublicationValidated",
                "vectorStorePublicationValidated", "canonicalSearchValidated")), "A real successful corpus execution is required.")
    p = prior.get("plan", {})
    require(p.get("runId") == owner["runId"] and p.get("ownerManifestSha256") == owner_sha
            and p.get("corpusManifestSha256") == manifest_sha and sorted(p.get("caseIds", [])) == sorted(c["caseId"] for c in cases),
            "The prior corpus proof must bind this exact namespace, manifest and complete case set.")
    maximum = 2 * facts + 2 * len(cases)
    require(maximum <= 238, "This bounded retrieval probe permits at most 238 HTTP calls.")
    return cases, {"schemaVersion": "saaia-linux-retrieval-facts-plan.v1", "runId": owner["runId"],
        "caseCount": len(cases), "factCount": facts, "maximumHttpCalls": maximum,
        "ownerManifestSha256": owner_sha, "corpusManifestSha256": manifest_sha,
        "executeRequested": False, "canonicalRowsSeeded": False, "jobAdmissionAllowed": False,
        "paidProviderEnabled": False, "newReadinessHttpRequestPlanned": False}


def deadline(value, now):
    parsed = datetime.datetime.fromisoformat(str(value).replace("Z", "+00:00"))
    require(parsed.tzinfo is not None and 0 < (parsed - now).total_seconds() <= 900,
            "Use a timezone-aware shared campaign deadline within the next 15 minutes.")
    return parsed


def baseline(cases, observation):
    require(observation.get("schemaVersion") == "saaia-owned-worker-observation.v1"
            and type(observation.get("activeJobs")) is int and observation["activeJobs"] == 0,
            "The current SQL observation must have no active ingestion job.")
    documents = observation.get("documents")
    require(isinstance(documents, list) and len(documents) == len(cases)
            and {d.get("docPath") for d in documents} == {c["caseId"] + ".pdf" for c in cases},
            "Current SQL publication does not exactly cover the frozen cases.")
    result = {}
    for case in cases:
        row = next(d for d in documents if d["docPath"] == case["caseId"] + ".pdf")
        for field in ("docId", "revisionId"):
            require(str(uuid.UUID(row[field])) == row[field], "Current publication UUID is not canonical.")
        require(row.get("status") == "indexed" and type(row.get("indexedVersion")) is int and row["indexedVersion"] > 0
                and row.get("sourceSha256") == case["sourceSha256"] and row.get("pageCount") == case["expectations"]["pageCount"]
                and re.fullmatch(r"[a-f0-9]{64}", str(row.get("activeVectorPlanFingerprint"))) is not None,
                "Current SQL publication source, generation, page count or plan is invalid.")
        chunks = row.get("chunks", [])
        require(chunks and len({c.get("retrievalChunkId") for c in chunks}) == len(chunks), "SQL chunks must be nonempty and distinct.")
        for chunk in chunks:
            require(isinstance(chunk.get("text"), str) and hashlib.sha256(chunk["text"].encode("utf-8")).hexdigest() == chunk.get("checksumSha256"),
                    "Current canonical chunk UTF8 checksum differs.")
        artifacts = row.get("binaryArtifacts", [])
        require(len(artifacts) == 1, "Current publication requires one canonical bundle.")
        artifact = artifacts[0]
        require(artifact.get("artifactType") == "canonical_ingestion_bundle" and artifact.get("compression") == "gzip"
                and type(artifact.get("byteSize")) is int and 0 < artifact["byteSize"] <= 64 * 1024 * 1024,
                "Current canonical bundle format or size is invalid.")
        compressed = base64.b64decode(artifact["payloadBase64"])
        with gzip.GzipFile(fileobj=io.BytesIO(compressed)) as reader:
            raw = reader.read(64 * 1024 * 1024 + 1)
        require(len(raw) == artifact["byteSize"] and len(compressed) == artifact.get("storedSize")
                and hashlib.sha256(raw).hexdigest() == artifact.get("contentHash"), "Current canonical bundle bytes/checksum differ.")
        bundle = json.loads(raw.decode("utf-8"))
        document = bundle.get("document", {})
        require(bundle.get("schemaVersion") == "canonical_ingestion_bundle_v1" and document.get("documentId") == row["docId"]
                and document.get("revisionId") == row["revisionId"] and document.get("source", {}).get("sha256") == case["sourceSha256"],
                "The current physical canonical document differs from its SQL source/revision.")
        pages = {}
        for page in document.get("pages", []):
            number = page.get("pageNumber")
            require(type(number) is int and number > 0 and number not in pages, "Canonical physical pages must be distinct positive integers.")
            pages[number] = [_text_variant(b) for b in page.get("blocks", [])] + [_table_text(t) for t in page.get("tables", [])]
        legacy = critical_fact_issues(case['expectations']['criticalFacts'], pages, chunks)
        physical_windows = retrieval_windows = None
        surface_errors = []
        if has_structured_chunks(chunks):
            scope = dict(document_id=row['docId'], revision_id=row['revisionId'], source_sha256=case['sourceSha256'])
            physical_windows, source_errors = document_source_windows(document, **scope)
            retrieval_windows, retrieval_errors = build_retrieval_windows(document, chunks, **scope)
            surface_errors = source_errors + retrieval_errors
        require(not surface_errors and not critical_fact_issues(case["expectations"]["criticalFacts"], pages, chunks,
                physical_windows=physical_windows, retrieval_windows=retrieval_windows),
                "Independent physical-page facts are absent from the current canonical bundle or its retrieval chunks.")
        verified = copy.deepcopy(row)
        if retrieval_windows is not None:
            verified['_verifiedRetrievalWindows'] = retrieval_windows
            verified['_legacyRawFactIssues'] = legacy
        verified["_verifiedPhysicalFacts"] = {f["id"]: {"page": f["page"],
            "annotationSha256": hashlib.sha256(f["text"].encode("utf-8")).hexdigest()}
            for f in case["expectations"]["criticalFacts"]}
        result[case["caseId"]] = verified
    return result


def observation_identity(observation):
    # Do not require exactly one historical DONE job after lifecycle/recovery.
    # Do require the full current document data and job history to stay unchanged.
    # SQL orders jobs by doc_path; historical attempts on the same document can
    # therefore arrive in either order without any state change.
    jobs = observation.get("jobs")
    require(isinstance(jobs, list) and all(isinstance(j, dict) and isinstance(j.get("jobId"), str)
            and isinstance(j.get("docPath"), str) for j in jobs), "Current job history is malformed.")
    require(all(str(uuid.UUID(j["jobId"])) == j["jobId"] and j["docPath"] for j in jobs),
            "Current job history requires canonical UUIDs and nonempty document paths.")
    require(len({j["jobId"] for j in jobs}) == len(jobs), "Current job history contains duplicate IDs.")
    return digest({"documents": observation.get("documents"), "jobs": sorted(jobs, key=lambda j: (j["docPath"], j["jobId"])),
                   "activeJobs": observation.get("activeJobs")})


def fact_check(case, row, fact, response, read):
    items = response.get("items")
    require(isinstance(items, list), "Search returned no explicit result array.")
    pattern = re.compile(r"(?<!\w)" + re.escape(exact_text(fact["text"])) + r"(?!\w)")
    identity = _current_items(items, case, row, fact["page"])
    found = [(i + 1, item) for i, item in enumerate(items) if fact_in_current_item(item, row, fact)]
    request, value = read.get("request", {}), read.get("response", {})
    chunks = {c["retrievalChunkId"]: c for c in row["chunks"]}
    chunk_id, doc = request.get("chunkId"), value.get("document", {})
    read_ok = (chunk_id in {item["chunkId"] for _, item in found} and request.get("docId") == row["docId"]
               and value.get("found") is True and value.get("anchorFound") is True
               and doc.get("docId") == row["docId"] and doc.get("revisionId") == row["revisionId"]
               and doc.get("sourceHash") == case["sourceSha256"] and isinstance(value.get("items"), list)
               and any(x.get("chunkId") == chunk_id and x.get("text") == chunks[chunk_id]["text"] for x in value["items"])
               and all(x.get("chunkId") in chunks and x.get("text") == chunks[x["chunkId"]]["text"] for x in value["items"]))
    no_degraded = retrieval_metrics_healthy(response)
    annotation_sha = hashlib.sha256(fact["text"].encode("utf-8")).hexdigest()
    physical = row.get("_verifiedPhysicalFacts", {}).get(fact["id"]) == {"page": fact["page"], "annotationSha256": annotation_sha}
    return {"factId": fact["id"], "physicalPage": fact["page"], "annotationSha256": hashlib.sha256(fact["text"].encode("utf-8")).hexdigest(),
        "sourceSha256": case["sourceSha256"], "docId": row["docId"], "revisionId": row["revisionId"],
        "indexedVersion": row["indexedVersion"], "planFingerprint": row["activeVectorPlanFingerprint"],
        "hitCount": len(items), "rank": found[0][0] if found else None, "chunkId": chunk_id,
        "currentIdentitiesExact": bool(identity), "exactFactFound": bool(found), "searchToReadExact": bool(read_ok),
        "exactFactFoundLegacyRawText": any(pattern.search(exact_text(item.get('text', ''))) for item in items),
        "factSurfaceBasis": 'verified_source_bound_windows' if '_verifiedRetrievalWindows' in row else 'legacy_raw_text',
        "noDegradedRetrievers": no_degraded, "physicalFactVerifiedInCurrentCanonicalBundle": physical,
        "passed": bool(identity and found and read_ok and no_degraded and physical)}


def negative_check(case, row, fact, positive_request, response, read, negatives):
    one = copy.deepcopy(case)
    one["expectations"]["criticalFacts"] = [fact]
    result = check_search_capture(one, row, {"positive": {"request": positive_request, "response": response},
                                           "negative": negatives, "read": read})
    selected = {name: result[name] for name in ("passed", "requestsSourceBound", "negativeControlsPassed",
        "absentQueryLexicalMatchesZero", "absentQueryDenseNeighborsCurrent", "absentQueryDenseNeighborCount", "absentDocumentEmpty")}
    selected["negativeMetricsExplicitlyNotDegraded"] = all(retrieval_metrics_healthy(n.get("response", {})) for n in negatives)
    selected["passed"] = selected["passed"] and selected["negativeMetricsExplicitlyNotDegraded"]
    return selected
