"""Filesystem-only, explicit-allowlist projections of three Linux campaign proofs.

NOEXEC by default. No subprocess, SSH, HTTP, owner/config/key/PDF/SQL reads.
Only the exact named public JSON files are read; no recursive source traversal.
The output is new JSON projections plus a byte-hash seal, never raw captures.
"""
from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import math
from pathlib import Path
import re
import sys
import uuid

CORPUS_FILES = (
    "execution-result.json", "publication-verification.json", "backend-binary-identity.json",
    "bound-runtime-provenance.json", "empty-baseline.json", "text-parser-conformance.json",
    "sql-text-conformance.json", "tei-attestation.json",
)
LIFECYCLE_FILES = ("lifecycle-result.json", "runtime-identity.json")
FORBIDDEN_KEYS = re.compile(r"^(?:api.?key|password|secret|connectionstring|headers|authorization|text(?:_content)?|"
                            r"payload(?:base64)?|vector|vectors|rawsql|sqlquery|documentcontent)$", re.I)
SECRET = re.compile(r"(?:\b(?:sk|sess)-[A-Za-z0-9_-]{12,}|-----BEGIN[ A-Z]*PRIVATE KEY-----|postgres(?:ql)?://)", re.I)
HEX = re.compile(r"[a-f0-9]{64}")
SAFE_CODE = re.compile(r"[A-Za-z0-9_.:+/@-]{1,256}")
ARTIFACTS = {"config.json", "tokenizer.json", "tokenizer_config.json", "sentence_bert_config.json",
             "1_Pooling/config.json", "onnx/model.onnx"}
EXTRAS = {"docling": "probe-result.json", "docling-operator": "operator-result.json",
          "docling-attestation": "runtime-attestation.json", "ocr-v1": "result-recovered.json",
          "ocr-v1-operator": "operator-result.json", "ocr-v1-attestation": "runtime-attestation.json",
          "ocr-v2": "result.json", "ocr-v2-operator": "operator-result.json",
          "ocr-v2-attestation": "runtime-attestation.json"}
DOCLING_CHECKS = ("statusSuccess", "noReportedErrors", "canonicalSchema", "onePhysicalPage", "configurationObserved",
    "doOcrObserved", "forceOcrObserved", "packageVersionsMatchInstalled", "requiredPackagesPresent", "allManifestFactsPresentOnAnnotatedPage")
OCR_CHECKS = ("vstestExitZero", "exactlyOnePassedNoSkip", "testBackendUnchanged", "linuxImageBackendUnchanged",
    "testAssemblyUnchanged", "inventoryUnchanged", "hostResolutionMentionsSnapshotBackend",
    "hostResolutionMentionsSnapshotTestAssembly", "engineDiagnosticsPresent")


def require(value, reason):
    if not value:
        raise ValueError(reason)


def file_sha(raw):
    return hashlib.sha256(raw).hexdigest()


def safe_path(path):
    require(path.is_absolute() and ".." not in path.parts, "Use absolute paths without traversal.")
    for current in (path, *path.parents):
        require(not current.is_symlink(), "Export paths cannot traverse symlinks.")
    require(path.resolve(strict=False) == path, "Export path resolution changed its scope.")
    return path


def decode(raw):
    require(len(raw) <= 4 * 1024 * 1024, "A public source JSON exceeds its bounded size.")
    def unique(pairs):
        result = {}
        for key, value in pairs:
            require(key not in result, "Duplicate JSON keys cannot be sealed.")
            result[key] = value
        return result
    obj = json.loads(raw.decode("utf-8-sig"), object_pairs_hook=unique,
                     parse_constant=lambda _: (_ for _ in ()).throw(ValueError("Nonfinite JSON constant")))
    require(isinstance(obj, dict), "A public proof must be a JSON object.")
    def inspect(value):
        if isinstance(value, dict):
            for key, child in value.items():
                require(FORBIDDEN_KEYS.fullmatch(key) is None, "Forbidden raw/credential field in a supposedly public proof.")
                inspect(child)
        elif isinstance(value, list):
            require(len(value) <= 8192, "A public proof array exceeds its bounded size.")
            for child in value:
                inspect(child)
        elif isinstance(value, str):
            require(SECRET.search(value) is None, "Credential-shaped value found; no value will be printed.")
    inspect(obj)
    return obj


