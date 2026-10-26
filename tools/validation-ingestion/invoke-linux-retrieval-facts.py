"""Read all frozen fact annotations through real canonical search then source read.

NOEXEC by default. No build/start/seed/enqueue/LLM. Execute reads the synthetic
key, verifies the current owned publications, then stops the exact five IDs.
Use the same --campaign-deadline-utc for Gold and recipe to enforce 15 minutes
across their two namespaces. No extra readiness HTTP is hidden in the budget.
"""
from __future__ import annotations

import argparse
import copy
import datetime
import importlib.util
import json
from pathlib import Path
import re
import sys
import time
import urllib.parse
import urllib.request
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from linux_owned_runtime import OwnedDocker, assert_owned_path
from linux_lifecycle_checks import require, validate_configuration_continuity
from linux_retrieval_fact_checks import baseline, deadline, fact_check, negative_check, observation_identity, validate_inputs
from tools.ingestion_gold.critical_facts import exact_text
import linux_runtime_queries as queries

_spec = importlib.util.spec_from_file_location("retrieval_prior_corpus", Path(__file__).with_name("invoke-linux-ingestion.py"))
corpus = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(corpus)


def owned_http(docker, owner, headers, route, method, body, deadline_monotonic, *, clock=time.monotonic, opener_factory=None):
    require((method == "POST" and route == "/rag/search") or (method == "GET" and route.startswith("/documents/context?")),
            "Only current canonical search/read routes may execute in this observer.")
    require(clock() < deadline_monotonic, "The shared wall deadline expired before ownership inspection.")
    docker.inspect_all()
    require(clock() < deadline_monotonic, "The shared wall deadline expired during ownership inspection.")
    request = urllib.request.Request(owner["endpoints"]["backend"] + route, method=method,
        data=None if body is None else json.dumps(body, allow_nan=False).encode("utf-8"),
        headers={"Content-Type": "application/json", **headers})
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *unused):
            raise ValueError("Retrieval redirects are forbidden.")
    opener = (opener_factory or urllib.request.build_opener)(urllib.request.ProxyHandler({}), NoRedirect())
    remaining = deadline_monotonic - clock()
    require(remaining > 0, "The shared wall deadline expired before opening HTTP.")
    with opener.open(request, timeout=min(15, remaining)) as response:
        require(response.status == 200, "A full 200 canonical search/read observation is required.")
        raw = response.read(8 * 1024 * 1024 + 1)
        require(len(raw) <= 8 * 1024 * 1024, "The retrieval response exceeds its bounded capture size.")
        require(clock() <= deadline_monotonic, "The shared wall deadline expired while reading HTTP.")
        return json.loads(raw.decode("utf-8"))


def require_finished_in_window(deadline_monotonic, *, clock=time.monotonic):
    require(clock() <= deadline_monotonic, "The final current-publication observation exceeded the shared retrieval window.")


class FactProbe:
    def __init__(self, api, save, *, maximum_calls, deadline_monotonic, clock=time.monotonic):
        self.transport, self.save = api, save
        self.maximum_calls, self.deadline, self.clock = maximum_calls, deadline_monotonic, clock
        self.calls, self.results, self.controls, self.cache = 0, [], [], {}

    def request(self, route, method="GET", body=None):
        remaining = self.deadline - self.clock()
        require(self.calls < self.maximum_calls and remaining > 0, "The fixed HTTP-count or shared campaign deadline was reached.")
        self.calls += 1
        value = self.transport(route, method, body, min(15, remaining))
        self.save(f"{self.calls:04d}-http.private.json", {"route": route, "method": method, "request": body, "response": value})
        return value

    def execute(self, cases, rows):
        for case in cases:
            row = rows[case["caseId"]]
            chunks = {c["retrievalChunkId"]: c for c in row["chunks"]}
            for index, fact in enumerate(case["expectations"]["criticalFacts"]):
                started = self.clock()
                request = {"query": fact["text"], "docId": row["docId"], "sourceBackedCanonical": True,
                    "pageStart": fact["page"], "pageEnd": fact["page"], "topK": 20, "maxPerDoc": 20, "maxPerPage": 20}
                response = self.request("/rag/search", "POST", request)
                require(response.get("query") == fact["text"], "Search query echo differs from the frozen annotation.")
                pattern = re.compile(r"(?<!\w)" + re.escape(exact_text(fact["text"])) + r"(?!\w)")
                chosen = next((item for item in response.get("items", []) if item.get("chunkId") in chunks
                               and pattern.search(exact_text(item.get("text", "")))), None)
                read, reused = {}, False
                if chosen:
                    key = (row["docId"], row["revisionId"], chosen["chunkId"], row["sourceSha256"])
                    reused = key in self.cache
                    if not reused:
                        query = {"docId": row["docId"], "chunkId": chosen["chunkId"]}
                        self.cache[key] = {"request": query, "response": self.request("/documents/context?" +
                            urllib.parse.urlencode({**query, "before": 0, "after": 0, "limit": 5}))}
                    read = self.cache[key]
                result = fact_check(case, row, fact, response, read)
                result.update({"caseId": case["caseId"], "latencySeconds": round(self.clock() - started, 6), "readReused": reused})
                self.results.append(result)
                if index == 0:
                    negatives = []
                    absent = str(uuid.uuid4())
                    while absent in {r["docId"] for r in rows.values()}:
                        absent = str(uuid.uuid4())
                    for kind in ("absent_query", "absent_document"):
                        control = copy.deepcopy(request)
                        control["query" if kind == "absent_query" else "docId"] = "saaianomatch" + uuid.uuid4().hex if kind == "absent_query" else absent
                        negatives.append({"kind": kind, "request": control, "response": self.request("/rag/search", "POST", control)})
                    self.controls.append({"caseId": case["caseId"], **negative_check(case, row, fact, request, response, read, negatives)})
        return bool(self.results and all(r["passed"] for r in self.results) and all(r["passed"] for r in self.controls))


