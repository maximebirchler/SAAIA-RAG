"""Pure filesystem/projection tests with fabricated JSON; no runtime or network."""
from __future__ import annotations
import copy
import importlib.util
import json
import os
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest
import uuid
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("public_export_v3_test", Path(__file__).with_name("export-linux-public-proofs-v3.py"))
M = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(M)


def terminal(kind="gold", family="production", run="11111111-2222-4333-8444-555555555555", passed=True):
    schema = "saaia-linux-ingestion-execution.v1"
    if kind == "lifecycle": schema = "saaia-linux-lifecycle-execution.v1"
    if kind == "failed-replacement": schema = "saaia-linux-failed-replacement-execution.v1"
    if kind.startswith("retrieval-"): schema = "saaia-linux-retrieval-facts-execution.v1"
    if kind.startswith("auto-"): schema = "saaia-linux-automatic-ingestion-execution.v1"
    obj = {"schemaVersion": schema, "executed": True, "passed": passed,
        "startedAtUtc": "2026-10-26T00:00:00Z", "finishedAtUtc": "2026-10-26T00:01:00Z",
        "qualificationMode": M.FAMILIES[family], "environmentObservedFromContainer": "Production" if family == "production" else "Development",
        "configurationSignatureVerified": family == "production", "deploymentQualificationEligible": family == "production",
        "deploymentQualificationPassed": passed and family == "production", "paidProviderCalls": 0,
        "plan": {"runId": run, "ownerManifestSha256": "a"*64, "corpusManifestSha256": "b"*64},
        "cleanup": {"zeroActiveJobs": True, "ownedContainersStopped": True, "dockerPublishedPortBindingsAbsent": True}}
    if kind.startswith("retrieval-"):
        obj.update(httpAttemptCount=4, passedFactCount=1, failedFactCount=0, facts=[{
            "factId": "pressure-1", "caseId": "fr-native-critical-v2", "physicalPage": 1, "annotationSha256": "a"*64,
            "sourceSha256": "b"*64, "planFingerprint": "c"*64, "passed": True, "rank": 1,
            "hitCount": 1, "latencySeconds": 0.01, "searchToReadExact": True}], negativeControls=[])
    if kind.startswith("auto-"):
        obj["plan"].update(admissionProfile=kind[5:], sourceSha256=["a"*64, "b"*64, "c"*64],
            sourceCaseIds=["fr-native-critical-v2", "native-two-column-v1", "fr-scan-critical-v2"], targetDocPaths=["auto-a.pdf", "auto-b.pdf", "auto-c.pdf"])
        obj.update(twoWorkers={"status": "NON_EXECUTED", "reason": "not_observed"},
                   conditionalDeleteReturn={"status": "NON_EXECUTED", "reason": "not_observed"})
    return obj


def identity_rows():
    return [{"role": role, "id": str(i+1)*64, "imageId": "sha256:"+"b"*64,
        "memoryLimitBytes": 1024**3, "nanoCpus": 10**9, "running": False, "loopbackPorts": []}
        for i, role in enumerate(sorted(M.ROLES5))]


def auxiliary(name):
    if name == "publication-verification.json":
        return {"schemaVersion": "saaia-owned-worker-publication-verification.v1", "passed": True, "caseCount": 1,
            "results": [{"caseId": "synthetic-case", "ok": True, "issues": [], "criticalPhysicalFactCount": 1}]}
    if name == "backend-binary-identity.json":
        return {"assemblySha256": "a"*64, "imageId": "sha256:"+"b"*64, "codeRevisionObservedByReadiness": "sha256:"+"c"*64}
    if name == "bound-runtime-provenance.json":
        return {"embeddingsBoundRevisionMatches": True, "qdrantBoundRevisionMatches": True, "qdrantRuntimeRevision": "sha256:"+"b"*64}
    if name == "empty-baseline.json":
        return {key: 0 for key in ("documents", "jobs", "revisions", "units", "chunks", "canonicalBundles")}
    if name == "text-parser-conformance.json":
        return {key: True for key in ("unicode_punctuation", "unicode_case", "conjunction", "accent_preserved")}
    if name == "sql-text-conformance.json":
        return {"tables": [{"table": table, "rows": 1, "mismatches": 0} for table in (
            "document_units", "retrieval_chunks", "contextual_text_entries", "exact_match_entries")]}
    if name == "runtime-identity.json":
        return {"backendAssemblySha256": "a"*64, "configurationSha256": "b"*64, "containers": identity_rows()}
    if name == "tei-attestation.json":
        return {"passed": True, "nativeInfo": {"model_id": "intfloat/multilingual-e5-base", "model_sha": None,
            "max_input_length": 512, "version": "1.6.1", "auto_truncate": False},
            "modelArtifactSha256": {"onnx/model.onnx": "a"*64, "config.json": "b"*64},
            "selectedSnapshotBindMapping": {"hostModelRoot": "/private/never-export"}, "container": {"argv": ["secret path"]}}
    raise AssertionError("Unknown test public file")


