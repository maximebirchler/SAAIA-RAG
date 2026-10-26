"""Pure contracts for three hash-pinned sources and automatic ingestion only.

No import reads a key, source PDF, configuration, Docker, HTTP or PostgreSQL.
"""
from __future__ import annotations

import copy
import datetime
import hashlib
import re
import uuid

from linux_lifecycle_checks import TENANT, ACTIVE, digest, require
from linux_owned_runtime import validate_owner
import linux_runtime_queries as queries
from linux_retrieval_fact_checks import baseline
from vector_point_identity import vector_point_id
from tools.ingestion_gold.contracts import validate_manifest

SOURCE_IDS = ("fr-native-critical-v2", "native-two-column-v1", "fr-scan-critical-v2")
TARGETS = ("auto-a.pdf", "auto-b.pdf", "auto-c.pdf")
PROFILES = {"scanner": (True, False), "watcher": (False, True), "defaults": (True, True)}
TIMINGS = {"MaxFilesPerScan": 1, "MinFileAgeSeconds": 1, "ScanIntervalSeconds": 2,
           "MissingGraceSeconds": 5, "WatcherDebounceMs": 250, "WorkerConcurrency": 2}
SOURCES = {"scanner", "watcher", "scanner_missing", "scanner_source_returned"}


def validate_inputs(owner, manifest, profile, *, owner_sha, manifest_sha):
    validate_owner(owner)
    require(profile in PROFILES and owner.get("admissionProfile") == profile,
            "The exact owned namespace must declare this automatic admission profile.")
    all_cases = validate_manifest(manifest)
    cases = [next((c for c in all_cases if c["caseId"] == name), None) for name in SOURCE_IDS]
    require(all(c is not None and isinstance(c.get("generator"), str) for c in cases)
            and len({c["sourceSha256"] for c in cases}) == 3,
            "Use the three distinct hash-pinned generated native/native/scan sources.")
    return cases, {"schemaVersion": "saaia-linux-automatic-ingestion-plan.v1", "runId": owner["runId"],
        "admissionProfile": profile, "ownerManifestSha256": owner_sha, "corpusManifestSha256": manifest_sha,
        "sourceCaseIds": list(SOURCE_IDS), "sourceSha256": [c["sourceSha256"] for c in cases],
        "targetDocPaths": list(TARGETS), "executeRequested": False, "canonicalRowsSeeded": False,
        "apiJobEnqueueAllowed": False, "sqlReadOnly": True, "paidProviderEnabled": False,
        "maximumCampaignSeconds": 1800, "maximumPhaseSeconds": 300,
        "acceleratedTimingsAreDeploymentSla": False, "productStatus": "TESTE_NON_APPROUVE"}


def validate_configuration(config, backend, profile, strict_disabled_llm):
    require(profile in PROFILES, "Unknown automatic admission profile.")
    options = config.get("Ingestion", {})
    scanner, watcher = PROFILES[profile]
    require(options.get("ScannerEnabled") is scanner and options.get("WatcherEnabled") is watcher
            and options.get("WorkerEnabled") is True,
            "The actual configuration must enable only the requested ingestion triggers and worker.")
    require(all(type(options.get(k)) is int and options[k] == v for k, v in TIMINGS.items()),
            "This bounded probe requires its explicit accelerated timing, cap-one and two-worker profile.")
    # Retain the unmodified strict LLM guard. Only its API-only ingestion
    # restriction is replaced by the exact measured profile above.
    restricted_view = copy.deepcopy(config)
    restricted_view["Ingestion"].update({"ScannerEnabled": False, "WatcherEnabled": False})
    strict_disabled_llm(restricted_view, backend)
    env = dict(e.split("=", 1) for e in backend.get("Config", {}).get("Env", []) if "=" in e)
    # appsettings.Local.json is separately forbidden. Any environment override
    # of these controlled options is refused instead of guessing precedence.
    controlled = {("Ingestion:" + k).casefold() for k in (*TIMINGS, "ScannerEnabled", "WatcherEnabled", "WorkerEnabled")}
    require(not any(k.replace("__", ":").casefold() in controlled for k in env),
            "Automatic ingestion settings must come from the single observed configuration, without environment overrides.")
    return {"scannerEnabled": scanner, "watcherEnabled": watcher, "workerConcurrency": 2,
            "acceleratedOptions": dict(TIMINGS), "llmWorkersDisabled": True}


