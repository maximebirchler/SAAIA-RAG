"""Qualify invalid-PDF replacement after a real owned Linux lifecycle PASS.

NOEXEC by default. No build, installation, start, SSH or SQL mutation. Execute
changes one hash-pinned generated working PDF and stops the same five owned IDs.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
from pathlib import Path
import re
import sys
import urllib.request

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from linux_failed_replacement_checks import (ControlledPdfCopy, bind_baseline, exercise_failure,
                                             validate_lifecycle_proof)
from linux_lifecycle_checks import (CASE_ID, DOC_PATH, require, validate_configuration_continuity,
                                    validate_gc_configuration, validate_plan)
from linux_owned_runtime import OwnedDocker, assert_owned_path
from tools.ingestion_gold.contracts import validate_manifest

_spec = importlib.util.spec_from_file_location("replacement_lifecycle_driver", Path(__file__).with_name("invoke-linux-lifecycle.py"))
life = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(life)
corpus = life.corpus


def run(args):
    owner, manifest = corpus.read_json(args.owner), corpus.read_json(args.manifest)
    prior_corpus, prior_lifecycle = corpus.read_json(args.corpus_proof), corpus.read_json(args.lifecycle_proof)
    case = next((c for c in validate_manifest(manifest) if c["caseId"] == CASE_ID), None)
    require(case is not None, "The hash-pinned generated French scan is missing.")
    plan = validate_plan(owner, prior_corpus, case, owner_sha=corpus.sha(args.owner), manifest_sha=corpus.sha(args.manifest))
    previous_summary = validate_lifecycle_proof(prior_lifecycle, plan)
    require(type(args.maximum_wait_seconds) is int and 30 <= args.maximum_wait_seconds <= 1800
            and type(args.terminal_stability_seconds) is int and 5 <= args.terminal_stability_seconds <= 60,
            "Job and stability observations must be explicitly bounded.")
    plan.update({"schemaVersion": "saaia-linux-failed-replacement-plan.v1", "executeRequested": bool(args.execute),
                 "corpusProofSha256": corpus.sha(args.corpus_proof), "lifecycleProofSha256": corpus.sha(args.lifecycle_proof),
                 "maximumWaitSeconds": args.maximum_wait_seconds, "terminalStabilitySeconds": args.terminal_stability_seconds,
                 "previousIndexedVersion": previous_summary["indexedVersion"],
                 "previousIngestionVersion": previous_summary["ingestionVersion"],
                 "fault": "exact_first_16_source_bytes_without_objects_or_xref", "latePointReplayRepeated": False})
    if not args.execute:
        return plan
    require(sys.platform == "linux", "Execute only on the independently prepared owned Linux host.")
    root = Path(owner["root"])
    for path in (args.owner, args.manifest, args.corpus_proof, args.lifecycle_proof, args.source_directory,
                 args.output, args.api_key_file, Path(owner["configurationPath"])):
        assert_owned_path(path, root)
    source = assert_owned_path(args.source_directory / DOC_PATH, root)
    target = assert_owned_path(Path(owner["documentsRoot"]) / DOC_PATH, root)
    require(source != target and source.is_file() and target.is_file() and not args.output.exists(),
            "Use distinct known source/working files and a new evidence directory.")
    args.output.mkdir(mode=0o700)
    private = args.output / "private-observations"
    private.mkdir(mode=0o700)
    docker, headers, lifecycle, controlled = OwnedDocker(owner, execute=True), {}, None, None
    before, recovery_attempted = None, False
    result = {"schemaVersion": "saaia-linux-failed-replacement-execution.v1", "executed": True, "passed": False,
              "plan": plan, "startedAtUtc": life.utc(), "stage": "ownership-preflight", "steps": [], "cleanup": {},
              "canonicalRowsSeeded": False, "paidProviderCalls": 0,
              "failedReplacement": {"status": "NON_EXECUTED", "reason": "not_reached"},
              "limits": ["One generated scan PDF; no direct SQL mutation or late-point replay.",
                         "A stable failed terminal is observed for a bounded interval, not forever.",
                         "During corrupt source bytes, retained SQL/vectors are checked without claiming source freshness.",
                         "Unsigned Development remains ineligible for Production qualification."]}

    def scope_check():
        for path in (source, target, private):
            assert_owned_path(path, root)

    def save(name, value):
        require(Path(name).name == name and name.endswith(".private.json"), "Only simple private capture names are permitted.")
        corpus.write_json(private / name, value)

    def api(role, route, method="GET", body=None):
        docker.inspect_all()
        require(role in {"backend", "qdrant"} and route.startswith("/") and not route.startswith("//"),
                "Only owned role-relative routes may be called.")
        # This runner never writes directly to Qdrant, including during cleanup.
        require(role != "qdrant" or method == "POST" and route == "/collections/" + owner["qdrantCollection"] + "/points",
                "Replacement qualification permits only exact-ID Qdrant observations.")
        request = urllib.request.Request(owner["endpoints"][role] + route,
            data=None if body is None else json.dumps(body, allow_nan=False).encode("utf-8"), method=method,
            headers={"Content-Type": "application/json", **(headers if role == "backend" else {})})
        class NoRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, *unused):
                raise ValueError("Replacement HTTP redirects are forbidden.")
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
        with opener.open(request, timeout=60) as response:
            require(response.status == 200, "A complete 200 HTTP observation is required.")
            raw = response.read(8 * 1024 * 1024 + 1)
            require(len(raw) <= 8 * 1024 * 1024, "Replacement response exceeds its bounded capture size.")
            return json.loads(raw.decode("utf-8"))

    def recover(label):
        nonlocal recovery_attempted
        controlled.restore()
        # No source-backed search while the old revision still represents the failed generation.
        snapshot = lifecycle.snapshot()
        row = snapshot.get("document") or {}
        require(row.get("docId") == before["docId"] and snapshot.get("activeJobs") == 0,
                "Recovery requires the same document and zero active jobs.")
        recovery_attempted = True
        recovered, _, _ = lifecycle.upsert({**before, "ingestionVersion": row["ingestionVersion"]}, label)
        return {"status": "PASS", "generation": recovered["indexedVersion"], "sourceSha256": recovered["sourceSha256"],
                "freshSearchToReadValidated": True, "revisionId": recovered["revisionId"],
                "planFingerprint": recovered["plan"], "sourceRestoredExactly": True}

    try:
        safe, inspected = docker.inspect_all()
        backend_record = next(r for r in owner["containers"] if r["role"] == "backend")
        backend = next(r for r in inspected if r["Id"] == backend_record["id"])
        config = corpus.read_json(Path(owner["configurationPath"]))
        corpus.validate_disabled_llm(config, backend)
        prior_identity_path = assert_owned_path(args.lifecycle_proof.parent / "runtime-identity.json", root)
        prior_identity = corpus.read_json(prior_identity_path)
        require(corpus.sha(Path(owner["configurationPath"])) == prior_identity.get("configurationSha256"),
                "The mounted configuration differs from the lifecycle PASS configuration.")
        old_ownership_path = assert_owned_path(args.corpus_proof.parent / "runtime-ownership.json", root)
        validate_configuration_continuity(corpus.sha(Path(owner["configurationPath"])), corpus.read_json(old_ownership_path))
        qdrant_record = next(r for r in owner["containers"] if r["role"] == "qdrant")
        validate_gc_configuration(owner, config, next(r for r in inspected if r["Id"] == qdrant_record["id"]))
        binary_path = assert_owned_path(args.corpus_proof.parent / "backend-binary-identity.json", root)
        actual_binary = docker.command(["exec", backend_record["id"], "sha256sum", "/app/SAAIA.Backend.dll"]).split()[0]
        require(re.fullmatch(r"[a-f0-9]{64}", actual_binary) is not None
                and actual_binary == corpus.read_json(binary_path).get("assemblySha256") == prior_identity.get("backendAssemblySha256"),
                "The live DLL differs from the exact corpus/lifecycle PASS DLL.")
        docker.command(["exec", backend_record["id"], "test", "!", "-e", "/app/appsettings.Local.json"])
        corpus.write_json(args.output / "runtime-identity.json", {"containers": safe,
            "backendAssemblySha256": actual_binary, "configurationSha256": corpus.sha(Path(owner["configurationPath"]))})
        require(args.api_key_file.is_file() and args.api_key_file.stat().st_size <= 2048, "A bounded synthetic bootstrap key is required.")
        key = args.api_key_file.read_text(encoding="utf-8").strip()
        require(bool(key) and all(33 <= ord(c) <= 126 for c in key), "The synthetic bootstrap key is malformed.")
        headers.update(corpus.synthetic_admin_headers(key))
        controlled = ControlledPdfCopy(source, target, case["sourceSha256"], scope_check)
        controlled.check()
        lifecycle = life.Lifecycle(owner, case, config, docker, api, save, maximum_wait=args.maximum_wait_seconds,
                                   source_check=controlled.check)
        ready = lifecycle.api("backend", "/ready")
        result.update(corpus.qualification_identity(owner, backend, ready))
        require(ready.get("ok") is True and ready.get("details", {}).get("vector_gc_enabled") is True,
                "The existing backend is not ready with the qualified GC configuration.")
        result["stage"] = "verify-lifecycle-baseline"
        before, _, points = lifecycle.publication("prior-lifecycle-publication")
        bind_baseline(before, previous_summary)
        baseline_vectors = life.validate_points(before, points)
        require(all(baseline_vectors[k] == previous_summary.get(k) for k in
                ("pointCount", "pointIdsSha256", "vectorsAndPayloadSha256")),
                "The initial physical vectors differ from the final lifecycle proof.")
        lifecycle.history_ids.update(p["id"] for p in points)
        unrelated_baseline = lifecycle.unrelated_vectors()
        result["stage"] = "controlled-invalid-replacement"
        result["failedReplacement"] = exercise_failure(lifecycle, controlled, before, points,
                                                       stable_seconds=args.terminal_stability_seconds)
        lifecycle.steps.append(result["failedReplacement"])
        result["stage"] = "restore-and-fresh-reingestion"
        result["recovery"] = recover("restored-source-fresh-publication")
        unrelated_final = lifecycle.unrelated_vectors()
        require(unrelated_baseline == unrelated_final, "Unrelated publications or vectors changed during failed replacement/recovery.")
        result["unrelatedPublicationPreservation"] = {"sqlStateAndChunkChecksumsUnchanged": True,
            "currentPhysicalVectorsAndPayloadsUnchanged": True, **unrelated_final}
        result["passed"], result["stage"] = True, "completed"
    except Exception as failure:
        result.update({"failureType": type(failure).__name__, "failureStage": result["stage"]})
        save("failure.private.json", {"type": type(failure).__name__, "stage": result["stage"], "message": str(failure)})
    finally:
        if lifecycle is not None:
            try:
                result["cleanup"].update(lifecycle.cleanup_jobs(scope_check))
            except Exception as failure:
                result["cleanup"]["observationFailureType"] = type(failure).__name__
        if controlled is not None:
            try:
                controlled.restore()
                result["cleanup"]["workingSourceRestored"] = True
                if controlled.mutated and not recovery_attempted and before is not None and result["cleanup"].get("zeroActiveJobs") is True:
                    # A failed probe never becomes PASS because recovery succeeds.
                    lifecycle.source_check = controlled.check
                    result["cleanup"]["emergencyRecovery"] = recover("cleanup-restored-source-fresh-publication")
                    result["cleanup"].update(lifecycle.cleanup_jobs(scope_check))
            except Exception as failure:
                result["cleanup"].update({"workingSourceRestored": False, "restoreOrRecoveryFailureType": type(failure).__name__})
        try:
            result["cleanup"].update(docker.stop())
        except Exception as failure:
            result["cleanup"]["stopFailureType"] = type(failure).__name__
        result["steps"] = [] if lifecycle is None else lifecycle.steps
        result["passed"] = bool(result["passed"] and corpus.cleanup_success(result["cleanup"])
                                and result["cleanup"].get("workingSourceRestored") is True)
        result["deploymentQualificationPassed"] = result["passed"] and result.get("deploymentQualificationEligible", False)
        result["finishedAtUtc"] = life.utc()
        corpus.write_json(args.output / "failed-replacement-result.json", result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("owner", "manifest", "corpus-proof", "lifecycle-proof", "source-directory", "output", "api-key-file"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--maximum-wait-seconds", type=int, default=300)
    parser.add_argument("--terminal-stability-seconds", type=int, default=10)
    parser.add_argument("--execute", action="store_true")
    result = run(parser.parse_args())
    print(json.dumps(result if not result.get("executed") else {"passed": result["passed"],
        "deploymentQualificationPassed": result["deploymentQualificationPassed"], "cleanup": result["cleanup"], "paidProviderCalls": 0}))
    return 0 if not result.get("executed") or result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
