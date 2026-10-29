"""Bind a planning answer to a NEW real-worker publication, never a projection.

This adapter consumes actual SQL GUIDs and immutable canonical/RAW captures.
It does not rebuild chunks, reinterpret PDF layout, derive chunk identities or
change the frozen native-fixture verifier. Culinary/PDF ownership review remains
a separate acceptance gate, even when every mechanical check passes.
"""
import argparse
import base64
import datetime
import hashlib
import json
import os
import pathlib
import re
import stat
import uuid

import verify_planning_result as planning
import verify_worker_corpus as worker
import linux_planning_contracts as linux
from embedding_attestation import attest_info


PROJECTIONS = {"retrievalChunks": "retrievalChunkId", "units": "unitId", "sections": "sectionId", "contentCards": "contentCardId"}
NATIVE_TYPES = {"native_text_recovery", "native_layout_recovery", "native_table_cell_observation"}
RAW_FALLBACKS = {"native_raw_page_size_limit", "native_raw_document_size_limit", "native_raw_catalog_size_limit",
                 "native_raw_page_time_limit", "native_raw_page_inventory_noncontiguous", "native_raw_page_projection_unrepresentable"}


class CaptureMap:
    """Explicit, byte-bound transfer of closed Linux evidence to this machine."""
    def __init__(self, manifest_path):
        self.path = planning.extended_windows_path(pathlib.Path(manifest_path))
        raw = self.path.read_bytes()
        self.sha256 = planning.sha(raw)
        value = json.loads(raw)
        require(value["schemaVersion"] == "saaia-worker-capture-transfer.v1", "worker_capture_map_schema_invalid")
        remote = value["remoteOwnerRoot"]
        require(isinstance(remote, str) and remote.startswith("/") and "\\" not in remote
                and all(part not in ("", ".", "..") for part in remote.split("/")[1:]), "worker_capture_remote_root_invalid")
        self.remote_root = pathlib.PurePosixPath(remote)
        local_text = value["localCapturedRoot"]
        local = pathlib.Path(local_text)
        require(local.is_absolute() and not any(part in (".", "..") for part in re.split(r"[\\/]", local_text)),
                "worker_capture_local_root_must_be_absolute")
        # Inspect the spelling supplied by the transfer manifest BEFORE the
        # shared long-path helper resolves it; resolving first hides junctions.
        for ancestor in reversed((local, *local.parents)):
            self._no_links(ancestor)
        self.local_root = planning.extended_windows_path(local)
        require(self.local_root.is_dir() and self.local_root.resolve(strict=True) == self.local_root,
                "worker_capture_local_root_not_exact")
        self.entries = {}
        folded = set()
        for entry in value["files"]:
            relative = entry["relativePath"]
            require(isinstance(relative, str) and not relative.startswith("/") and "\\" not in relative
                    and all(part not in ("", ".", "..") and ":" not in part for part in relative.split("/")),
                    "worker_capture_relative_path_invalid")
            require(relative.casefold() not in folded and check_hash(entry["sha256"])
                    and type(entry["bytes"]) is int and entry["bytes"] >= 0, "worker_capture_file_entry_invalid_or_duplicate")
            folded.add(relative.casefold())
            self.entries[relative] = entry
        require(self.entries, "worker_capture_empty_transfer_inventory")
        for relative in self.entries:
            self._checked_path(relative)

    @staticmethod
    def _no_links(path):
        observed = path.lstat()
        require(not path.is_symlink() and not (getattr(observed, "st_file_attributes", 0)
                & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)), "worker_capture_symlink_or_reparse_forbidden")

    def _checked_path(self, relative):
        entry = self.entries[relative]
        result = self.local_root.joinpath(*relative.split("/"))
        current = self.local_root
        for part in relative.split("/"):
            current = current / part
            self._no_links(current)
        require(result.is_file() and result.resolve(strict=True) == result
                and result.is_relative_to(self.local_root), "worker_capture_file_outside_exact_root")
        require(result.stat().st_size == entry["bytes"], "worker_capture_file_size_changed")
        with result.open("rb") as stream:
            actual = hashlib.file_digest(stream, "sha256").hexdigest()
        require(actual == entry["sha256"], "worker_capture_file_sha_changed")
        return result

    def resolve(self, value, expected_sha=None):
        text = str(value)
        if not text.startswith("/"):
            # Control files must keep their original Linux owner paths. PDFs
            # and independent renders use their original local paths outside
            # this resolver, with separate source-manifest SHA checks.
            raise ValueError("worker_capture_control_path_must_be_remote")
        require("\\" not in text and all(part not in ("", ".", "..") for part in text.split("/")[1:]),
                "worker_capture_remote_path_invalid")
        remote = pathlib.PurePosixPath(text)
        require(remote.is_relative_to(self.remote_root) and remote != self.remote_root, "worker_capture_remote_path_outside_owner")
        relative = remote.relative_to(self.remote_root).as_posix()
        require(relative in self.entries, "worker_capture_required_file_not_transferred")
        require(expected_sha is None or self.entries[relative]["sha256"] == expected_sha,
                "worker_capture_transfer_hash_not_equal_original_pin")
        return self._checked_path(relative)