def values(source, *, booleans=(), integers=(), codes=(), hashes=(), dates=()):
    result = {}
    for name in booleans:
        if name in source:
            require(type(source[name]) is bool, "A public boolean has the wrong type: " + name)
            result[name] = source[name]
    for name in integers:
        if name in source:
            require(type(source[name]) is int and 0 <= source[name] <= 10**12, "A public count has the wrong type/range: " + name)
            result[name] = source[name]
    for name in codes:
        if name in source:
            value = source[name]
            require(value is None or (isinstance(value, str) and SAFE_CODE.fullmatch(value)), "An identifier is not a bounded code: " + name)
            result[name] = value
    for name in hashes:
        if name in source:
            value = source[name]
            require(isinstance(value, str) and HEX.fullmatch(value), "An expected SHA256 is malformed: " + name)
            result[name] = value
    for name in dates:
        if name in source:
            value = source[name]
            require(isinstance(value, str) and datetime.datetime.fromisoformat(value.replace("Z", "+00:00")).tzinfo,
                    "A public timestamp lacks a timezone: " + name)
            result[name] = value
    return result


def cleanup(obj):
    return values(obj, booleans=("zeroActiveJobs", "ownedContainersStopped", "dockerPublishedPortBindingsAbsent",
        "onlyNewScopedJobsCanceled", "workingSourceRestored", "volumesDeleted", "imagesDeleted"),
        integers=("activeJobsBeforeStop", "containerCount"), codes=("observationFailureType", "stopFailureType", "restoreFailureType"))


def execution(obj, lifecycle=False):
    require(obj.get("schemaVersion") == ("saaia-linux-lifecycle-execution.v1" if lifecycle else "saaia-linux-ingestion-execution.v1"),
            "Unexpected execution schema.")
    require(type(obj.get("passed")) is bool and obj.get("executed") is True and obj.get("finishedAtUtc"),
            "A live/incomplete or nonexecuted campaign cannot be exported as a terminal proof.")
    result = values(obj, booleans=("executed", "passed", "canonicalRowsSeeded", "ingestionWorkerExecuted", "sqlPublicationValidated",
        "vectorStorePublicationValidated", "canonicalSearchValidated", "embeddingRuntimeAttested", "vectorValidated",
        "deploymentQualificationEligible", "deploymentQualificationPassed", "configurationSignatureVerified",
        "modelArtifactsUnchangedAfterRun", "controlledLateWriteIsWorkerCancellationProof", "lateWriteReappearanceRemoved",
        "generationsAndPhysicalPointIdsNeverReused"), integers=("paidProviderCalls", "apiEnqueuedJobCount", "jobPollCount"),
        codes=("schemaVersion", "qualificationMode", "environmentObservedFromContainer", "configurationSignatureMode",
               "stage", "failureStage", "failureType"), dates=("startedAtUtc", "finishedAtUtc"))
    result["cleanup"] = cleanup(obj.get("cleanup", {}))
    plan = obj.get("plan", {})
    result["plan"] = values(plan, booleans=("executeRequested", "embeddingsReal", "canonicalRowsSeeded", "bootstrapPerformedByRunner",
        "paidProviderEnabled", "sqlReadOnly", "attemptCancellation", "controlledLateWriteIsWorkerCancellationProof", "vectorValidated"),
        integers=("sourceCount", "maximumWaitSeconds"), codes=("schemaVersion", "runId", "project", "caseId", "tenantId",
        "embeddingAdapter", "productStatus"), hashes=("ownerManifestSha256", "embeddingProfileSha256", "corpusManifestSha256",
        "corpusProofSha256", "sourceSha256"))
    if "caseIds" in plan:
        require(isinstance(plan["caseIds"], list) and all(isinstance(s, str) and re.fullmatch(r"[a-z0-9-]{1,96}", s) for s in plan["caseIds"]),
                "Malformed public case IDs.")
        result["plan"]["caseIds"] = plan["caseIds"]
    if lifecycle:
        result["steps"] = [values(step, booleans=("searchToReadPassed", "exactOldPointsAbsent", "activePublicationPreserved",
            "observedPresent", "workerHttpCancellationProven", "activePlanCleared", "searchEmpty"),
            integers=("ingestionVersion", "indexedVersion", "chunkCount", "pointCount", "generation"),
            codes=("phase", "docId", "revisionId", "planId", "pointId", "lastGcCode"),
            hashes=("planFingerprint", "sourceSha256", "pointIdsSha256", "vectorsAndPayloadSha256", "capturedPointSha256"),
            dates=("lastSweep",)) for step in obj.get("steps", [])]
        result["cancel"] = values(obj.get("cancel", {}), booleans=("apiAcknowledged", "previousPublicationPreserved",
            "upstreamEngineCancellationVerified", "currentSourceFreshnessReverified"), integers=("abandonedGeneration",),
            codes=("status", "reason", "jobId", "observedBeforeCancel"))
        result["failedReplacement"] = values(obj.get("failedReplacement", {}), codes=("status", "reason"))
        result["unrelatedPublicationPreservation"] = values(obj.get("unrelatedPublicationPreservation", {}),
            booleans=("sqlStateAndChunkChecksumsUnchanged", "currentPhysicalVectorsAndPayloadsUnchanged"),
            integers=("pointCount",), hashes=("vectorsAndPayloadSha256",))
    return result


