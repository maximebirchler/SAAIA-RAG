"""Pure guards and static read-only SQL for one synthetic Linux lifecycle.

No import performs Docker, HTTP, key access or document I/O.  The caller must
already have qualified the same exact container namespace with the corpus runner.
"""
from __future__ import annotations

import hashlib
import datetime
import json
import math
import re
import urllib.parse
import uuid

from linux_owned_runtime import validate_owner
from vector_point_identity import vector_point_id

CASE_ID = "fr-scan-critical-v2"
DOC_PATH = CASE_ID + ".pdf"
TENANT = "77777777-7777-4777-8777-777777777777"
ACTIVE = {"queued", "running", "paused"}
ACTIVE_SQL = "SELECT count(*) FROM ingestion_jobs WHERE status IN ('queued','running','paused')"


def digest(value: object) -> str:
    return hashlib.sha256(json.dumps(value, sort_keys=True, ensure_ascii=False,
                                    separators=(",", ":"), allow_nan=False).encode("utf-8")).hexdigest()


def require(condition: bool, reason: str) -> None:
    if not condition:
        raise ValueError(reason)


def validate_plan(owner: dict, proof: dict, case: dict, *, owner_sha: str, manifest_sha: str) -> dict:
    validate_owner(owner)
    require(case.get("caseId") == CASE_ID and isinstance(case.get("generator"), str)
            and re.fullmatch(r"[a-f0-9]{64}", str(case.get("sourceSha256", ""))) is not None,
            "Only the hash-pinned generated French scan is eligible for lifecycle mutation.")
    require(proof.get("schemaVersion") == "saaia-linux-ingestion-execution.v1"
            and all(proof.get(name) is True for name in ("executed", "passed", "ingestionWorkerExecuted",
                     "sqlPublicationValidated", "vectorStorePublicationValidated", "canonicalSearchValidated")),
            "Lifecycle requires a real successful Linux corpus execution, not a dry-run or mocked protocol.")
    prior = proof.get("plan", {})
    require(prior.get("runId") == owner["runId"] and prior.get("ownerManifestSha256") == owner_sha
            and prior.get("corpusManifestSha256") == manifest_sha and CASE_ID in prior.get("caseIds", []),
            "The prior corpus proof must bind this exact owner namespace, source manifest and selected case.")
    return {"schemaVersion": "saaia-linux-lifecycle-plan.v1", "executeRequested": False,
            "runId": owner["runId"], "caseId": CASE_ID, "ownerManifestSha256": owner_sha,
            "corpusManifestSha256": manifest_sha, "sourceSha256": case["sourceSha256"],
            "tenantId": TENANT, "canonicalRowsSeeded": False, "bootstrapPerformedByRunner": False,
            "paidProviderEnabled": False, "sqlReadOnly": True,
            "controlledLateWriteIsWorkerCancellationProof": False,
            "productStatus": "TESTE_NON_APPROUVE"}


