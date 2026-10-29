"""Synthetic hostile captures for the worker adapter; no worker/network calls."""
import base64
import copy
import gzip
import json
import pathlib
import re
import tempfile
import unittest
import uuid
from unittest.mock import Mock

import planning_worker_observation as adapter
import test_planning_result
import test_worker_corpus


def encoded(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def packed(value):
    data = encoded(value)
    compressed = gzip.compress(data)
    return {"compression": "gzip", "byteSize": len(data), "storedSize": len(compressed),
            "contentHash": adapter.planning.sha(data), "payloadBase64": base64.b64encode(compressed).decode()}


def uncompressed(value):
    data = encoded(value)
    return {"contentHash": adapter.planning.sha(data), "payloadBase64": base64.b64encode(data).decode()}


def fixture(root, mode="raw_pages"):
    observation, sources = test_worker_corpus.fixture(root)
    sources.update(schemaVersion="ingestion_real_acceptance_v1", newWorkerObservationSeenDuringRegistration=False,
                   originalFilesModified=False, createdAtUtc="2026-10-26T20:00:00Z")
    observation["capturedAtUtc"] = "2026-10-27T01:00:00Z"
    observation["ownerRunId"] = str(uuid.uuid4())
    tenant = str(uuid.uuid4())
    for key in ("backendAssemblySha256", "profileSha256", "dependencyManifestSha256", "configurationSha256"):
        observation[key] = "b" * 64
    for number, doc in enumerate(observation["documents"]):
        doc["ingestionVersion"] = doc["indexedVersion"] = 7
        doc["ingestionJob"]["payload"]["version"] = 7
        doc["ingestionJob"]["tenant_id"] = tenant
        run = str(uuid.uuid4())
        profile = str(uuid.uuid4())
        doc["profiles"] = [{"document_profile_id": profile, "doc_id": doc["docId"], "revision_id": doc["revisionId"]}]
        doc["processingRuns"] = [{"processing_run_id": run, "job_id": doc["ingestionJob"]["job_id"],
                                   "doc_id": doc["docId"], "revision_id": doc["revisionId"]}]
        bundle = adapter.worker.blob(doc["canonicalArtifact"])
        canonical = bundle["document"]
        manifest_sha = "c" * 64
        canonical["manifestSha256"] = manifest_sha
        geometry = {"width": 600, "height": 800, "unit": "pt", "rotationDegrees": 0,
                    "coordinateSpace": "normalized_top_left_v1"}
        text = f"Exact native line {number} 😀\r\nAnother own literal.\r\n"
        raw_sha = adapter.planning.sha(text.encode())
        native = {"blockId": "native-" + str(number), "blockType": "native_text_recovery", "ordinal": 1, "readingOrder": 1,
                  "text": {"raw": text, "rawSha256": raw_sha}, "qualityFlags": ["unverified_native"], "polygon": None,
                  "provenance": {"engine": "synthetic", "engineVersion": "not-real", "method": "unit_fixture", "stageId": "fixture"}}
        canonical["pages"] = [{"pageNumber": 1, "blocks": [native], "geometry": geometry, "qualityFlags": ["page_unverified"]}]
        bundle["manifestEnvelope"] = {"manifestSha256": manifest_sha, "manifest": {"processingRunId": run}}
        anchor = bundle["sourceAnchors"][0]
        anchor.update(manifestSha256=manifest_sha, regions=[{"pageNumber": 1, "blockIds": [native["blockId"]],
                                                          "spanIds": [], "tableCellIds": []}])
        doc["canonicalArtifact"] = {"artifactType": "canonical_ingestion_bundle", **packed(bundle)}
        sql_anchor = doc["sourceAnchors"][0]
        sql_anchor.update(canonicalAnchor=copy.deepcopy(anchor), contentHash=adapter.planning.sha(encoded(anchor)), manifestHash=manifest_sha)
        root_binding = {"identity": {"tenantId": tenant, "documentId": doc["docId"], "revisionId": doc["revisionId"],
                                    "sourceSha256": doc["sourceSha256"]}, "canonicalBundleSha256": doc["canonicalArtifact"]["contentHash"]}
        generation_input = "\n".join(["native_raw_page_v1", tenant, doc["docId"], doc["revisionId"], doc["sourceSha256"],
                                      run, manifest_sha, root_binding["canonicalBundleSha256"]])
        generation = {"generationId": adapter.planning.sha(generation_input.encode()), "projectionVersion": "native_raw_page_v1",
                      "processingRunId": run, "manifestSha256": manifest_sha}
        snapshot = {"schemaVersion": "native_raw_page_snapshot_v1", "root": root_binding, "generation": generation,
                    "physicalPage": 1, "pageCount": 1, "geometry": geometry, "pageQualityFlags": ["page_unverified"],
                    "availability": "observed", "reason": None, "blocks": [adapter.expected_raw_block(native)]}
        page_artifact = packed(snapshot)
        descriptor = {"physicalPage": 1, "availability": "observed", "reason": None, "blockCount": 1,
                      "sha256": page_artifact["contentHash"], "utf8Bytes": page_artifact["byteSize"],
                      "compressedBytes": page_artifact["storedSize"]}
        reason = None if mode == "raw_pages" else "native_raw_page_time_limit"
        catalog = {"schemaVersion": "native_raw_page_catalog_v1", "root": root_binding, "generation": generation,
                   "mode": mode, "reason": reason, "pageCount": 1, "pages": [descriptor] if mode == "raw_pages" else []}
        doc["nativeRawCatalog"] = {"tenant_id": tenant, "doc_id": doc["docId"], "revision_id": doc["revisionId"],
            "canonicalBundleHash": doc["canonicalArtifact"]["contentHash"], "sourceHash": doc["sourceSha256"],
            "generationId": generation["generationId"], "manifestHash": manifest_sha, "processing_run_id": run,
            "projection_version": "native_raw_page_v1", "mode": mode, "reason": reason, "page_count": 1, **uncompressed(catalog)}
        doc["nativeRawPages"] = [{"tenant_id": tenant, "doc_id": doc["docId"], "revision_id": doc["revisionId"],
            "sourceHash": doc["sourceSha256"], "generationId": generation["generationId"], "page_number": 1,
            "availability": "observed", "reason": None, "block_count": 1, **page_artifact,
            "byte_size": page_artifact["byteSize"], "stored_size": page_artifact["storedSize"]}] if mode == "raw_pages" else []
    for unit in observation["units"]:
        unit["unitId"] = str(uuid.uuid4())
    for chunk in observation["retrievalChunks"]:
        chunk["metadata"] = {"contentRole": "content", "extractionQualitySignals": [],
                             "extractionTextStatus": "native", "extractionOcrCandidate": False}
    return observation, sources


def rebind_page(doc, mutate):
    row = doc["nativeRawPages"][0]
    snapshot = adapter.worker.blob(row)
    mutate(snapshot)
    row.update(packed(snapshot))
    row.update(byte_size=row["byteSize"], stored_size=row["storedSize"])
    catalog = adapter.worker.blob(doc["nativeRawCatalog"], compressed=False)
    descriptor = catalog["pages"][0]
    descriptor.update(sha256=row["contentHash"], utf8Bytes=row["byteSize"], compressedBytes=row["storedSize"])
    doc["nativeRawCatalog"].update(uncompressed(catalog))


def native_type_page(doc, block_type):
    """Rebind all affected hashes exactly; a stale checksum must not be the refusal."""
    bundle = adapter.worker.blob(doc["canonicalArtifact"])
    block = bundle["document"]["pages"][0]["blocks"][0]
    block["blockType"] = block_type
    block["polygon"] = {"points": [{"x": 0.1, "y": 0.2}, {"x": 0.3, "y": 0.4}]}
    doc["canonicalArtifact"].update(packed(bundle))
    catalog = adapter.worker.blob(doc["nativeRawCatalog"], compressed=False)
    root, generation = catalog["root"], catalog["generation"]
    root["canonicalBundleSha256"] = doc["canonicalArtifact"]["contentHash"]
    identity = root["identity"]
    generation["generationId"] = adapter.planning.sha("\n".join([
        "native_raw_page_v1", identity["tenantId"], doc["docId"], doc["revisionId"], doc["sourceSha256"],
        generation["processingRunId"], generation["manifestSha256"], root["canonicalBundleSha256"]]).encode())
    row = doc["nativeRawPages"][0]
    snapshot = adapter.worker.blob(row)
    snapshot.update(root=root, generation=generation, blocks=[adapter.expected_raw_block(block)])
    row.update(packed(snapshot), generationId=generation["generationId"])
    row.update(byte_size=row["byteSize"], stored_size=row["storedSize"])
    catalog["pages"][0].update(sha256=row["contentHash"], utf8Bytes=row["byteSize"], compressedBytes=row["storedSize"])
    doc["nativeRawCatalog"].update(uncompressed(catalog), canonicalBundleHash=root["canonicalBundleSha256"],
                                  generationId=generation["generationId"])
    return bundle


class PlanningWorkerObservationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = pathlib.Path(self.temporary.name)
        self.observation, self.sources = fixture(self.root)

    def build(self):
        return adapter.build_corpus_index(self.observation, self.sources, "a" * 64)

    def reject(self, code):
        with self.assertRaisesRegex(ValueError, "^" + code + "$"):
            self.build()

    def test_native_types_match_the_producer_selector_constants_exactly(self):
        directory = pathlib.Path(__file__).resolve().parents[2] / "backend/SAAIA.Backend/DocumentIntelligence"
        names = ("CanonicalNativeTextCoverageReconciler", "CanonicalNativeLayoutReconciler", "CanonicalNativeTableCellReconciler")
        constants = set()
        for name in names:
            text = (directory / (name + ".cs")).read_text(encoding="utf-8-sig")
            value, = re.findall(r'const string (?:RecoveryBlockType|ObservationBlockType)\s*=\s*"([^"]+)";', text)
            constants.add(value)
        selector = (directory / "CanonicalObservationSelector.cs").read_text(encoding="utf-8-sig")
        references = re.findall(r'(CanonicalNative\w+Reconciler)\.(?:RecoveryBlockType|ObservationBlockType)',
                                selector.split("internal static bool IsNativeObservation(string blockType)")[1].split(";")[0])
        self.assertEqual(set(names), set(references))
        self.assertEqual(constants, adapter.NATIVE_TYPES)

    def test_native_table_cell_observation_is_retained_with_its_polygon(self):
        doc = self.observation["documents"][0]
        bundle = native_type_page(doc, "native_table_cell_observation")
        self.assertEqual(1, adapter.verify_raw(doc, bundle)["pagesVerified"])

    def test_invented_native_table_recovery_type_is_rejected_after_rehashing(self):
        doc = self.observation["documents"][0]
        bundle = native_type_page(doc, "native_table_recovery")
        with self.assertRaisesRegex(ValueError, "^worker_adapter_raw_snapshot_differs_from_canonical$"):
            adapter.verify_raw(doc, bundle)

    def test_table_cell_raw_keeps_strict_text_polygon_type_and_inventory_guards(self):
        mutations = [lambda page: page["blocks"][0].update(raw="Altered cell"),
                     lambda page: page["blocks"][0].update(rawSha256="f" * 64),
                     lambda page: page["blocks"][0].update(observedPolygon=None),
                     lambda page: page["blocks"][0]["observedPolygon"]["points"][0].update(x=0.9),
                     lambda page: page["blocks"][0].update(blockType="unknown_native_kind"),
                     lambda page: page["blocks"].append(copy.deepcopy(page["blocks"][0])),
                     lambda page: page["blocks"].clear()]
        for number, mutation in enumerate(mutations):
            with self.subTest(number=number):
                observation, _ = fixture(self.root)
                doc = observation["documents"][0]
                bundle = native_type_page(doc, "native_table_cell_observation")
                rebind_page(doc, mutation)
                with self.assertRaisesRegex(ValueError, "^worker_adapter_raw_snapshot_differs_from_canonical$"):
                    adapter.verify_raw(doc, bundle)

    def test_actual_sql_guids_at_version_seven_are_preserved_without_v1_reprojection(self):
        result = self.build()
        self.assertEqual({row["retrievalChunkId"] for row in self.observation["retrievalChunks"]}, set(result["chunks"]))
        self.assertTrue(all(doc["indexedVersion"] == 7 for doc in result["documents"].values()))
        self.assertEqual(4, sum(item["pagesVerified"] for item in result["rawPublicationReview"].values()))

    def test_inline_fallback_requires_real_codec_reason_and_is_reported_explicitly(self):
        observation, sources = fixture(self.root, mode="inline_fallback")
        index = adapter.build_corpus_index(observation, sources, "a" * 64)
        self.assertEqual({"inline_fallback"}, {entry["mode"] for entry in index["rawPublicationReview"].values()})
        doc = observation["documents"][0]
        catalog = adapter.worker.blob(doc["nativeRawCatalog"], compressed=False)
        catalog["reason"] = "pretend_clean"
        doc["nativeRawCatalog"].update(reason="pretend_clean", **uncompressed(catalog))
        with self.assertRaisesRegex(ValueError, "worker_adapter_raw_fallback_not_explicit_or_valid"):
            adapter.build_corpus_index(observation, sources, "a" * 64)

    def test_raw_inner_revision_cannot_be_changed_even_with_rebound_outer_hashes(self):
        doc = self.observation["documents"][0]
        rebind_page(doc, lambda page: page["root"]["identity"].update(revisionId=str(uuid.uuid4())))
        self.reject("worker_adapter_raw_snapshot_differs_from_canonical")

    def test_raw_words_cannot_be_changed_even_with_rebound_page_and_catalog_hashes(self):
        doc = self.observation["documents"][0]
        rebind_page(doc, lambda page: page["blocks"][0].update(raw="Changed own content"))
        self.reject("worker_adapter_raw_snapshot_differs_from_canonical")

    def test_raw_geometry_quality_and_engine_changes_are_rejected_independently(self):
        mutations = [lambda page: page["geometry"].update(width=1200),
                     lambda page: page.update(pageQualityFlags=[]),
                     lambda page: page["blocks"][0].update(engineVersion="invented")]
        for mutation in mutations:
            with self.subTest(mutation=mutation):
                self.observation, self.sources = fixture(self.root)
                rebind_page(self.observation["documents"][0], mutation)
                self.reject("worker_adapter_raw_snapshot_differs_from_canonical")

    def test_catalog_inner_generation_cannot_be_replaced_with_valid_new_hash(self):
        doc = self.observation["documents"][0]
        catalog = adapter.worker.blob(doc["nativeRawCatalog"], compressed=False)
        catalog["generation"]["generationId"] = "f" * 64
        doc["nativeRawCatalog"].update(uncompressed(catalog))
        self.reject("worker_adapter_raw_catalog_inner_root_or_generation_changed")

    def test_anchor_content_hash_and_physical_page_binding_are_not_just_declared(self):
        self.observation["documents"][0]["sourceAnchors"][0]["contentHash"] = "0" * 64
        self.reject("worker_adapter_anchor_content_or_manifest_hash_changed")

    def test_missing_actual_processing_run_or_profile_is_rejected(self):
        self.observation["documents"][0]["profiles"] = []
        self.reject("worker_adapter_real_profile_or_processing_run_missing")

    def test_fact_registration_after_worker_or_unexecuted_job_does_not_qualify(self):
        self.sources["createdAtUtc"] = "2026-10-27T00:00:30Z"
        self.reject("worker_adapter_worker_execution_chronology_invalid")
        self.sources["createdAtUtc"] = "2026-10-27T02:00:00Z"
        self.reject("worker_adapter_acceptance_facts_not_registered_before_observation")

    def test_preflight_clone_must_equal_real_worker_rows_metadata_included(self):
        snapshot = {"schemaVersion": "saaia-planning-corpus-sql-observation.v1",
                    **{name: copy.deepcopy(self.observation[name]) for name in adapter.PROJECTIONS}}
        adapter.compare_planning_snapshot(snapshot, self.observation)
        snapshot["retrievalChunks"][0]["metadata"] = {"sectionTitle": "Invented ownership"}
        with self.assertRaisesRegex(ValueError, "worker_adapter_planning_corpus_differs_from_real_worker"):
            adapter.compare_planning_snapshot(snapshot, self.observation)

    def test_duplicate_unit_or_card_identity_is_rejected(self):
        self.observation["units"].append(copy.deepcopy(self.observation["units"][0]))
        self.reject("worker_adapter_projection_identity_duplicate")

    def test_original_fixture_mode_cannot_masquerade_as_worker_manifest(self):
        with self.assertRaisesRegex(ValueError, "worker_adapter_real_worker_manifest_required"):
            adapter.load_worker_corpus({"schemaVersion": "saaia-planning-complete-corpus.v1", "origin": "native_fixture"})

    def prepare_worker_files(self):
        def write(name, value, raw=False):
            path = self.root / name
            path.write_bytes(value if raw else encoded(value))
            return {"path": str(path), "sha256": adapter.planning.sha(path.read_bytes())}
        render = write("synthetic-render.png", b"synthetic-only-no-actual-pdf-rendering", raw=True)
        for case in self.sources["cases"]:
            case["expectations"]["criticalFacts"] = [{"physicalPage": 1, "render": render, "inspection": "Synthetic oracle test only."}]
        sources_pin = write("sources.json", self.sources)
        self.observation.update(sourceManifestPath=sources_pin["path"], sourceManifestSha256=sources_pin["sha256"])
        terminal = {"items": [{"jobId": doc["ingestionJob"]["job_id"], "status": "done", "attempts": 1,
                                "action": "upsert", "docPath": doc["docPath"]}
                               for doc in self.observation["documents"]]}
        evidence = {"backendLog": write("backend.log", b"synthetic log bytes", raw=True),
            "emptyBaseline": write("baseline.json", {"documents": 0, "chunks": 0, "jobs": 0}),
            "scanResponse": write("scan.json", {"scanned": 4, "enqueued": 4}),
            "terminalJobs": write("terminal.json", terminal),
            "doclingHealth": write("health.json", {"status": "ok", "upstream": True}),
            "embeddingInfo": write("embedding.json", {"offline": True, "dimensions": 768,
                "model_id": "synthetic-not-real", "model_revision": "synthetic", "model_file_sha256": "d" * 64}),
            "dependencyOwnership": write("dependencies.json", {"syntheticOnly": True})}
        self.observation["workerExecutionEvidence"] = evidence
        observation_pin = write("observation.json", self.observation)
        documents, _ = adapter.worker.evaluate(self.observation, self.sources, sources_pin["sha256"])
        manifest = {"schemaVersion": "saaia-planning-complete-corpus.v1", "origin": "real_ingestion_worker",
                    "completeDocuments": True, "boundaries": {"nativeFixture": False}, "documents": documents,
                    "documentCount": 4, "physicalPageCount": 4, "observationPath": observation_pin["path"],
                    "observationSha256": observation_pin["sha256"], "sourceManifestSha256": sources_pin["sha256"]}
        return manifest

    def test_complete_file_binding_preserves_independent_render_and_actual_execution_evidence(self):
        manifest = self.prepare_worker_files()
        index, _, execution = adapter.load_worker_corpus(manifest)
        self.assertEqual(4, len(index["documents"]))
        self.assertEqual(7, execution["boundExecutionArtifacts"])
        self.assertFalse(execution["linuxProductionQualified"])
        (self.root / "backend.log").write_bytes(b"altered closed worker log")
        with self.assertRaisesRegex(ValueError, "worker_adapter_execution_evidence_bytes_changed"):
            adapter.load_worker_corpus(manifest)

    def test_paid_result_adapter_uses_exact_new_rows_and_keeps_semantic_acceptance_false(self):
        manifest = self.prepare_worker_files()
        value = test_planning_result.fixture()
        for number, (doc, chunk, reference, item, claim) in enumerate(zip(self.observation["documents"],
                self.observation["retrievalChunks"], value["api"]["result"]["evidence"],
                value["payload"]["evidence"], value["api"]["result"]["claims"])):
            title = f"exact text {number}"
            reference.update(docId=doc["docId"], revisionId=doc["revisionId"], docPath=doc["docPath"],
                             sourceHash=doc["sourceSha256"], pageStart=1, pageEnd=1, chunkId=chunk["retrievalChunkId"])
            item.update(candidateTitle=title, content=chunk["text"], contentOffset=0,
                        originalContentLength=adapter.planning.utf16_length(chunk["text"]), physicalPageStart=1, physicalPageEnd=1,
                        sourceQuality={"textStatus": "native", "ocrCandidate": False, "signals": [], "semanticAccuracy": "not_verified"})
            claim.update(selectedItem=title, text=title)
            value["api"]["result"]["answerText"] = value["api"]["result"]["answerText"].replace(f"Recette {number + 1}", title)
        value["sql"]["canonicalHashes"] = [{"revisionId": doc["revisionId"], "sha256": doc["canonicalArtifact"]["contentHash"]}
                                           for doc in self.observation["documents"]]
        runtime = self.root / "attempt/owned-environment"
        (runtime / "runtime/private-traces").mkdir(parents=True)
        def write(path, data):
            path.write_bytes(encoded(data))
            return adapter.planning.sha(path.read_bytes())
        manifest_path = self.root / "manifest.json"
        manifest_sha = write(manifest_path, manifest)
        value["profile"].update(schemaVersion="saaia-local-openai-planning-campaign.v1",
                               corpus={"manifestPath": str(manifest_path), "manifestSha256": manifest_sha})
        test_planning_result.rebind(value, sync_reader=True)
        profile_path = self.root / "profile.json"
        profile_sha = write(profile_path, value["profile"])
        for name, filename in (("submission", "planning-submission.private.json"), ("api", "planning-terminal.private.json"),
                               ("sql", "planning-sql-job.private.json")):
            write(runtime / "runtime" / filename, value[name])
        snapshot = {"schemaVersion": "saaia-planning-corpus-sql-observation.v1",
                    **{name: self.observation[name] for name in adapter.PROJECTIONS}}
        snapshot_path = runtime / "runtime/planning-corpus-sql.private.json"
        snapshot_sha = write(snapshot_path, snapshot)
        write(runtime / "runtime/private-traces/final.json", value["traces"][0])
        (runtime / "runtime/paid-backend.stdout.log").write_bytes(b"operation=llm.call measurements=" + encoded(value["probes"][0]) + b"\n")
        write(runtime.parent / "planning-result.json", {"profileSha256": profile_sha, "corpusManifestSha256": manifest_sha,
            "jobId": "job-1", "lifecycleComplete": True, "externalProviderUsed": True, "preflight": False,
            "corpusSqlReadback": {"path": str(snapshot_path), "sha256": snapshot_sha}})
        result = adapter.verify(profile_path, runtime)
        self.assertTrue(result["passed"])
        self.assertFalse(result["chunkIdsRecomputedByVerifier"])
        self.assertFalse(result["semanticPlanningApproved"])
        self.assertFalse(result["sourceToSqlSemanticEquivalenceCertified"])

    def test_terminal_job_or_nonempty_origin_store_is_rejected_after_hashes_are_rebound(self):
        def pin(name, value):
            path = self.root / name
            path.write_bytes(encoded(value))
            return {"path": str(path), "sha256": adapter.planning.sha(path.read_bytes())}
        evidence = {key: pin(key + ".json", {}) for key in ("backendLog", "emptyBaseline", "scanResponse", "terminalJobs",
                                                           "doclingHealth", "embeddingInfo", "dependencyOwnership")}
        evidence["emptyBaseline"] = pin("emptyBaseline.json", {"documents": 1})
        self.observation["workerExecutionEvidence"] = evidence
        with self.assertRaisesRegex(ValueError, "worker_adapter_origin_store_not_empty"):
            adapter.verify_execution_evidence(self.observation)
        evidence["emptyBaseline"] = pin("emptyBaseline.json", {"documents": 0})
        evidence["scanResponse"] = pin("scanResponse.json", {"scanned": 4, "enqueued": 4})
        evidence["terminalJobs"] = pin("terminalJobs.json", {"items": [{"jobId": str(uuid.uuid4()), "status": "done", "attempts": 1,
                                                                      "action": "upsert", "docPath": "wrong.pdf"}]})
        with self.assertRaisesRegex(ValueError, "worker_adapter_terminal_jobs_not_actual_completed_worker_jobs"):
            adapter.verify_execution_evidence(self.observation)

    def prepare_tei_profile(self):
        revision = "a" * 40
        cache = "/data"
        model_root = cache + "/models--intfloat--multilingual-e5-base/snapshots/" + revision
        profile = {"schemaVersion": "saaia-tei-embedding-profile.v1", "adapter": "tei_cpu", "dimensions": 768,
            "imageId": "sha256:" + "b" * 64, "modelFileSha256": "c" * 64, "modelFileRelativePath": "model.safetensors",
            "truncationPolicy": "reject", "containerModelRoot": model_root,
            "modelRevisionAttestation": "owned_command_and_weights", "modelArtifacts": {"model.safetensors": "c" * 64},
            "expectedInfo": {"model_id": "intfloat/multilingual-e5-base", "version": "synthetic-TEI", "model_sha": None, "max_input_length": 512},
            "Rag": {"EmbeddingsBaseUrl": "http://127.0.0.1:22222", "EmbeddingsModel": "intfloat/multilingual-e5-base",
                    "EmbeddingsModelRevision": revision, "EmbeddingsRuntimeRevision": "sha256:" + "b" * 64}}
        attestation = {"nativeInfo": copy.deepcopy(profile["expectedInfo"]), "actualModelBinding": {
            "hostModelRoot": "/owned/model" + model_root, "containerModelRoot": model_root, "containerCacheRoot": cache,
            "modelRevision": revision, "modelFileRelativePath": "model.safetensors", "mountSource": "/owned/model/data", "mountDestination": "/data"},
            "modelArtifacts": copy.deepcopy(profile["modelArtifacts"]), "passed": True}
        profile_path = self.root / "embedding-profile.json"
        profile_path.write_bytes(encoded(profile))
        self.observation["profileSha256"] = adapter.planning.sha(profile_path.read_bytes())
        self.observation["embeddingProfileEvidence"] = {"path": str(profile_path), "sha256": self.observation["profileSha256"]}
        self.write_evidence("embeddingInfo", attestation)
        return attestation

    def write_evidence(self, name, value):
        pin = self.observation["workerExecutionEvidence"][name]
        path = pathlib.Path(pin["path"])
        path.write_bytes(encoded(value))
        pin["sha256"] = adapter.planning.sha(path.read_bytes())

    def test_native_tei_info_and_separate_model_binding_are_bound_to_frozen_profile(self):
        self.prepare_worker_files()
        attestation = self.prepare_tei_profile()
        self.assertNotIn("offline", attestation["nativeInfo"])
        self.assertNotIn("model_revision", attestation["nativeInfo"])
        result = adapter.verify_execution_evidence(self.observation)
        self.assertEqual("tei_cpu", result["observedEmbeddingRuntime"])
        self.assertTrue(result["embeddingProfileBound"])
        for mutate, reason in ((lambda a: a["nativeInfo"].update(version="changed"), "Observed native TEI info differs"),
                               (lambda a: a["actualModelBinding"].update(modelRevision="d" * 40), "worker_adapter_tei_attestation_binding_changed"),
                               (lambda a: a["modelArtifacts"].update({"model.safetensors": "d" * 64}), "worker_adapter_tei_attestation_binding_changed"),
                               (lambda a: a["actualModelBinding"].update(hostModelRoot="/elsewhere"), "worker_adapter_tei_attestation_mount_changed")):
            with self.subTest(reason=reason):
                changed = copy.deepcopy(attestation)
                mutate(changed)
                self.write_evidence("embeddingInfo", changed)
                with self.assertRaisesRegex(ValueError, reason):
                    adapter.verify_execution_evidence(self.observation)

    def test_tei_declared_success_without_independent_profile_or_with_changed_profile_is_rejected(self):
        self.prepare_worker_files()
        self.prepare_tei_profile()
        pin = self.observation.pop("embeddingProfileEvidence")
        with self.assertRaisesRegex(ValueError, "worker_adapter_tei_independent_profile_missing"):
            adapter.verify_execution_evidence(self.observation)
        self.observation["embeddingProfileEvidence"] = pin
        pathlib.Path(pin["path"]).write_bytes(b"changed profile")
        with self.assertRaisesRegex(ValueError, "worker_adapter_embedding_profile_bytes_changed"):
            adapter.verify_execution_evidence(self.observation)

    def test_raw_api_pascal_case_is_accepted_but_ambiguous_aliases_and_wrong_document_are_rejected(self):
        self.prepare_worker_files()
        rows = [{"JobId": doc["ingestionJob"]["job_id"], "Status": "done", "Attempts": 1, "Action": "upsert", "DocPath": doc["docPath"]}
                for doc in self.observation["documents"]]
        self.write_evidence("terminalJobs", {"Items": rows})
        adapter.verify_execution_evidence(self.observation)
        for changed in ({"Items": rows, "items": rows}, {"Items": [dict(rows[0], jobId=rows[0]["JobId"]), *rows[1:]]}):
            self.write_evidence("terminalJobs", changed)
            with self.assertRaisesRegex(ValueError, "missing or ambiguous required field"):
                adapter.verify_execution_evidence(self.observation)
        rows[0]["DocPath"] = "another-owned-original.pdf"
        self.write_evidence("terminalJobs", {"Items": rows})
        with self.assertRaisesRegex(ValueError, "worker_adapter_terminal_jobs_not_actual_completed_worker_jobs"):
            adapter.verify_execution_evidence(self.observation)

    def prepare_capture_map(self, manifest):
        """Create synthetic Linux originals directly, never rewrite captures."""
        remote = "/home/maxime/saaia-ingestion-qualification/" + str(uuid.uuid4())
        captured = self.root / "captured"
        captured.mkdir()
        entries = []
        def copied(relative, raw):
            local = captured / relative
            local.parent.mkdir(parents=True, exist_ok=True)
            local.write_bytes(raw)
            digest = adapter.planning.sha(raw)
            entries.append({"relativePath": relative, "bytes": len(raw), "sha256": digest})
            return {"path": remote + "/" + relative, "sha256": digest}
        observation = copy.deepcopy(self.observation)
        for key, pin in observation["workerExecutionEvidence"].items():
            observation["workerExecutionEvidence"][key] = copied("evidence/" + pathlib.Path(pin["path"]).name,
                                                                pathlib.Path(pin["path"]).read_bytes())
        original_sources = pathlib.Path(observation["sourceManifestPath"]).read_bytes()
        source_pin = copied("source-manifest.json", original_sources)
        observation.update(sourceManifestPath=source_pin["path"], sourceManifestSha256=source_pin["sha256"])
        if "embeddingProfileEvidence" in observation:
            pin = observation["embeddingProfileEvidence"]
            observation["embeddingProfileEvidence"] = copied("embedding-profile.json", pathlib.Path(pin["path"]).read_bytes())
        observation_pin = copied("evidence/worker-corpus.json", encoded(observation))
        mapped_manifest = dict(manifest, observationPath=observation_pin["path"], observationSha256=observation_pin["sha256"])
        value = {"schemaVersion": "saaia-worker-capture-transfer.v1", "remoteOwnerRoot": remote,
                 "localCapturedRoot": str(captured), "files": entries}
        map_path = self.root / "transfer.json"
        map_path.write_bytes(encoded(value))
        return mapped_manifest, map_path, value

    def test_explicit_linux_transfer_preserves_original_json_and_local_source_pdf_render_pins(self):
        manifest = self.prepare_worker_files()
        self.prepare_tei_profile()
        manifest, path, value = self.prepare_capture_map(manifest)
        before = {entry["relativePath"]: (self.root / "captured" / entry["relativePath"]).read_bytes() for entry in value["files"]}
        index, observation, result = adapter.load_worker_corpus(manifest, adapter.CaptureMap(path))
        self.assertEqual(4, len(index["documents"]))
        self.assertEqual("tei_cpu", result["observedEmbeddingRuntime"])
        self.assertTrue(observation["sourceManifestPath"].startswith(value["remoteOwnerRoot"] + "/"))
        self.assertTrue(all(doc["physicalPdfPath"] == original["physicalPdfPath"]
                            for doc, original in zip(observation["documents"], self.observation["documents"])))
        self.assertEqual(before, {entry["relativePath"]: (self.root / "captured" / entry["relativePath"]).read_bytes() for entry in value["files"]})

    def test_transfer_refuses_traversal_prefix_lookalikes_local_control_paths_and_missing_files(self):
        manifest, path, value = self.prepare_capture_map(self.prepare_worker_files())
        transfer = adapter.CaptureMap(path)
        for remote, reason in ((value["remoteOwnerRoot"] + "/../outside.json", "worker_capture_remote_path_invalid"),
                               (value["remoteOwnerRoot"] + "-other/source-manifest.json", "worker_capture_remote_path_outside_owner"),
                               (str(self.root / "sources.json"), "worker_capture_control_path_must_be_remote"),
                               (value["remoteOwnerRoot"] + "/absent.json", "worker_capture_required_file_not_transferred")):
            with self.subTest(remote=remote), self.assertRaisesRegex(ValueError, reason):
                transfer.resolve(remote)
        value["files"][0]["relativePath"] = "../outside.json"
        path.write_bytes(encoded(value))
        with self.assertRaisesRegex(ValueError, "worker_capture_relative_path_invalid"):
            adapter.CaptureMap(path)

    def test_transfer_same_length_byte_change_and_hash_rebinding_cannot_replace_original_pin(self):
        manifest, path, value = self.prepare_capture_map(self.prepare_worker_files())
        transfer = adapter.CaptureMap(path)
        relative = value["files"][0]["relativePath"]
        local = self.root / "captured" / relative
        raw = local.read_bytes()
        local.write_bytes(bytes([raw[0] ^ 1]) + raw[1:])
        with self.assertRaisesRegex(ValueError, "worker_capture_file_sha_changed"):
            transfer.resolve(value["remoteOwnerRoot"] + "/" + relative)
        original_sha = value["files"][0]["sha256"]
        value["files"][0]["sha256"] = adapter.planning.sha(local.read_bytes())
        path.write_bytes(encoded(value))
        rebound = adapter.CaptureMap(path)
        with self.assertRaisesRegex(ValueError, "worker_capture_transfer_hash_not_equal_original_pin"):
            rebound.resolve(value["remoteOwnerRoot"] + "/" + relative, original_sha)

    def test_transfer_rejects_case_collisions_and_windows_reparse_flag(self):
        manifest, path, value = self.prepare_capture_map(self.prepare_worker_files())
        duplicate = dict(value["files"][0], relativePath=value["files"][0]["relativePath"].upper())
        value["files"].append(duplicate)
        path.write_bytes(encoded(value))
        with self.assertRaisesRegex(ValueError, "worker_capture_file_entry_invalid_or_duplicate"):
            adapter.CaptureMap(path)
        reparse = Mock()
        reparse.lstat.return_value.st_file_attributes = 0x400
        reparse.is_symlink.return_value = False
        with self.assertRaisesRegex(ValueError, "worker_capture_symlink_or_reparse_forbidden"):
            adapter.CaptureMap._no_links(reparse)

    def test_transfer_rejects_actual_file_or_root_symlink_before_long_path_resolution(self):
        manifest, path, value = self.prepare_capture_map(self.prepare_worker_files())
        link = self.root / "capture-link"
        try:
            link.symlink_to(self.root / "captured", target_is_directory=True)
        except OSError as error:
            self.skipTest("Host does not permit synthetic symlink creation: " + str(error))
        value["localCapturedRoot"] = str(link)
        path.write_bytes(encoded(value))
        with self.assertRaisesRegex(ValueError, "worker_capture_symlink_or_reparse_forbidden"):
            adapter.CaptureMap(path)
        value["localCapturedRoot"] = str(self.root / "captured")
        relative = value["files"][0]["relativePath"]
        local = self.root / "captured" / relative
        moved = self.root / "moved-file"
        local.rename(moved)
        local.symlink_to(moved)
        path.write_bytes(encoded(value))
        with self.assertRaisesRegex(ValueError, "worker_capture_symlink_or_reparse_forbidden"):
            adapter.CaptureMap(path)


if __name__ == "__main__":
    unittest.main()
