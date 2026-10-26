"""Qualify filesystem-only scanner, watcher or combined automatic ingestion.

NOEXEC by default. The operator prepares and starts five fresh owned containers.
This tool never builds, starts, enqueues ingestion, seeds SQL or contacts an LLM.
Only three known working PDF paths may change. Originals remain hash-pinned.
"""
from __future__ import annotations

import argparse
import copy
import datetime
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import sys
import time
import urllib.parse
import urllib.request
import uuid

REPO = next(p for p in Path(__file__).resolve().parents if (p / "tools/validation-ingestion").is_dir())
sys.path.insert(0, str(REPO))
sys.path.insert(0, str(REPO / "tools/validation-ingestion"))
sys.path.insert(0, str(Path(__file__).resolve().parent))
from linux_owned_runtime import OwnedDocker, assert_owned_path, mounted_source, validate_container
from linux_lifecycle_checks import ACTIVE, digest, gc_observed, require, validate_gc_configuration, validate_points
from linux_automatic_ingestion_checks import (PROFILES, SOURCE_IDS, TARGETS, TENANT, active_plan, advanced,
    current_row, missing_state, publication_identity, returned_delete, scope, snapshot_sql,
    source_attribution, validate_backend_identity, validate_bound_readiness, validate_configuration,
    validate_inputs, validate_job_profile, worker_overlap)
from embedding_attestation import attest_info, attest_tei_container, tei_model_binding
from canonical_search_checks import retrieval_metrics_healthy, search_fact
from linux_retrieval_fact_checks import fact_check
from vector_point_identity import vector_point_id
import linux_runtime_queries as queries

_spec = importlib.util.spec_from_file_location("automatic_corpus_helpers", REPO / "tools/validation-ingestion/invoke-linux-ingestion.py")
corpus = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(corpus)


def utc():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


class ControlledFiles:
    """Atomic replacement/removal of three known working copies only."""
    def __init__(self, root, documents, source_directory, cases, inspect):
        self.root, self.documents, self.inspect = root, documents, inspect
        self.sources = {c["caseId"]: (source_directory / (c["caseId"] + ".pdf"), c["sourceSha256"]) for c in cases}
        self.known_hashes = {value[1] for value in self.sources.values()}
        self.expected = {}

    def originals(self):
        for path, expected in self.sources.values():
            assert_owned_path(path, self.root)
            require(path.is_file() and corpus.sha(path) == expected, "A frozen original synthetic PDF changed.")

    def target(self, name):
        require(name in TARGETS, "Only three exact working PDF paths are mutable.")
        return assert_owned_path(self.documents / name, self.root)

    def verify(self):
        self.originals()
        present = {p.name for p in self.documents.iterdir()}
        require(present == set(self.expected), "An unexpected working file or missing known copy exists.")
        for name, expected in self.expected.items():
            require(corpus.sha(self.target(name)) == expected, "A working source changed outside this controlled operation.")

    def replace(self, name, source_case):
        self.inspect()
        self.originals()
        destination = self.target(name)
        require(not destination.exists() or corpus.sha(destination) in self.known_hashes,
                "Never replace an unknown working file.")
        source, expected = self.sources[source_case]
        temporary = assert_owned_path(self.documents / (".auto-copy-" + uuid.uuid4().hex + ".tmp"), self.root)
        try:
            with temporary.open("xb") as output, source.open("rb") as incoming:
                shutil.copyfileobj(incoming, output)
                output.flush()
                os.fsync(output.fileno())
            require(corpus.sha(temporary) == expected, "Prepared atomic PDF copy differs from its frozen source.")
            self.inspect()
            require(not destination.exists() or corpus.sha(destination) in self.known_hashes, "The target changed before atomic replacement.")
            os.replace(temporary, destination)
            self.expected[name] = expected
            require(corpus.sha(destination) == expected, "The atomically published working PDF differs.")
            return utc()
        finally:
            if temporary.exists():
                temporary.unlink()

    def remove(self, name):
        self.inspect()
        self.originals()
        destination = self.target(name)
        require(name in self.expected and destination.is_file() and corpus.sha(destination) == self.expected[name],
                "Only the exact known working source may be removed.")
        destination.unlink()
        del self.expected[name]
        return utc()