def capture_path(value, transfer=None, expected_sha=None):
    if transfer is not None:
        return transfer.resolve(value, expected_sha)
    require(not (os.name == "nt" and str(value).startswith("/") and not str(value).startswith("//")),
            "worker_capture_map_required_for_linux_path")
    return planning.extended_windows_path(pathlib.Path(value))


def require(value, reason):
    planning.require(value, reason)


def check_hash(value):
    return isinstance(value, str) and re.fullmatch(r"[a-f0-9]{64}", value) is not None


def unique_rows(rows, key, reason):
    values = {row[key]: row for row in rows}
    require(len(values) == len(rows), reason)
    return values


def anchor_raw_hashes(artifact):
    """Hash each anchor's original canonical JSON slice, preserving number form.

    SQL jsonb does not preserve a serialized object. Its content_hash does refer
    to canonical source bytes. Extract those bytes from the hash-checked bundle
    instead of guessing .NET float/Unicode serialization with Python json.dumps.
    """
    raw = base64.b64decode(re.sub(r"\s", "", artifact["payloadBase64"]), validate=True)
    import gzip
    text = gzip.decompress(raw).decode("utf-8", "strict")
    decoder = json.JSONDecoder()
    offset = 1
    while offset < len(text):
        while text[offset].isspace() or text[offset] == ",":
            offset += 1
        if text[offset] == "}":
            break
        key, offset = decoder.raw_decode(text, offset)
        while text[offset].isspace():
            offset += 1
        require(text[offset] == ":", "worker_adapter_canonical_object_invalid")
        offset += 1
        while text[offset].isspace():
            offset += 1
        if key != "sourceAnchors":
            _, offset = decoder.raw_decode(text, offset)
            continue
        require(text[offset] == "[", "worker_adapter_canonical_anchor_array_invalid")
        offset += 1
        hashes = {}
        while offset < len(text):
            while text[offset].isspace() or text[offset] == ",":
                offset += 1
            if text[offset] == "]":
                return hashes
            anchor, end = decoder.raw_decode(text, offset)
            require(anchor["anchorId"] not in hashes, "worker_adapter_duplicate_canonical_anchor")
            hashes[anchor["anchorId"]] = planning.sha(text[offset:end].encode("utf-8"))
            offset = end
    raise ValueError("worker_adapter_canonical_anchor_array_missing")


def expected_raw_block(block):
    raw = block["text"]["raw"]
    require(planning.sha(raw.encode("utf-8")) == block["text"]["rawSha256"], "worker_adapter_canonical_native_raw_hash_changed")
    provenance = block["provenance"]
    return {"blockId": block["blockId"], "blockType": block["blockType"], "ordinal": block["ordinal"],
            "readingOrder": block["readingOrder"], "raw": raw, "rawSha256": block["text"]["rawSha256"],
            "observedPolygon": block.get("polygon"), "qualityFlags": block["qualityFlags"],
            **{key: provenance[key] for key in ("engine", "engineVersion", "method", "stageId")}}