def snapshot_sql() -> str:
    # Both substitutions are internal constants, never caller-provided SQL/text.
    return f"""SELECT json_build_object(
 'activeJobs',(SELECT count(*) FROM ingestion_jobs WHERE status IN ('queued','running','paused')),
 'document',(SELECT json_build_object('tenantId',d.tenant_id,'docId',d.doc_id,'docPath',d.doc_path,
  'status',d.status,'ingestionVersion',d.ingestion_version,'indexedVersion',d.indexed_version,
  'plan',d.ingestion_vector_plan_fingerprint,'revisionId',r.revision_id,'sourceSha256',encode(r.source_hash,'hex'),
  'chunks',COALESCE((SELECT json_agg(json_build_object('retrievalChunkId',c.retrieval_chunk_id,
   'chunkIndex',c.chunk_index,'pageStart',c.page_start,'pageEnd',c.page_end,'text',c.text_content,
   'checksumSha256',encode(c.checksum,'hex')) ORDER BY c.chunk_index)
   FROM retrieval_chunks c WHERE c.revision_id=r.revision_id),'[]'::json))
  FROM documents d LEFT JOIN document_revisions r ON r.tenant_id=d.tenant_id AND r.doc_id=d.doc_id
   AND r.indexed_version=d.indexed_version WHERE d.tenant_id='{TENANT}'::uuid AND d.doc_path='{DOC_PATH}'),
 'watermark',(SELECT highest_generation FROM ingestion_generation_watermarks w JOIN documents d
  USING(tenant_id,doc_id) WHERE d.tenant_id='{TENANT}'::uuid AND d.doc_path='{DOC_PATH}'),
 'plans',COALESCE((SELECT json_agg(json_build_object('planId',p.plan_id,'generation',p.generation,
  'fingerprint',p.plan_fingerprint,'manifestSealed',p.manifest_sealed,'manifestSha256',p.manifest_sha256,
  'pointCount',p.point_count,'targetEndpoint',p.target_endpoint,'targetCollection',p.target_collection,
  'targetRevision',p.target_revision,'retiredAt',p.retired_at,'lastSweep',p.last_sweep_completed_at,
  'lastGcCode',p.last_gc_code,'lastAbsence',p.last_batch_absence_at,'gcAttempts',p.gc_attempts,
  'points',COALESCE((SELECT json_agg(json_build_object('pointId',v.point_id,'canonicalChunkId',v.canonical_chunk_id)
   ORDER BY v.ordinal) FROM ingestion_vector_plan_points v WHERE v.plan_id=p.plan_id),'[]'::json))
  ORDER BY p.generation,p.plan_id) FROM ingestion_vector_plans p JOIN documents d USING(tenant_id,doc_id)
  WHERE d.tenant_id='{TENANT}'::uuid AND d.doc_path='{DOC_PATH}'),'[]'::json),
 'otherDocuments',COALESCE((SELECT json_agg(json_build_object('tenantId',d.tenant_id,'docId',d.doc_id,
  'status',d.status,'ingestionVersion',d.ingestion_version,'indexedVersion',d.indexed_version,
  'plan',d.ingestion_vector_plan_fingerprint,'chunks',COALESCE((SELECT json_agg(json_build_object(
   'canonicalChunkId',c.retrieval_chunk_id,'checksumSha256',encode(c.checksum,'hex'))
   ORDER BY c.chunk_index) FROM retrieval_chunks c JOIN document_revisions r USING(revision_id)
   WHERE r.tenant_id=d.tenant_id AND r.doc_id=d.doc_id AND r.indexed_version=d.indexed_version),'[]'::json))
  ORDER BY d.tenant_id,d.doc_id) FROM documents d WHERE NOT(d.tenant_id='{TENANT}'::uuid
  AND d.doc_path='{DOC_PATH}')),'[]'::json))"""


def validate_configuration_continuity(current_sha: str, prior: dict) -> None:
    require(re.fullmatch(r"[a-f0-9]{64}", str(current_sha)) is not None
            and current_sha == prior.get("signedConfigurationSha256"),
            "The mounted configuration differs from the exact one that passed the prior Linux corpus run.")


def unrelated_point_manifest(snapshot: dict) -> dict:
    rows = snapshot.get("otherDocuments")
    require(isinstance(rows, list), "Unrelated SQL publications require an explicit captured array.")
    result = {}
    for row in rows:
        tenant, doc_id = str(uuid.UUID(row["tenantId"])), str(uuid.UUID(row["docId"]))
        generation = row.get("indexedVersion")
        require(type(generation) is int and generation >= 0, "An unrelated indexed version is malformed.")
        if generation == 0:
            continue
        require(row.get("status") == "indexed" and isinstance(row.get("chunks"), list) and row["chunks"],
                "The unrelated initial publication is not a complete current indexed document.")
        for chunk in row["chunks"]:
            canonical = str(uuid.UUID(chunk["canonicalChunkId"]))
            identity = vector_point_id(canonical, row["plan"])
            require(identity not in result, "Unrelated publications share a physical point ID.")
            result[identity] = {"tenant_id": tenant, "doc_id": doc_id, "chunk_id": canonical,
                                "ingestion_version": generation, "ingestion_plan_fingerprint": row["plan"]}
    require(len(result) <= 4096, "The unrelated preservation observation exceeds its bounded scope.")
    return result


def validate_unrelated_points(expected: dict, points: list) -> dict:
    require(isinstance(points, list) and len(points) == len(expected), "Unrelated current physical vectors are missing.")
    seen = set()
    for point in points:
        identity, payload, vector = point.get("id"), point.get("payload", {}), point.get("vector")
        require(identity in expected and identity not in seen and all(payload.get(k) == v for k, v in expected[identity].items()),
                "An unrelated physical vector has a duplicate, foreign or stale payload identity.")
        seen.add(identity)
        require(isinstance(vector, list) and len(vector) == 768 and all(type(n) in (int, float) and math.isfinite(n) for n in vector)
                and sum(n * n for n in vector) > 0, "An unrelated real vector is invalid.")
    return {"pointCount": len(points), "vectorsAndPayloadSha256": digest(sorted(points, key=lambda p: p["id"]))}


