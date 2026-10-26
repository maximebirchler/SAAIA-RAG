"""Qualify an already prepared, owned Linux Docker stack; no bootstrap or SSH.

Without --execute: validate public inputs and print a plan, never Docker/HTTP/key.
With --execute: real API enqueue, read-only SQL, canonical/search/vector checks,
then cancellation of owned pending jobs and exact-ID container stop in finally.
"""
from __future__ import annotations

import argparse
import copy
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sys
import time
import urllib.parse
import urllib.request
import urllib.error
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
sys.path.insert(0, str(Path(__file__).resolve().parent))
from embedding_attestation import attest_info, attest_tei_container, tei_model_binding, validate_expected_profile
from linux_owned_runtime import OwnedDocker, assert_owned_path, mounted_source, validate_owner
import linux_runtime_queries as queries
from vector_point_identity import vector_point_id
from verify_worker_publication import verify
from canonical_search_checks import search_fact
from tools.ingestion_gold.contracts import select_cases, validate_manifest
from tools.ingestion_gold.critical_facts import exact_text


def sha(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def read_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write_json(path: Path, value: object) -> None:
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2, allow_nan=False)
        stream.write("\n")


def synthetic_admin_headers(key: str) -> dict[str, str]:
    """One isolated bootstrap admin key, never a regular user's credentials."""
    return {"X-Api-Key": key, "X-Admin-Key": key}


def parse_job_listing(response: dict) -> list[dict]:
    """Dapper dictionaries preserve PascalCase, unlike typed camelCase DTOs.

    Accept the two explicit wire forms; never silently coalesce conflicting
    aliases or treat missing status as a running job until a timeout.
    """
    if not isinstance(response, dict):
        raise ValueError("Ingestion job listing must be an explicit JSON object.")
    def field(value: dict, camel: str, pascal: str):
        present = [name for name in (camel, pascal) if name in value]
        if len(present) != 1:
            raise ValueError("Ingestion job wire has a missing or ambiguous required field: " + camel)
        return value[present[0]]
    rows = field(response, "items", "Items")
    if not isinstance(rows, list) or len(rows) > 200:
        raise ValueError("Ingestion job listing must be a bounded explicit array.")
    parsed = []
    for row in rows:
        if not isinstance(row, dict):
            raise ValueError("An ingestion job wire item is not an object.")
        item = {name: field(row, name, name[0].upper() + name[1:])
                for name in ("jobId", "action", "docPath", "status", "attempts")}
        if (not isinstance(item["jobId"], str) or not isinstance(item["docPath"], str)
                or not item["docPath"] or len(item["docPath"]) > 4096
                or item["action"] not in {"upsert", "delete"}
                or item["status"] not in {"queued", "running", "paused", "done", "failed", "canceled", "cancelled"}
                or type(item["attempts"]) is not int or item["attempts"] < 0):
            raise ValueError("Ingestion job wire identity, action, status or attempts is invalid.")
        item["jobId"] = str(uuid.UUID(item["jobId"]))
        parsed.append(item)
    if len({item["jobId"] for item in parsed}) != len(parsed):
        raise ValueError("An ingestion job is duplicated in the wire listing.")
    return parsed


def validate_disabled_llm(config: dict, backend: dict) -> None:
    # The signed deployment document wins over environment in Production.
    env = dict(item.split("=", 1) for item in backend.get("Config", {}).get("Env", []) if "=" in item)
    advanced = config.get("AdvancedAnalysis", {})
    if (advanced.get("WorkerEnabled") is not False or advanced.get("Provider") != "disabled"
            or config.get("License", {}).get("AdvancedAnalysisEnabled") is not False):
        raise ValueError("The signed qualification config must explicitly disable advanced/provider capabilities.")
    for name in ("AllowExternalProviderContent", "AllowExternalProviderMetadata"):
        if advanced.get(name) is not False:
            raise ValueError("External corpus transmission must be explicitly disabled.")
    cap_b = config.get("RuntimeGovernance", {}).get("CapabilityBWorkerEnabled")
    if cap_b is None:
        cap_b = env.get("RuntimeGovernance__CapabilityBWorkerEnabled", "").lower() == "true" if "RuntimeGovernance__CapabilityBWorkerEnabled" in env else None
    if cap_b is not False or env.get("BACKOFFICE_LLM_ENABLED", "false").lower() != "false":
        raise ValueError("Automatic/backoffice LLM workers must be explicitly disabled.")
    ingestion = config.get("Ingestion", {})
    for name in ("WatcherEnabled", "ScannerEnabled"):
        value = ingestion.get(name)
        if value is None and "Ingestion__" + name in env:
            value = env["Ingestion__" + name].lower() == "true"
        if value is not False:
            raise ValueError("Only API-triggered ingestion may run in this isolated qualification.")


