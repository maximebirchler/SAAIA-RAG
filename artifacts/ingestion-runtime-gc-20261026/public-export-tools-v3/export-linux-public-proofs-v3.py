"""Filesystem-only, allowlisted public projections for separated Linux campaigns.

NOEXEC plans can name future evidence. Execute reads only exact public filenames;
it never opens owner/config/key/PDF/SQL/log files and never runs SSH or a service.
Ship this file together with its immutable public_projection_base_v2.py module.
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
import stat
import sys
import uuid

BASE_PATH = Path(__file__).with_name("public_projection_base_v2.py")
BASE_SHA256 = "27d709df697eab65d8ba835634954e4290753a83859ac1417d18d7d9da5dc862"
if hashlib.sha256(BASE_PATH.read_bytes()).hexdigest() != BASE_SHA256:
    raise ValueError("The pinned public projection base changed.")
SPEC = importlib.util.spec_from_file_location("public_projection_base_v3", BASE_PATH)
BASE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BASE)

require, values, numbers, decode, file_sha, safe_path = BASE.require, BASE.values, BASE.numbers, BASE.decode, BASE.file_sha, BASE.safe_path
NAMESPACE_PARENT = Path("/home/maxime/saaia-ingestion-qualification")
FAMILIES = {"production": "production", "development": "development_diagnostic"}
KINDS = {
    "gold": BASE.CORPUS_FILES,
    "recipe": BASE.CORPUS_FILES,
    "lifecycle": ("lifecycle-result.json", "runtime-identity.json"),
    "failed-replacement": ("failed-replacement-result.json", "runtime-identity.json"),
    "retrieval-gold": ("retrieval-facts-result.json", "runtime-identity.json", "sql-text-conformance.json"),
    "retrieval-recipe": ("retrieval-facts-result.json", "runtime-identity.json", "sql-text-conformance.json"),
    "auto-scanner": ("automatic-ingestion-result.json", "backend-binary-identity.json", "bound-runtime-provenance.json",
                     "text-parser-conformance.json", "sql-text-conformance.json", "tei-attestation.json"),
    "auto-watcher": ("automatic-ingestion-result.json", "backend-binary-identity.json", "bound-runtime-provenance.json",
                     "text-parser-conformance.json", "sql-text-conformance.json", "tei-attestation.json"),
    "auto-defaults": ("automatic-ingestion-result.json", "backend-binary-identity.json", "bound-runtime-provenance.json",
                      "text-parser-conformance.json", "sql-text-conformance.json", "tei-attestation.json"),
}
ROLES = {family + "-" + kind: (family, kind) for family in FAMILIES for kind in KINDS}
RESULTS = {"execution-result.json", "lifecycle-result.json", "failed-replacement-result.json",
           "retrieval-facts-result.json", "automatic-ingestion-result.json"}
OFFLINE = {"docling": ("docling-offline", "v1", "probe-result.json")}
for _version in ("v1", "v2", "v3"):
    OFFLINE["ocr-" + _version] = ("ocr-e2e", _version, "result-recovered.json" if _version == "v1" else "result.json")
EXTRA_ARGUMENTS = {kind + suffix: (kind, basename) for kind, (_, _, proof) in OFFLINE.items()
                   for suffix, basename in (("-proof", proof), ("-operator", "operator-result.json"),
                                            ("-attestation", "runtime-attestation.json"))}
CONTAINER_FILE = "runtime-container-attestation.json"
SIGNATURE_FILE = "signature-rejection-result.json"
SIGNATURE_FILES = {SIGNATURE_FILE, "signature-negative-result.json"}
HF_MODEL = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,95}/[A-Za-z0-9][A-Za-z0-9._-]{0,95}")
ROLES5 = {"backend", "docling", "tei", "qdrant", "postgres"}
CONTAINER_FIELDS = {"role", "id", "imageId", "running", "externalEgressBlockedByDockerInternalNetwork",
                    "memoryLimitBytes", "nanoCpus", "loopbackPorts"}
RUNTIME_ATTESTATION_FIELDS = {"schemaVersion", "runId", "containers", "backendAssemblySha256", "configurationSha256",
    "codeRevisionObservedByReadiness", "qualificationMode", "environmentObservedFromContainer", "configurationSignatureMode",
    "configurationSignatureVerified", "observedAtUtc"}


def decode(raw):
    obj = BASE.decode(raw)
    # The isolated bootstrap format is not an OpenAI key. Reject its complete
    # recognizable prefix as well, without reading any key/credential store.
    require(re.search(r"saaia_linux_qual_[A-Za-z0-9_-]{12,}", json.dumps(obj, ensure_ascii=False)) is None,
            "Synthetic credential-shaped value found; no value will be printed.")
    return obj


def bounded_array(obj, name, maximum=8192):
    result = obj.get(name, [])
    require(isinstance(result, list) and len(result) <= maximum, "Malformed bounded public array: " + name)
    return result


def string_array(obj, name, *, pattern=BASE.SAFE_CODE, maximum=256):
    items = bounded_array(obj, name, maximum)
    require(all(isinstance(s, str) and pattern.fullmatch(s) for s in items), "Malformed public identifier/hash array: " + name)
    return items


def source_reference(value):
    if not isinstance(value, str) or value.count("/") != 1:
        return False
    role, name = value.split("/", 1)
    if role in ROLES:
        return name in {*KINDS[ROLES[role][1]], CONTAINER_FILE}
    if role == "signature-control":
        return name in SIGNATURE_FILES
    return role.startswith("offline-") and role[8:] in EXTRA_ARGUMENTS and name == EXTRA_ARGUMENTS[role[8:]][1]


def no_private_paths(obj, *, field=None):
    """Check exported values, not source fields that the projection discarded."""
    if isinstance(obj, dict):
        for key, value in obj.items():
            if "/" in key:
                require(field == "modelArtifactSha256" and key in BASE.ARTIFACTS, "A public field name resembles a private path.")
            else:
                no_private_paths(key)
            no_private_paths(value, field=key)
    elif isinstance(obj, list):
        for value in obj:
            no_private_paths(value, field=field)
    elif isinstance(obj, str):
        require(not obj.startswith(("/", "~")) and "\\" not in obj
                and re.match(r"^[A-Za-z]:", obj) is None and re.search(r"[A-Za-z][A-Za-z0-9+.-]*://", obj) is None,
                "A projected public value resembles a private path or URL.")
        if "/" in obj:
            require((field == "model_id" and HF_MODEL.fullmatch(obj)
                     and all(p not in {".", ".."} for p in obj.split("/")))
                    or (field in {"sourceFile", "sources", "selectedSources"} and source_reference(obj)),
                    "Only explicit symbolic source references or Hugging Face model IDs may contain a slash.")


def read_public(path):
    safe_path(path)
    flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_BINARY", 0)
    fd = os.open(path, flags)
    try:
        before = os.fstat(fd)
        require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1 and before.st_size <= 4 * 1024 * 1024,
                "Public JSON must be a bounded regular file without hardlink aliases.")
        if sys.platform == "linux":
            # Verify the actually opened source, including renamed/symlinked
            # parents, before reading any content from its descriptor.
            require(os.readlink("/proc/self/fd/" + str(fd)) == str(path), "Opened public source resolves outside its selected path.")
        pieces, total = [], 0
        while True:
            part = os.read(fd, min(65536, 4 * 1024 * 1024 + 1 - total))
            if not part:
                break
            pieces.append(part)
            total += len(part)
            require(total <= 4 * 1024 * 1024, "Public JSON grew beyond the bounded size.")
        after = os.fstat(fd)
        require((before.st_ino, before.st_dev, before.st_size, before.st_mtime_ns) ==
                (after.st_ino, after.st_dev, after.st_size, after.st_mtime_ns), "A public source changed while reading it.")
        return b"".join(pieces)
    finally:
        os.close(fd)


def cleanup(obj):
    result = BASE.cleanup(obj)
    result.update(values(obj, booleans=("producerStoppedBeforeFinalJobObservation",),
        integers=("jobsCanceled", "cancelRequests", "activeJobsAfterBackendStopped"),
        codes=("cancelObservationFailureType", "finalJobObservationFailureType", "restoreOrRecoveryFailureType")))
    if isinstance(obj.get("emergencyRecovery"), dict):
        result["emergencyRecovery"] = step(obj["emergencyRecovery"])
    return result


def step(obj):
    result = values(obj, booleans=("searchToReadPassed", "exactOldPointsAbsent", "activePublicationPreserved",
        "observedPresent", "workerHttpCancellationProven", "activePlanCleared", "searchEmpty", "previousPublicationPreserved",
        "previousVectorsPreserved", "physicalVectorsAndPayloadsUnchanged", "currentSourceFreshnessClaimed",
        "unboundedRetryObserved", "retryObservationIsTimeBounded", "canonicalPhysicalFactsVerified",
        "finalDeletionExecuted", "indexedVersionZero", "activePlanNull", "currentSearchEmpty"),
        integers=("ingestionVersion", "indexedVersion", "chunkCount", "pointCount", "generation", "attempts",
                  "terminalStableSeconds", "doneJobCount"),
        codes=("phase", "status", "terminal", "reason", "jobId", "docId", "revisionId", "planId", "pointId", "lastGcCode", "sourceCaseId"),
        hashes=("planFingerprint", "sourceSha256", "pointIdsSha256", "vectorsAndPayloadSha256", "capturedPointSha256"),
        dates=("lastSweep",))
    if "sources" in obj:
        result["sources"] = string_array(obj, "sources", pattern=re.compile(r"(?:scanner|watcher|scanner_missing|scanner_source_returned)"))
    if isinstance(obj.get("fault"), dict):
        result["fault"] = values(obj["fault"], booleans=("prefixOnly",), integers=("originalByteCount", "truncatedByteCount"),
                               hashes=("sourceSha256", "truncatedSha256"))
    return result


def status_projection(obj):
    result = values(obj, booleans=("apiAcknowledged", "previousPublicationPreserved", "upstreamEngineCancellationVerified",
        "currentSourceFreshnessReverified", "sameSqlSnapshot", "parallelOcrComputeProven", "queuedBeforeReturn",
        "sourceReturnBeforeExecution", "deleteCanceled", "terminalPublishedGenerationPreserved",
        "continuousPublicationAvailabilityProven", "freshPublicationVerified", "upstreamHttpCancellationProven"),
        integers=("abandonedGeneration",), codes=("status", "reason", "jobId", "observedBeforeCancel"))
    for name, pattern in (("jobIds", re.compile(r"[a-f0-9-]{36}")), ("ownerSha256", BASE.HEX)):
        if name in obj:
            result[name] = string_array(obj, name, pattern=pattern, maximum=2)
    if "consumerIndexes" in obj:
        indexes = bounded_array(obj, "consumerIndexes", 2)
        require(all(type(i) is int and i in {0, 1} for i in indexes), "Malformed public consumer indexes.")
        result["consumerIndexes"] = indexes
    return result


def execution(obj, kind):
    expected = {"gold": "saaia-linux-ingestion-execution.v1", "recipe": "saaia-linux-ingestion-execution.v1",
        "lifecycle": "saaia-linux-lifecycle-execution.v1", "failed-replacement": "saaia-linux-failed-replacement-execution.v1",
        "retrieval-gold": "saaia-linux-retrieval-facts-execution.v1", "retrieval-recipe": "saaia-linux-retrieval-facts-execution.v1",
        "auto-scanner": "saaia-linux-automatic-ingestion-execution.v1", "auto-watcher": "saaia-linux-automatic-ingestion-execution.v1",
        "auto-defaults": "saaia-linux-automatic-ingestion-execution.v1"}[kind]
    require(obj.get("schemaVersion") == expected, "Unexpected terminal schema for the selected campaign role.")
    translated = copy.deepcopy(obj)
    translated["schemaVersion"] = "saaia-linux-ingestion-execution.v1"
    auto = kind.startswith("auto-")
    if auto:
        # AUTO binds three source hashes; the old corpus plan has one optional
        # source hash. Preserve their real types instead of pretending they agree.
        translated["plan"] = {k: v for k, v in translated.get("plan", {}).items() if k != "sourceSha256"}
    result = BASE.execution(translated)
    result["schemaVersion"] = expected
    result["cleanup"] = cleanup(obj.get("cleanup", {}))
    result["plan"].update(values(obj.get("plan", {}), booleans=("apiJobEnqueueAllowed", "jobAdmissionAllowed",
        "acceleratedTimingsAreDeploymentSla", "newReadinessHttpRequestPlanned"),
        integers=("caseCount", "factCount", "maximumHttpCalls", "maximumRetrievalWindowSeconds", "maximumRequestSeconds",
                  "transportRetryCount", "maximumCampaignSeconds", "maximumPhaseSeconds", "terminalStabilitySeconds"),
        codes=("admissionProfile",), dates=("campaignDeadlineUtc",), hashes=("lifecycleProofSha256",)))
    result.update(values(obj, booleans=("functionalPassed", "otherPublicationsAndFullVectorsPreserved", "sourceAndPublicationStable",
        "currentCanonicalPhysicalFactsVerified", "factCoverageComplete", "caseControlCoverageComplete", "readinessHttpRequeried"),
        integers=("newJobAdmissions", "httpAttemptCount", "passedFactCount", "failedFactCount", "apiIngestionEnqueueCount", "sqlMutations",
                  "maximumActiveJobsObserved", "quietObservationSeconds"), dates=("retrievalFinishedAtUtc", "qualificationFinishedAtUtc")))
    if kind == "lifecycle" or kind == "failed-replacement" or auto:
        result["steps"] = [step(s) for s in bounded_array(obj, "steps", 256)]
        for name in ("cancel", "twoWorkers", "conditionalDeleteReturn"):
            if name in obj:
                result[name] = status_projection(obj[name])
        for name in ("failedReplacement", "recovery"):
            if name in obj:
                result[name] = step(obj[name])
        if "unrelatedPublicationPreservation" in obj:
            result["unrelatedPublicationPreservation"] = values(obj["unrelatedPublicationPreservation"],
                booleans=("sqlStateAndChunkChecksumsUnchanged", "currentPhysicalVectorsAndPayloadsUnchanged"),
                integers=("pointCount",), hashes=("vectorsAndPayloadSha256",))
    if auto:
        plan = obj.get("plan", {})
        result["plan"]["sourceCaseIds"] = string_array(plan, "sourceCaseIds", pattern=re.compile(r"[a-z0-9-]{1,96}"), maximum=3)
        result["plan"]["sourceSha256"] = string_array(plan, "sourceSha256", pattern=BASE.HEX, maximum=3)
        result["plan"]["targetDocPathCount"] = len(bounded_array(plan, "targetDocPaths", 3))
        config = obj.get("observedAdmissionConfiguration", {})
        result["observedAdmissionConfiguration"] = values(config, booleans=("scannerEnabled", "watcherEnabled", "llmWorkersDisabled"),
            integers=("workerConcurrency",))
        result["observedAdmissionConfiguration"]["acceleratedOptions"] = values(config.get("acceleratedOptions", {}), integers=(
            "MaxFilesPerScan", "MinFileAgeSeconds", "ScanIntervalSeconds", "MissingGraceSeconds", "WatcherDebounceMs", "WorkerConcurrency"))
        result["boundIngestionReadiness"] = values(obj.get("boundIngestionReadiness", {}), integers=("boundWorkerConcurrency",))
        require(plan.get("admissionProfile") == kind[5:], "AUTO result belongs to a different trigger profile.")
    if kind.startswith("retrieval-"):
        result["facts"] = []
        for fact in bounded_array(obj, "facts", 106):
            projected = values(fact, booleans=("currentIdentitiesExact", "exactFactFound", "searchToReadExact", "noDegradedRetrievers",
                "physicalFactVerifiedInCurrentCanonicalBundle", "passed", "readReused"),
                integers=("physicalPage", "indexedVersion", "hitCount"), codes=("caseId", "factId", "docId", "revisionId", "chunkId"),
                hashes=("annotationSha256", "sourceSha256", "planFingerprint"))
            rank = fact.get("rank")
            require(rank is None or type(rank) is int and 1 <= rank <= 10000, "Malformed public fact rank.")
            projected["rank"] = rank
            projected.update(numbers(fact, ("latencySeconds",)))
            result["facts"].append(projected)
        result["negativeControls"] = [values(control, booleans=("passed", "requestsSourceBound", "negativeControlsPassed",
            "absentQueryLexicalMatchesZero", "absentQueryDenseNeighborsCurrent", "absentDocumentEmpty", "negativeMetricsExplicitlyNotDegraded"),
            integers=("absentQueryDenseNeighborCount",), codes=("caseId",)) for control in bounded_array(obj, "negativeControls", 13)]
        result["qualificationEvidenceInheritedFromPriorCorpus"] = True
        result["exportedFactCount"] = len(result["facts"])
    return result


def containers(obj):
    result = []
    for row in bounded_array(obj, "containers", 5):
        require(isinstance(row, dict) and set(row) <= CONTAINER_FIELDS and row.get("role") in ROLES5
                and re.fullmatch(r"[a-f0-9]{64}", str(row.get("id")))
                and re.fullmatch(r"sha256:[a-f0-9]{64}", str(row.get("imageId"))), "Malformed public container identity.")
        projected = values(row, booleans=("running", "externalEgressBlockedByDockerInternalNetwork"),
            integers=("memoryLimitBytes", "nanoCpus"), codes=("role", "id", "imageId"))
        if "loopbackPorts" in row:
            ports = bounded_array(row, "loopbackPorts", 4)
            require(all(type(p) is int and 1024 <= p <= 65535 and p != 5432 for p in ports), "Malformed public loopback port.")
            projected["loopbackPorts"] = ports
        result.append(projected)
    require(len({r["role"] for r in result}) == len(result), "Public container roles are duplicated.")
    require(len({r["id"] for r in result}) == len(result), "Public container IDs are duplicated across roles.")
    return result


def project(name, obj, kind):
    if name in SIGNATURE_FILES:
        observed = name == "signature-negative-result.json"
        require(obj.get("schemaVersion") == ("saaia-real-production-signature-negative.v1" if observed else "saaia-linux-signature-rejection-control.v1")
                and obj.get("executed") is True and type(obj.get("passed")) is bool
                and (observed or obj.get("finishedAtUtc")), "Unexpected or incomplete signature control schema.")
        result = values(obj, booleans=("executed", "passed", "configurationTampered", "startupRejected",
            "rejectionObservedInDiagnostics", "sourceConfigurationUnchanged", "oomKilled", "stopped", "privateKeyTransferred",
            "tamperedConfigurationRejected", "ownedContainerStopped"), integers=("paidProviderCalls",),
            codes=("schemaVersion", "runId", "failureType", "failureCode", "containerId", "imageId", "codeRevisionObservedByReadiness", "networkMode"),
            hashes=("sourceConfigurationSha256", "originalConfigurationSha256", "tamperedConfigurationSha256", "signatureFileSha256",
                    "backendAssemblySha256", "diagnosticsSha256"),
            dates=("startedAtUtc", "finishedAtUtc"))
        require(type(obj.get("exitCode")) is int and -32768 <= obj["exitCode"] <= 2**31-1, "Invalid signature control exit code.")
        result["exitCode"] = obj["exitCode"]
        result["independentNegativeControlNotProductionQualification"] = True
        if "containerId" in result:
            require(re.fullmatch(r"[a-f0-9]{64}", result["containerId"]), "Invalid signature control CID.")
        if "imageId" in result:
            require(re.fullmatch(r"sha256:[a-f0-9]{64}", result["imageId"]), "Invalid signature control image ID.")
    elif name in RESULTS:
        result = execution(obj, kind)
    elif name == "runtime-identity.json":
        result = values(obj, hashes=("backendAssemblySha256", "configurationSha256"))
        result["containers"] = containers(obj)
    elif name == CONTAINER_FILE:
        require(set(obj) <= RUNTIME_ATTESTATION_FIELDS and obj.get("schemaVersion") == "saaia-linux-public-runtime-attestation.v1"
                and len(obj.get("containers", [])) == 5, "Unexpected public runtime attestation schema or container set.")
        result = values(obj, booleans=("configurationSignatureVerified",), codes=("schemaVersion", "runId",
            "codeRevisionObservedByReadiness", "environmentObservedFromContainer", "qualificationMode", "configurationSignatureMode"),
            hashes=("backendAssemblySha256", "configurationSha256"), dates=("observedAtUtc",))
        result["containers"] = containers(obj)
    else:
        result = BASE.project(name, obj)
        if name == "backend-binary-identity.json" and "imageId" in result:
            require(re.fullmatch(r"sha256:[a-f0-9]{64}", str(result["imageId"])), "A public backend image must be an immutable Docker image ID.")
        if name == "tei-attestation.json":
            model = result.get("nativeInfo", {}).get("model_id")
            require(model is None or (isinstance(model, str) and HF_MODEL.fullmatch(model)
                    and all(part not in {".", ".."} for part in model.split("/"))),
                    "Public TEI model_id must be a Hugging Face organization/model identifier, never a local path.")
            result["container"] = values(obj.get("container", {}), booleans=("autoTruncate", "ownedContainerVerified"),
                hashes=("modelFileSha256",), codes=("imageId", "modelRevision"))
            if "imageId" in result["container"]:
                require(re.fullmatch(r"sha256:[a-f0-9]{64}", str(result["container"]["imageId"])),
                        "A public TEI image must be an immutable Docker image ID.")
    no_private_paths(result)
    return result


def qualification(proof, family, kind):
    expected = FAMILIES[family]
    observed = proof.get("qualificationMode")
    require(observed is None or observed == expected, "Production and Development evidence cannot be relabeled or mixed.")
    environment = proof.get("environmentObservedFromContainer")
    require(environment is None or environment == ("Production" if family == "production" else "Development"),
            "The selected evidence family contradicts its observed container environment.")
    if proof.get("passed") is True:
        require(observed == expected, "A successful terminal proof needs its observed qualification mode.")
        if family == "production":
            require(proof.get("deploymentQualificationEligible") is True
                    and (kind.startswith("retrieval-") or (environment == "Production" and proof.get("configurationSignatureVerified") is True)),
                    "A successful Production proof needs verified Production runtime evidence.")
        else:
            require(proof.get("deploymentQualificationEligible") is False,
                    "Development proof cannot claim Production eligibility.")
    return {"selectedFamily": family, "expectedQualificationMode": expected,
        "observedQualificationMode": observed, "observedQualificationModePresent": observed is not None,
        "signatureEvidenceInheritedFromPriorCorpus": kind.startswith("retrieval-"),
        "runtimeQualificationInferredByExporter": False}


def namespace(root):
    safe_path(root)
    require(root.parent == NAMESPACE_PARENT and str(uuid.UUID(root.name)) == root.name,
            "Every campaign must belong to an explicitly named dedicated canonical UUID namespace.")
    return root


def campaign_folder(folder):
    safe_path(folder)
    root = namespace(folder.parent.parent)
    require(folder.parent == root / "evidence" and re.fullmatch(r"[a-z][a-z0-9-]{0,95}", folder.name),
            "Select an explicit direct namespace/evidence/campaign folder.")
    return root


def plan(args):
    rows, groups = [], {}
    for role, (family, kind) in ROLES.items():
        folder = getattr(args, role.replace("-", "_"), None)
        if folder is None:
            continue
        root = campaign_folder(folder)
        groups[role] = {"runId": root.name, "family": family, "kind": kind, "folder": folder}
        rows += [{"role": role, "root": root, "family": family, "kind": kind, "path": folder / name} for name in KINDS[kind]]
    for label, (offline_kind, basename) in EXTRA_ARGUMENTS.items():
        path = getattr(args, label.replace("-", "_"), None)
        if path is None:
            continue
        safe_path(path)
        prefix, version, _ = OFFLINE[offline_kind]
        probe = path.parent.parent if path.parent.name == "evidence" else path.parent
        root = namespace(probe.parent)
        require(path.name == basename and probe.name == prefix + "-" + uuid.UUID(root.name).hex[:12] + "-" + version,
                "Optional offline proof is outside its exact named versioned folder.")
        role = "offline-" + label
        groups[role] = {"runId": root.name, "family": "independent_engine_probe", "kind": offline_kind, "folder": probe}
        rows.append({"role": role, "root": root, "family": "independent_engine_probe", "kind": offline_kind, "path": path})
    signature = getattr(args, "signature_control", None)
    if signature is not None:
        require(signature.name in SIGNATURE_FILES, "Signature control must use its exact public filename.")
        if signature.name == "signature-negative-result.json":
            root = namespace(signature.parent.parent)
            require(signature.parent.name == "signature-negative-final-v1", "Select the exact observed versioned signature control folder.")
        else:
            root = campaign_folder(signature.parent)
        safe_path(signature)
        groups["signature-control"] = {"runId": root.name, "family": "independent_signature_control",
                                       "kind": "signature-rejection", "publicFile": signature.name, "folder": signature.parent}
        rows.append({"role": "signature-control", "root": root, "family": "independent_signature_control",
                     "kind": "signature-rejection", "path": signature})
    for spec in getattr(args, "container_attestation", None) or []:
        require(isinstance(spec, str) and spec.count("=") == 1, "Use ROLE=absolute-path for public container attestations.")
        role, value = spec.split("=", 1)
        require(role in groups and role in ROLES, "Select the corresponding campaign before adding its container attestation.")
        path = safe_path(Path(value))
        require(path == groups[role]["folder"] / CONTAINER_FILE, "Container attestation must use its exact campaign folder and public filename.")
        rows.append({"role": role, "root": Path(path).parent.parent.parent, "family": groups[role]["family"],
                     "kind": groups[role]["kind"], "path": path})
    require(rows and len(rows) <= 150 and len({row["path"] for row in rows}) == len(rows),
            "Select distinct nonempty bounded public proof files, without campaign aliases.")
    output = safe_path(args.output)
    namespace(output.parent)
    require(output.parent.name in {g["runId"] for g in groups.values()}
            and re.fullmatch(r"public-export-[a-z0-9-]{1,64}", output.name),
            "A new public-export-* output must be a direct child of one selected UUID namespace.")
    public_groups = {role: {k: v for k, v in group.items() if k != "folder"} for role, group in groups.items()}
    return {"schemaVersion": "saaia-linux-public-export-plan.v3", "executeRequested": bool(args.execute),
        "selectedFileCount": len(rows), "campaigns": public_groups,
        "sources": [row["role"] + "/" + row["path"].name for row in rows],
        "ownerConfigurationAndKeysSelected": False, "rawCapturesSelected": False,
        "futureInputsAllowedInNoExecute": True}, rows


def export(args):
    summary, rows = plan(args)
    if not args.execute:
        return summary
    require(not args.output.exists(), "Never overwrite a public export directory.")
    prepared = []
    for row in rows:
        source = row["path"]
        raw = read_public(source)
        obj = decode(raw)
        projected = project(source.name, obj, row["kind"])
        family = None
        if source.name in RESULTS:
            require(projected.get("plan", {}).get("runId") == row["root"].name,
                    "A terminal result claims another namespace than its selected evidence folder.")
            family = qualification(projected, row["family"], row["kind"])
        elif source.name == CONTAINER_FILE:
            require(projected.get("runId") == row["root"].name, "Container attestation claims another namespace.")
            family = qualification(projected, row["family"], row["kind"])
        elif source.name in SIGNATURE_FILES:
            require(projected.get("runId") in {None, row["root"].name}, "Signature control claims another namespace.")
        envelope = {"schemaVersion": "saaia-linux-public-proof-projection.v3", "campaignRole": row["role"],
            "campaignRunId": row["root"].name, "qualificationFamily": row["family"], "sourceFile": source.name,
            "sourceSha256": file_sha(raw), "sourceByteSize": len(raw), "proof": projected,
            "projectionOnly": True, "independentRuntimeValidationPerformedByExporter": False}
        if family is not None:
            envelope["qualificationSeparation"] = family
        if source.name in SIGNATURE_FILES:
            envelope["sourceRunIdPresent"] = projected.get("runId") is not None
            envelope["containerNamespaceIndependentlyAttestedByExporter"] = False
        wire = (json.dumps(envelope, ensure_ascii=False, indent=2, allow_nan=False) + "\n").encode("utf-8")
        decode(wire)
        no_private_paths(envelope)
        prepared.append((row, raw, wire))
    require(all(read_public(row["path"]) == raw for row, raw, _ in prepared), "A selected source changed during export preparation.")
    args.output.mkdir(mode=0o700)
    seal = {"schemaVersion": "saaia-linux-public-export-seal.v3", "createdAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "campaigns": summary["campaigns"], "selectedSources": summary["sources"], "files": [],
        "projectionOnly": True, "runtimeAcceptanceInferredByExporter": False, "networkCalls": 0,
        "subprocessCalls": 0, "secretStoresRead": 0, "rawCapturesExported": False,
        "projectionBaseSha256": BASE_SHA256}
    for row, raw, wire in prepared:
        name = row["role"] + "--" + row["path"].name
        with (args.output / name).open("xb") as target:
            target.write(wire)
        seal["files"].append({"path": name, "sha256": file_sha(wire), "byteSize": len(wire),
            "sourceFile": row["role"] + "/" + row["path"].name, "sourceSha256": file_sha(raw), "sourceByteSize": len(raw)})
    require(all(read_public(row["path"]) == raw for row, raw, _ in prepared), "A selected source changed before sealing.")
    wire = (json.dumps(seal, ensure_ascii=False, indent=2, allow_nan=False) + "\n").encode("utf-8")
    decode(wire)
    no_private_paths(seal)
    with (args.output / "public-export-seal.json").open("xb") as target:
        target.write(wire)
    return {"schemaVersion": seal["schemaVersion"], "selectedFileCount": len(prepared), "sealSha256": file_sha(wire),
        "projectionOnly": True, "rawCapturesExported": False, "runtimeAcceptanceInferredByExporter": False}


def expected_files(campaigns, selections):
    require(isinstance(campaigns, dict) and campaigns and isinstance(selections, list)
            and len(selections) == len(set(selections)) and len(selections) <= 150, "Malformed sealed campaign selection.")
    required, allowed = set(), set()
    for role, group in campaigns.items():
        require(isinstance(group, dict) and str(uuid.UUID(group.get("runId", ""))) == group["runId"], "Malformed sealed namespace.")
        if role in ROLES:
            family, kind = ROLES[role]
            require(group.get("family") == family and group.get("kind") == kind, "A sealed role contradicts its qualification family.")
            names = {role + "/" + name for name in KINDS[kind]}
            required |= names
            allowed |= names | {role + "/" + CONTAINER_FILE}
        elif role.startswith("offline-") and role[8:] in EXTRA_ARGUMENTS:
            offline_kind, name = EXTRA_ARGUMENTS[role[8:]]
            require(group.get("family") == "independent_engine_probe" and group.get("kind") == offline_kind,
                    "Malformed sealed offline role.")
            required.add(role + "/" + name)
            allowed.add(role + "/" + name)
        elif role == "signature-control":
            require(group.get("family") == "independent_signature_control" and group.get("kind") == "signature-rejection",
                    "Malformed sealed signature control role.")
            require(group.get("publicFile") in SIGNATURE_FILES, "Unknown sealed signature proof filename.")
            required.add(role + "/" + group["publicFile"])
            allowed.add(role + "/" + group["publicFile"])
        else:
            raise ValueError("Unknown sealed campaign role.")
    require(required <= set(selections) <= allowed, "The sealed selection differs from complete explicit role allowlists.")
    return {name.replace("/", "--", 1) for name in selections}


def verify(folder, expected_sha):
    safe_path(folder)
    wire = read_public(folder / "public-export-seal.json")
    require(BASE.HEX.fullmatch(str(expected_sha)) and file_sha(wire) == expected_sha, "The separately received seal SHA256 differs.")
    seal = decode(wire)
    no_private_paths(seal)
    require(seal.get("schemaVersion") == "saaia-linux-public-export-seal.v3" and seal.get("projectionBaseSha256") == BASE_SHA256,
            "Unexpected seal format or projection implementation.")
    expected = expected_files(seal.get("campaigns"), seal.get("selectedSources"))
    entries = bounded_array(seal, "files", 150)
    require(len(entries) == len(expected) and {e.get("path") for e in entries} == expected,
            "Sealed entries differ from the exact public selection.")
    require({p.name for p in folder.iterdir()} == expected | {"public-export-seal.json"}, "The copied directory contains an extra/missing entry.")
    for entry in entries:
        path = safe_path(folder / entry["path"])
        raw = read_public(path)
        require(file_sha(raw) == entry.get("sha256") and len(raw) == entry.get("byteSize"), "A copied public JSON differs from its seal.")
        obj = decode(raw)
        role, name = entry["sourceFile"].split("/", 1)
        require(role in seal["campaigns"] and name in {p.split("/", 1)[1] for p in seal["selectedSources"] if p.startswith(role + "/")}
                and entry["path"] == role + "--" + name and obj.get("schemaVersion") == "saaia-linux-public-proof-projection.v3"
                and obj.get("campaignRole") == role and obj.get("sourceFile") == name
                and obj.get("sourceSha256") == entry.get("sourceSha256") and obj.get("sourceByteSize") == entry.get("sourceByteSize")
                and obj.get("campaignRunId") == seal["campaigns"][role]["runId"]
                and obj.get("qualificationFamily") == seal["campaigns"][role]["family"]
                and obj.get("projectionOnly") is True and obj.get("independentRuntimeValidationPerformedByExporter") is False,
                "A projection/source/role/namespace differs from the seal.")
        no_private_paths(obj)
    return {"passed": True, "fileCount": len(entries), "sealSha256": expected_sha,
            "projectionOnly": True, "runtimeAcceptanceInferredByVerifier": False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for role in ROLES:
        parser.add_argument("--" + role, type=Path)
    for extra in EXTRA_ARGUMENTS:
        parser.add_argument("--" + extra, type=Path)
    parser.add_argument("--container-attestation", action="append", default=[])
    parser.add_argument("--signature-control", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--verify-directory", type=Path)
    parser.add_argument("--expected-seal-sha256")
    parser.add_argument("--execute", action="store_true")
    args = parser.parse_args()
    if args.verify_directory:
        require(not args.execute and args.expected_seal_sha256, "Verification needs the independently received seal SHA256 and no --execute.")
        result = verify(args.verify_directory, args.expected_seal_sha256)
    else:
        require(args.output is not None, "Supply the new public export directory.")
        result = export(args)
    print(json.dumps(result, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as failure:
        print(json.dumps({"passed": False, "failureType": type(failure).__name__}))
        raise SystemExit(1)
