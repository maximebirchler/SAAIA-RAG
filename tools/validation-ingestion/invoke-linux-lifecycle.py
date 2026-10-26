"""Exercise one real worker lifecycle and exact-manifest GC after a Linux corpus PASS.

NOEXEC by default. This runner never builds, starts containers, seeds canonical
rows or connects through SSH. --execute reads only an owned synthetic admin key.
Its finally cancels only newly admitted scoped jobs and stops the exact five IDs.
"""
from __future__ import annotations

import argparse
import copy
import datetime
import importlib.util
import json
from pathlib import Path
import re
import shutil
import sys
import time
import urllib.parse
import urllib.request
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from linux_owned_runtime import OwnedDocker, assert_owned_path
from linux_lifecycle_checks import (ACTIVE, ACTIVE_SQL, CASE_ID, DOC_PATH, TENANT, advancement, cancellation_outcome,
    current_publication, digest, gc_observed, require, snapshot_sql, unrelated_point_manifest,
    validate_configuration_continuity, validate_gc_configuration, validate_plan, validate_points, validate_unrelated_points)
from canonical_search_checks import check_search_capture, search_fact
from vector_point_identity import vector_point_id
from tools.ingestion_gold.contracts import validate_manifest
from tools.ingestion_gold.critical_facts import exact_text

_spec = importlib.util.spec_from_file_location("linux_corpus_driver", Path(__file__).with_name("invoke-linux-ingestion.py"))
corpus = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(corpus)


def utc() -> str:
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