class AutomaticProbe:
    def __init__(self, owner, cases, config, profile, docker, files, api, save, *, maximum_wait=300,
                 clock=time.monotonic, sleep=time.sleep):
        self.owner, self.cases, self.config, self.profile, self.docker = owner, cases, config, profile, docker
        self.files, self.transport, self.save = files, api, save
        self.clock, self.sleep, self.maximum_wait = clock, sleep, maximum_wait
        self.end = clock() + 1800
        self.sequence, self.steps, self.protected = 0, [], {}
        self.overlap = {"status": "NON_EXECUTED", "reason": "two_simultaneous_running_consumers_not_observed"}
        self.conditional = {"status": "NON_EXECUTED", "reason": "not_a_combined_profile"}
        self.maximum_active = 0
        self.retired_ids = set()

    def capture(self, label, value):
        self.sequence += 1
        self.save(f"{self.sequence:05d}-{label}.private.json", value)
        return value

    def api(self, role, route, method="GET", body=None):
        require(self.clock() < self.end, "The automatic qualification wall deadline expired.")
        result = self.transport(role, route, method, body)
        require(self.clock() <= self.end, "The automatic HTTP observation exceeded the campaign wall deadline.")
        return self.capture("http-" + role, {"observedAtUtc": utc(), "route": route, "method": method,
            "request": body, "response": result})["response"]

    def snapshot(self):
        require(self.clock() < self.end, "The automatic qualification wall deadline expired.")
        self.files.verify()
        snapshot = json.loads(self.docker.sql(snapshot_sql()))
        self.capture("sql", {"observedAtUtc": utc(), "snapshot": snapshot})
        states = scope(snapshot)
        validate_job_profile(snapshot, self.profile)
        require(self.clock() < self.end, "The SQL observation exceeded the campaign wall deadline.")
        self.maximum_active = max(self.maximum_active, snapshot["publication"]["activeJobs"])
        observed = worker_overlap(snapshot)
        if observed["status"] == "PASS":
            self.overlap = observed
        for path, expected in self.protected.items():
            row = next((d for d in snapshot["publication"]["documents"] if d.get("docPath") == path), {})
            require(states.get(path, {}).get("status") == "indexed" and states[path].get("missingSince") is None
                    and publication_identity(row) == expected,
                    "A physically present protected publication changed or was wrongly marked missing.")
        return snapshot

    def wait(self, label, predicate):
        end = min(self.end, self.clock() + self.maximum_wait)
        while True:
            snapshot = self.snapshot()
            result = predicate(snapshot)
            if result is not None and result is not False:
                return snapshot, result
            require(self.clock() < end, "The bounded automatic phase did not converge: " + label)
            self.sleep(0.25)

    def wait_current(self, selected, label):
        def check(snapshot):
            if snapshot["publication"]["activeJobs"]:
                return None
            try:
                return {path: current_row(snapshot, path, self.cases[index]) for path, index in selected.items()}
            except ValueError:
                return None
        return self.wait(label, check)

    def points(self, ids, *, vectors=True):
        require(0 < len(ids) <= 256 and len(set(ids)) == len(ids)
                and all(str(uuid.UUID(i)) == i for i in ids), "Only bounded exact known physical IDs may be read.")
        result = self.api("qdrant", "/collections/" + self.owner["qdrantCollection"] + "/points", "POST",
            {"ids": ids, "with_payload": True, "with_vector": vectors})
        require(result.get("status") == "ok" and isinstance(result.get("result"), list), "Qdrant read did not return an explicit full result array.")
        return result["result"]

    def search_read(self, row, case):
        fact = search_fact(case)
        request = {"query": fact["text"], "docId": row["docId"], "sourceBackedCanonical": True,
            "pageStart": fact["page"], "pageEnd": fact["page"], "topK": 20, "maxPerDoc": 20, "maxPerPage": 20}
        response = self.api("backend", "/rag/search", "POST", request)
        require(response.get("query") == fact["text"], "Automatic search did not echo the independently annotated query.")
        from tools.ingestion_gold.critical_facts import exact_text
        pattern = re.compile(r"(?<!\w)" + re.escape(exact_text(fact["text"])) + r"(?!\w)")
        chosen = next((i for i in response.get("items", []) if i.get("chunkId") in
                       {c["retrievalChunkId"] for c in row["chunks"]} and pattern.search(exact_text(i.get("text", "")))), None)
        read = {}
        if chosen:
            query = {"docId": row["docId"], "chunkId": chosen["chunkId"]}
            read = {"request": query, "response": self.api("backend", "/documents/context?" + urllib.parse.urlencode(
                {**query, "before": 0, "after": 0, "limit": 5}))}
        checked = fact_check(case, row, fact, response, read)
        require(checked["passed"], "Automatic publication did not provide exact current source-bound search then read.")
        return checked

    def publication(self, path, index, label, *, snapshot=None):
        snap = self.snapshot() if snapshot is None else snapshot
        row = current_row(snap, path, self.cases[index])
        plan = active_plan(snap, row, self.owner, self.config)
        points = self.points([p["pointId"] for p in plan["points"]])
        checked = validate_points(row, points)
        searched = self.search_read(row, self.cases[index])
        attribution = source_attribution(snap, path, self.profile)
        self.steps.append({"phase": label, "docPath": path, "sourceCaseId": self.cases[index]["caseId"],
            "sourceSha256": row["sourceSha256"], "docId": row["docId"], "revisionId": row["revisionId"],
            "ingestionVersion": row["ingestionVersion"], "indexedVersion": row["indexedVersion"],
            "planFingerprint": row["plan"], "canonicalPhysicalFactsVerified": True,
            "searchToReadPassed": searched["passed"], **checked, **attribution})
        self.retired_ids.update(p["id"] for p in points)
        return row, plan, points

    def update(self, path, index, before, label):
        prior_ids = set(self.retired_ids)
        self.files.replace(path, self.cases[index]["caseId"])
        snap, _ = self.wait_current({path: index}, label)
        row, plan, points = self.publication(path, index, label, snapshot=snap)
        advanced(before, row, prior_ids)
        return row, plan, points

    def preserve(self, rows):
        self.protected = {path: publication_identity(row) for path, row in rows.items()}

    def wait_gc(self, old):
        def check(snapshot):
            remaining = self.points([p["pointId"] for p in old["points"]], vectors=False)
            if gc_observed(snapshot, old, remaining):
                return next(p for p in snapshot["plans"] if p["planId"] == old["planId"])
            return None
        _, state = self.wait("retired-exact-plan-gc", check)
        self.steps.append({"phase": "retired-exact-plan-gc", "planId": old["planId"],
            "pointCount": old["pointCount"], "lastSweep": state["lastSweep"], "exactOldPointsAbsent": True})

    def wait_removed(self, row, plan, points):
        watcher = self.profile == "watcher"
        self.wait("watcher-missing" if watcher else "scanner-deleted", lambda snap:
            True if missing_state(snap, TARGETS[0], row, watcher_only=watcher) else None)
        if watcher:
            retained = self.points([p["id"] for p in points])
            require(digest(sorted(retained, key=lambda p: p["id"])) == digest(sorted(points, key=lambda p: p["id"])),
                    "Watcher-only missing state removed or changed the last valid vectors.")
            self.steps.append({"phase": "watcher-missing", "previousPublicationPreserved": True,
                "previousVectorsPreserved": True, "finalDeletionExecuted": False})
        else:
            result = self.api("backend", "/rag/search", "POST", {"query": search_fact(self.cases[0])["text"],
                "docId": row["docId"], "sourceBackedCanonical": True, "topK": 20})
            require(result.get("items") == [] and retrieval_metrics_healthy(result),
                    "A deleted automatic document is still served or search is degraded.")
            self.wait_gc(plan)
            self.steps.append({"phase": "scanner-deleted", "indexedVersionZero": True,
                "activePlanNull": True, "currentSearchEmpty": True})

    def combined_return_race(self, before):
        self.files.remove(TARGETS[0])
        def find(snapshot):
            jobs = [j for j in snapshot["jobs"] if j["docPath"] == TARGETS[0] and j["action"] == "delete"
                    and j["payload"].get("version", 0) > before["ingestionVersion"]]
            queued = next((j for j in jobs if j["status"] == "queued" and j.get("startedAt") is None), None)
            if queued:
                return {"queued": queued}
            state = scope(snapshot).get(TARGETS[0], {})
            if state.get("status") == "deleted":
                return {"completed": True}
            return None
        _, observed = self.wait("conditional-delete-admission", find)
        restored = self.files.replace(TARGETS[0], SOURCE_IDS[0])
        if "queued" not in observed:
            self.conditional = {"status": "NON_EXECUTED", "reason": "delete_completion_won_before_queued_window_observation"}
        else:
            job = observed["queued"]
            self.conditional = self.wait("source-returned-delete-terminal", lambda snap:
                returned_delete(snap, job["jobId"], restored_at=restored, before=before))[1]
        snap, _ = self.wait_current({TARGETS[0]: 0}, "source-returned-current")
        row, plan, points = self.publication(TARGETS[0], 0, "source-returned-current", snapshot=snap)
        if self.conditional["status"] == "PASS":
            require(row["indexedVersion"] > before["ingestionVersion"], "Source-returned deletion did not lead to a fresh publication.")
            self.conditional["freshPublicationVerified"] = True
        return row, plan, points

    def execute(self):
        first = self.snapshot()
        if self.profile == "defaults":
            require(TARGETS[0] in self.files.expected, "Combined profile requires one verified PDF preexisting before backend boot.")
        else:
            require(not first["documentStates"] and not first["jobs"] and not first["publication"]["documents"],
                    "Scanner-only and watcher-only must start with an empty fresh database and documents root.")
            self.files.replace(TARGETS[0], SOURCE_IDS[0])
        snap, _ = self.wait_current({TARGETS[0]: 0}, "initial-a")
        a, _, _ = self.publication(TARGETS[0], 0, "initial-a", snapshot=snap)
        if self.profile == "defaults":
            require(any(j["docPath"] == TARGETS[0] and j["status"] == "done" and j["payload"]["source"] == "scanner"
                        for j in snap["jobs"]), "Preexisting combined source was not discovered by the scanner.")
            self.files.replace(TARGETS[2], SOURCE_IDS[2])
            # Only wait for a genuine running scan or its actual completion.
            self.wait("post-boot-scan-admission", lambda s: True if any(j["docPath"] == TARGETS[2]
                and j["status"] in {"running", "done"} for j in s["jobs"]) else None)
        a, _, _ = self.update(TARGETS[0], 1, a, "changed-a-source2")
        a, _, _ = self.update(TARGETS[0], 0, a, "restored-a-source1")
        self.preserve({TARGETS[0]: a})
        self.files.replace(TARGETS[1], SOURCE_IDS[1])
        if self.profile != "defaults":
            self.files.replace(TARGETS[2], SOURCE_IDS[2])
        snap, rows = self.wait_current({TARGETS[0]: 0, TARGETS[1]: 1, TARGETS[2]: 2}, "all-three-cap-one")
        b, _, b_points = self.publication(TARGETS[1], 1, "beyond-first-batch-b", snapshot=snap)
        c, _, c_points = self.publication(TARGETS[2], 2, "beyond-first-batch-c", snapshot=snap)
        a, a_plan, a_points = self.publication(TARGETS[0], 0, "present-a-not-missing", snapshot=snap)
        if self.profile == "defaults":
            require(any(j["payload"]["source"] == "watcher" and j["status"] == "done" for j in snap["jobs"]),
                    "The combined runtime did not observe a completed watcher admission.")
        self.preserve({TARGETS[1]: b, TARGETS[2]: c})
        if self.profile == "defaults":
            a, a_plan, a_points = self.combined_return_race(a)
        before, before_ids = a, set(self.retired_ids)
        self.files.remove(TARGETS[0])
        self.wait_removed(a, a_plan, a_points)
        self.files.replace(TARGETS[0], SOURCE_IDS[0])
        snap, _ = self.wait_current({TARGETS[0]: 0, TARGETS[1]: 1, TARGETS[2]: 2}, "durable-recreated-a")
        a, _, _ = self.publication(TARGETS[0], 0, "durable-recreated-a", snapshot=snap)
        if self.profile != "watcher":
            advanced(before, a, before_ids)
        self.preserve({TARGETS[0]: a, TARGETS[1]: b, TARGETS[2]: c})
        quiet = self.clock() + 6
        history = digest(snap["jobs"])
        while self.clock() < quiet:
            stable = self.snapshot()
            require(stable["publication"]["activeJobs"] == 0 and digest(stable["jobs"]) == history,
                    "The stable automatic sources created spontaneous new admissions during the quiet observation.")
            self.sleep(0.5)
        for path, points in ((TARGETS[1], b_points), (TARGETS[2], c_points)):
            actual = self.points([p["id"] for p in points])
            require(digest(sorted(actual, key=lambda p: p["id"])) == digest(sorted(points, key=lambda p: p["id"])),
                    "An unrelated protected document lost a real vector or payload.")
        return {"functionalPassed": True, "twoWorkers": self.overlap, "conditionalDeleteReturn": self.conditional,
            "maximumActiveJobsObserved": self.maximum_active, "steps": self.steps,
            "otherPublicationsAndFullVectorsPreserved": True, "quietObservationSeconds": 6,
            "passed": self.profile != "defaults" or self.overlap["status"] == "PASS"}