def verify_raw(doc, bundle):
    canonical = bundle["document"]
    row = doc["nativeRawCatalog"]
    catalog = worker.blob(row, compressed=False)
    require(catalog.get("schemaVersion") == "native_raw_page_catalog_v1", "worker_adapter_raw_catalog_schema_invalid")
    root, generation = catalog["root"], catalog["generation"]
    identity = root["identity"]
    require(identity["documentId"] == doc["docId"] and identity["revisionId"] == doc["revisionId"]
            and identity["sourceSha256"] == doc["sourceSha256"]
            and identity["tenantId"] == row["tenant_id"] == doc["ingestionJob"]["tenant_id"]
            and root["canonicalBundleSha256"] == row["canonicalBundleHash"] == doc["canonicalArtifact"]["contentHash"]
            and catalog["mode"] == row["mode"] and catalog["reason"] == row["reason"]
            and catalog["pageCount"] == row["page_count"] == doc["pageCount"]
            and generation["generationId"] == row["generationId"]
            and generation["projectionVersion"] == row["projection_version"] == "native_raw_page_v1"
            and generation["processingRunId"] == row["processing_run_id"]
            and generation["manifestSha256"] == row["manifestHash"] == canonical["manifestSha256"],
            "worker_adapter_raw_catalog_inner_root_or_generation_changed")
    manifest = bundle["manifestEnvelope"]
    require(manifest["manifestSha256"] == canonical["manifestSha256"]
            and manifest["manifest"]["processingRunId"] == generation["processingRunId"],
            "worker_adapter_processing_run_binding_changed")
    generation_input = "\n".join(["native_raw_page_v1", identity["tenantId"], doc["docId"], doc["revisionId"],
        doc["sourceSha256"], generation["processingRunId"], generation["manifestSha256"], root["canonicalBundleSha256"]])
    require(generation["generationId"] == planning.sha(generation_input.encode()), "worker_adapter_raw_generation_digest_changed")
    if catalog["mode"] == "inline_fallback":
        require(catalog["reason"] in RAW_FALLBACKS and catalog["pages"] == [] and doc["nativeRawPages"] == [],
                "worker_adapter_raw_fallback_not_explicit_or_valid")
        return {"mode": "inline_fallback", "reason": catalog["reason"], "pagesVerified": 0,
                "canonicalFallbackStillAvailable": True}
    require(catalog["mode"] == "raw_pages" and catalog["reason"] is None
            and [descriptor["physicalPage"] for descriptor in catalog["pages"]] == list(range(1, doc["pageCount"] + 1)),
            "worker_adapter_raw_catalog_page_inventory_changed")
    rows = unique_rows(doc["nativeRawPages"], "page_number", "worker_adapter_duplicate_raw_page")
    require(set(rows) == set(range(1, doc["pageCount"] + 1)), "worker_adapter_raw_sql_page_inventory_changed")
    for descriptor, page in zip(catalog["pages"], canonical["pages"]):
        physical = descriptor["physicalPage"]
        row = rows[physical]
        snapshot = worker.blob({**row, "byteSize": row["byte_size"], "storedSize": row["stored_size"]})
        blocks = [expected_raw_block(block) for block in page["blocks"] if block["blockType"] in NATIVE_TYPES]
        blocks.sort(key=lambda block: (block["readingOrder"], block["ordinal"], block["blockId"]))
        availability, reason = ("observed", None) if blocks else ("unavailable", "no_captured_native_observation")
        count = len(blocks) or None
        require(row["doc_id"] == doc["docId"] and row["revision_id"] == doc["revisionId"]
                and row["tenant_id"] == identity["tenantId"]
                and row["generationId"] == generation["generationId"] and row["sourceHash"] == doc["sourceSha256"]
                and descriptor["sha256"] == row["contentHash"] and descriptor["utf8Bytes"] == row["byte_size"]
                and descriptor["compressedBytes"] == row["stored_size"]
                and descriptor["availability"] == row["availability"] == snapshot["availability"] == availability
                and descriptor["reason"] == row["reason"] == snapshot["reason"] == reason
                and descriptor["blockCount"] == row["block_count"] == count
                and snapshot["schemaVersion"] == "native_raw_page_snapshot_v1"
                and snapshot["root"] == root and snapshot["generation"] == generation
                and snapshot["physicalPage"] == physical and snapshot["pageCount"] == doc["pageCount"]
                and snapshot["geometry"] == page["geometry"] and snapshot["pageQualityFlags"] == page["qualityFlags"]
                and snapshot["blocks"] == (blocks or None), "worker_adapter_raw_snapshot_differs_from_canonical")
    return {"mode": "raw_pages", "reason": None, "pagesVerified": len(rows), "canonicalFallbackStillAvailable": True}