def validate_bound_readiness(ready):
    details = ready.get("details", {})
    require(ready.get("ok") is True and details.get("canonical_provenance_ready") is True
            and details.get("canonical_artifacts_enabled") is True and details.get("tei_dim") == 768,
            "The real automatic backend is not ready for canonical 768-dimensional publication.")
    require(type(details.get("ingestion_worker_concurrency")) is int
            and details["ingestion_worker_concurrency"] == 2,
            "The backend-bound ingestion concurrency must actually be two, not just declared in the configuration.")
    return {"boundWorkerConcurrency": details.get("ingestion_worker_concurrency")}


def validate_job_profile(snapshot, profile):
    require(profile in PROFILES, "Unknown automatic job profile.")
    allowed = {"watcher"} if profile == "watcher" else SOURCES - {"watcher"} if profile == "scanner" else SOURCES
    require(all(job.get("payload", {}).get("source") in allowed for job in snapshot.get("jobs", [])),
            "An observed automatic job came from a trigger disabled in this profile.")


def validate_backend_identity(expected, assembly_sha, image_id):
    require(re.fullmatch(r"[a-f0-9]{64}", str(assembly_sha)) is not None
            and re.fullmatch(r"sha256:[a-f0-9]{64}", str(image_id)) is not None
            and expected.get("assemblySha256") == assembly_sha and expected.get("imageId") == image_id,
            "Automatic runtime does not use the independently pinned final backend image/DLL.")


def snapshot_sql():
    # Static SQL: no caller input, path interpolation or mutation. The outer
    # SELECT observes publication and admission metadata in one DB snapshot.
    return """SELECT json_build_object('publication',(""" + queries.PUBLICATION + """),
 'documentStates',COALESCE((SELECT json_agg(json_build_object('tenantId',d.tenant_id,'docId',d.doc_id,
  'docPath',d.doc_path,'status',d.status,'ingestionVersion',d.ingestion_version,'indexedVersion',d.indexed_version,
  'missingSince',d.missing_since,'lastSeenAt',d.last_seen_at,'plan',d.ingestion_vector_plan_fingerprint,
  'watermark',(SELECT w.highest_generation FROM ingestion_generation_watermarks w
   WHERE w.tenant_id=d.tenant_id AND w.doc_id=d.doc_id)) ORDER BY d.doc_path) FROM documents d),'[]'::json),
 'jobs',COALESCE((SELECT json_agg(json_build_object('jobId',job_id,'tenantId',tenant_id,'docPath',doc_path,
  'action',action,'status',status,'attempts',attempts,'lockedBy',locked_by,'startedAt',started_at,
  'finishedAt',finished_at,'lastError',last_error,'createdAt',created_at,'payload',payload)
  ORDER BY doc_path,job_id) FROM ingestion_jobs),'[]'::json),
 'plans',COALESCE((SELECT json_agg(json_build_object('planId',p.plan_id,'docId',p.doc_id,'tenantId',p.tenant_id,
  'generation',p.generation,'fingerprint',p.plan_fingerprint,'manifestSealed',p.manifest_sealed,
  'manifestSha256',p.manifest_sha256,'pointCount',p.point_count,'targetEndpoint',p.target_endpoint,
  'targetCollection',p.target_collection,'targetRevision',p.target_revision,'retiredAt',p.retired_at,
  'lastSweep',p.last_sweep_completed_at,'lastGcCode',p.last_gc_code,'lastAbsence',p.last_batch_absence_at,
  'points',COALESCE((SELECT json_agg(json_build_object('pointId',v.point_id,'canonicalChunkId',v.canonical_chunk_id)
   ORDER BY v.ordinal) FROM ingestion_vector_plan_points v WHERE v.plan_id=p.plan_id),'[]'::json))
  ORDER BY p.doc_id,p.generation,p.plan_id) FROM ingestion_vector_plans p),'[]'::json))"""