def validate_gc_configuration(owner: dict, config: dict, qdrant: dict) -> None:
    gc = config.get("VectorGc", {})
    require(gc.get("Enabled") is True and type(gc.get("PollSeconds")) is int and gc["PollSeconds"] == 1
            and type(gc.get("RecheckSeconds")) is int and gc["RecheckSeconds"] == 2
            and gc.get("AllowCurrentTarget", True) is True and not gc.get("ApprovedTargets", []),
            "Use only enabled current-target GC with poll=1, recheck=2 and no approved historical/external targets.")
    rag = config.get("Rag", {})
    require(rag.get("QdrantCollection") == owner["qdrantCollection"],
            "The configured vector collection is outside the exact owned namespace.")
    parsed = urllib.parse.urlparse(str(rag.get("QdrantBaseUrl", "")))
    record = next(r for r in owner["containers"] if r["role"] == "qdrant")
    require(qdrant.get("Id") == record["id"], "Qdrant current-target inspection is not the owned container.")
    aliases = {record["service"], qdrant.get("Name", "").lstrip("/"), qdrant.get("Config", {}).get("Hostname", "")}
    for network in qdrant.get("NetworkSettings", {}).get("Networks", {}).values():
        aliases.update(network.get("Aliases") or [])
    ports = {int(key.split("/")[0]) for key in (qdrant.get("HostConfig", {}).get("PortBindings") or {}) if key.endswith("/tcp")}
    own_loopback = str(rag.get("QdrantBaseUrl", "")).rstrip("/") == owner["endpoints"]["qdrant"].rstrip("/")
    require(parsed.scheme == "http" and parsed.hostname and parsed.port and not parsed.username and not parsed.password
            and not parsed.query and not parsed.fragment and parsed.path in {"", "/"}
            and (own_loopback or (parsed.hostname in aliases and parsed.port in ports)),
            "GC's current target must resolve to the inspected owned Qdrant service/port or its declared loopback.")


def current_publication(snapshot: dict, source_sha: str, *, doc_id: str | None = None) -> dict:
    row = snapshot.get("document") or {}
    require(type(snapshot.get("activeJobs")) is int and snapshot["activeJobs"] == 0
            and row.get("tenantId") == TENANT and row.get("docPath") == DOC_PATH,
            "Publication requires the single synthetic tenant/document and zero active jobs.")
    identity = str(uuid.UUID(row.get("docId", "")))
    require(doc_id is None or identity == doc_id, "Lifecycle document identity changed.")
    indexed, ingestion = row.get("indexedVersion"), row.get("ingestionVersion")
    require(type(indexed) is int and type(ingestion) is int and 0 < indexed <= ingestion
            and type(snapshot.get("watermark")) is int and snapshot["watermark"] >= ingestion and row.get("status") == "indexed"
            and row.get("sourceSha256") == source_sha and re.fullmatch(r"[a-f0-9]{64}", str(row.get("plan"))) is not None,
            "The SQL publication, source hash, generation or durable watermark is invalid.")
    str(uuid.UUID(row.get("revisionId", "")))
    chunks = row.get("chunks")
    require(isinstance(chunks, list) and 0 < len(chunks) <= 256, "Lifecycle needs a bounded nonempty SQL chunk publication.")
    seen = set()
    for chunk in chunks:
        canonical = str(uuid.UUID(chunk.get("retrievalChunkId", "")))
        require(canonical not in seen and isinstance(chunk.get("text"), str)
                and hashlib.sha256(chunk["text"].encode("utf-8")).hexdigest() == chunk.get("checksumSha256"),
                "SQL canonical chunk identity or exact UTF8 checksum is invalid.")
        seen.add(canonical)
    plans = [p for p in snapshot.get("plans", []) if p.get("fingerprint") == row["plan"] and p.get("generation") == indexed]
    require(len(plans) == 1, "The active SQL plan must have one registered immutable manifest.")
    plan = plans[0]
    points = plan.get("points", [])
    expected = {vector_point_id(chunk["retrievalChunkId"], row["plan"]): chunk["retrievalChunkId"] for chunk in chunks}
    require(plan.get("manifestSealed") is True and plan.get("retiredAt") is None
            and re.fullmatch(r"[a-f0-9]{64}", str(plan.get("manifestSha256", ""))) is not None
            and plan.get("pointCount") == len(expected) == len(points)
            and {p.get("pointId"): p.get("canonicalChunkId") for p in points} == expected,
            "The active sealed vector manifest differs from its canonical SQL chunks.")
    return row