def build_corpus_index(observation, sources, source_sha):
    require(sources["schemaVersion"] == "ingestion_real_acceptance_v1"
            and sources["newWorkerObservationSeenDuringRegistration"] is False
            and sources["originalFilesModified"] is False,
            "worker_adapter_independent_acceptance_manifest_required")
    registered = datetime.datetime.fromisoformat(sources["createdAtUtc"].replace("Z", "+00:00"))
    observed = datetime.datetime.fromisoformat(observation["capturedAtUtc"].replace("Z", "+00:00"))
    require(registered.tzinfo is not None and observed.tzinfo is not None and registered <= observed,
            "worker_adapter_acceptance_facts_not_registered_before_observation")
    documents, _ = worker.evaluate(observation, sources, source_sha)
    metadata = {doc["docId"]: doc for doc in documents}
    chunks = unique_rows(observation["retrievalChunks"], "retrievalChunkId", "worker_adapter_duplicate_chunk")
    revisions = {doc["revisionId"] for doc in documents}
    index = {"documents": {}, "chunks": chunks, "anchors": {}, "cards": {}, "packageChunks": {}}
    raw_reviews = {}
    for name, key in PROJECTIONS.items():
        rows = unique_rows(observation[name], key, "worker_adapter_projection_identity_duplicate")
        require(all(row["revisionId"] in revisions for row in rows.values()), "worker_adapter_projection_unknown_revision")
        for identifier, row in rows.items():
            uuid.UUID(identifier)
            if name in ("units", "contentCards"):
                field = "text" if name == "units" else "searchText"
                require(planning.sha(row[field].encode("utf-8")) == row["checksumSha256"], "worker_adapter_projection_literal_checksum_changed")
    index["cards"] = {row["contentCardId"]: row for row in observation["contentCards"]}
    for doc in observation["documents"]:
        bundle = worker.blob(doc["canonicalArtifact"])
        canonical = bundle["document"]
        job = doc["ingestionJob"]
        started = datetime.datetime.fromisoformat(job["started_at"].replace("Z", "+00:00"))
        finished = datetime.datetime.fromisoformat(job["finished_at"].replace("Z", "+00:00"))
        require(started.tzinfo is not None and finished.tzinfo is not None and registered <= started <= finished <= observed,
                "worker_adapter_worker_execution_chronology_invalid")
        profiles = unique_rows(doc["profiles"], "document_profile_id", "worker_adapter_profile_duplicate")
        runs = unique_rows(doc["processingRuns"], "processing_run_id", "worker_adapter_processing_run_duplicate")
        processing_id = bundle["manifestEnvelope"]["manifest"]["processingRunId"]
        require(profiles and all(profile["doc_id"] == doc["docId"] and profile["revision_id"] == doc["revisionId"]
                                for profile in profiles.values())
                and processing_id in runs and runs[processing_id]["job_id"] == job["job_id"]
                and runs[processing_id]["doc_id"] == doc["docId"] and runs[processing_id]["revision_id"] == doc["revisionId"],
                "worker_adapter_real_profile_or_processing_run_missing")
        require(all(card["docId"] == doc["docId"] and card["documentProfileId"] in profiles
                    for card in index["cards"].values() if card["revisionId"] == doc["revisionId"]),
                "worker_adapter_card_profile_binding_changed")
        doc_index = {**metadata[doc["docId"]], "canonical": canonical}
        index["documents"][doc["docId"]] = doc_index
        raw_hashes = anchor_raw_hashes(doc["canonicalArtifact"])
        page_map = {page["pageNumber"]: page for page in canonical["pages"]}
        for row in doc["sourceAnchors"]:
            anchor = row["canonicalAnchor"]
            require(row["contentHash"] == raw_hashes[anchor["anchorId"]]
                    and row["manifestHash"] == anchor["manifestSha256"] == canonical["manifestSha256"],
                    "worker_adapter_anchor_content_or_manifest_hash_changed")
            require(anchor["regions"] and row["pageStart"] == min(region["pageNumber"] for region in anchor["regions"])
                    and row["pageEnd"] == max(region["pageNumber"] for region in anchor["regions"]),
                    "worker_adapter_anchor_page_bounds_changed")
            for region in anchor["regions"]:
                page = page_map.get(region["pageNumber"])
                require(page is not None and set(region["blockIds"]) <= {block["blockId"] for block in page["blocks"]}
                        and set(region.get("spanIds", [])) <= {span["spanId"] for block in page["blocks"] for span in block.get("spans", [])}
                        and set(region.get("tableCellIds", [])) <= {cell["cellId"] for table in page.get("tables", []) for cell in table["cells"]},
                        "worker_adapter_anchor_block_not_on_physical_page")
            if row["projectionType"] == "retrieval_chunk":
                require(row["anchorId"] not in index["anchors"], "worker_adapter_anchor_identity_duplicate")
                index["anchors"][row["anchorId"]] = row["projectionId"]
        raw_reviews[doc["docId"]] = verify_raw(doc, bundle)
    index["rawPublicationReview"] = raw_reviews
    return index