def publication(obj):
    require(obj.get("schemaVersion") == "saaia-owned-worker-publication-verification.v1", "Unexpected publication schema.")
    result = values(obj, booleans=("passed", "caseCoverageComplete", "completedJobsObserved", "workerExecutionAndNoSeedingRequireRunnerProof",
        "sqlPublicationValidated", "vectorStorePublicationValidated", "canonicalSearchExecuted", "canonicalSearchValidated", "embeddingsReal", "vectorValidated"),
        integers=("caseCount",), codes=("schemaVersion", "embeddingAdapter"))
    result["results"] = []
    for row in obj.get("results", []):
        projected = values(row, booleans=("ok", "canonicalBundleChecksumExact", "canonicalChecksPassed", "readApiFactsPassed", "qdrantPointIdsAndPlanExact"),
            integers=("chunkCount", "chunkChecksumFailures", "sourceAnchorCount", "criticalPhysicalFactCount", "qdrantPointCount"),
            codes=("caseId", "documentId", "revisionId"), hashes=("sourceSha256",))
        issues = row.get("issues", [])
        require(isinstance(issues, list) and all(isinstance(x, str) and re.fullmatch(r"[a-z0-9_]{1,128}", x) for x in issues),
                "Publication issues must remain bounded diagnostic codes, never document text.")
        projected["issues"] = issues
        search = row.get("canonicalSearch")
        if search is not None:
            projected["canonicalSearch"] = values(search, booleans=("passed", "requestsSourceBound", "searchIdentityExact", "positiveFactFound",
                "negativeControlsPassed", "searchToReadExact", "noDegradedRetrievers", "absentQueryLexicalMatchesZero",
                "absentQueryDenseNeighborsCurrent", "absentDocumentEmpty"), integers=("physicalPage", "positiveHitCount", "negativeControlCount",
                "absentQueryDenseNeighborCount"), codes=("factId",))
        result["results"].append(projected)
    return result


def numbers(obj, names):
    result = {}
    for name in names:
        if name in obj:
            require(type(obj[name]) in (int, float) and math.isfinite(obj[name]) and 0 <= obj[name] <= 10**12,
                    "A public duration/time has the wrong type or range: " + name)
            result[name] = obj[name]
    return result


def issue_codes(obj):
    issues = obj.get("issues", [])
    require(isinstance(issues, list) and all(isinstance(x, str) for x in issues), "Malformed offline diagnostic issues.")
    # An OSError may contain an absolute private path. Never export free text.
    return {"issueCount": len(issues), "diagnosticCodes": [x for x in issues if re.fullmatch(r"[A-Za-z0-9_.:]{1,160}", x)]}