def args_for(root_parent, selections=("production-gold",), *, create=False):
    args = SimpleNamespace(execute=False, container_attestation=[])
    folders = {}
    for i, role in enumerate(selections):
        root = root_parent / str(uuid.UUID(int=100+i))
        folder = root / "evidence" / (role + "-final-v1")
        setattr(args, role.replace("-", "_"), folder)
        folders[role] = folder
        if create:
            folder.mkdir(parents=True)
            family, kind = M.ROLES[role]
            for name in M.KINDS[kind]:
                obj = terminal(kind, family, root.name) if name in M.RESULTS else auxiliary(name)
                (folder/name).write_text(json.dumps(obj), encoding="utf-8")
    args.output = next(iter(folders.values())).parent.parent / "public-export-production-v3"
    return args, folders


class ExportV3Tests(unittest.TestCase):
    def test_noexecute_plans_all_44_future_production_files_without_read_or_write(self):
        roles = ["production-"+kind for kind in M.KINDS]
        args, _ = args_for(M.NAMESPACE_PARENT, roles)
        with patch.object(M, "safe_path", side_effect=lambda p: p), \
                patch.object(M, "read_public", side_effect=AssertionError("No source proof read")), \
                patch.object(Path, "mkdir", side_effect=AssertionError("No output write")):
            result = M.export(args)
        self.assertFalse(result["executeRequested"])
        self.assertEqual(44, result["selectedFileCount"])
        self.assertEqual(9, len(result["campaigns"]))
        self.assertEqual(9, len({g["runId"] for g in result["campaigns"].values()}))

    def test_production_and_development_use_separate_explicit_roles_and_roots(self):
        args, _ = args_for(M.NAMESPACE_PARENT, ("production-gold", "development-gold"))
        with patch.object(M, "safe_path", side_effect=lambda p: p): result = M.plan(args)[0]
        self.assertEqual(16, result["selectedFileCount"])
        self.assertNotEqual(result["campaigns"]["production-gold"]["runId"], result["campaigns"]["development-gold"]["runId"])
        with self.assertRaises(ValueError): M.qualification(M.project("execution-result.json", terminal(family="development"), "gold"), "production", "gold")
        with self.assertRaises(ValueError): M.qualification(M.project("execution-result.json", terminal(), "gold"), "development", "gold")

    def test_source_folders_cannot_escape_uuid_evidence_or_alias_other_campaigns(self):
        args, folders = args_for(M.NAMESPACE_PARENT)
        for wrong in (M.NAMESPACE_PARENT / "not-uuid/evidence/gold", next(iter(folders.values())).parent.parent/"private/gold"):
            args.production_gold = wrong
            with patch.object(M, "safe_path", side_effect=lambda p: p), self.assertRaises(ValueError): M.plan(args)
        args, folders = args_for(M.NAMESPACE_PARENT)
        args.development_gold = args.production_gold
        with patch.object(M, "safe_path", side_effect=lambda p: p), self.assertRaises(ValueError): M.plan(args)

    def test_model_identifier_rejects_posix_windows_relative_paths_and_urls(self):
        for model in ("/data/snapshot", "C:/cache/model", "../model", "cache/nested/model", "https://host/model", "org/.."):
            obj = auxiliary("tei-attestation.json"); obj["nativeInfo"]["model_id"] = model
            with self.subTest(model=model), self.assertRaises(ValueError): M.project("tei-attestation.json", obj, "gold")
        result = M.project("tei-attestation.json", auxiliary("tei-attestation.json"), "gold")
        self.assertEqual("intfloat/multilingual-e5-base", result["nativeInfo"]["model_id"])
        self.assertNotIn("private", json.dumps(result))

    def test_backend_code_revision_and_tei_image_weights_revision_are_preserved_without_argv(self):
        backend=M.project("backend-binary-identity.json",auxiliary("backend-binary-identity.json"),"gold")
        self.assertEqual("sha256:"+"c"*64,backend["codeRevisionObservedByReadiness"])
        obj=auxiliary("tei-attestation.json")
        obj["container"].update(imageId="sha256:"+"b"*64,modelFileSha256="a"*64,modelRevision="d"*40,
            autoTruncate=False,ownedContainerVerified=True)
        projected=M.project("tei-attestation.json",obj,"gold")
        self.assertEqual("sha256:"+"b"*64,projected["container"]["imageId"])
        self.assertEqual("d"*40,projected["container"]["modelRevision"])
        self.assertNotIn("argv",projected["container"])

    def test_failure_codes_cannot_smuggle_relative_or_absolute_paths(self):
        for code in ("/private/key", "Error:/private/key", "private/file", "C:private", "file://private/key"):
            obj = terminal(passed=False); obj["failureType"] = code
            with self.subTest(code=code), self.assertRaises(ValueError): M.project("execution-result.json", obj, "gold")

    def test_raw_fields_duplicates_secrets_and_nonfinite_values_fail_before_projection(self):
        for raw in (b'{"payload":{}}', b'{"vector":[1]}', b'{"ApiKey":"fake"}', b'{"text":"private"}',
                    b'{"passed":true,"passed":false}', b'{"score":NaN}', b'{"x":"sk-fake-secret-value-12345"}'):
            with self.subTest(raw=raw), self.assertRaises(ValueError): M.decode(raw)

    def test_synthetic_bootstrap_key_prefix_cannot_leak_through_an_unknown_public_field(self):
        fake = "saaia_linux_qual_" + "a" * 48
        with self.assertRaises(ValueError): M.decode(json.dumps({"unknown": fake}).encode())

    def test_owner_config_rawlogs_and_private_observations_are_not_allowlisted(self):
        for name in ("owner.json", "runtime-ownership.json", "deployment.config.json", "private-observations.json", "raw-log.json"):
            with self.subTest(name=name), self.assertRaises(ValueError): M.project(name, {}, "gold")
        args, _ = args_for(M.NAMESPACE_PARENT)
        args.container_attestation = ["production-gold=" + str(args.production_gold / "runtime-ownership.json")]
        with patch.object(M, "safe_path", side_effect=lambda p: p), self.assertRaises(ValueError): M.plan(args)

    def test_terminal_failure_and_unknown_qualification_stay_failed_and_unknown(self):
        obj = terminal(passed=False); obj.update(failureType="ValueError", failureStage="owned-preflight")
        for key in ("qualificationMode", "environmentObservedFromContainer", "configurationSignatureVerified", "deploymentQualificationEligible"):
            obj.pop(key)
        result = M.project("execution-result.json", obj, "gold")
        separation = M.qualification(result, "production", "gold")
        self.assertFalse(result["passed"])
        self.assertIsNone(separation["observedQualificationMode"])
        self.assertFalse(separation["observedQualificationModePresent"])

    def test_successful_production_without_verified_signature_cannot_be_relabelled(self):
        for key in ("qualificationMode", "environmentObservedFromContainer", "configurationSignatureVerified", "deploymentQualificationEligible"):
            obj = terminal(); obj.pop(key)
            with self.subTest(key=key), self.assertRaises(ValueError): M.qualification(M.project("execution-result.json", obj, "gold"), "production", "gold")

    def test_auto_exports_observations_and_cleanup_without_target_paths_or_diagnostic_text(self):
        obj = terminal("auto-defaults")
        obj["cleanup"].update(cancelRequests=1, activeJobsAfterBackendStopped=0, producerStoppedBeforeFinalJobObservation=True)
        obj["steps"] = [{"phase":"changed-a", "sourceCaseId":"fr-native-critical-v2", "sourceSha256":"a"*64,
            "docPath":"/private/ignored.pdf", "sources":["watcher"], "searchToReadPassed":True}]
        obj["limits"] = ["private sentence from a fixture"]
        result = M.project("automatic-ingestion-result.json", obj, "auto-defaults")
        self.assertEqual(3,result["plan"]["targetDocPathCount"])
        self.assertEqual("NON_EXECUTED",result["twoWorkers"]["status"])
        self.assertEqual(0,result["cleanup"]["activeJobsAfterBackendStopped"])
        self.assertNotIn("private",json.dumps(result))
        with self.assertRaises(ValueError): M.project("automatic-ingestion-result.json",obj,"auto-scanner")

    def test_retrieval_exports_fact_ids_hashes_rank_and_latency_without_query_text(self):
        obj = terminal("retrieval-gold")
        obj["facts"][0]["query"] = "private source phrase"
        result = M.project("retrieval-facts-result.json",obj,"retrieval-gold")
        self.assertEqual("pressure-1", result["facts"][0]["factId"])
        self.assertEqual("a"*64,result["facts"][0]["annotationSha256"])
        self.assertEqual(0.01,result["facts"][0]["latencySeconds"])
        self.assertNotIn("private",json.dumps(result))
        self.assertTrue(result["qualificationEvidenceInheritedFromPriorCorpus"])

    def test_all_106_fact_records_are_preserved_not_only_their_totals(self):
        combined=[]
        for kind,count in (("retrieval-gold",90),("retrieval-recipe",16)):
            obj=terminal(kind)
            obj["facts"]=[{**obj["facts"][0],"factId":"fact-"+str(i)} for i in range(count)]
            obj["passedFactCount"]=count
            result=M.project("retrieval-facts-result.json",obj,kind)
            self.assertEqual(count,result["exportedFactCount"])
            self.assertEqual([f["factId"] for f in obj["facts"]],[f["factId"] for f in result["facts"]])
            combined.extend(result["facts"])
        self.assertEqual(106,len(combined))

    def test_signature_negative_control_keeps_exit_139_and_oom_false_without_log_text(self):
        obj={"schemaVersion":"saaia-linux-signature-rejection-control.v1","runId":terminal()["plan"]["runId"],
            "executed":True,"passed":True,"finishedAtUtc":"2026-10-26T00:01:00Z","exitCode":139,"oomKilled":False,
            "configurationTampered":True,"startupRejected":True,"rejectionObservedInDiagnostics":True,
            "diagnosticsSha256":"a"*64,"failureMessage":"/private/key/path must never be exported"}
        result=M.project(M.SIGNATURE_FILE,obj,"signature-rejection")
        self.assertEqual(139,result["exitCode"])
        self.assertFalse(result["oomKilled"])
        self.assertTrue(result["independentNegativeControlNotProductionQualification"])
        self.assertNotIn("private",json.dumps(result))
        args,folders=args_for(M.NAMESPACE_PARENT)
        args.signature_control=next(iter(folders.values())).parent/"signature-rejection-v1"/M.SIGNATURE_FILE
        with patch.object(M,"safe_path",side_effect=lambda p:p):
            planned=M.plan(args)[0]
        self.assertEqual(9,planned["selectedFileCount"])
        self.assertEqual("independent_signature_control",planned["campaigns"]["signature-control"]["family"])

    def test_exact_observed_signature_negative_schema_needs_no_invented_time_or_run_id(self):
        obj={"schemaVersion":"saaia-real-production-signature-negative.v1","executed":True,"passed":True,
            "privateKeyTransferred":False,"containerId":"a"*64,"imageId":"sha256:"+"b"*64,
            "originalConfigurationSha256":"c"*64,"tamperedConfigurationSha256":"d"*64,"signatureFileSha256":"e"*64,
            "paidProviderCalls":0,"exitCode":139,"oomKilled":False,"networkMode":"none",
            "tamperedConfigurationRejected":True,"ownedContainerStopped":True,"sourceConfigurationUnchanged":True}
        result=M.project("signature-negative-result.json",obj,"signature-rejection")
        self.assertNotIn("runId",result);self.assertNotIn("finishedAtUtc",result)
        self.assertEqual(139,result["exitCode"]);self.assertFalse(result["privateKeyTransferred"])
        args,folders=args_for(M.NAMESPACE_PARENT)
        root=next(iter(folders.values())).parent.parent
        args.signature_control=root/"signature-negative-final-v1/signature-negative-result.json"
        with patch.object(M,"safe_path",side_effect=lambda p:p):planned=M.plan(args)[0]
        self.assertEqual("signature-negative-result.json",planned["campaigns"]["signature-control"]["publicFile"])

    def test_failed_replacement_keeps_failed_terminal_as_expected_fault_not_whole_campaign_failure(self):
        obj = terminal("failed-replacement")
        obj["failedReplacement"] = {"status":"PASS","terminal":"failed","generation":6,"attempts":3,
            "previousPublicationPreserved":True,"currentSourceFreshnessClaimed":False,
            "fault":{"sourceSha256":"a"*64,"truncatedSha256":"b"*64,"truncatedByteCount":16,"prefixOnly":True}}
        result=M.project("failed-replacement-result.json",obj,"failed-replacement")
        self.assertTrue(result["passed"])
        self.assertEqual("failed",result["failedReplacement"]["terminal"])
        self.assertFalse(result["failedReplacement"]["currentSourceFreshnessClaimed"])

    def test_public_container_attestation_requires_exact_schema_and_five_unique_ids(self):
        obj = {"schemaVersion":"saaia-linux-public-runtime-attestation.v1","runId":terminal()["plan"]["runId"],
            "containers":identity_rows(),"backendAssemblySha256":"a"*64,"codeRevisionObservedByReadiness":"sha256:"+"c"*64}
        result=M.project(M.CONTAINER_FILE,obj,"gold")
        self.assertEqual(5,len(result["containers"]))
        obj["containers"][0]["id"]="partial"
        with self.assertRaises(ValueError):M.project(M.CONTAINER_FILE,obj,"gold")

    def test_public_container_attestation_cannot_reuse_one_cid_for_five_roles(self):
        obj={"schemaVersion":"saaia-linux-public-runtime-attestation.v1","containers":identity_rows()}
        for item in obj["containers"]:item["id"]="a"*64
        with self.assertRaises(ValueError):M.project(M.CONTAINER_FILE,obj,"gold")

    def test_runtime_attestation_rejects_full_docker_or_configuration_fields(self):
        obj={"schemaVersion":"saaia-linux-public-runtime-attestation.v1","containers":identity_rows()}
        obj["Config"]={"unwanted":"private configuration"}
        with self.assertRaises(ValueError):M.project(M.CONTAINER_FILE,obj,"gold")
        obj.pop("Config");obj["containers"][0]["Mounts"]=[]
        with self.assertRaises(ValueError):M.project(M.CONTAINER_FILE,obj,"gold")

    def test_container_attestation_cannot_claim_development_environment_in_production_role(self):
        obj={"qualificationMode":"production","environmentObservedFromContainer":"Development"}
        with self.assertRaises(ValueError):M.qualification(obj,"production","gold")

    def test_offline_ocr_v3_is_optional_and_not_relabelled_as_full_production_worker(self):
        args, folders=args_for(M.NAMESPACE_PARENT)
        root=next(iter(folders.values())).parent.parent
        args.ocr_v3_proof=root/("ocr-e2e-"+uuid.UUID(root.name).hex[:12]+"-v3")/"evidence/result.json"
        with patch.object(M,"safe_path",side_effect=lambda p:p): plan=M.plan(args)[0]
        self.assertEqual(9,plan["selectedFileCount"])
        self.assertEqual("independent_engine_probe",plan["campaigns"]["offline-ocr-v3-proof"]["family"])

    def test_descriptor_rejects_hardlink_and_linux_opened_path_mismatch_before_reading(self):
        with tempfile.TemporaryDirectory() as tmp:
            source=Path(tmp).resolve()/"public.json";source.write_text("{}")
            actual=os.stat(source)
            with patch.object(M.os,"fstat",return_value=SimpleNamespace(st_mode=actual.st_mode,st_nlink=2,st_size=2)),\
                    patch.object(M.os,"read",side_effect=AssertionError("No content read")):
                with self.assertRaises(ValueError):M.read_public(source)
            with patch.object(M.sys,"platform","linux"),patch.object(M.os,"readlink",return_value="/private/other"),\
                    patch.object(M.os,"read",side_effect=AssertionError("No content read")):
                with self.assertRaises(ValueError):M.read_public(source)

    def test_symlink_components_are_refused_without_reading_target(self):
        with tempfile.TemporaryDirectory() as tmp:
            path=Path(tmp).resolve()/"proof.json"
            with patch.object(Path,"is_symlink",return_value=True):
                with self.assertRaises(ValueError):M.safe_path(path)

    def test_full_nine_campaign_export_and_exact_inventory_verification(self):
        with tempfile.TemporaryDirectory() as tmp:
            parent=Path(tmp).resolve()
            with patch.object(M,"NAMESPACE_PARENT",parent):
                args,_=args_for(parent,["production-"+k for k in M.KINDS],create=True);args.execute=True
                result=M.export(args)
                self.assertEqual(44,result["selectedFileCount"])
                self.assertEqual(44,M.verify(args.output,result["sealSha256"])["fileCount"])
                (args.output/"unexpected-private-file.txt").write_text("never read")
                with self.assertRaises(ValueError):M.verify(args.output,result["sealSha256"])

    def test_copy_tamper_wrong_independent_hash_and_extra_subdirectory_are_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            parent=Path(tmp).resolve()
            with patch.object(M,"NAMESPACE_PARENT",parent):
                args,_=args_for(parent,create=True);args.execute=True;result=M.export(args)
                with self.assertRaises(ValueError):M.verify(args.output,"0"*64)
                child=next(p for p in args.output.iterdir() if p.name!="public-export-seal.json")
                child.write_bytes(child.read_bytes()+b" ")
                with self.assertRaises(ValueError):M.verify(args.output,result["sealSha256"])

    def test_source_change_before_output_preparation_leaves_no_seal_or_directory(self):
        with tempfile.TemporaryDirectory() as tmp:
            parent=Path(tmp).resolve()
            with patch.object(M,"NAMESPACE_PARENT",parent):
                args,folders=args_for(parent,create=True);args.execute=True
                actual=M.read_public;counts={}
                def changed(path):
                    value=actual(path);counts[path]=counts.get(path,0)+1
                    return value+b" " if counts[path]>1 else value
                with patch.object(M,"read_public",side_effect=changed),self.assertRaises(ValueError):M.export(args)
                self.assertFalse(args.output.exists())

    def test_source_change_after_projection_write_prevents_seal_and_extra_subdirectory_is_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            parent=Path(tmp).resolve()
            with patch.object(M,"NAMESPACE_PARENT",parent):
                args,_=args_for(parent,create=True);args.execute=True
                actual=M.read_public;counts={}
                def changed(path):
                    value=actual(path);counts[path]=counts.get(path,0)+1
                    return value+b" " if counts[path]>2 else value
                with patch.object(M,"read_public",side_effect=changed),self.assertRaises(ValueError):M.export(args)
                self.assertTrue(args.output.exists())
                self.assertFalse((args.output/"public-export-seal.json").exists())
        with tempfile.TemporaryDirectory() as tmp:
            parent=Path(tmp).resolve()
            with patch.object(M,"NAMESPACE_PARENT",parent):
                args,_=args_for(parent,create=True);args.execute=True;result=M.export(args)
                (args.output/"extra-directory").mkdir()
                with self.assertRaises(ValueError):M.verify(args.output,result["sealSha256"])

    def test_verifier_rejects_wrong_role_namespace_binding_even_with_resealed_local_bytes(self):
        with tempfile.TemporaryDirectory() as tmp:
            parent=Path(tmp).resolve()
            with patch.object(M,"NAMESPACE_PARENT",parent):
                args,_=args_for(parent,create=True);args.execute=True;M.export(args)
                seal_path=args.output/"public-export-seal.json";seal=json.loads(seal_path.read_text())
                entry=seal["files"][0];file=args.output/entry["path"];obj=json.loads(file.read_text())
                obj["campaignRunId"]=str(uuid.uuid4());wire=json.dumps(obj).encode();file.write_bytes(wire)
                entry.update(sha256=M.file_sha(wire),byteSize=len(wire));seal_wire=json.dumps(seal).encode();seal_path.write_bytes(seal_wire)
                with self.assertRaises(ValueError):M.verify(args.output,M.file_sha(seal_wire))


if __name__=="__main__":
    unittest.main(verbosity=2)