def compare_planning_snapshot(snapshot, observation):
    require(snapshot["schemaVersion"] == "saaia-planning-corpus-sql-observation.v1", "worker_adapter_planning_sql_schema_changed")
    for name, key in PROJECTIONS.items():
        current = unique_rows(snapshot[name], key, "worker_adapter_planning_projection_duplicate")
        original = unique_rows(observation[name], key, "worker_adapter_origin_projection_duplicate")
        require(current == original, "worker_adapter_planning_corpus_differs_from_real_worker")


def verify_registration_evidence(scan, documents):
    """Bind enqueue receipts without inventing a scanner invocation."""
    if scan.get("mode") != "sequential_explicit_enqueue":
        # Preserve the original scanner-count admission unchanged.
        require(scan["scanned"] == scan["enqueued"] == len(documents),
                "worker_adapter_scan_did_not_enqueue_originals")
        return {"mode": scan.get("mode", "scan_count_response"), "scannerInvoked": scan.get("scannerInvoked"),
                "scanned": scan["scanned"], "enqueued": scan["enqueued"], "scannerExecutionValidated": False}
    require(len(documents) == 4 and scan.get("scannerInvoked") is False
            and type(scan.get("scanned")) is int and scan["scanned"] == 0
            and type(scan.get("enqueued")) is int and scan["enqueued"] == 4
            and type(scan.get("hashVerifiedOriginalInputs")) is int and scan["hashVerifiedOriginalInputs"] == 4,
            "worker_adapter_explicit_enqueue_counts_or_mode_invalid")
    accepted, completed = scan.get("acceptedJobs"), scan.get("completedCases")
    require(isinstance(accepted, list) and len(accepted) == 4
            and all(isinstance(row, dict) and isinstance(row.get("jobId"), str)
                    and isinstance(row.get("caseId"), str) and row["caseId"] for row in accepted)
            and isinstance(completed, list) and len(completed) == 4
            and all(isinstance(case, str) for case in completed), "worker_adapter_explicit_enqueue_inventory_invalid")
    jobs = {doc["ingestionJob"]["job_id"]: doc for doc in documents}
    cases = {row["caseId"] for row in accepted}
    require(len(jobs) == len(cases) == len({row["jobId"] for row in accepted}) == 4
            and set(jobs) == {row["jobId"] for row in accepted}
            and set(completed) == cases, "worker_adapter_explicit_enqueue_inventory_invalid")
    for row in accepted:
        doc, response = jobs[row["jobId"]], row.get("response")
        job = doc["ingestionJob"]
        require(isinstance(response, dict) and response.get("jobId") == job["job_id"]
                and row.get("docPath") == response.get("docPath") == doc["docPath"] == job["doc_path"] == row["caseId"] + ".pdf"
                and row.get("action") == response.get("action") == job["action"] == "upsert"
                and response.get("tenantId") == job["tenant_id"]
                and response.get("docId") == doc["docId"] == job["payload"]["docId"]
                and type(response.get("version")) is int and response["version"] > 0
                and response["version"] == doc["indexedVersion"] == doc["ingestionVersion"] == job["payload"]["version"],
                "worker_adapter_explicit_enqueue_job_binding_changed")
    return {"mode": "sequential_explicit_enqueue", "scannerInvoked": False, "scanned": 0, "enqueued": 4,
            "acceptedJobsBound": 4, "scannerExecutionValidated": False}