def validate_points(row: dict, points: list, *, full_vectors: bool = True) -> dict:
    expected = {vector_point_id(c["retrievalChunkId"], row["plan"]): c["retrievalChunkId"] for c in row["chunks"]}
    require(isinstance(points, list) and len(points) == len(expected), "Current Qdrant point coverage is incomplete.")
    observed = set()
    for point in points:
        payload, identity = point.get("payload", {}), point.get("id")
        require(identity in expected and identity not in observed and payload.get("tenant_id") == TENANT
                and payload.get("doc_id") == row["docId"] and payload.get("chunk_id") == expected[identity]
                and type(payload.get("ingestion_version")) is int and payload["ingestion_version"] == row["indexedVersion"]
                and payload.get("ingestion_plan_fingerprint") == row["plan"],
                "Qdrant returned a duplicate, foreign or obsolete point identity.")
        observed.add(identity)
        if full_vectors:
            vector = point.get("vector")
            require(isinstance(vector, list) and len(vector) == 768 and all(type(n) in (int, float) and math.isfinite(n) for n in vector)
                    and sum(n * n for n in vector) > 0, "A real nonzero 768-dimensional captured vector is required.")
    return {"pointCount": len(points), "pointIdsSha256": digest(sorted(expected)), "vectorsAndPayloadSha256": digest(points)}


def advancement(before: dict, after: dict, version: int, retired_ids: set[str]) -> None:
    require(type(version) is int and version > before["ingestionVersion"] and after["indexedVersion"] == version
            and after["ingestionVersion"] == version and after["docId"] == before["docId"]
            and after["revisionId"] != before["revisionId"] and after["plan"] != before["plan"],
            "Publication did not advance its document generation, revision and plan.")
    ids = {vector_point_id(c["retrievalChunkId"], after["plan"]) for c in after["chunks"]}
    require(not ids.intersection(retired_ids), "A previously published physical vector ID was reused.")


def gc_observed(snapshot: dict, old: dict, remaining: list, *, after_sweep: str | None = None,
                reappearance: bool = False) -> bool:
    candidates = [p for p in snapshot.get("plans", []) if p.get("planId") == old["planId"]]
    require(len(candidates) == 1, "A historical immutable plan disappeared from SQL.")
    state = candidates[0]
    for name in ("generation", "fingerprint", "manifestSha256", "pointCount", "points", "targetEndpoint", "targetCollection", "targetRevision"):
        require(state.get(name) == old.get(name), "The historical immutable vector manifest changed.")
    require(isinstance(remaining, list), "Qdrant absence requires an actual successful points response.")
    if remaining:
        known = {p["pointId"] for p in old["points"]}
        require(len({p.get("id") for p in remaining}) == len(remaining) and all(p.get("id") in known for p in remaining),
                "GC observation returned unexpected or duplicate point IDs.")
    sweep = state.get("lastSweep")
    def timestamp(value):
        parsed = datetime.datetime.fromisoformat(str(value).replace("Z", "+00:00"))
        require(parsed.tzinfo is not None, "A GC observation timestamp must include its timezone.")
        return parsed
    if sweep is not None:
        timestamp(sweep)
    if state.get("lastAbsence") is not None:
        timestamp(state["lastAbsence"])
    return bool(state.get("manifestSealed") is True and state.get("retiredAt") is not None and sweep
                and (after_sweep is None or timestamp(sweep) > timestamp(after_sweep)) and state.get("lastAbsence") and not remaining
                and (not reappearance or state.get("lastGcCode") == "reappearance_removed"))


def cancellation_outcome(ack: dict, terminal: dict, previous: dict, current: dict, version: int) -> dict:
    if terminal["status"] == "done":
        return {"status": "NON_EXECUTED", "reason": "completion_won_before_cancellation_effect",
                "upstreamEngineCancellationVerified": False}
    require(terminal["status"] in {"canceled", "cancelled"} and ack.get("canceled") is True,
            "Cancellation was not acknowledged with a matching canceled terminal.")
    require(current["indexedVersion"] == previous["indexedVersion"] and current["plan"] == previous["plan"]
            and current["revisionId"] == previous["revisionId"] and current["ingestionVersion"] >= version,
            "Cancellation replaced the prior publication or regressed its durable generation.")
    return {"status": "PASS", "apiAcknowledged": True, "previousPublicationPreserved": True,
            "abandonedGeneration": version, "upstreamEngineCancellationVerified": False,
            "currentSourceFreshnessReverified": False}
