"""Drive only the owned loopback backend APIs; never talks to a model endpoint."""
import argparse
import json
import pathlib
import subprocess
import time
import uuid
from urllib.parse import urlsplit
from urllib.request import ProxyHandler, Request, HTTPRedirectHandler, build_opener


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        raise ValueError("owned_backend_redirect_forbidden")


def write_new(path, value):
    with path.open("x", encoding="utf-8") as handle:
        json.dump(value, handle, ensure_ascii=False, indent=2)


def run(args):
    root = args.runtime.resolve()
    owner = json.loads((root / "environment.json").read_text(encoding="utf-8-sig"))
    advanced = json.loads((root / "advanced-worker.json").read_text(encoding="utf-8-sig"))
    if owner["schemaVersion"] != "saaia-winui-local-runtime.v1" or pathlib.Path(owner["outputDirectory"]).resolve() != root:
        raise ValueError("owned_runtime_required")
    if not advanced.get("wireQualification") or owner["state"] != "ready":
        raise ValueError("ready_wire_worker_required")
    base = advanced["backendUrl"]
    parsed = urlsplit(base)
    if parsed.scheme != "http" or parsed.hostname != "127.0.0.1" or not 1024 <= parsed.port <= 65535:
        raise ValueError("loopback_backend_required")
    secrets = json.loads((root / "runtime" / "fixture-secrets.json").read_text(encoding="utf-8-sig"))
    cases = json.loads(args.cases.read_text(encoding="utf-8-sig"))["cases"]
    opener = build_opener(ProxyHandler({}), NoRedirect())

    def http(path, method="GET", body=None, session=None, turn=None):
        if not path.startswith("/") or path.startswith("//"):
            raise ValueError("owned_relative_route_required")
        headers = {"X-Api-Key": secrets["apiKey"], "Content-Type": "application/json"}
        if session:
            headers["X-SAAIA-Session-Id"] = session
        if turn:
            headers["X-SAAIA-Turn-Id"] = turn
        raw = None if body is None else json.dumps(body, ensure_ascii=False).encode("utf-8")
        with opener.open(Request(base + path, data=raw, method=method, headers=headers), timeout=15) as response:
            return json.loads(response.read().decode("utf-8"))

    for case in cases:
        case = case | {"variant": args.variant}
        case_dir = root / case["caseId"]
        case_dir.mkdir()
        control = root / "wire-control.json"
        temporary = root / ("wire-control-" + uuid.uuid4().hex + ".tmp")
        write_new(temporary, {"caseId": case["caseId"], "scenario": case})
        temporary.replace(control)
        user = str(uuid.uuid4())
        turn = "wire-" + uuid.uuid4().hex
        session_request = {"userId": user, "title": "Validation du transport documentaire", "clientUser": "wire-qualification"}
        session = http("/chat/sessions", "POST", session_request, turn=turn)
        session_id = session["sessionId"]
        handoff = {"schemaVersion": "saaia.advanced-analysis-handoff.v1", "handoffId": str(uuid.uuid4()),
            "createdAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "requestText": "Qualification de littéraux et d'identités dans le document " + case["docPath"] +
                " : vérifie une fenêtre primaire et les observations natives en page physique " + str(case["physicalPage"]) +
                ", puis une lecture canonique bornée en page physique " + str(case["lightPrimaryPage"]) +
                ". Copie finalement une ligne native exacte. Ne déduis aucune interprétation du tableau.",
            "language": "fr", "originIntent": "rag.answer", "reasonCode": "wire_qualification",
            "transferStage": "after_retrieval",
            "load": {"planKind": "wire_qualification", "deliverable": "citation littérale contrôlée",
                "answerUnitCount": 1, "atomicEvidenceCount": 1, "rowCount": 0, "columnCount": 0,
                "structuredLayout": False, "atomicEvidenceType": "content_claim", "atomicEvidenceMode": "single_item",
                "selectionPolicy": "single_item", "questionFocus": "content", "requestedDocumentName": "",
                "boundedNamedDocumentExtraction": False, "candidateScopePaths": [], "rowLabels": [], "columns": []},
            "researchState": {"memoryIsEvidence": False, "evidenceRevalidationRequired": True,
                "executedTools": [], "executedQueries": [], "attempts": [], "evidenceReferences": [{
                    "evidenceId": "wire-initial-" + case["caseId"], "docId": case["docId"],
                    "revisionId": case["revisionId"], "fileName": case["docPath"], "docPath": case["docPath"],
                    "sourceHash": case["sourceSha256"], "chunkId": case["initialChunkId"],
                    "pageStart": case["primaryPage"], "pageEnd": case["primaryPage"]}]},
            "dataPolicy": {"externalProviderContentAuthorized": False, "externalProviderMetadataAuthorized": False,
                "authorizationSource": "owned_loopback_wire_qualification"}}
        request = {"userId": user, "sessionId": session_id, "handoff": handoff}
        if args.variant == "source-hash-forged":
            request["handoff"]["researchState"]["evidenceReferences"][0]["sourceHash"] = "0" * 64
        write_new(case_dir / "api-create-request.json", request)
        job = http("/advanced-analysis/jobs", "POST", request, session_id, turn)
        write_new(case_dir / "api-created.json", job)
        deadline = time.monotonic() + 180
        snapshots = [job]
        while job["status"] in ("queued", "running"):
            if time.monotonic() > deadline:
                raise TimeoutError("owned_wire_job_deadline")
            time.sleep(0.2)
            job = http("/advanced-analysis/jobs/" + job["jobId"] + "?userId=" + user, session=session_id, turn=turn)
            if job["revision"] != snapshots[-1]["revision"]:
                snapshots.append(job)
        write_new(case_dir / "api-snapshots.json", snapshots)
        write_new(case_dir / "api-final.json", job)
        inspected = subprocess.run([str(args.dotnet), str(args.importer), "inspect-job", "--owner", str(root / "environment.json"),
            "--job-id", job["jobId"], "--output", str(case_dir / "sql-job.json")], capture_output=True, text=True, timeout=60)
        (case_dir / "sql-inspect.stdout.log").write_text(inspected.stdout, encoding="utf-8")
        (case_dir / "sql-inspect.stderr.log").write_text(inspected.stderr, encoding="utf-8")
        if inspected.returncode:
            raise ValueError("owned_job_sql_inspection_failed")
        expected_errors = {"source-hash-forged": "evidence_revalidation_failed",
                           "invented-citation": "advanced_writer_evidence_id_invalid", "output-limit": "advanced_llm_output_limit"}
        if args.variant in expected_errors:
            if job["status"] != "failed" or job.get("result") is not None or job.get("lastErrorCode") != expected_errors[args.variant]:
                raise ValueError("controlled_wire_rejection_not_observed:" + str(job.get("lastErrorCode")))
        elif job["status"] != "succeeded" or job.get("result", {}).get("outcome") != "answered":
            raise ValueError("controlled_wire_job_did_not_succeed:" + str(job.get("lastErrorCode")))
    write_new(root / "api-run-completed.json", {"schemaVersion": "saaia-wire-api-run-v1", "cases": len(cases),
              "fakeProvider": True, "realApiWorkerSqlTools": True, "externalCalls": 0, "passed": True})


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime", type=pathlib.Path, required=True)
    parser.add_argument("--cases", type=pathlib.Path, required=True)
    parser.add_argument("--dotnet", type=pathlib.Path, required=True)
    parser.add_argument("--importer", type=pathlib.Path, required=True)
    parser.add_argument("--variant", choices=("positive", "native-raw-sha-correction", "source-hash-forged",
                                            "invented-citation", "output-limit"), default="positive")
    run(parser.parse_args())