def verify_execution_evidence(observation, transfer=None):
    required = {"backendLog", "emptyBaseline", "scanResponse", "terminalJobs", "doclingHealth", "embeddingInfo", "dependencyOwnership"}
    evidence = observation["workerExecutionEvidence"]
    require(required <= set(evidence), "worker_adapter_execution_evidence_missing")
    for value in evidence.values():
        source = capture_path(value["path"], transfer, value["sha256"])
        require(check_hash(value["sha256"]) and planning.sha(source.read_bytes()) == value["sha256"],
                "worker_adapter_execution_evidence_bytes_changed")
    def read_evidence(name):
        entry = evidence[name]
        return planning.read_json(capture_path(entry["path"], transfer, entry["sha256"]))
    baseline = read_evidence("emptyBaseline")
    require(baseline and all(type(value) is int and value == 0 for value in baseline.values()), "worker_adapter_origin_store_not_empty")
    scan = read_evidence("scanResponse")
    registration = verify_registration_evidence(scan, observation["documents"])
    terminal = read_evidence("terminalJobs")
    jobs = {job["jobId"]: job for job in linux.parse_job_listing(terminal)}
    published_jobs = {doc["ingestionJob"]["job_id"]: doc["ingestionJob"] for doc in observation["documents"]}
    require(set(jobs) == set(published_jobs)
            and all(job["status"] == "done" and job["attempts"] == published_jobs[key]["attempts"] >= 1
                    and job["action"] == published_jobs[key]["action"] == "upsert"
                    and job["docPath"] == published_jobs[key]["doc_path"] for key, job in jobs.items()),
            "worker_adapter_terminal_jobs_not_actual_completed_worker_jobs")
    health = read_evidence("doclingHealth")
    embeddings = read_evidence("embeddingInfo")
    require(health["status"] == "ok" and health["upstream"] is True, "worker_adapter_observed_dependency_identity_invalid")
    if "nativeInfo" in embeddings:
        # A real TEI /info has no Transformers offline/dimensions/revision
        # fields. Bind its native response and separate model attestation to
        # the independently hash-pinned profile; never manufacture /info data.
        pin = observation.get("embeddingProfileEvidence", {})
        require(check_hash(pin.get("sha256")) and pin["sha256"] == observation["profileSha256"],
                "worker_adapter_tei_independent_profile_missing")
        profile_file = capture_path(pin["path"], transfer, pin["sha256"])
        require(planning.sha(profile_file.read_bytes()) == pin["sha256"], "worker_adapter_embedding_profile_bytes_changed")
        profile = planning.read_json(profile_file)
        require(profile.get("adapter") == "tei_cpu", "worker_adapter_tei_profile_contract_invalid")
        attest_info(embeddings["nativeInfo"], profile, profile["Rag"]["EmbeddingsBaseUrl"].rstrip("/"))
        binding = embeddings["actualModelBinding"]
        rag = profile["Rag"]
        require(embeddings["passed"] is True and embeddings["modelArtifacts"] == profile["modelArtifacts"]
                and binding["modelRevision"] == rag["EmbeddingsModelRevision"]
                and binding["modelFileRelativePath"] == profile["modelFileRelativePath"]
                and binding["containerModelRoot"] == profile["containerModelRoot"],
                "worker_adapter_tei_attestation_binding_changed")
        locations = {key: pathlib.PurePosixPath(binding[key]) for key in
                     ("containerCacheRoot", "containerModelRoot", "hostModelRoot", "mountSource", "mountDestination")}
        require(all(path.is_absolute() and ".." not in path.parts for path in locations.values())
                and locations["containerModelRoot"] == locations["containerCacheRoot"] /
                    ("models--" + rag["EmbeddingsModel"].replace("/", "--")) / "snapshots" / binding["modelRevision"]
                and locations["containerModelRoot"].is_relative_to(locations["mountDestination"])
                and locations["hostModelRoot"] == locations["mountSource"] /
                    locations["containerModelRoot"].relative_to(locations["mountDestination"]),
                "worker_adapter_tei_attestation_mount_changed")
        embedding_model, embedding_revision, embedding_runtime = embeddings["nativeInfo"]["model_id"], binding["modelRevision"], "tei_cpu"
    else:
        require(embeddings["offline"] is True and embeddings["dimensions"] == 768
                and embeddings["model_id"] and embeddings["model_revision"] and check_hash(embeddings["model_file_sha256"]),
                "worker_adapter_observed_dependency_identity_invalid")
        embedding_model, embedding_revision, embedding_runtime = embeddings["model_id"], embeddings["model_revision"], "transformers_cpu"
    uuid.UUID(observation["ownerRunId"])
    require(all(check_hash(observation[key]) for key in ("sourceManifestSha256", "backendAssemblySha256",
        "profileSha256", "dependencyManifestSha256", "configurationSha256")), "worker_adapter_execution_pin_missing")
    return {"boundExecutionArtifacts": len(evidence), "observedLocalEmbeddingModel": embedding_model,
            "observedLocalEmbeddingRevision": embedding_revision, "observedEmbeddingRuntime": embedding_runtime,
            "embeddingProfileBound": embedding_runtime == "tei_cpu",
            "doclingUpstreamObserved": True, "linuxProductionQualified": False, "registrationEvidence": registration}