def offline_probe(obj, kind):
    docling = kind == "docling"
    require(obj.get("schemaVersion") == ("saaia-docling-network-none-probe.v1" if docling else "saaia-linux-ocr-e2e.v1"),
            "Unexpected offline probe schema.")
    require(type(obj.get("passed")) is bool, "Offline probes require an explicit boolean verdict.")
    result = values(obj, booleans=("passed", "executed", "fullWorkerExecuted", "paidProviderUsed", "forceOcrOverride",
        "mainAutoScanProfileUnchanged", "requiresExternalOwnerImageNetworkMountAttestation", "sourceUnchanged", "bootHealthPassed",
        "requiresExternalDockerNetworkAndImageAttestation"),
        integers=("expectedFactCount", "passedFactCount", "textObservationCount", "tableObservationCount", "healthHttpStatus",
        "healthAttempts", "conversionHttpStatus", "responseBytes"), codes=("schemaVersion", "caseId", "exactTest"),
        hashes=("sourceSha256", "manifestSha256", "probeSha256", "responseSha256", "testBackendSha256",
        "linuxImageBackendSha256", "testAssemblySha256", "packageInventorySha256"), dates=("startedAtUtc",))
    result.update(numbers(obj, ("durationSeconds",)))
    result.update(issue_codes(obj))
    known_checks = DOCLING_CHECKS if docling else OCR_CHECKS
    require(isinstance(obj.get("checks", {}), dict) and set(obj.get("checks", {})) <= set(known_checks),
            "Unknown offline check schema must be reviewed explicitly.")
    result["checks"] = values(obj.get("checks", {}), booleans=known_checks)
    if docling:
        result["factResults"] = [values(row, booleans=("passed",), integers=("physicalPage",),
            codes=("id",), hashes=("annotationSha256",)) for row in obj.get("factResults", [])]
        packages = obj.get("observedPackageVersions", {})
        allowed = {"docling-serve", "docling-slim", "docling-parse", "docling-ibm-models", "rapidocr", "onnxruntime", "torch"}
        require(isinstance(packages, dict) and set(packages) <= allowed, "Unknown offline package inventory.")
        result["observedPackageVersions"] = values(packages, codes=sorted(allowed))
        pages = obj.get("physicalPageNumbers", [])
        require(isinstance(pages, list) and all(type(n) is int and n > 0 for n in pages), "Offline physical pages are not an explicit integer list.")
        result["physicalPageNumbers"] = pages
        result["responseConfiguration"] = values(obj.get("responseConfiguration", {}),
            booleans=("doOcr", "forceOcr"), codes=("status",))
    else:
        counts = obj.get("counts")
        result["counts"] = None if counts is None else values(counts, integers=("total", "executed", "passed", "failed", "notExecuted"))
        result["evidenceFileCount"] = len(obj.get("evidenceFiles", []))
        result["knownEvidenceHashes"] = []
        for row in obj.get("evidenceFiles", []):
            if row.get("name") in {"ocr-e2e.trx", "run-started.json", "ocrmypdf-version.txt", "ghostscript-version.txt",
                                   "tesseract-version.txt", "tesseract-languages.txt"}:
                result["knownEvidenceHashes"].append(values(row, codes=("name",), integers=("sizeBytes",), hashes=("sha256",)))
    result["offlineEngineProbeOnlyNotFullWorker"] = True
    return result


def offline_operator(obj):
    allowed = {"executed", "passed", "paidProviderCalls", "startedAtUnix", "finishedAtUnix", "containerId", "imageId",
        "probeExitCode", "exitCode", "oomKilled", "networkMode", "cacheMountedReadOnly", "probeResult", "testResult",
        "stopped", "failureType", "failure"}
    require(set(obj) <= allowed and obj.get("executed") is True and type(obj.get("passed")) is bool
            and "finishedAtUnix" in obj, "Unexpected or incomplete offline operator summary schema.")
    result = values(obj, booleans=("executed", "passed", "oomKilled", "cacheMountedReadOnly", "stopped"),
        integers=("paidProviderCalls",), codes=("containerId", "imageId", "networkMode", "failureType"))
    for name in ("probeExitCode", "exitCode"):
        if name in obj:
            require(type(obj[name]) is int and -32768 <= obj[name] <= 2**31 - 1, "Malformed bounded operator exit code.")
            result[name] = obj[name]
    result.update(numbers(obj, ("startedAtUnix", "finishedAtUnix")))
    result["embeddedProbeAndFailureTextExcluded"] = True
    return result