def owned_http(docker, owner, headers, role, route, method, body, end, *, cleanup=False):
    allowed = ((role == "backend" and ((method == "GET" and (route == "/ready" or route.startswith("/documents/context?")))
                or (method == "POST" and route == "/rag/search")
                or (cleanup and method == "POST" and route == "/admin/jobs/cancel")))
            or (role == "tei" and route == "/info" and method == "GET")
            or (role == "qdrant" and route == "/collections/" + owner["qdrantCollection"] + "/points" and method == "POST"))
    require(allowed and time.monotonic() < end, "An HTTP route, mutation or deadline is outside the automatic probe.")
    docker.inspect_all()
    require(time.monotonic() < end, "Ownership inspection exhausted the bounded HTTP deadline.")
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *unused):
            raise ValueError("Automatic qualification HTTP redirects are forbidden.")
    request = urllib.request.Request(owner["endpoints"][role] + route, method=method,
        data=None if body is None else json.dumps(body, allow_nan=False).encode("utf-8"),
        headers={"Content-Type": "application/json", **headers})
    remaining = end - time.monotonic()
    require(remaining > 0, "The HTTP deadline expired before opening transport.")
    with urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect()).open(request, timeout=min(30, remaining)) as response:
        require(response.status == 200, "A full HTTP200 observation is required.")
        data = response.read(16 * 1024 * 1024 + 1)
        require(len(data) <= 16 * 1024 * 1024 and time.monotonic() <= end, "HTTP capture exceeded its byte or wall bound.")
        return json.loads(data.decode("utf-8"))