def load_worker_corpus(manifest, transfer=None):
    require(manifest["schemaVersion"] == "saaia-planning-complete-corpus.v1"
            and manifest["origin"] == "real_ingestion_worker" and manifest["completeDocuments"] is True
            and manifest["boundaries"]["nativeFixture"] is False and "packagePath" not in manifest,
            "worker_adapter_real_worker_manifest_required")
    observation_path = capture_path(manifest["observationPath"], transfer, manifest["observationSha256"])
    require(planning.sha(observation_path.read_bytes()) == manifest["observationSha256"], "worker_adapter_observation_changed")
    observation = planning.read_json(observation_path)
    source_path = capture_path(observation["sourceManifestPath"], transfer, observation["sourceManifestSha256"])
    source_sha = planning.sha(source_path.read_bytes())
    require(source_sha == manifest["sourceManifestSha256"] == observation["sourceManifestSha256"], "worker_adapter_independent_source_manifest_changed")
    sources = planning.read_json(source_path)
    for case in sources["cases"]:
        facts = case["expectations"]["criticalFacts"]
        require(facts, "worker_adapter_independent_visual_facts_missing")
        for fact in facts:
            render = planning.extended_windows_path(pathlib.Path(fact["render"]["path"]))
            require(1 <= fact["physicalPage"] <= case["expectations"]["pageCount"] and fact["inspection"]
                    and planning.sha(render.read_bytes()) == fact["render"]["sha256"],
                    "worker_adapter_independent_render_or_page_changed")
    corpus = build_corpus_index(observation, sources, source_sha)
    expected = {doc["docId"]: doc for doc in manifest["documents"]}
    require(len(expected) == len(manifest["documents"]) == manifest["documentCount"] == len(corpus["documents"])
            and sum(doc["pageCount"] for doc in corpus["documents"].values()) == manifest["physicalPageCount"]
            and set(expected) == set(corpus["documents"]), "worker_adapter_manifest_document_inventory_changed")
    for identifier, doc in corpus["documents"].items():
        require(all(expected[identifier].get(key) == value for key, value in doc.items() if key != "canonical"),
                "worker_adapter_manifest_published_identity_changed")
    execution = verify_execution_evidence(observation, transfer)
    return corpus, observation, execution