def scope(snapshot, *, allow_failed=False):
    states, jobs = snapshot.get("documentStates"), snapshot.get("jobs")
    require(isinstance(states, list) and len(states) <= 3 and isinstance(jobs, list) and len(jobs) <= 200,
            "Automatic observations must be explicit bounded arrays.")
    require(len({d.get("docPath") for d in states}) == len(states)
            and all(d.get("docPath") in TARGETS and d.get("tenantId") == TENANT for d in states),
            "A document outside the three known synthetic paths or tenant is present.")
    plans = snapshot.get("plans")
    require(isinstance(plans, list) and len(plans) <= 200
            and all(p.get("tenantId") == TENANT and p.get("docId") in {d["docId"] for d in states} for p in plans),
            "A vector plan outside the three owned documents is present.")
    require(len({j.get("jobId") for j in jobs}) == len(jobs), "Automatic job IDs are duplicated.")
    for job in jobs:
        require(str(uuid.UUID(job.get("jobId", ""))) == job["jobId"] and job.get("tenantId") == TENANT
                and job.get("docPath") in TARGETS and job.get("action") in {"upsert", "delete"}
                and job.get("status") in ACTIVE | {"done", "failed", "canceled", "cancelled"}
                and type(job.get("attempts")) is int and job["attempts"] >= 0
                and isinstance(job.get("payload"), dict) and job["payload"].get("source") in SOURCES,
                "A job has foreign scope, unknown status or nonautomatic admission provenance.")
        require(job["action"] != "delete" or (job["payload"].get("source") == "scanner_missing"
                and job["payload"].get("requiresSourceAbsence") is True),
                "Automatic deletion must carry the explicit source-absence condition.")
        require(allow_failed or job["status"] != "failed", "A real automatic ingestion job reached failed.")
    publication = snapshot.get("publication", {})
    require(type(publication.get("activeJobs")) is int
            and publication["activeJobs"] == sum(j["status"] in ACTIVE for j in jobs),
            "The active-job count is not explained by the exact owned automatic jobs.")
    return {d["docPath"]: d for d in states}


def worker_overlap(snapshot):
    running = [j for j in snapshot["jobs"] if j.get("status") == "running"]
    witnesses = {}
    for job in running:
        owner = job.get("lockedBy")
        match = re.fullmatch(r".+-w([01]):[a-f0-9]{32}", str(owner))
        if match and job.get("startedAt") and job.get("payload", {}).get("executionLeaseOwner") == owner:
            witnesses.setdefault(match.group(1), job)
    if set(witnesses) != {"0", "1"}:
        return {"status": "NON_EXECUTED", "reason": "two_simultaneous_running_consumers_not_observed"}
    return {"status": "PASS", "sameSqlSnapshot": True, "consumerIndexes": [0, 1],
            "jobIds": [witnesses[k]["jobId"] for k in ("0", "1")],
            "ownerSha256": [hashlib.sha256(witnesses[k]["lockedBy"].encode()).hexdigest() for k in ("0", "1")],
            "parallelOcrComputeProven": False}


def publication_identity(row):
    return digest({k: row.get(k) for k in ("docId", "revisionId", "indexedVersion", "sourceSha256",
                                         "activeVectorPlanFingerprint", "chunks", "binaryArtifacts")})


def current_row(snapshot, path, case):
    states = scope(snapshot)
    state = states.get(path, {})
    require(state.get("status") == "indexed" and state.get("missingSince") is None
            and type(state.get("ingestionVersion")) is int and type(state.get("indexedVersion")) is int
            and 0 < state["indexedVersion"] <= state["ingestionVersion"]
            and type(state.get("watermark")) is int and state["watermark"] >= state["ingestionVersion"],
            "A current automatic publication is incomplete or still missing.")
    matching = [d for d in snapshot["publication"].get("documents", []) if d.get("docPath") == path]
    require(len(matching) == 1, "The current publication is absent or duplicated.")
    # Reuse the independently source-bound physical-page oracle without altering
    # the frozen source manifest: this temporary view changes only the filename
    # label used for matching a controlled target path.
    view = copy.deepcopy(case)
    view["caseId"] = path[:-4]
    row = baseline([view], {"schemaVersion": "saaia-owned-worker-observation.v1", "activeJobs": 0,
                           "documents": matching})[view["caseId"]]
    require(row["docId"] == state["docId"] and row["indexedVersion"] == state["indexedVersion"]
            and row["activeVectorPlanFingerprint"] == state["plan"], "SQL admission and publication disagree.")
    row.update(tenantId=TENANT, ingestionVersion=state["ingestionVersion"], plan=state["plan"])
    return row


