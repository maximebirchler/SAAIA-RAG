"""Four ORIGINAL PDFs through a freshly owned Linux worker; no paid provider.

Requires an independently prepared/signed CPU28 runtime with a NEW backend.
Success keeps it ready for audit/export; failure cancels/stops only owned jobs/CIDs.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import sys
import time
import traceback
import urllib.error
import urllib.request

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "validation-ingestion"))
from embedding_attestation import attest_info, attest_tei_container, tei_model_binding, validate_expected_profile
from linux_owned_runtime import OwnedDocker, assert_owned_path, mounted_source, validate_owner
from verify_worker_corpus import evaluate, validate_vectors, write_new
import linux_planning_contracts as helpers


def require(value, reason):
    if not value:
        raise ValueError(reason)


def digest(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def capture_failure(error, output, *, filename="failure-traceback.private.txt"):
    """Retain the original traceback privately, without dumping stack locals."""
    path = output / filename
    with path.open("x", encoding="utf-8", newline="") as target:
        target.write("".join(traceback.format_exception(error)))
    public_code = str(error) if isinstance(error, ValueError) else "real_ingestion_failed"
    if re.fullmatch(r"[a-z][a-z0-9_]{0,127}", public_code) is None:
        public_code = "real_ingestion_failed"
    return {"failureType": type(error).__name__, "failureCode": public_code,
            "privateDiagnostic": {"path": str(path), "sha256": digest(path)}}


def capture_cleanup_failure(error, output, filename):
    """A diagnostic write failure must not prevent the remaining safe cleanup."""
    try:
        return capture_failure(error, output, filename=filename)
    except Exception as diagnostic_error:
        return {"failureType": type(error).__name__, "failureCode": "real_ingestion_failed",
                "privateDiagnosticFailureType": type(diagnostic_error).__name__}


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args):
        raise ValueError("planning_ingestion_loopback_redirect_forbidden")


def run(args, owner, profile, manifest):
    root = Path(owner["root"])
    output = assert_owned_path(args.output, root)
    require(not output.exists(), "fresh_linux_ingestion_evidence_required")
    source_directory = assert_owned_path(args.source_directory, root)
    require(not any(Path(owner["documentsRoot"]).iterdir()), "new_owned_documents_directory_must_be_empty")
    output.mkdir(mode=0o700, parents=True)
    docker = OwnedDocker(owner, execute=True)
    record = {"schemaVersion": "saaia-planning-linux-real-ingestion-result.v1", "passed": False,
        "ownerRunId": owner["runId"], "canonicalRowsSeeded": False, "paidProviderCalls": 0, "state": "starting",
        "workerCampaignMaximumWaitSeconds": args.maximum_wait_seconds,
        "sourceManifestSha256": digest(args.source_manifest), "sourceManifestPath": str(args.source_manifest),
        "cpuProfile": "cpu28_3cores_7GiB", "keepOwnerReadyOnSuccess": True, "cleanup": {}}
    headers = None; baseline_verified = False
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())

    def api(role, route, method="GET", body=None):
        _, observed = docker.inspect_all()
        current_backend = next(row for row in observed if row["Id"] == next(r["id"] for r in owner["containers"] if r["role"] == "backend"))
        helpers.validate_backend_resources(owner, current_backend)
        require(route.startswith("/") and not route.startswith("//"), "exact_role_relative_route_required")
        local_headers = {"Content-Type": "application/json"}
        if role == "backend" and headers:
            local_headers.update(headers)
        request = urllib.request.Request(owner["endpoints"][role] + route, method=method,
            data=None if body is None else json.dumps(body, allow_nan=False).encode(), headers=local_headers)
        with opener.open(request, timeout=60) as response:
            data = response.read(512 * 1024 * 1024 + 1)
            require(len(data) <= 512 * 1024 * 1024, "local_ingestion_response_size_limit")
            return json.loads(data.decode("utf-8"))

    def evidence(path):
        return {"path": str(path), "sha256": digest(path)}

    try:
        safe, actual = docker.inspect_all()
        config_path = Path(owner["configurationPath"])
        config = read(config_path)
        backend = next(row for row in actual if row["Id"] == next(r["id"] for r in owner["containers"] if r["role"] == "backend"))
        origin = read(root / "evidence/planning-bootstrap-origin.json")
        helpers.validate_backend_resource_phase(owner, origin, owner.get("backendResourcePolicy", {}))
        record["backendResourcesObserved"] = helpers.validate_backend_resources(owner, backend)
        record["backendResourcePolicy"] = owner["backendResourcePolicy"]
        helpers.validate_disabled_llm(config, backend)
        timeout_policy = owner.get("fullDocumentTimeoutPolicy")
        if timeout_policy:
            docling_container = next(row for row in actual if row["Id"] == next(r["id"] for r in owner["containers"] if r["role"] == "docling"))
            record["fullDocumentTimeoutsObserved"] = helpers.validate_full_document_timeouts(timeout_policy,config,docling_container)
            record["fullDocumentTimeoutPolicy"] = timeout_policy
            record["cpuProfile"] = timeout_policy["profile"]
        require(digest(config_path) == owner["bootstrapPreparation"]["files"]["deploy/deployment.config.json"], "signed_config_bytes_changed")
        require(owner["bootstrapPreparation"]["backendSourceRevision"] == args.backend_source_revision, "new_backend_source_binding_changed")
        binary = docker.command(["exec", backend["Id"], "sha256sum", "/app/SAAIA.Backend.dll"]).split()[0]
        require(binary == args.backend_sha256 and backend["Image"] == args.backend_image, "actually_loaded_linux_backend_bytes_changed")
        write_new({"containers": safe, "backendAssemblySha256": binary, "backendImage": backend["Image"]}, output / "runtime-ownership.private.json")
        key_path = assert_owned_path(root / "private/api-key.txt", root)
        require(0 < key_path.stat().st_size <= 2048, "owned_synthetic_key_size_invalid")
        key = key_path.read_text(encoding="utf-8").strip()
        require(key and all(33 <= ord(c) <= 126 for c in key), "owned_synthetic_key_invalid")
        headers = helpers.synthetic_admin_headers(key)
        ready = api("backend", "/ready")
        write_new(ready, output / "ready.private.json")
        qualification = helpers.qualification_identity(owner, backend, ready)
        require(qualification["deploymentQualificationEligible"] is True and ready.get("ok") is True
                and ready.get("details", {}).get("canonical_provenance_ready") is True
                and ready["details"].get("canonical_artifacts_enabled") is True and ready["details"].get("tei_dim") == 768,
                "new_signed_backend_canonical_runtime_not_ready")
        record["productionIdentity"] = qualification
        require(all(config["Rag"].get(name) == profile["Rag"][name] for name in ("EmbeddingsModel", "EmbeddingsModelRevision", "EmbeddingsRuntimeRevision")), "signed_embedding_identity_changed")
        info = api("tei", "/info"); attest_info(info, profile, owner["endpoints"]["tei"])
        tei = next(row for row in actual if row["Id"] == next(r["id"] for r in owner["containers"] if r["role"] == "tei"))
        binding = tei_model_binding(profile, tei)
        snapshot = assert_owned_path(Path(binding["hostModelRoot"]), root)
        entries = list(snapshot.rglob("*"))
        require(not any(p.is_symlink() for p in entries) and {p.relative_to(snapshot).as_posix() for p in entries if p.is_file()} == set(profile["modelArtifacts"]), "actual_tei_model_inventory_changed")
        for relative, expected in profile["modelArtifacts"].items():
            model = assert_owned_path(snapshot / relative, root)
            require(digest(model) == expected and mounted_source(tei, str(Path(binding["containerModelRoot"]) / relative)) == str(model), "actual_tei_model_artifact_changed")
        attest_tei_container(profile, {"imageId": tei["Image"], "modelFileSha256": digest(snapshot / profile["modelFileRelativePath"]),
            "modelRevision": binding["modelRevision"], "autoTruncate": "--auto-truncate" in tei["Config"]["Cmd"], "ownedContainerVerified": True})
        write_new({"nativeInfo": info, "actualModelBinding": binding, "modelArtifacts": profile["modelArtifacts"], "passed": True}, output / "tei-attestation.private.json")
        health = api("docling", "/health")
        write_new(health, output / "docling-health.private.json")
        require(health.get("status") == "ok" and health.get("upstream") is True and health.get("regionalTextRepair", {}).get("enabled") is True,
                "actual_docling_regional_runtime_not_ready")
        baseline = json.loads(docker.sql("SELECT jsonb_build_object('documents',(SELECT count(*) FROM documents),'jobs',(SELECT count(*) FROM ingestion_jobs),'advancedJobs',(SELECT count(*) FROM advanced_analysis_jobs),'revisions',(SELECT count(*) FROM document_revisions),'chunks',(SELECT count(*) FROM retrieval_chunks),'bundles',(SELECT count(*) FROM document_revision_binary_artifacts))"))
        write_new(baseline, output / "empty-baseline.private.json")
        require(all(type(v) is int and v == 0 for v in baseline.values()), "new_linux_publication_store_not_empty")
        baseline_verified = True
        try:
            collection = api("qdrant", "/collections/" + owner["qdrantCollection"])
            require(collection.get("result", {}).get("points_count") == 0, "new_vector_collection_not_empty")
        except urllib.error.HTTPError as error:
            require(error.code == 404, "vector_baseline_status_unexpected")
        for case in manifest["cases"]:
            source = assert_owned_path(source_directory / (case["caseId"] + ".pdf"), root)
            require(digest(source) == case["sourceSha256"], "staged_original_pdf_hash_changed")
            destination = Path(owner["documentsRoot"]) / source.name
            with source.open("rb") as source_stream, destination.open("xb") as destination_stream:
                shutil.copyfileobj(source_stream, destination_stream)
            require(digest(destination) == case["sourceSha256"], "owned_original_pdf_copy_changed")
        scan = api("backend", "/ingestion/scan", "POST", {"max": 4})
        write_new(scan, output / "scan-response.private.json")
        require(scan.get("enqueued") == scan.get("scanned") == 4, "exactly_four_real_jobs_required")
        deadline = time.monotonic() + args.maximum_wait_seconds; poll = 0
        while True:
            jobs = api("backend", "/ingestion/jobs?limit=20")
            job_file = output / f"jobs-{poll:05d}.private.json"; write_new(jobs, job_file); poll += 1
            items = helpers.parse_job_listing(jobs)
            require(not any(j["status"] in {"failed", "canceled", "cancelled", "paused"} for j in items), "actual_worker_job_failed")
            if len(items) == 4 and all(j["status"] == "done" and j["attempts"] >= 1 for j in items):
                break
            require(time.monotonic() < deadline, "complete_pdf_worker_deadline_expired")
            time.sleep(2)
        observation = json.loads(docker.sql((Path(__file__).parent / "worker-publication.sql").read_text(encoding="utf-8")))
        for doc in observation["documents"]:
            case = next(c for c in manifest["cases"] if c["caseId"] + ".pdf" == doc["docPath"])
            doc["physicalPdfPath"] = case["physicalPdfPath"]
            doc["ingestionInputPdfPath"] = str(Path(owner["documentsRoot"]) / doc["docPath"])
        logs = docker.command(["logs", "--timestamps", backend["Id"]], timeout=30)
        log_path = output / "backend-log.private.txt"
        with log_path.open("x", encoding="utf-8", newline="") as target:
            target.write(logs)
        observation.update(ownerRunId=owner["runId"], sourceManifestPath=str(args.source_manifest), sourceManifestSha256=digest(args.source_manifest),
            canonicalRowsSeeded=False, backendAssemblySha256=binary, backendImageId=backend["Image"], configurationSha256=digest(config_path),
            profileSha256=digest(args.embedding_profile), dependencyManifestSha256=digest(args.owner), qualificationMode="production",
            embeddingProfileEvidence=evidence(args.embedding_profile),
            workerExecutionEvidence={name: evidence(value) for name, value in {"backendLog": log_path,
                "emptyBaseline": output / "empty-baseline.private.json", "scanResponse": output / "scan-response.private.json",
                "terminalJobs": job_file, "doclingHealth": output / "docling-health.private.json",
                "embeddingInfo": output / "tei-attestation.private.json", "dependencyOwnership": output / "runtime-ownership.private.json"}.items()})
        observation_path = output / "worker-corpus.private.json"; write_new(observation, observation_path)
        documents, points = evaluate(observation, manifest, digest(args.source_manifest), source_directory)
        vectors = {}
        for doc_path, expected in points.items():
            found = []
            for offset in range(0, len(expected), 64):
                found.extend(api("qdrant", "/collections/" + owner["qdrantCollection"] + "/points", "POST", {
                    "ids": [p["pointId"] for p in expected[offset:offset + 64]], "with_payload": ["chunk_id", "ingestion_plan_fingerprint"], "with_vector": True})["result"])
            vectors[doc_path] = found
        write_new(vectors, output / "worker-vector-points.private.json"); validate_vectors(points, vectors)
        write_new({"schemaVersion": "saaia-planning-complete-corpus.v1", "origin": "real_ingestion_worker", "completeDocuments": True,
            "exactSqlReadback": True, "observationPath": str(observation_path), "observationSha256": digest(observation_path),
            "sourceManifestSha256": digest(args.source_manifest), "documents": documents, "documentCount": 4,
            "physicalPageCount": sum(d["pageCount"] for d in documents), "boundaries": {"nativeFixture": False, "realWorker": True,
                "semanticRecipeOwnershipAccepted": False, "modelAnswerAccepted": False, "winUiRenderingValidated": False}}, output / "complete-corpus.json")
        record.update(passed=True, state="ingested_owner_kept_ready", workerObserved=True, documentCount=4, publication=evidence(observation_path),
                      corpusManifest=evidence(output / "complete-corpus.json"), vectorPublicationValidated=True, semanticOwnershipAcceptance="requires_independent_pdf_review")
    except Exception as error:
        record.update(state="failed", **capture_failure(error, output))
    finally:
        if not record["passed"]:
            cancellation = record["cleanup"]["jobCancellation"] = {"attempted": False}
            try:
                if headers and baseline_verified:
                    cancellation["attempted"] = True
                    jobs = helpers.parse_job_listing(api("backend", "/ingestion/jobs?limit=20"))
                    active = [job for job in jobs if job["status"] in {"queued", "running", "paused"}]
                    cancellation.update(activeJobCount=len(active), cancelRequestsCompleted=0)
                    for job in active:
                        api("backend", "/admin/jobs/cancel", "POST", {"jobId": job["jobId"]})
                        cancellation["cancelRequestsCompleted"] += 1
            except Exception as error:
                cancellation.update(capture_cleanup_failure(error, output, "cleanup-cancellation-traceback.private.txt"))
            finally:
                # Cancellation requires a live runtime. Stopping instead uses
                # OwnedDocker's complete identity checks with require_running=False.
                # A dead dependency or rejected HTTP request cannot skip this phase.
                try:
                    record["cleanup"].update(docker.stop())
                except Exception as error:
                    record["cleanup"]["containerStop"] = capture_cleanup_failure(
                        error, output, "cleanup-stop-traceback.private.txt")
        write_new(record, output / "result.json")
    require(record["passed"], "real_linux_ingestion_not_accepted_inspect_private_receipt")
    return record


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--owner", required=True, type=Path)
    parser.add_argument("--embedding-profile", required=True, type=Path)
    parser.add_argument("--source-manifest", required=True, type=Path)
    parser.add_argument("--source-directory", required=True, type=Path)
    parser.add_argument("--backend-image", required=True)
    parser.add_argument("--backend-sha256", required=True)
    parser.add_argument("--backend-source-revision", required=True)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--maximum-wait-seconds", type=int, default=10800)
    parser.add_argument("--execute", action="store_true")
    args = parser.parse_args()
    owner, profile, manifest = validate_owner(read(args.owner)), read(args.embedding_profile), read(args.source_manifest)
    require(manifest["schemaVersion"] == "ingestion_real_acceptance_v1" and len(manifest["cases"]) == 4
            and manifest["newWorkerObservationSeenDuringRegistration"] is False, "four_independently_reviewed_originals_required")
    require(validate_expected_profile(profile, owner["endpoints"]["tei"]) == "tei_cpu", "exact_native_tei_profile_required")
    require(re.fullmatch("[a-f0-9]{64}", args.backend_sha256) and re.fullmatch("[a-f0-9]{64}", args.backend_source_revision)
            and re.fullmatch("sha256:[a-f0-9]{64}", args.backend_image) and 60 <= args.maximum_wait_seconds <= 14400,
            "reviewed_new_backend_and_bounded_deadline_required")
    if not args.execute:
        print(json.dumps({"executeRequested": False, "runId": owner["runId"], "sourceCount": 4,
            "realWorkerRequired": True, "canonicalRowsSeeded": False, "providerCalls": 0, "privateFilesRead": False, "dockerCalled": False}))
        return
    print(json.dumps(run(args, owner, profile, manifest)))


if __name__ == "__main__":
    main()