def verify(profile_path, runtime, capture_map_path=None):
    profile_path = planning.extended_windows_path(pathlib.Path(profile_path))
    runtime = planning.extended_windows_path(pathlib.Path(runtime))
    profile = planning.read_json(profile_path)
    require(profile["schemaVersion"] == "saaia-local-openai-planning-campaign.v1", "planning_profile_required")
    manifest_path = planning.extended_windows_path(pathlib.Path(profile["corpus"]["manifestPath"]))
    manifest_hash = planning.sha(manifest_path.read_bytes())
    require(manifest_hash == profile["corpus"]["manifestSha256"].lower(), "planning_corpus_manifest_changed")
    transfer = CaptureMap(capture_map_path) if capture_map_path is not None else None
    corpus, observation, execution = load_worker_corpus(planning.read_json(manifest_path), transfer)
    files = {name: runtime / "runtime" / filename for name, filename in {
        "submission": "planning-submission.private.json", "api": "planning-terminal.private.json",
        "sql": "planning-sql-job.private.json", "corpusSql": "planning-corpus-sql.private.json"}.items()}
    values = {name: planning.read_json(path) for name, path in files.items()}
    compare_planning_snapshot(values["corpusSql"], observation)
    run_path = runtime.parent / "planning-result.json"
    run = planning.read_json(run_path)
    require(run["profileSha256"].lower() == planning.sha(profile_path.read_bytes())
            and run["corpusManifestSha256"].lower() == manifest_hash and run["jobId"] == values["api"]["jobId"]
            and run["lifecycleComplete"] is True and run["externalProviderUsed"] is True and run["preflight"] is False
            and planning.sha(files["corpusSql"].read_bytes()) == run["corpusSqlReadback"]["sha256"].lower(),
            "worker_adapter_planning_execution_binding_changed")
    trace_paths = sorted((runtime / "runtime/private-traces").glob("*.json"))
    result = planning.evaluate(profile, corpus, values["submission"], values["api"], values["sql"],
                               [planning.read_json(path) for path in trace_paths], planning.read_call_probes(runtime / "runtime/paid-backend.stdout.log"))
    result.update(schemaVersion="saaia-real-worker-planning-mechanical-review.v1", corpusOrigin="real_ingestion_worker",
        canonicalRowsReprojectedByVerifier=False, chunkIdsRecomputedByVerifier=False,
        publishedChunksUsedExactlyAsObserved=True, workerExecutionEvidence=execution,
        rawPublicationReview=corpus["rawPublicationReview"],
        independentPhysicalPdfRecipeOwnershipReviewRequired=True, sourceToSqlSemanticEquivalenceCertified=False,
        inputPins={"profileSha256": planning.sha(profile_path.read_bytes()), "manifestSha256": manifest_hash,
                   "workerObservationSha256": planning.read_json(manifest_path)["observationSha256"],
                   "independentSourceManifestSha256": observation["sourceManifestSha256"],
                   **{name + "Sha256": planning.sha(path.read_bytes()) for name, path in files.items()},
                   "executionResultSha256": planning.sha(run_path.read_bytes()),
                   "paidProbeLogSha256": planning.sha((runtime / "runtime/paid-backend.stdout.log").read_bytes()),
                   "traceFiles": [{"name": path.name, "sha256": planning.sha(path.read_bytes())} for path in trace_paths]})
    result["explicitCaptureTransfer"] = {"used": transfer is not None, "originalLinuxJsonRewritten": False,
        "mapSha256": transfer.sha256 if transfer is not None else None}
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", type=pathlib.Path, required=True)
    parser.add_argument("--runtime", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--capture-map", type=pathlib.Path)
    args = parser.parse_args()
    result = verify(args.profile, args.runtime, args.capture_map)
    with planning.extended_windows_path(args.output).open("x", encoding="utf-8") as handle:
        json.dump(result, handle, indent=2)
    print(json.dumps({"passed": True, "cells": result["gridCellCount"], "semanticPlanningApproved": False}))