def offline_attestation(obj):
    require(obj.get("schemaVersion") == "saaia-linux-offline-container-attestation.v1", "Unexpected offline runtime attestation schema.")
    allowed = {"schemaVersion", "containerId", "imageId", "networkMode", "user", "memoryLimitBytes", "nanoCpus", "pidsLimit",
        "entrypointUnchanged", "devicesRequested", "portsPublished", "cacheMountedReadOnly", "sourceMountedReadOnly",
        "evidenceMountOnlyWritable", "stopped", "oomKilled", "beforeInspectSha256", "afterInspectSha256", "stoppedInspectSha256",
        "configEntrypointSha256", "configCmdSha256"}
    require(set(obj) <= allowed and {"containerId", "imageId", "networkMode", "user", "memoryLimitBytes", "nanoCpus",
            "pidsLimit", "stopped", "oomKilled"} <= set(obj), "Unexpected or incomplete offline runtime attestation fields.")
    return values(obj, booleans=("entrypointUnchanged", "devicesRequested", "portsPublished", "cacheMountedReadOnly",
        "sourceMountedReadOnly", "evidenceMountOnlyWritable", "stopped", "oomKilled"),
        integers=("memoryLimitBytes", "nanoCpus", "pidsLimit"), codes=("schemaVersion", "containerId", "imageId", "networkMode", "user"),
        hashes=("beforeInspectSha256", "afterInspectSha256", "stoppedInspectSha256", "configEntrypointSha256", "configCmdSha256"))


def project(name, obj):
    if name == "probe-result.json":
        return offline_probe(obj, "docling")
    if name in {"result-recovered.json", "result.json"}:
        return offline_probe(obj, "ocr")
    if name == "operator-result.json":
        return offline_operator(obj)
    if name == "runtime-attestation.json":
        return offline_attestation(obj)
    if name in {"execution-result.json", "lifecycle-result.json"}:
        return execution(obj, name.startswith("lifecycle"))
    if name == "publication-verification.json":
        return publication(obj)
    if name == "backend-binary-identity.json":
        return values(obj, hashes=("assemblySha256",), codes=("imageId", "codeRevisionObservedByReadiness"), booleans=("windowsAssemblyIdentityReused",))
    if name == "bound-runtime-provenance.json":
        return values(obj, booleans=("embeddingsBoundRevisionMatches", "qdrantBoundRevisionMatches"),
                      codes=("qdrantRevisionType", "qdrantImageId", "qdrantRuntimeRevision"))
    if name == "empty-baseline.json":
        return values(obj, integers=("documents", "jobs", "revisions", "units", "chunks", "canonicalBundles"))
    if name == "text-parser-conformance.json":
        return values(obj, booleans=("unicode_punctuation", "unicode_case", "conjunction", "accent_preserved"))
    if name == "sql-text-conformance.json":
        rows = obj.get("tables")
        require(isinstance(rows, list) and len(rows) == 4 and {r.get("table") for r in rows} == {
            "document_units", "retrieval_chunks", "contextual_text_entries", "exact_match_entries"}, "Four text checksum tables are required.")
        return {"tables": [values(row, codes=("table",), integers=("rows", "mismatches")) for row in rows]}
    if name == "tei-attestation.json":
        result = values(obj, booleans=("passed", "kernelFileOpenTracingPerformed"), integers=("dimensionObservedByBackendReadiness",))
        result["nativeInfo"] = values(obj.get("nativeInfo", {}), booleans=("auto_truncate",),
            integers=("max_input_length", "max_concurrent_requests", "max_batch_tokens", "max_batch_requests", "tokenization_workers"),
            codes=("model_id", "model_sha", "model_dtype", "version", "sha", "docker_label"))
        artifacts = obj.get("modelArtifactSha256", {})
        require(isinstance(artifacts, dict) and artifacts and set(artifacts) <= ARTIFACTS
                and all(isinstance(v, str) and HEX.fullmatch(v) for v in artifacts.values()), "Malformed public model artifact inventory.")
        result["modelArtifactSha256"] = artifacts
        result["containerPathsAndArgvExcludedFromProjection"] = True
        return result
    if name == "runtime-identity.json":
        return values(obj, hashes=("backendAssemblySha256", "configurationSha256"))
    raise ValueError("Filename is outside the exact public export allowlist.")