class Lifecycle:
    """Real protocol; injected transports permit offline protocol tests only."""
    def __init__(self, owner, case, config, docker, api, save, *, maximum_wait=300,
                 clock=time.monotonic, sleep=time.sleep, source_check=lambda: None):
        self.owner, self.case, self.config, self.docker = owner, case, config, docker
        self.transport, self.save = api, save
        self.maximum_wait, self.clock, self.sleep, self.source_check = maximum_wait, clock, sleep, source_check
        self.doc_id, self.other_digest = None, None
        self.admitted = set()
        self.history_ids = set()
        self.steps = []
        self.counter = 0

    def capture(self, label, value):
        self.counter += 1
        self.save(f"{self.counter:05d}-{label}.private.json", value)
        return value

    def api(self, role, route, method="GET", body=None):
        self.source_check()
        response = self.transport(role, route, method, body)
        return self.capture(role + "-http", {"observedAtUtc": utc(), "method": method,
                            "route": route, "request": body, "response": response})["response"]

    def snapshot(self):
        self.source_check()
        value = json.loads(self.docker.sql(snapshot_sql()))
        self.capture("sql-snapshot", {"observedAtUtc": utc(), "snapshot": value})
        others = digest(value.get("otherDocuments"))
        if self.other_digest is None:
            self.other_digest = others
        require(others == self.other_digest, "A document outside the one selected lifecycle scope changed.")
        return value

    def points(self, ids, *, vectors=False):
        require(isinstance(ids, list) and 0 < len(ids) <= 256 and len(set(ids)) == len(ids),
                "Only one bounded distinct set of exact point IDs may be observed.")
        for identity in ids:
            require(str(uuid.UUID(identity)) == identity, "A point ID is not a canonical UUID.")
        response = self.api("qdrant", "/collections/" + self.owner["qdrantCollection"] + "/points", "POST",
                            {"ids": ids, "with_payload": True, "with_vector": vectors})
        require(response.get("status") == "ok" and isinstance(response.get("result"), list),
                "Qdrant points observation is not an explicit successful result array.")
        return response["result"]

    def search_read(self, row):
        fact = search_fact(self.case)
        request = {"query": fact["text"], "docId": row["docId"], "sourceBackedCanonical": True,
                   "pageStart": fact["page"], "pageEnd": fact["page"], "topK": 20, "maxPerDoc": 20, "maxPerPage": 20}
        response = self.api("backend", "/rag/search", "POST", request)
        controls = []
        for kind in ("absent_query", "absent_document"):
            control = copy.deepcopy(request)
            control["query" if kind == "absent_query" else "docId"] = (
                "saaianomatch" + uuid.uuid4().hex if kind == "absent_query" else str(uuid.uuid4()))
            controls.append({"kind": kind, "request": control,
                             "response": self.api("backend", "/rag/search", "POST", control)})
        chosen = next((item for item in response.get("items", [])
                       if item.get("chunkId") in {c["retrievalChunkId"] for c in row["chunks"]}
                       and re.search(r"(?<!\w)" + re.escape(exact_text(fact["text"])) + r"(?!\w)",
                                     exact_text(item.get("text", "")))), None)
        read = {}
        if chosen:
            query = {"docId": row["docId"], "chunkId": chosen["chunkId"]}
            read = {"request": query, "response": self.api("backend", "/documents/context?" +
                urllib.parse.urlencode({**query, "before": 0, "after": 0, "limit": 5}))}
        adapted = {**row, "activeVectorPlanFingerprint": row["plan"]}
        checked = check_search_capture(self.case, adapted,
            {"positive": {"request": request, "response": response}, "negative": controls, "read": read})
        require(checked["passed"], "Current source-bound search, negative controls or search-to-read identity failed.")
        return checked

    def unrelated_vectors(self):
        expected = unrelated_point_manifest(self.snapshot())
        ids, points = sorted(expected), []
        for offset in range(0, len(ids), 128):
            points.extend(self.points(ids[offset:offset + 128], vectors=True))
        return validate_unrelated_points(expected, points)

    def publication(self, label, *, snapshot=None, search=True):
        snap = snapshot if snapshot is not None else self.snapshot()
        row = current_publication(snap, self.case["sourceSha256"], doc_id=self.doc_id)
        self.doc_id = row["docId"]
        plan = next(p for p in snap["plans"] if p["fingerprint"] == row["plan"] and p["generation"] == row["indexedVersion"])
        rag = self.config.get("Rag", {})
        require(plan["targetCollection"] == self.owner["qdrantCollection"]
                and plan["targetEndpoint"].rstrip("/") == str(rag.get("QdrantBaseUrl", "")).rstrip("/")
                and plan["targetRevision"] == rag.get("QdrantRuntimeRevision"),
                "The registered active vector target differs from the configured owned collection/runtime.")
        points = self.points([p["pointId"] for p in plan["points"]], vectors=True)
        checked = validate_points(row, points)
        searched = self.search_read(row) if search else None
        summary = {"phase": label, "docId": row["docId"], "revisionId": row["revisionId"],
                   "ingestionVersion": row["ingestionVersion"], "indexedVersion": row["indexedVersion"],
                   "planFingerprint": row["plan"], "sourceSha256": row["sourceSha256"],
                   "chunkCount": len(row["chunks"]), **checked,
                   "searchToReadPassed": None if searched is None else searched["passed"]}
        if search:
            self.steps.append(summary)
        return row, plan, points

    def enqueue(self, action, previous):
        require(action in {"upsert", "delete"}, "Only explicit lifecycle upsert/delete are allowed.")
        ack = self.api("backend", "/ingest/enqueue", "POST", {"docPath": DOC_PATH, "action": action})
        identity = str(uuid.UUID(ack.get("jobId", "")))
        require(identity not in self.admitted, "Lifecycle enqueue reused a previously admitted job ID.")
        # Remember the returned job before further checks so cleanup can still
        # cancel an admitted scoped job if a later acknowledgement field is bad.
        if ack.get("docPath") == DOC_PATH and ack.get("tenantId") == TENANT:
            self.admitted.add(identity)
        require(ack.get("docPath") == DOC_PATH and ack.get("tenantId") == TENANT
                and ack.get("docId") == self.doc_id and ack.get("action") == action
                and type(ack.get("version")) is int and ack["version"] > previous,
                "Enqueue changed scope or reused/regressed a durable document generation.")
        require(identity in self.admitted, "The admitted job is outside the selected synthetic scope.")
        return ack

    def job(self, identity):
        rows = corpus.parse_job_listing(self.api("backend", "/ingestion/jobs?limit=200"))
        found = [r for r in rows if r["jobId"] == identity]
        require(len(found) == 1 and found[0]["docPath"] == DOC_PATH,
                "The exact admitted job disappeared or changed document scope.")
        return found[0]

    def wait_job(self, ack):
        deadline = self.clock() + self.maximum_wait
        while True:
            observed = self.job(ack["jobId"])
            if observed["status"] not in ACTIVE:
                return observed
            require(self.clock() < deadline, "The admitted real worker job exceeded its bounded deadline.")
            self.sleep(0.5)

    def upsert(self, before, label):
        ack = self.enqueue("upsert", before["ingestionVersion"])
        require(self.wait_job(ack)["status"] == "done", "The real worker upsert did not complete successfully.")
        row, plan, points = self.publication(label)
        advancement(before, row, ack["version"], self.history_ids)
        self.history_ids.update(p["id"] for p in points)
        return row, plan, points

    def wait_gc(self, old, *, protected=None, after_sweep=None, reappearance=False):
        deadline = self.clock() + self.maximum_wait
        ids = [p["pointId"] for p in old["points"]]
        while True:
            snap = self.snapshot()
            remaining = self.points(ids)
            if protected is not None:
                row, _, points = self.publication("gc-protected-current", snapshot=snap, search=False)
                require(row["plan"] == protected["plan"] and row["revisionId"] == protected["revisionId"],
                        "GC changed the active protected publication.")
                require({p["id"] for p in points} == {vector_point_id(c["retrievalChunkId"], protected["plan"]) for c in protected["chunks"]},
                        "GC removed a current protected physical point.")
            if gc_observed(snap, old, remaining, after_sweep=after_sweep, reappearance=reappearance):
                state = next(p for p in snap["plans"] if p["planId"] == old["planId"])
                summary = {"phase": "late-write-gc" if reappearance else "retired-plan-gc",
                           "planId": old["planId"], "generation": old["generation"],
                           "pointCount": old["pointCount"], "lastSweep": state["lastSweep"],
                           "lastGcCode": state["lastGcCode"], "exactOldPointsAbsent": True,
                           "activePublicationPreserved": protected is not None}
                self.steps.append(summary)
                return state
            require(self.clock() < deadline, "GC did not prove a completed exact-manifest sweep and point absence within its bound.")
            self.sleep(0.25)

    def late_write(self, old, captured, protected, last_sweep):
        # No fabricated vector, metadata, tenant, generation or point ID enters
        # Qdrant.  The sole write is the previously captured exact old point.
        point = copy.deepcopy(captured[0])
        require(point["id"] in {p["pointId"] for p in old["points"]}, "Late-write point is outside the retired manifest.")
        body = {"points": [{name: point[name] for name in ("id", "payload", "vector")}]}
        ack = self.api("qdrant", "/collections/" + self.owner["qdrantCollection"] + "/points?wait=true", "PUT", body)
        require(ack.get("status") == "ok" and ack.get("result", {}).get("status") == "completed",
                "Controlled exact late-write was not synchronously acknowledged.")
        found = self.points([point["id"]], vectors=True)
        require(len(found) == 1 and digest(found[0]) == digest(point),
                "The controlled old point was not observed with its captured exact vector and payload.")
        self.steps.append({"phase": "controlled-late-write", "pointId": point["id"], "observedPresent": True,
                           "capturedPointSha256": digest(point), "workerHttpCancellationProven": False})
        return self.wait_gc(old, protected=protected, after_sweep=last_sweep, reappearance=True)

    def cancel_optional(self, current):
        ack = self.enqueue("upsert", current["ingestionVersion"])
        deadline = self.clock() + min(30, self.maximum_wait)
        observed = self.job(ack["jobId"])
        while observed["status"] == "queued" and self.clock() < deadline:
            self.sleep(0.1)
            observed = self.job(ack["jobId"])
        if observed["status"] not in {"queued", "running"}:
            terminal = self.wait_job(ack)
            require(terminal["status"] == "done", "Optional cancellation reached an unexpected terminal before an attempt.")
            now, _, points = self.publication("optional-cancel-completion-won")
            advancement(current, now, ack["version"], self.history_ids)
            self.history_ids.update(p["id"] for p in points)
            return {"status": "NON_EXECUTED", "reason": "completion_won_before_cancellation_attempt",
                    "upstreamEngineCancellationVerified": False}
        reply = self.api("backend", "/admin/jobs/cancel", "POST", {"jobId": ack["jobId"]})
        terminal = self.wait_job(ack)
        now, _, points = self.publication("optional-cancel-after-terminal")
        checked = cancellation_outcome(reply, terminal, current, now, ack["version"])
        checked.update({"jobId": ack["jobId"], "observedBeforeCancel": observed["status"]})
        if terminal["status"] == "done":
            advancement(current, now, ack["version"], self.history_ids)
            self.history_ids.update(p["id"] for p in points)
        return checked

    def cleanup_jobs(self, scope_check):
        # Document fidelity is no longer a precondition for cleanup. Ownership,
        # non-symlink paths, exact admitted IDs and the scoped doc path remain so.
        self.source_check = scope_check
        rows = corpus.parse_job_listing(self.api("backend", "/ingestion/jobs?limit=200"))
        for item in rows:
            if item["jobId"] in self.admitted and item["docPath"] == DOC_PATH and item["status"] in ACTIVE:
                self.api("backend", "/admin/jobs/cancel", "POST", {"jobId": item["jobId"]})
        deadline = self.clock() + 60
        while True:
            scope_check()
            active = int(self.docker.sql(ACTIVE_SQL))
            self.capture("cleanup-active-jobs", {"activeJobs": active, "observedAtUtc": utc()})
            if active == 0 or self.clock() >= deadline:
                break
            self.sleep(0.5)
        return {"zeroActiveJobs": active == 0, "activeJobsBeforeStop": active, "onlyNewScopedJobsCanceled": True}