def run(args):
    owner, manifest, profile = corpus.read_json(args.owner), corpus.read_json(args.manifest), corpus.read_json(args.embedding_profile)
    cases, plan = validate_inputs(owner, manifest, args.admission_profile,
        owner_sha=corpus.sha(args.owner), manifest_sha=corpus.sha(args.manifest))
    corpus.validate_inputs(owner, profile, manifest, list(SOURCE_IDS))
    require(30 <= args.maximum_wait_seconds <= 300, "Use a phase bound between30 and300 seconds.")
    plan.update(executeRequested=bool(args.execute), maximumPhaseSeconds=args.maximum_wait_seconds)
    if not args.execute:
        return plan
    require(sys.platform == "linux", "Execute only on the independently prepared owned Linux host.")
    root = Path(owner["root"])
    for path in (args.owner, args.manifest, args.embedding_profile, args.backend_identity, args.output,
                 args.api_key_file, args.source_directory, Path(owner["configurationPath"])):
        assert_owned_path(path, root)
    require(not args.output.exists(), "Use a new automatic evidence directory.")
    args.output.mkdir(mode=0o700)
    private = args.output / "private-observations"
    private.mkdir(mode=0o700)
    docker, headers = OwnedDocker(owner, execute=True), {}
    end = time.monotonic() + 1800
    probe = files = None
    result = {"schemaVersion": "saaia-linux-automatic-ingestion-execution.v1", "executed": True, "passed": False,
        "plan": plan, "startedAtUtc": utc(), "paidProviderCalls": 0, "canonicalRowsSeeded": False,
        "apiIngestionEnqueueCount": 0, "sqlMutations": 0, "cleanup": {}, "stage": "owned-preflight",
        "limits": ["Three distinct source PDFs and accelerated timings qualify a bounded Linux filesystem scenario, not a universal SLA.",
            "Two workers require two simultaneous SQL running leases; this does not prove parallel OCR computation.",
            "Watcher-only missing retains the last publication; scanner performs final deletion.",
            "A source-returned delete race not captured at runtime is NON_EXECUTED, with its separate causal PostgreSQL proof retained.",
            "An unsigned Development runtime is not a Production signature qualification."]}
    config_sha, model_hashes = None, {}

    def save(name, value):
        require(Path(name).name == name and name.endswith(".private.json"), "Private captures need a new simple filename.")
        corpus.write_json(private / name, value)

    def api(role, route, method="GET", body=None):
        return owned_http(docker, owner, headers, role, route, method, body, end)

    try:
        safe, inspected = docker.inspect_all()
        backend_record = next(r for r in owner["containers"] if r["role"] == "backend")
        backend = next(c for c in inspected if c["Id"] == backend_record["id"])
        config_path = Path(owner["configurationPath"])
        config, config_sha = corpus.read_json(config_path), corpus.sha(config_path)
        result["observedAdmissionConfiguration"] = validate_configuration(config, backend, args.admission_profile, corpus.validate_disabled_llm)
        require(all(config.get("Rag", {}).get(k) == v for k, v in profile["Rag"].items() if k != "EmbeddingsBaseUrl"),
                "The bound embedding identity/input format differs from the independently frozen native TEI profile.")
        qdrant_record = next(r for r in owner["containers"] if r["role"] == "qdrant")
        validate_gc_configuration(owner, config, next(c for c in inspected if c["Id"] == qdrant_record["id"]))
        expected_binary = corpus.read_json(args.backend_identity)
        binary = docker.command(["exec", backend_record["id"], "sha256sum", "/app/SAAIA.Backend.dll"]).split()[0]
        validate_backend_identity(expected_binary, binary, backend_record["imageId"])
        docker.command(["exec", backend_record["id"], "test", "!", "-e", "/app/appsettings.Local.json"])
        corpus.write_json(args.output / "backend-binary-identity.json", {"assemblySha256": binary, "imageId": backend_record["imageId"]})
        corpus.write_json(args.output / "runtime-ownership.json", {"containers": safe, "signedConfigurationSha256": config_sha})
        files = ControlledFiles(root, Path(owner["documentsRoot"]), args.source_directory, cases, docker.inspect_all)
        files.originals()
        if args.admission_profile == "defaults":
            target = files.target(TARGETS[0])
            started = datetime.datetime.fromisoformat(backend["State"]["StartedAt"].replace("Z", "+00:00"))
            require(target.is_file() and corpus.sha(target) == cases[0]["sourceSha256"]
                    and target.stat().st_mtime <= started.timestamp(), "Combined source A must actually preexist before backend startup.")
            files.expected[TARGETS[0]] = cases[0]["sourceSha256"]
        files.verify()
        require(args.api_key_file.is_file() and args.api_key_file.stat().st_size <= 2048, "A bounded isolated synthetic admin key file is required.")
        key = args.api_key_file.read_text(encoding="utf-8").strip()
        require(bool(key) and all(33 <= ord(c) <= 126 for c in key), "Malformed isolated runtime key.")
        headers.update(corpus.synthetic_admin_headers(key))
        ready = api("backend", "/ready")
        save("ready.private.json", ready)
        result["boundIngestionReadiness"] = validate_bound_readiness(ready)
        result.update(corpus.qualification_identity(owner, backend, ready))
        image = json.loads(docker.command(["image", "inspect", qdrant_record["imageId"]]))[0]
        corpus.write_json(args.output / "bound-runtime-provenance.json",
            corpus.readiness_provenance(profile, config, ready, qdrant_record["imageId"], image))
        parser = json.loads(docker.sql(queries.TEXT_PARSER))
        require(len(parser) == 4 and all(v is True for v in parser.values()), "The actual PostgreSQL Unicode text parser is incompatible.")
        corpus.write_json(args.output / "text-parser-conformance.json", parser)
        tei_record = next(r for r in owner["containers"] if r["role"] == "tei")
        tei = next(c for c in inspected if c["Id"] == tei_record["id"])
        binding = tei_model_binding(profile, tei)
        for relative, expected in profile["modelArtifacts"].items():
            path = assert_owned_path(Path(binding["hostModelRoot"]) / relative, root)
            require(mounted_source(tei, binding["containerModelRoot"] + "/" + relative) == str(path)
                    and path.is_file() and corpus.sha(path) == expected,
                    "An actual mounted TEI artifact differs from the frozen profile or is hidden by another bind.")
            model_hashes[str(path)] = expected
        info = api("tei", "/info")
        attest_info(info, profile, owner["endpoints"]["tei"])
        evidence = {"imageId": tei["Image"], "modelFileSha256": model_hashes[str(Path(binding["hostModelRoot"]) / profile["modelFileRelativePath"])],
            "modelRevision": binding["modelRevision"], "autoTruncate": "--auto-truncate" in tei["Config"].get("Cmd", []), "ownedContainerVerified": True}
        attest_tei_container(profile, evidence)
        corpus.write_json(args.output / "tei-attestation.json", {"passed": True, "nativeInfo": info, "container": evidence,
            "selectedSnapshotBindMapping": binding, "modelArtifactSha256": profile["modelArtifacts"], "kernelFileOpenTracingPerformed": False})
        probe = AutomaticProbe(owner, cases, config, args.admission_profile, docker, files, api, save,
            maximum_wait=args.maximum_wait_seconds)
        probe.end = end
        result["stage"] = "filesystem-only-automatic-lifecycle"
        result.update(probe.execute())
        checksums = json.loads(docker.sql(queries.TEXT_CHECKSUMS))
        require(len(checksums.get("tables", [])) == 4 and all(t.get("mismatches") == 0 for t in checksums["tables"]),
                "Automatic canonical text differs from its stored UTF8 checksums.")
        corpus.write_json(args.output / "sql-text-conformance.json", checksums)
        files.verify()
        require(corpus.sha(config_path) == config_sha and all(corpus.sha(Path(p)) == expected for p, expected in model_hashes.items()),
                "Mounted configuration or actual model artifacts changed during automatic qualification.")
        require(time.monotonic() <= end, "Final observations exceeded the automatic campaign wall deadline.")
        result["stage"] = "completed"
        result["qualificationFinishedAtUtc"] = utc()
    except Exception as failure:
        result.update(passed=False, failureType=type(failure).__name__, failureStage=result["stage"])
        save("failure.private.json", {"type": type(failure).__name__, "message": str(failure), "stage": result["stage"]})
    finally:
        if probe is not None:
            result.update(steps=probe.steps, twoWorkers=probe.overlap, conditionalDeleteReturn=probe.conditional,
                          maximumActiveJobsObserved=probe.maximum_active)
        cleanup_end = time.monotonic() + 60
        try:
            # Cleanup intentionally does not depend on valid source bytes. All
            # scope/ownership checks remain active, even after a malformed PDF.
            snapshot = json.loads(docker.sql(snapshot_sql()))
            scope(snapshot, allow_failed=True)
            canceled = set()
            while True:
                active = [j for j in snapshot["jobs"] if j["status"] in ACTIVE]
                for job in active:
                    if job["jobId"] not in canceled:
                        reply = owned_http(docker, owner, headers, "backend", "/admin/jobs/cancel", "POST",
                            {"jobId": job["jobId"]}, cleanup_end, cleanup=True)
                        save("cleanup-cancel-" + job["jobId"] + ".private.json", reply)
                        canceled.add(job["jobId"])
                if not active or time.monotonic() >= cleanup_end:
                    break
                time.sleep(0.25)
                snapshot = json.loads(docker.sql(snapshot_sql()))
                scope(snapshot, allow_failed=True)
            result["cleanup"]["cancelRequests"] = len(canceled)
        except Exception as failure:
            result["cleanup"]["cancelObservationFailureType"] = type(failure).__name__
        try:
            # Stop the producer before the final active-job SELECT. Automatic
            # scanner/watcher otherwise could enqueue after a zero observation.
            _, raw = docker.inspect_all()
            backend_record = next(r for r in owner["containers"] if r["role"] == "backend")
            validate_container(owner, backend_record, next(c for c in raw if c["Id"] == backend_record["id"]))
            docker.command(["stop", "--time", "45", backend_record["id"]], timeout=65)
            docker.inspect_all(require_running=False)
            pg = next(r for r in owner["containers"] if r["role"] == "postgres")
            active = int(docker.command(["exec", "-i", pg["id"], "psql", "-X", "-A", "-t", "-q", "-w",
                "-v", "ON_ERROR_STOP=1", "--username", owner["databaseUser"], "--dbname", owner["database"]],
                stdin="BEGIN READ ONLY;\n" + queries.ACTIVE + ";\nROLLBACK;\n", timeout=30).strip())
            result["cleanup"].update(zeroActiveJobs=active == 0, activeJobsAfterBackendStopped=active,
                                     producerStoppedBeforeFinalJobObservation=True)
        except Exception as failure:
            result["cleanup"]["finalJobObservationFailureType"] = type(failure).__name__
        try:
            result["cleanup"].update(docker.stop())
        except Exception as failure:
            result["cleanup"]["stopFailureType"] = type(failure).__name__
        result["passed"] = bool(result["passed"] and corpus.cleanup_success(result["cleanup"]))
        result["deploymentQualificationPassed"] = result["passed"] and result.get("deploymentQualificationEligible") is True
        result["finishedAtUtc"] = utc()
        corpus.write_json(args.output / "automatic-ingestion-result.json", result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("owner", "manifest", "embedding-profile", "backend-identity", "source-directory", "api-key-file", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--admission-profile", choices=sorted(PROFILES), required=True)
    parser.add_argument("--maximum-wait-seconds", type=int, default=300)
    parser.add_argument("--execute", action="store_true")
    result = run(parser.parse_args())
    print(json.dumps(result if not result.get("executed") else {"passed": result["passed"], "stage": result["stage"],
        "twoWorkers": result.get("twoWorkers"), "conditionalDeleteReturn": result.get("conditionalDeleteReturn"),
        "paidProviderCalls": 0, "apiIngestionEnqueueCount": 0, "cleanup": result["cleanup"]}))
    return 0 if not result.get("executed") or result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