def plan(args):
    root = safe_path(args.namespace_root)
    run = uuid.UUID(root.name)
    require(root.parent.as_posix() == "/home/maxime/saaia-ingestion-qualification", "An exact dedicated Linux namespace is required.")
    role_roots = {"gold": root, "lifecycle": root, "recipe": safe_path(getattr(args, "recipe_namespace_root", None) or root)}
    for role_root in role_roots.values():
        uuid.UUID(role_root.name)
        require(role_root.parent == root.parent, "Every role must use an explicitly named UUID qualification namespace.")
    rows = []
    for role in ("gold", "recipe", "lifecycle"):
        folder = safe_path(getattr(args, role))
        require(folder.parent == role_roots[role] / "evidence" and re.fullmatch(r"[a-z][a-z0-9-]{0,95}", folder.name),
                "Select one explicit direct campaign folder per role under namespace/evidence.")
        rows.extend((role, folder / name) for name in (LIFECYCLE_FILES if role == "lifecycle" else CORPUS_FILES))
    for role, basename in EXTRAS.items():
        argument = getattr(args, role.replace("-", "_") + ("_proof" if role in {"docling", "ocr-v1", "ocr-v2"} else ""), None)
        if argument is None:
            continue
        path = safe_path(argument)
        prefix = "docling-offline-" if role.startswith("docling") else "ocr-e2e-"
        version = "v2" if role.startswith("ocr-v2") else "v1"
        parent = root / (prefix + run.hex[:12] + "-" + version)
        require(path.name == basename and path.parent in {parent, parent / "evidence"},
                "Optional probe path is outside its exact owned versioned folder/filename.")
        rows.append((role, path))
    require(len({p for _, p in rows}) == len(rows), "Campaign folders cannot alias each other.")
    output = safe_path(args.output)
    require(output.parent == root and re.fullmatch(r"public-export-[a-z0-9-]{1,64}", output.name),
            "Export output must be a new public-export-* child of the exact namespace.")
    return {"schemaVersion": "saaia-linux-public-export-plan.v1", "runId": str(run), "executeRequested": bool(args.execute),
            "selectedFileCount": len(rows), "sources": [role + "/" + path.name for role, path in rows],
            "campaignRunIds": {role: str(uuid.UUID(path.name)) for role, path in role_roots.items()},
            "rawCapturesSelected": False, "ownerConfigurationAndKeysSelected": False}, rows


def export(args):
    summary, rows = plan(args)
    if not args.execute:
        return summary
    require(not args.output.exists(), "Never overwrite an export directory.")
    prepared = []
    for role, source in rows:
        safe_path(source)
        require(source.is_file(), "A required public source proof is missing: " + role + "/" + source.name)
        raw = source.read_bytes()
        obj = decode(raw)
        projected = project(source.name, obj)
        if source.name in {"execution-result.json", "lifecycle-result.json"}:
            require(projected.get("plan", {}).get("runId") == summary["campaignRunIds"][role],
                    "A campaign result claims a different namespace than the explicitly selected role.")
        # Validate the projection again before any destination is created.
        projection = {"schemaVersion": "saaia-linux-public-proof-projection.v1", "campaignRole": role,
            "sourceFile": source.name, "sourceSha256": file_sha(raw), "sourceByteSize": len(raw), "proof": projected,
            "projectionOnly": True, "independentRuntimeValidationPerformedByExporter": False}
        wire = (json.dumps(projection, indent=2, ensure_ascii=False, allow_nan=False) + "\n").encode("utf-8")
        decode(wire)
        prepared.append((role, source, raw, wire))
    args.output.mkdir(mode=0o700)
    manifest = {"schemaVersion": "saaia-linux-public-export-seal.v1", "runId": summary["runId"],
        "campaignRunIds": summary["campaignRunIds"], "optionalSelections": sorted(role for role, _ in rows if role in EXTRAS),
        "createdAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "files": [],
        "networkCalls": 0, "subprocessCalls": 0, "secretStoresRead": 0, "rawCapturesExported": False,
        "projectionOnly": True, "runtimeAcceptanceInferredByExporter": False}
    for role, source, original, wire in prepared:
        require(source.read_bytes() == original, "A source proof changed while preparing the export.")
        destination = args.output / (role + "--" + source.name)
        with destination.open("xb") as stream:
            stream.write(wire)
        manifest["files"].append({"path": destination.name, "sha256": file_sha(wire), "byteSize": len(wire),
                                  "sourceFile": role + "/" + source.name, "sourceSha256": file_sha(original)})
    wire = (json.dumps(manifest, indent=2, ensure_ascii=False) + "\n").encode("utf-8")
    with (args.output / "public-export-seal.json").open("xb") as stream:
        stream.write(wire)
    return {"schemaVersion": manifest["schemaVersion"], "selectedFileCount": len(prepared),
            "sealSha256": file_sha(wire), "projectionOnly": True, "rawCapturesExported": False}