def run(args):
    owner, manifest, prior = corpus.read_json(args.owner), corpus.read_json(args.manifest), corpus.read_json(args.corpus_proof)
    cases, plan = validate_inputs(owner, prior, manifest, owner_sha=corpus.sha(args.owner), manifest_sha=corpus.sha(args.manifest))
    plan.update({"executeRequested": bool(args.execute), "corpusProofSha256": corpus.sha(args.corpus_proof),
                 "campaignDeadlineUtc": args.campaign_deadline_utc, "maximumRetrievalWindowSeconds": 900,
                 "maximumRequestSeconds": 15, "transportRetryCount": 0})
    if not args.execute:
        return plan
    require(sys.platform == "linux", "Execute only on the independently prepared owned Linux host.")
    end = deadline(args.campaign_deadline_utc, datetime.datetime.now(datetime.timezone.utc))
    mono_end = time.monotonic() + (end - datetime.datetime.now(datetime.timezone.utc)).total_seconds()
    root = Path(owner["root"])
    for path in (args.owner, args.manifest, args.corpus_proof, args.output, args.api_key_file, args.source_directory,
                 Path(owner["configurationPath"])):
        assert_owned_path(path, root)
    require(not args.output.exists(), "Use a fresh read-only retrieval evidence directory.")
    args.output.mkdir(mode=0o700)
    private = args.output / "private-observations"
    private.mkdir(mode=0o700)
    docker, headers, probe = OwnedDocker(owner, execute=True), {}, None
    result = {"schemaVersion": "saaia-linux-retrieval-facts-execution.v1", "executed": True, "passed": False,
        "plan": plan, "startedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "canonicalRowsSeeded": False, "newJobAdmissions": 0, "paidProviderCalls": 0, "stage": "owned-preflight",
        "cleanup": {}, "facts": [], "negativeControls": [], "readinessHttpRequeried": False,
        "limits": ["Literal annotation recall is distinct from natural-language ranking and final LLM reasoning.",
                   "Physical facts are independently checked in the current source-bound canonical bundle; read chunks may span pages.",
                   "Runtime provenance is linked to the prior real PASS, exact CID/image/config/DLL; no extra readiness HTTP is hidden.",
                   "No job is admitted or canceled by this observer; the exact five owned containers are stopped in finally."]}
    before, sources = None, []

    def save(name, value):
        require(Path(name).name == name and name.endswith(".private.json"), "Only new simple private capture filenames are allowed.")
        corpus.write_json(private / name, value)

    def check_sources():
        for source, target, expected in sources:
            for path in (source, target):
                assert_owned_path(path, root)
                require(path.is_file() and corpus.sha(path) == expected, "A source PDF changed from its frozen physical annotation manifest.")

    def api(route, method, body, timeout):
        return owned_http(docker, owner, headers, route, method, body, mono_end)

    try:
        safe, inspected = docker.inspect_all()
        backend_record = next(r for r in owner["containers"] if r["role"] == "backend")
        backend = next(c for c in inspected if c["Id"] == backend_record["id"])
        config_path = Path(owner["configurationPath"])
        config = corpus.read_json(config_path)
        corpus.validate_disabled_llm(config, backend)
        identity_path = assert_owned_path(args.corpus_proof.parent / "runtime-ownership.json", root)
        validate_configuration_continuity(corpus.sha(config_path), corpus.read_json(identity_path))
        binary_path = assert_owned_path(args.corpus_proof.parent / "backend-binary-identity.json", root)
        expected = corpus.read_json(binary_path)
        actual = docker.command(["exec", backend_record["id"], "sha256sum", "/app/SAAIA.Backend.dll"]).split()[0]
        require(re.fullmatch(r"[a-f0-9]{64}", actual) is not None and actual == expected.get("assemblySha256"),
                "Current backend DLL differs from the prior real corpus PASS.")
        docker.command(["exec", backend_record["id"], "test", "!", "-e", "/app/appsettings.Local.json"])
        corpus.write_json(args.output / "runtime-identity.json", {"backendAssemblySha256": actual,
            "configurationSha256": corpus.sha(config_path), "containers": safe})
        for case in cases:
            source = assert_owned_path(args.source_directory / (case["caseId"] + ".pdf"), root)
            target = assert_owned_path(Path(owner["documentsRoot"]) / source.name, root)
            require(source != target, "Original and working PDF copies must be distinct.")
            sources.append((source, target, case["sourceSha256"]))
        check_sources()
        before = json.loads(docker.sql(queries.PUBLICATION))
        save("baseline-publication.private.json", before)
        rows = baseline(cases, before)
        checksums = json.loads(docker.sql(queries.TEXT_CHECKSUMS))
        corpus.write_json(args.output / "sql-text-conformance.json", checksums)
        require(len(checksums.get("tables", [])) == 4 and all(r.get("mismatches") == 0 for r in checksums["tables"]),
                "Current stored text/checksum conformance failed before retrieval.")
        require(args.api_key_file.is_file() and args.api_key_file.stat().st_size <= 2048, "A bounded isolated synthetic key file is required.")
        key = args.api_key_file.read_text(encoding="utf-8").strip()
        require(bool(key) and all(33 <= ord(c) <= 126 for c in key), "Malformed isolated backend key.")
        headers.update(corpus.synthetic_admin_headers(key))
        probe = FactProbe(api, save, maximum_calls=plan["maximumHttpCalls"], deadline_monotonic=mono_end)
        result["stage"] = "source-bound-fact-search-and-read"
        okay = probe.execute(cases, rows)
        check_sources()
        after = json.loads(docker.sql(queries.PUBLICATION))
        save("final-publication.private.json", after)
        stable = observation_identity(before) == observation_identity(after)
        require(stable, "Current publications, revisions, job history or active-job state changed during observation.")
        require_finished_in_window(mono_end)
        result.update({"sourceAndPublicationStable": True, "currentCanonicalPhysicalFactsVerified": True,
            "factCoverageComplete": len(probe.results) == plan["factCount"], "caseControlCoverageComplete": len(probe.controls) == len(cases),
            "retrievalFinishedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            "passed": bool(okay and len(probe.results) == plan["factCount"]), "stage": "completed"})
    except Exception as failure:
        result.update({"failureType": type(failure).__name__, "failureStage": result["stage"]})
        save("failure.private.json", {"type": type(failure).__name__, "message": str(failure), "stage": result["stage"]})
    finally:
        if probe is not None:
            result.update({"facts": probe.results, "negativeControls": probe.controls, "httpAttemptCount": probe.calls,
                "passedFactCount": sum(x["passed"] for x in probe.results), "failedFactCount": sum(not x["passed"] for x in probe.results)})
        try:
            active = int(docker.sql(queries.ACTIVE))
            result["cleanup"].update({"zeroActiveJobs": active == 0, "activeJobsBeforeStop": active, "jobsCanceled": 0})
        except Exception as failure:
            result["cleanup"]["observationFailureType"] = type(failure).__name__
        try:
            result["cleanup"].update(docker.stop())
        except Exception as failure:
            result["cleanup"]["stopFailureType"] = type(failure).__name__
        result["passed"] = bool(result["passed"] and corpus.cleanup_success(result["cleanup"]))
        # Preserve the prior qualification boundary; do not turn unsigned Dev into Production.
        result["qualificationMode"] = prior.get("qualificationMode")
        result["deploymentQualificationEligible"] = prior.get("deploymentQualificationEligible") is True
        result["deploymentQualificationPassed"] = result["passed"] and result["deploymentQualificationEligible"]
        result["finishedAtUtc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        corpus.write_json(args.output / "retrieval-facts-result.json", result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("owner", "manifest", "corpus-proof", "source-directory", "output", "api-key-file"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--campaign-deadline-utc")
    parser.add_argument("--execute", action="store_true")
    result = run(parser.parse_args())
    print(json.dumps(result if not result.get("executed") else {"passed": result["passed"], "httpAttemptCount": result.get("httpAttemptCount", 0),
        "passedFactCount": result.get("passedFactCount", 0), "cleanup": result["cleanup"], "paidProviderCalls": 0}))
    return 0 if not result.get("executed") or result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