def validate_inputs(owner: dict, profile: dict, manifest: dict, selected: list[str]) -> tuple[list[dict], dict]:
    validate_owner(owner)
    if validate_expected_profile(profile, owner["endpoints"]["tei"]) != "tei_cpu":
        raise ValueError("The Linux deployment qualification requires native TEI, not a substitute.")
    required_artifacts = {"config.json", "tokenizer.json", "1_Pooling/config.json"}
    if not required_artifacts.issubset(profile.get("modelArtifacts", {})) or not profile.get("modelFileRelativePath"):
        raise ValueError("Linux TEI requires independently hashed weights, tokenizer, model and pooling configuration.")
    cases = select_cases(validate_manifest(manifest), selected)
    if not selected or len(selected) != len(set(selected)):
        raise ValueError("Select a nonempty, distinct corpus subset.")
    return cases, {"schemaVersion": "saaia-linux-ingestion-plan.v1", "executeRequested": False,
                   "runId": owner["runId"], "project": owner["project"], "caseIds": selected,
                   "sourceCount": len(cases), "embeddingAdapter": "tei_cpu", "embeddingsReal": True,
                   "canonicalRowsSeeded": False, "paidProviderEnabled": False, "vectorValidated": False,
                   "bootstrapPerformedByRunner": False, "productStatus": "TESTE_NON_APPROUVE"}


def cleanup_success(cleanup: dict) -> bool:
    return all(cleanup.get(name) is True for name in
               ("zeroActiveJobs", "ownedContainersStopped", "dockerPublishedPortBindingsAbsent"))


def qualification_identity(owner: dict, backend: dict, readiness: dict) -> dict:
    env = dict(item.split("=", 1) for item in backend.get("Config", {}).get("Env", []) if "=" in item)
    declared_environments = {env[name] for name in ("DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT") if env.get(name)}
    if len(declared_environments) > 1:
        raise ValueError("Conflicting .NET environment declarations cannot qualify a deployment.")
    environment = next(iter(declared_environments), "Production")
    details = readiness.get("details", {})
    signature = details.get("config_signature_verified") is True
    mode = owner.get("qualificationMode", "production")
    if mode == "production" and (environment != "Production" or not signature):
        raise ValueError("Production qualification requires the real Production environment and verified signature.")
    if mode == "development_diagnostic" and environment != "Development":
        raise ValueError("The explicit diagnostic mode is reserved for the observed Development environment.")
    return {"qualificationMode": mode, "environmentObservedFromContainer": environment,
            "configurationSignatureMode": details.get("config_signature_mode"),
            "configurationSignatureVerified": signature,
            "deploymentQualificationEligible": mode == "production" and environment == "Production" and signature}


def readiness_provenance(profile: dict, config: dict, readiness: dict, image_id: str, image: dict) -> dict:
    details = readiness.get("details", {})
    if (details.get("embeddings_model_revision") != profile["Rag"]["EmbeddingsModelRevision"]
            or details.get("embeddings_runtime_revision") != profile["Rag"]["EmbeddingsRuntimeRevision"]):
        raise ValueError("Backend-bound embedding provenance differs from the frozen observed TEI profile.")
    qdrant_revision = config.get("Rag", {}).get("QdrantRuntimeRevision")
    if image.get("Id") != image_id or details.get("qdrant_runtime_revision") != qdrant_revision:
        raise ValueError("Backend-bound Qdrant provenance differs from its actual image/configuration.")
    manifests = {str(value).rsplit("@", 1)[-1] for value in image.get("RepoDigests", [])}
    if qdrant_revision == image_id:
        revision_type = "image_config_id"
    elif qdrant_revision in manifests and re.fullmatch(r"sha256:[a-f0-9]{64}", str(qdrant_revision)):
        revision_type = "repository_manifest_digest"
    else:
        raise ValueError("Qdrant declared runtime is neither the observed image ID nor an inspected manifest digest.")
    return {"embeddingsBoundRevisionMatches": True, "qdrantBoundRevisionMatches": True,
            "qdrantRevisionType": revision_type, "qdrantImageId": image_id,
            "qdrantRuntimeRevision": qdrant_revision}