def active_plan(snapshot, row, owner, config):
    plans = [p for p in snapshot.get("plans", []) if p.get("docId") == row["docId"]
             and p.get("fingerprint") == row["plan"] and p.get("generation") == row["indexedVersion"]]
    require(len(plans) == 1, "The current publication has no unique registered vector plan.")
    plan = plans[0]
    expected = {vector_point_id(c["retrievalChunkId"], row["plan"]): c["retrievalChunkId"] for c in row["chunks"]}
    rag = config["Rag"]
    require(plan.get("tenantId") == TENANT and plan.get("manifestSealed") is True and plan.get("retiredAt") is None
            and re.fullmatch(r"[a-f0-9]{64}", str(plan.get("manifestSha256"))) is not None
            and plan.get("pointCount") == len(expected) == len(plan.get("points", []))
            and {p.get("pointId"): p.get("canonicalChunkId") for p in plan["points"]} == expected
            and plan.get("targetCollection") == owner["qdrantCollection"]
            and plan.get("targetEndpoint", "").rstrip("/") == rag["QdrantBaseUrl"].rstrip("/")
            and plan.get("targetRevision") == rag["QdrantRuntimeRevision"],
            "The current sealed manifest, exact physical IDs or owned vector target differ.")
    return plan


def advanced(before, after, retired_ids):
    require(after["docId"] == before["docId"] and after["indexedVersion"] > before["ingestionVersion"]
            and after["revisionId"] != before["revisionId"] and after["plan"] != before["plan"],
            "Automatic replacement did not advance the durable generation, revision and vector plan.")
    require(not retired_ids.intersection(vector_point_id(c["retrievalChunkId"], after["plan"]) for c in after["chunks"]),
            "Automatic replacement reused previously retired physical IDs.")


def missing_state(snapshot, path, before, *, watcher_only):
    state = scope(snapshot).get(path, {})
    if watcher_only:
        rows = [d for d in snapshot["publication"].get("documents", []) if d.get("docPath") == path]
        return bool(state.get("status") == "missing" and state.get("missingSince")
                    and len(rows) == 1 and publication_identity(rows[0]) == publication_identity(before))
    return state.get("status") == "deleted" and state.get("indexedVersion") == 0 and state.get("plan") is None


def source_attribution(snapshot, path, profile):
    validate_job_profile(snapshot, profile)
    allowed = {"scanner"} if profile == "scanner" else {"watcher"} if profile == "watcher" else {"scanner", "watcher", "scanner_source_returned"}
    jobs = [j for j in snapshot["jobs"] if j["docPath"] == path and j["action"] == "upsert" and j["status"] == "done"]
    require(jobs and any(j["payload"]["source"] in allowed and j["attempts"] > 0 for j in jobs),
            "No completed automatic job proves the requested trigger for this document.")
    return {"sources": sorted({j["payload"]["source"] for j in jobs}), "doneJobCount": len(jobs)}


def returned_delete(snapshot, identity, *, restored_at, before):
    jobs = [j for j in snapshot["jobs"] if j["jobId"] == identity]
    require(len(jobs) == 1, "The observed conditional delete disappeared.")
    job = jobs[0]
    if job["status"] in ACTIVE:
        return None
    def stamp(value):
        parsed = datetime.datetime.fromisoformat(str(value).replace("Z", "+00:00"))
        require(parsed.tzinfo is not None, "Conditional-delete timestamps must be timezone aware.")
        return parsed
    if job["startedAt"] is None or stamp(job["startedAt"]) <= stamp(restored_at):
        return {"status": "NON_EXECUTED", "reason": "source_return_before_execution_window_not_proven"}
    require(job["status"] in {"canceled", "cancelled"} and job.get("lastError") == "scanner_source_returned_before_delete",
            "A scanner delete observed queued before source return was not safely canceled.")
    state = scope(snapshot).get(TARGETS[0], {})
    # A fresh upsert can already have published. It must preserve or advance,
    # never temporarily deactivate the previous valid publication.
    require(state.get("indexedVersion", 0) >= before["indexedVersion"] and state.get("plan") is not None,
            "The source-returned conditional delete deactivated the published document.")
    return {"status": "PASS", "jobId": identity, "queuedBeforeReturn": True,
            "sourceReturnBeforeExecution": True, "deleteCanceled": True,
            "terminalPublishedGenerationPreserved": True, "continuousPublicationAvailabilityProven": False,
            "upstreamHttpCancellationProven": False}