def run(args):
    owner, manifest, prior = corpus.read_json(args.owner), corpus.read_json(args.manifest), corpus.read_json(args.corpus_proof)
    case = next((c for c in validate_manifest(manifest) if c["caseId"] == CASE_ID), None)
    require(case is not None, "The synthetic lifecycle case is missing from the frozen corpus.")
    plan = validate_plan(owner, prior, case, owner_sha=corpus.sha(args.owner), manifest_sha=corpus.sha(args.manifest))
    require(type(args.maximum_wait_seconds) is int and 30 <= args.maximum_wait_seconds <= 1800,
            "Each lifecycle wait must be explicitly bounded between 30 and 1800 seconds.")
    plan.update({"executeRequested": bool(args.execute), "corpusProofSha256": corpus.sha(args.corpus_proof),
                 "maximumWaitSeconds": args.maximum_wait_seconds, "attemptCancellation": bool(args.attempt_cancel)})
    if not args.execute:
        return plan
    require(sys.platform == "linux", "Execute only on the independently prepared owned Linux host.")
    root = Path(owner["root"])
    for path in (args.owner, args.manifest, args.corpus_proof, args.source_directory, args.output, args.api_key_file,
                 Path(owner["configurationPath"])):
        assert_owned_path(path, root)
    source = assert_owned_path(args.source_directory / DOC_PATH, root)
    target = assert_owned_path(Path(owner["documentsRoot"]) / DOC_PATH, root)
    require(source != target and source.is_file() and target.is_file()
            and corpus.sha(source) == corpus.sha(target) == case["sourceSha256"] and not args.output.exists(),
            "Use a fresh evidence root and distinct, exact hash-pinned synthetic source and working copy.")
    args.output.mkdir(mode=0o700)
    private = args.output / "private-observations"
    private.mkdir(mode=0o700)
    docker, headers, lifecycle = OwnedDocker(owner, execute=True), {}, None
    target_present, source_mutated = True, False
    result = {"schemaVersion": "saaia-linux-lifecycle-execution.v1", "executed": True, "passed": False,
              "plan": plan, "startedAtUtc": utc(), "stage": "ownership-preflight", "steps": [],
              "canonicalRowsSeeded": False, "paidProviderCalls": 0,
              "cancel": {"status": "NON_EXECUTED", "reason": "not_requested", "upstreamEngineCancellationVerified": False},
              "failedReplacement": {"status": "NON_EXECUTED", "reason": "not_in_this_bounded_core_campaign"},
              "controlledLateWriteIsWorkerCancellationProof": False, "cleanup": {},
              "limits": ["One generated source only; unrelated publications must remain unchanged.",
                         "A controlled old-point PUT is not proof of canceling an in-flight worker HTTP request.",
                         "Docker binding release is observed; host-wide listener and packet capture are not claimed.",
                         "Unsigned Development results do not qualify the Production deployment."]}

    def save(name, value):
        require(Path(name).name == name and name.endswith(".private.json"), "Capture name must be a simple private JSON filename.")
        corpus.write_json(private / name, value)

    def source_check():
        for path in (source, target, private):
            assert_owned_path(path, root)
        require(corpus.sha(source) == case["sourceSha256"], "The immutable source copy changed.")
        require(target.is_file() and corpus.sha(target) == case["sourceSha256"] if target_present else not target.exists(),
                "The owned working source changed outside the declared lifecycle operation.")

    def api(role, route, method="GET", body=None):
        docker.inspect_all()
        require(role in {"backend", "qdrant"} and route.startswith("/") and not route.startswith("//"),
                "Only role-relative loopback lifecycle routes are permitted.")
        request_headers = {"Content-Type": "application/json", **(headers if role == "backend" else {})}
        wire = None if body is None else json.dumps(body, allow_nan=False).encode("utf-8")
        request = urllib.request.Request(owner["endpoints"][role] + route, data=wire, method=method, headers=request_headers)
        class NoRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, *unused):
                raise ValueError("Lifecycle HTTP redirects are forbidden.")
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
        with opener.open(request, timeout=60) as response:
            require(response.status == 200, "Lifecycle HTTP observation must be a complete 200 response.")
            raw = response.read(8 * 1024 * 1024 + 1)
            require(len(raw) <= 8 * 1024 * 1024, "Lifecycle HTTP response exceeds the bounded capture size.")
            return json.loads(raw.decode("utf-8"))

    try:
        safe, inspected = docker.inspect_all()
        backend_record = next(r for r in owner["containers"] if r["role"] == "backend")
        backend = next(r for r in inspected if r["Id"] == backend_record["id"])
        config = corpus.read_json(Path(owner["configurationPath"]))
        corpus.validate_disabled_llm(config, backend)
        old_ownership_path = assert_owned_path(args.corpus_proof.parent / "runtime-ownership.json", root)
        validate_configuration_continuity(corpus.sha(Path(owner["configurationPath"])), corpus.read_json(old_ownership_path))
        qdrant_record = next(r for r in owner["containers"] if r["role"] == "qdrant")
        validate_gc_configuration(owner, config, next(r for r in inspected if r["Id"] == qdrant_record["id"]))
        binary_proof = assert_owned_path(args.corpus_proof.parent / "backend-binary-identity.json", root)
        expected_binary = corpus.read_json(binary_proof)
        actual_binary = docker.command(["exec", backend_record["id"], "sha256sum", "/app/SAAIA.Backend.dll"]).split()[0]
        require(actual_binary == expected_binary.get("assemblySha256")
                and re.fullmatch(r"[a-f0-9]{64}", actual_binary) is not None,
                "The backend DLL differs from the one that passed the real corpus qualification.")
        docker.command(["exec", backend_record["id"], "test", "!", "-e", "/app/appsettings.Local.json"])
        corpus.write_json(args.output / "runtime-identity.json", {"containers": safe,
            "backendAssemblySha256": actual_binary, "configurationSha256": corpus.sha(Path(owner["configurationPath"]))})
        require(args.api_key_file.is_file() and args.api_key_file.stat().st_size <= 2048,
                "A bounded owned synthetic bootstrap key file is required.")
        key = args.api_key_file.read_text(encoding="utf-8").strip()
        require(bool(key) and all(33 <= ord(c) <= 126 for c in key), "The synthetic bootstrap key is malformed.")
        headers.update(corpus.synthetic_admin_headers(key))
        lifecycle = Lifecycle(owner, case, config, docker, api, save, maximum_wait=args.maximum_wait_seconds, source_check=source_check)
        ready = lifecycle.api("backend", "/ready")
        result.update(corpus.qualification_identity(owner, backend, ready))
        require(ready.get("ok") is True and ready.get("details", {}).get("vector_gc_enabled") is True,
                "The actual backend is not ready with GC enabled.")
        result["stage"] = "initial-publication-A"
        initial, plan_a, points_a = lifecycle.publication("initial-A")
        unrelated_baseline = lifecycle.unrelated_vectors()
        lifecycle.history_ids.update(p["id"] for p in points_a)
        result["stage"] = "same-source-publication-B"
        current, plan_b, _ = lifecycle.upsert(initial, "same-source-B")
        result["stage"] = "retired-A-real-GC"
        swept = lifecycle.wait_gc(plan_a, protected=current)
        result["stage"] = "controlled-late-point-reappearance"
        lifecycle.late_write(plan_a, points_a, current, swept["lastSweep"])
        lifecycle.publication("B-after-late-write-GC")
        result["stage"] = "delete-publication-B"
        source_check()
        target.unlink()
        target_present, source_mutated = False, True
        ack = lifecycle.enqueue("delete", current["ingestionVersion"])
        require(lifecycle.wait_job(ack)["status"] == "done", "The real worker delete did not complete.")
        snap = lifecycle.snapshot()
        deleted = snap.get("document") or {}
        require(snap.get("activeJobs") == 0 and deleted.get("docId") == current["docId"]
                and deleted.get("status") == "deleted" and deleted.get("indexedVersion") == 0
                and deleted.get("plan") is None and deleted.get("ingestionVersion") == ack["version"]
                and snap.get("watermark", -1) >= ack["version"], "Delete did not deactivate publication while preserving its durable generation.")
        empty = lifecycle.api("backend", "/rag/search", "POST", {"query": search_fact(case)["text"],
            "docId": current["docId"], "sourceBackedCanonical": True, "topK": 20})
        require(empty.get("items") == [], "The deleted document remained searchable.")
        lifecycle.steps.append({"phase": "delete", "generation": ack["version"], "indexedVersion": 0,
                                "activePlanCleared": True, "searchEmpty": True})
        lifecycle.wait_gc(plan_b)
        result["stage"] = "recreate-publication-C"
        source_check()
        with target.open("xb") as output, source.open("rb") as original:
            shutil.copyfileobj(original, output)
        target_present = True
        source_check()
        recreated, _, _ = lifecycle.upsert({**current, "ingestionVersion": deleted["ingestionVersion"]}, "recreated-C")
        if args.attempt_cancel:
            result["stage"] = "optional-real-job-cancellation"
            result["cancel"] = lifecycle.cancel_optional(recreated)
        result["steps"] = lifecycle.steps
        unrelated_final = lifecycle.unrelated_vectors()
        require(unrelated_baseline == unrelated_final, "Unrelated current vector payloads or vector values changed during lifecycle/GC.")
        result["unrelatedPublicationPreservation"] = {"sqlStateAndChunkChecksumsUnchanged": True,
            "currentPhysicalVectorsAndPayloadsUnchanged": True, **unrelated_final}
        result["lateWriteReappearanceRemoved"] = True
        result["generationsAndPhysicalPointIdsNeverReused"] = True
        result["passed"] = True
        result["stage"] = "completed"
    except Exception as failure:
        result.update({"failureType": type(failure).__name__, "failureStage": result["stage"]})
        save("failure.private.json", {"type": type(failure).__name__, "stage": result["stage"], "message": str(failure)})
    finally:
        if lifecycle is not None:
            result["steps"] = lifecycle.steps
            try:
                def cleanup_scope_check():
                    for path in (source, target, private):
                        assert_owned_path(path, root)
                result["cleanup"].update(lifecycle.cleanup_jobs(cleanup_scope_check))
            except Exception as failure:
                result["cleanup"]["observationFailureType"] = type(failure).__name__
        # Restore only this known working copy, after owned pending jobs settled.
        # Keep every database row, vector collection, volume and evidence file.
        if source_mutated:
            try:
                assert_owned_path(target, root)
                require(corpus.sha(source) == case["sourceSha256"], "The pinned restore source changed.")
                if not target.exists():
                    with target.open("xb") as output, source.open("rb") as original:
                        shutil.copyfileobj(original, output)
                require(corpus.sha(target) == case["sourceSha256"], "The restored synthetic PDF differs.")
                result["cleanup"]["workingSourceRestored"] = True
            except Exception as failure:
                result["cleanup"].update({"workingSourceRestored": False, "restoreFailureType": type(failure).__name__})
        else:
            try:
                assert_owned_path(target, root)
                result["cleanup"]["workingSourceRestored"] = target.is_file() and corpus.sha(target) == case["sourceSha256"]
            except Exception:
                result["cleanup"]["workingSourceRestored"] = False
        try:
            result["cleanup"].update(docker.stop())
        except Exception as failure:
            result["cleanup"]["stopFailureType"] = type(failure).__name__
        result["passed"] = bool(result["passed"] and corpus.cleanup_success(result["cleanup"])
                                and result["cleanup"].get("workingSourceRestored") is True)
        result["deploymentQualificationPassed"] = result["passed"] and result.get("deploymentQualificationEligible", False)
        result["finishedAtUtc"] = utc()
        corpus.write_json(args.output / "lifecycle-result.json", result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("owner", "manifest", "corpus-proof", "source-directory", "output", "api-key-file"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--maximum-wait-seconds", type=int, default=300)
    parser.add_argument("--attempt-cancel", action="store_true")
    parser.add_argument("--execute", action="store_true")
    result = run(parser.parse_args())
    print(json.dumps(result if not result.get("executed") else {"passed": result["passed"],
          "deploymentQualificationPassed": result["deploymentQualificationPassed"],
          "cleanup": result["cleanup"], "paidProviderCalls": 0}))
    return 0 if not result.get("executed") or result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