def run(args: argparse.Namespace) -> dict:
    owner, profile, manifest = read_json(args.owner), read_json(args.embedding_profile), read_json(args.manifest)
    cases, plan = validate_inputs(owner, profile, manifest, args.case_id)
    if not 30 <= args.maximum_wait_seconds <= 14400:
        raise ValueError("Use a bounded real-worker deadline between 30 and 14400 seconds.")
    plan.update({"executeRequested": bool(args.execute), "ownerManifestSha256": sha(args.owner), "embeddingProfileSha256": sha(args.embedding_profile),
                 "corpusManifestSha256": sha(args.manifest), "maximumWaitSeconds": args.maximum_wait_seconds})
    if not args.execute:
        return plan
    if sys.platform != "linux":
        raise ValueError("Execution is limited to the dedicated Linux host; use dry-run elsewhere.")
    scope = Path(owner["root"])
    for path in (args.owner, args.embedding_profile, args.manifest, args.source_directory,
                 args.output, args.api_key_file, args.model_file, Path(owner["configurationPath"])):
        assert_owned_path(path, scope)
    documents = assert_owned_path(Path(owner["documentsRoot"]), scope)
    if (args.output.exists() or not documents.is_dir() or any(documents.iterdir())
            or not 30 <= args.maximum_wait_seconds <= 14400):
        raise ValueError("Use fresh evidence, empty owned documents and a bounded deadline.")
    for case in cases:
        source = assert_owned_path(args.source_directory / (case["caseId"] + ".pdf"), scope)
        if not source.is_file() or sha(source) != case["sourceSha256"]:
            raise ValueError("A selected source PDF differs from the frozen manifest.")
    args.output.mkdir(mode=0o700)
    private = args.output / "private-observations"
    private.mkdir(mode=0o700)
    os.chmod(args.output, 0o700)
    docker = OwnedDocker(owner, execute=True)
    result = {"schemaVersion": "saaia-linux-ingestion-execution.v1", "passed": False, "executed": True,
              "startedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "plan": plan, "canonicalRowsSeeded": False, "paidProviderCalls": 0,
              "ingestionWorkerExecuted": False, "sqlPublicationValidated": False,
              "vectorStorePublicationValidated": False, "canonicalSearchValidated": False,
              "embeddingRuntimeAttested": False, "vectorValidated": False,
              "deploymentQualificationEligible": False, "deploymentQualificationPassed": False,
              "stage": "ownership_preflight",
              "cleanup": {"zeroActiveJobs": False, "ownedContainersStopped": False},
              "limits": ["This runner never builds, starts or installs the runtime.",
                         "Paid-provider count concerns this ingestion-only, disabled-provider scope; no packet capture is claimed.",
                         "TEI runtime/model attestation and publication are distinct from corpus-wide semantic recall."]}
    headers = None
    baseline_verified = False
    model_binding = None
    tei = None

    def model_artifacts():
        if model_binding is None:
            raise ValueError("The TEI model mount has not been established.")
        snapshot = assert_owned_path(Path(model_binding["hostModelRoot"]), scope)
        entries = list(snapshot.rglob("*"))
        if any(entry.is_symlink() for entry in entries):
            raise ValueError("The selected TEI snapshot cannot contain symlinks.")
        actual_files = {entry.relative_to(snapshot).as_posix() for entry in entries if entry.is_file()}
        if actual_files != set(profile["modelArtifacts"]):
            raise ValueError("The frozen TEI inventory must exactly cover files present in the actual mounted snapshot.")
        hashes = {}
        for relative, expected in profile["modelArtifacts"].items():
            path = assert_owned_path(snapshot / relative, scope)
            container_path = str(Path(model_binding["containerModelRoot"]) / relative)
            if mounted_source(tei, container_path) != str(path):
                raise ValueError("A more specific TEI bind masks the model artifact being hashed.")
            if not path.is_file() or sha(path) != expected:
                raise ValueError("A genuinely mounted TEI artifact differs from the frozen inventory.")
            hashes[relative] = expected
        weight = snapshot / profile["modelFileRelativePath"]
        if weight != args.model_file or hashes[profile["modelFileRelativePath"]] != profile["modelFileSha256"]:
            raise ValueError("The weight witness is not the one inside TEI's actual selected mounted snapshot.")
        return hashes

    def api(role: str, route: str, method: str = "GET", body: dict | None = None) -> dict:
        docker.inspect_all()
        if not route.startswith("/") or route.startswith("//"):
            raise ValueError("Only an exact local role-relative route is permitted.")
        payload = None if body is None else json.dumps(body, allow_nan=False).encode("utf-8")
        local_headers = {"Content-Type": "application/json"}
        if role == "backend" and headers:
            local_headers.update(headers)
        request = urllib.request.Request(owner["endpoints"][role] + route, data=payload,
                                         method=method, headers=local_headers)
        # No proxy or redirect may turn a loopback request into external traffic.
        class NoRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, request, response, code, message, response_headers, new_url):
                raise ValueError("Qualification HTTP redirects are forbidden.")
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
        with opener.open(request, timeout=60) as response:
            return json.loads(response.read().decode("utf-8"))

    try:
        safe, inspected = docker.inspect_all()
        backend = next(x for x in inspected if x["Id"] == next(r["id"] for r in owner["containers"] if r["role"] == "backend"))
        config = read_json(Path(owner["configurationPath"]))
        validate_disabled_llm(config, backend)
        for name in ("EmbeddingsModel", "EmbeddingsModelRevision", "EmbeddingsRuntimeRevision"):
            if config.get("Rag", {}).get(name) != profile["Rag"][name]:
                raise ValueError("Signed embedding configuration differs from the independently frozen TEI identity.")
        write_json(args.output / "runtime-ownership.json", {"containers": safe, "signedConfigurationSha256": sha(Path(owner["configurationPath"]))})
        # Only this explicit execution boundary reads the synthetic runtime key.
        if not args.api_key_file.is_file() or args.api_key_file.stat().st_size > 2048:
            raise ValueError("A bounded synthetic qualification API key file is required.")
        key = args.api_key_file.read_text(encoding="utf-8").strip()
        if not key or any(ord(c) < 33 or ord(c) > 126 for c in key):
            raise ValueError("Malformed synthetic qualification API key.")
        headers = synthetic_admin_headers(key)
        result["stage"] = "container_readiness_and_binary_identity"
        ready = api("backend", "/ready")
        write_json(private / "ready.private.json", ready)
        details = ready.get("details", {})
        result.update(qualification_identity(owner, backend, ready))
        if (ready.get("ok") is not True
                or details.get("canonical_provenance_ready") is not True
                or details.get("canonical_artifacts_enabled") is not True or details.get("tei_dim") != 768):
            raise ValueError("The actual containerized backend is not ready.")
        backend_id = next(r["id"] for r in owner["containers"] if r["role"] == "backend")
        docker.command(["exec", backend_id, "test", "!", "-e", "/app/appsettings.Local.json"])
        qdrant_record = next(r for r in owner["containers"] if r["role"] == "qdrant")
        image = json.loads(docker.command(["image", "inspect", qdrant_record["imageId"]]))[0]
        write_json(args.output / "bound-runtime-provenance.json", readiness_provenance(profile, config, ready, qdrant_record["imageId"], image))
        binary = docker.command(["exec", backend_id, "sha256sum", "/app/SAAIA.Backend.dll"]).split()[0]
        if not re.fullmatch(r"[a-f0-9]{64}", binary):
            raise ValueError("The actually containerized backend DLL has no valid byte hash.")
        write_json(args.output / "backend-binary-identity.json", {"assemblySha256": binary, "imageId": backend["Image"],
                   "codeRevisionObservedByReadiness": details.get("code_revision"), "windowsAssemblyIdentityReused": False})
        result["stage"] = "text_parser_and_empty_baseline"
        parser = json.loads(docker.sql(queries.TEXT_PARSER))
        write_json(args.output / "text-parser-conformance.json", parser)
        if len(parser) != 4 or any(value is not True for value in parser.values()):
            raise ValueError("Actual PostgreSQL text parser failed the four behavioral sentinels.")
        baseline = json.loads(docker.sql(queries.BASELINE))
        write_json(args.output / "empty-baseline.json", baseline)
        if any(type(value) is not int or value != 0 for value in baseline.values()):
            raise ValueError("The Docker qualification database is not empty.")
        baseline_verified = True
        try:
            empty_collection = api("qdrant", "/collections/" + owner["qdrantCollection"])
            if empty_collection.get("result", {}).get("points_count") != 0:
                raise ValueError("The unique qualification collection is not empty.")
        except urllib.error.HTTPError as failure:
            if failure.code != 404:
                raise
        result["stage"] = "native_tei_attestation"
        info = api("tei", "/info")
        attest_info(info, profile, owner["endpoints"]["tei"])
        tei_record = next(record for record in owner["containers"] if record["role"] == "tei")
        tei = next(item for item in inspected if item["Id"] == tei_record["id"])
        command = tei.get("Config", {}).get("Cmd", [])
        model_binding = tei_model_binding(profile, tei)
        artifact_hashes = model_artifacts()
        evidence = {"imageId": tei["Image"], "modelFileSha256": sha(args.model_file),
                    "modelRevision": model_binding["modelRevision"], "autoTruncate": "--auto-truncate" in command,
                    "ownedContainerVerified": True}
        attest_tei_container(profile, evidence)
        write_json(args.output / "tei-attestation.json", {"nativeInfo": info, "container": evidence, "passed": True,
                    "selectedSnapshotBindMapping": model_binding, "modelArtifactSha256": artifact_hashes,
                    "dimensionObservedByBackendReadiness": 768, "kernelFileOpenTracingPerformed": False})
        result["embeddingRuntimeAttested"] = True
        result["stage"] = "copy_source_and_api_enqueue"
        for case in cases:
            destination = documents / (case["caseId"] + ".pdf")
            with destination.open("xb") as stream, (args.source_directory / destination.name).open("rb") as source:
                shutil.copyfileobj(source, stream)
            if sha(destination) != case["sourceSha256"]:
                raise ValueError("Owned working PDF copy differs from the manifest.")
        scan = api("backend", "/ingestion/scan", "POST", {"max": len(cases)})
        write_json(private / "enqueue-response.private.json", scan)
        if scan.get("enqueued") != len(cases) or scan.get("scanned") != len(cases):
            raise ValueError("API scan did not enqueue exactly the selected source files.")
        result["apiEnqueuedJobCount"] = scan["enqueued"]
        result["stage"] = "actual_worker_execution"
        deadline = time.monotonic() + args.maximum_wait_seconds
        poll = 0
        while True:
            poll += 1
            jobs = api("backend", "/ingestion/jobs?limit=200")
            write_json(private / ("jobs-" + str(poll).zfill(4) + ".private.json"), jobs)
            items = parse_job_listing(jobs)
            if len(items) == len(cases) and all(item.get("status") == "done" for item in items):
                result["ingestionWorkerExecuted"] = True
                break
            if any(item.get("status") in {"failed", "canceled", "cancelled"} for item in items):
                raise ValueError("An actual ingestion job reached an unsuccessful terminal state.")
            if time.monotonic() >= deadline:
                raise TimeoutError("The bounded real ingestion deadline expired.")
            time.sleep(2)
        result["jobPollCount"] = poll
        result["stage"] = "read_only_publication_capture"
        observation = json.loads(docker.sql(queries.PUBLICATION))
        write_json(private / "sql-publication.private.json", observation)
        checksums = json.loads(docker.sql(queries.TEXT_CHECKSUMS))
        write_json(args.output / "sql-text-conformance.json", checksums)
        if (len(checksums.get("tables", [])) != 4
                or any(table.get("mismatches") != 0 for table in checksums["tables"])):
            raise ValueError("Stored canonical SQL text no longer matches its exact UTF8 checksum.")
        api_reads, vectors, searches = {}, {}, {}
        result["stage"] = "actual_search_source_read_and_qdrant_points"
        for row in observation["documents"]:
            case = next(case for case in cases if case["caseId"] + ".pdf" == row["docPath"])
            doc_id = str(uuid.UUID(row["docId"]))
            api_reads[row["docPath"]] = [{"physicalPage": page, "response": api("backend", "/documents/context?" +
                urllib.parse.urlencode({"docId": doc_id, "pageStart": page, "pageEnd": page, "before": 0, "after": 0, "limit": 50}))}
                for page in sorted({fact["page"] for fact in case["expectations"]["criticalFacts"]})]
            point_ids = [vector_point_id(chunk["retrievalChunkId"], row["activeVectorPlanFingerprint"]) for chunk in row["chunks"]]
            points = []
            for offset in range(0, len(point_ids), 128):
                points.extend(api("qdrant", "/collections/" + owner["qdrantCollection"] + "/points", "POST",
                    {"ids": point_ids[offset:offset + 128], "with_payload": ["chunk_id", "ingestion_plan_fingerprint"], "with_vector": False})["result"])
            vectors[row["docPath"]] = points
            fact = search_fact(case)
            request = {"query": fact["text"], "docId": doc_id, "sourceBackedCanonical": True,
                       "pageStart": fact["page"], "pageEnd": fact["page"], "topK": 20, "maxPerDoc": 20, "maxPerPage": 20}
            response = api("backend", "/rag/search", "POST", request)
            negative = []
            for kind in ("absent_query", "absent_document"):
                control = copy.deepcopy(request)
                control["query" if kind == "absent_query" else "docId"] = "saaianomatch" + uuid.uuid4().hex if kind == "absent_query" else str(uuid.uuid4())
                negative.append({"kind": kind, "request": control, "response": api("backend", "/rag/search", "POST", control)})
            chosen = next((hit for hit in response.get("items", []) if hit.get("chunkId") in {c["retrievalChunkId"] for c in row["chunks"]}
                and re.search(r"(?<!\w)" + re.escape(exact_text(fact["text"])) + r"(?!\w)", exact_text(hit.get("text", "")))), None)
            read = {}
            if chosen:
                read_request = {"docId": doc_id, "chunkId": chosen["chunkId"]}
                read = {"request": read_request, "response": api("backend", "/documents/context?" + urllib.parse.urlencode(
                    {**read_request, "before": 0, "after": 0, "limit": 5}))}
            searches[row["docPath"]] = {"positive": {"request": request, "response": response}, "negative": negative, "read": read}
        for name, value in [("api-context", api_reads), ("qdrant-points", vectors), ("api-search", searches)]:
            write_json(private / (name + ".private.json"), value)
        publication = verify(manifest, args.case_id, observation, api_reads, vectors, embedding_adapter="tei_cpu",
                             embeddings_real=True, searches=searches, require_search=True)
        write_json(args.output / "publication-verification.json", publication)
        for key in ("sqlPublicationValidated", "vectorStorePublicationValidated", "canonicalSearchValidated"):
            result[key] = publication[key]
        result["passed"] = publication["passed"]
        result["stage"] = "verification_completed"
    except Exception as failure:
        result["failureType"] = type(failure).__name__
        result["failureStage"] = result["stage"]
        write_json(private / "failure.private.json", {"type": type(failure).__name__, "stage": result["stage"], "message": str(failure)})
        # No response body, key, SQL content or private document text is public.
    finally:
        if model_binding is not None:
            try:
                model_artifacts()
                result["modelArtifactsUnchangedAfterRun"] = True
            except Exception as failure:
                result["modelArtifactsUnchangedAfterRun"] = False
                result["passed"] = False
        if baseline_verified:
            try:
                remaining = api("backend", "/ingestion/jobs?limit=200")
                expected = {case["caseId"] + ".pdf" for case in cases}
                for item in parse_job_listing(remaining):
                    if item.get("status") in {"queued", "running", "paused"}:
                        if item.get("docPath") not in expected:
                            raise ValueError("Refuse cancellation of an unexpected job, even in the isolated DB.")
                        api("backend", "/admin/jobs/cancel", "POST", {"jobId": str(uuid.UUID(item["jobId"]))})
                deadline = time.monotonic() + 60
                while True:
                    active = int(docker.sql(queries.ACTIVE))
                    if active == 0 or time.monotonic() >= deadline:
                        break
                    time.sleep(1)
                result["cleanup"].update({"activeJobsBeforeStop": active, "zeroActiveJobs": active == 0})
            except Exception as failure:
                result["cleanup"]["observationFailureType"] = type(failure).__name__
        try:
            result["cleanup"].update(docker.stop())
        except Exception as failure:
            result["cleanup"]["stopFailureType"] = type(failure).__name__
        result["passed"] = result["passed"] and cleanup_success(result["cleanup"])
        result["deploymentQualificationPassed"] = result["passed"] and result["deploymentQualificationEligible"]
        result["finishedAtUtc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        write_json(args.output / "execution-result.json", result)
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("owner", "embedding-profile", "manifest", "source-directory", "output", "api-key-file", "model-file"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--case-id", action="append", required=True)
    parser.add_argument("--maximum-wait-seconds", type=int, default=1800)
    parser.add_argument("--execute", action="store_true")
    args = parser.parse_args()
    proof = run(args)
    print(json.dumps(proof if not args.execute else {"passed": proof["passed"],
        "deploymentQualificationPassed": proof["deploymentQualificationPassed"],
        "qualificationMode": proof.get("qualificationMode"), "cleanup": proof["cleanup"], "paidProviderCalls": 0}))
    return 0 if not args.execute or proof["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