def verify(folder, expected_sha):
    folder = safe_path(folder)
    seal_path = safe_path(folder / "public-export-seal.json")
    raw = seal_path.read_bytes()
    require(HEX.fullmatch(expected_sha) and file_sha(raw) == expected_sha, "Seal hash differs from the separately received expected SHA256.")
    manifest = decode(raw)
    require(manifest.get("schemaVersion") == "saaia-linux-public-export-seal.v1", "Unexpected seal schema.")
    entries = manifest.get("files", [])
    extras = manifest.get("optionalSelections", [])
    require(isinstance(extras, list) and len(extras) == len(set(extras)) and set(extras) <= set(EXTRAS), "Unexpected optional seal selections.")
    require(isinstance(entries, list) and len(entries) == 18 + len(extras), "The three-campaign selection and optional files must be complete.")
    expected = {role + "--" + name for role in ("gold", "recipe", "lifecycle")
                for name in (LIFECYCLE_FILES if role == "lifecycle" else CORPUS_FILES)}
    expected.update(role + "--" + EXTRAS[role] for role in extras)
    require({e.get("path") for e in entries} == expected and len(entries) == len(expected), "Seal files differ from the exact export allowlist.")
    require({p.name for p in folder.iterdir()} == expected | {seal_path.name}, "Unexpected file/directory in the copied public export.")
    for entry in entries:
        path = safe_path(folder / entry["path"])
        wire = path.read_bytes()
        require(file_sha(wire) == entry.get("sha256") and len(wire) == entry.get("byteSize"), "A copied public file differs from the seal.")
        obj = decode(wire)
        require(obj.get("schemaVersion") == "saaia-linux-public-proof-projection.v1"
                and obj.get("sourceSha256") == entry.get("sourceSha256")
                and obj.get("campaignRole") + "/" + obj.get("sourceFile") == entry.get("sourceFile")
                and obj.get("campaignRole") + "--" + obj.get("sourceFile") == entry.get("path")
                and obj.get("projectionOnly") is True and obj.get("independentRuntimeValidationPerformedByExporter") is False,
                "Projection/source identity or scope differs from the seal.")
    return {"passed": True, "fileCount": len(entries), "sealSha256": expected_sha, "projectionOnly": True,
            "runtimeAcceptanceInferredByVerifier": False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("namespace-root", "recipe-namespace-root", "gold", "recipe", "lifecycle", "output", "verify-directory",
                 "docling-proof", "docling-operator", "docling-attestation", "ocr-v1-proof", "ocr-v1-operator", "ocr-v1-attestation",
                 "ocr-v2-proof", "ocr-v2-operator", "ocr-v2-attestation"):
        parser.add_argument("--" + name, type=Path)
    parser.add_argument("--expected-seal-sha256")
    parser.add_argument("--execute", action="store_true")
    args = parser.parse_args()
    if args.verify_directory:
        require(args.expected_seal_sha256 and not args.execute, "Verification requires the independent seal hash and performs no export.")
        result = verify(args.verify_directory, args.expected_seal_sha256)
    else:
        require(all(getattr(args, n) is not None for n in ("namespace_root", "gold", "recipe", "lifecycle", "output")),
                "Supply the exact namespace, three campaign folders and new export directory.")
        result = export(args)
    print(json.dumps(result, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as failure:
        # No source value or private content is printed by the CLI.
        print(json.dumps({"passed": False, "failureType": type(failure).__name__}))
        raise SystemExit(1)
