"""Pure retrieval fact protocol tests; simulated HTTP/SQL and canonical captures."""
from __future__ import annotations
import base64
import copy
import datetime
import gzip
import hashlib
import importlib.util
import json
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from .test_linux_lifecycle import inputs
from .test_worker_verifier import captured, search_capture, hybrid_nonce_capture

SCRIPT = Path(__file__).resolve().parents[2] / "validation-ingestion/invoke-linux-retrieval-facts.py"
SPEC = importlib.util.spec_from_file_location("retrieval_fact_driver_test", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
import linux_retrieval_fact_checks as CHECK


def values():
    owner, manifest, case, prior, _ = inputs()
    manifest["cases"] = [case]
    observations = captured()[2]
    # SQL json_build_object emits these explicit camelCase identities. The
    # older verifier-only capture did not need a job UUID at all.
    for index, job in enumerate(observations["jobs"]):
        job["jobId"] = str(__import__("uuid").UUID(int=1000 + index))
    return owner, manifest, case, prior, observations


def rewrite_bundle(observation, mutate):
    row = observation["documents"][0]
    artifact = row["binaryArtifacts"][0]
    bundle = json.loads(gzip.decompress(base64.b64decode(artifact["payloadBase64"])))
    mutate(bundle)
    raw = json.dumps(bundle).encode("utf-8")
    compressed = gzip.compress(raw, mtime=0)
    artifact.update(payloadBase64=base64.b64encode(compressed).decode(), byteSize=len(raw), storedSize=len(compressed),
                    contentHash=hashlib.sha256(raw).hexdigest())


class FakeHttp:
    def __init__(self, *, stale=False):
        self.observation = values()[-1]
        self.capture = next(iter(search_capture(captured()).values()))
        self.calls, self.stale = [], stale

    def __call__(self, route, method, body, timeout):
        self.calls.append((route, method, copy.deepcopy(body), timeout))
        if route.startswith("/documents/context?"):
            return copy.deepcopy(self.capture["read"]["response"])
        self.assert_route(route, method)
        if body["query"].startswith("saaianomatch") or body["docId"] != self.observation["documents"][0]["docId"]:
            return {"query": body["query"], "items": [], "metrics": {"degradedRetrievers": [], "returned": 0,
                "exactMatchReturned": 0, "sparseReturned": 0, "denseReturned": 0, "retrieversUsed": []}}
        response = copy.deepcopy(self.capture["positive"]["response"])
        response["query"] = body["query"]
        if self.stale:
            response["items"][0]["revisionId"] = "99999999-aaaa-bbbb-cccc-dddddddddddd"
        return response

    @staticmethod
    def assert_route(route, method):
        if (route, method) != ("/rag/search", "POST"):
            raise AssertionError("No enqueue, cancellation, readiness, provider or unrelated route is permitted")


class RetrievalFactTests(unittest.TestCase):
    def test_historical_jobs_with_equal_paths_are_order_independent_but_state_changes_are_not(self):
        before = {"activeJobs": 0, "documents": [], "jobs": [
            {"docPath": "test.pdf", "jobId": "00000000-0000-0000-0000-000000000002", "status": "done"},
            {"docPath": "test.pdf", "jobId": "00000000-0000-0000-0000-000000000001", "status": "canceled"}]}
        after = copy.deepcopy(before)
        after["jobs"].reverse()
        self.assertEqual(CHECK.observation_identity(before), CHECK.observation_identity(after))
        after["jobs"][0]["status"] = "failed"
        self.assertNotEqual(CHECK.observation_identity(before), CHECK.observation_identity(after))
        after["jobs"].append(copy.deepcopy(after["jobs"][0]))
        with self.assertRaises(ValueError): CHECK.observation_identity(after)
        for invalid in ({"status": "done"}, {"docPath": "test.pdf", "jobId": "invalid"}):
            with self.subTest(invalid=invalid), self.assertRaises(ValueError):
                CHECK.observation_identity({"documents": [], "activeJobs": 0, "jobs": [invalid]})

    def test_owned_inspection_cannot_open_http_after_consuming_the_shared_deadline(self):
        owner=values()[0]
        now=[0.0]
        runtime=SimpleNamespace(inspect_all=lambda:now.__setitem__(0,31.0))
        opened=[]
        with self.assertRaises(ValueError):
            MODULE.owned_http(runtime,owner,{},"/rag/search","POST",{},30.0,clock=lambda:now[0],
                opener_factory=lambda *_:opened.append(True))
        self.assertFalse(opened)

    def test_http_finish_and_final_sql_observation_cannot_be_claimed_inside_expired_window(self):
        owner=values()[0]
        now=[0.0]
        class Response:
            status=200
            def __enter__(self):return self
            def __exit__(self,*unused):return False
            def read(self,limit):
                now[0]=31.0
                return b'{}'
        opener=SimpleNamespace(open=lambda *args,**kwargs:Response())
        with self.assertRaises(ValueError):
            MODULE.owned_http(SimpleNamespace(inspect_all=lambda:None),owner,{},"/rag/search","POST",{},30.0,
                clock=lambda:now[0],opener_factory=lambda *_:opener)
        with self.assertRaises(ValueError): MODULE.require_finished_in_window(30.0,clock=lambda:31.0)
        MODULE.require_finished_in_window(30.0,clock=lambda:30.0)

    def test_noexecute_never_reads_key_pdf_config_or_constructs_docker_http(self):
        owner, manifest, _, prior, _ = values()
        args = SimpleNamespace(owner=Path("owner.json"), manifest=Path("manifest.json"), corpus_proof=Path("result.json"),
            campaign_deadline_utc=None, execute=False, api_key_file=Path("never-read-key"))
        with patch.object(MODULE.corpus, "read_json", side_effect=[owner, manifest, prior]), \
                patch.object(MODULE.corpus, "sha", return_value="0"*64), \
                patch.object(MODULE, "OwnedDocker", side_effect=AssertionError("No Docker")), \
                patch.object(Path, "open", side_effect=AssertionError("No private read")), \
                patch.object(MODULE.urllib.request, "build_opener", side_effect=AssertionError("No HTTP")):
            result = MODULE.run(args)
        self.assertFalse(result["executeRequested"])
        self.assertFalse(result["newReadinessHttpRequestPlanned"])
        self.assertEqual(12, result["maximumHttpCalls"])

    def test_real_gold_bound_is_204_and_complete_fact_set_is_not_silently_reduced(self):
        owner, manifest, _, prior, _ = inputs()
        prior["plan"]["caseIds"] = [case["caseId"] for case in manifest["cases"]]
        cases, plan = CHECK.validate_inputs(owner, prior, manifest, owner_sha="0"*64, manifest_sha="0"*64)
        self.assertEqual((12, 90, 204), (len(cases), plan["factCount"], plan["maximumHttpCalls"]))
        manifest["cases"][0]["expectations"]["criticalFacts"][0]["retrievalRequired"] = False
        with self.assertRaises(ValueError): CHECK.validate_inputs(owner, prior, manifest, owner_sha="0"*64, manifest_sha="0"*64)

    def test_missing_prior_pass_or_changed_namespace_cannot_reach_execution(self):
        owner, manifest, _, prior, _ = values()
        for name in ("executed", "passed", "canonicalSearchValidated", "ingestionWorkerExecuted"):
            changed = copy.deepcopy(prior); changed[name] = False
            with self.subTest(name=name), self.assertRaises(ValueError):
                CHECK.validate_inputs(owner, changed, manifest, owner_sha="0"*64, manifest_sha="0"*64)
        prior["plan"]["ownerManifestSha256"] = "f"*64
        with self.assertRaises(ValueError): CHECK.validate_inputs(owner, prior, manifest, owner_sha="0"*64, manifest_sha="0"*64)

    def test_current_generation_can_advance_without_exactly_one_historical_done_job(self):
        _, _, case, _, observation = values()
        row = observation["documents"][0]
        row.update(indexedVersion=7, revisionId="77777777-8888-4999-8aaa-bbbbbbbbbbbb")
        rewrite_bundle(observation, lambda bundle: bundle["document"].update(revisionId=row["revisionId"]))
        observation["jobs"] += [{"jobId":"old-failed", "status":"failed"}, {"jobId":"old-cancel", "status":"canceled"}]
        result = CHECK.baseline([case], observation)
        self.assertEqual(7, result[case["caseId"]]["indexedVersion"])
        self.assertEqual(5, len(result[case["caseId"]]["_verifiedPhysicalFacts"]))

    def test_canonical_physical_fact_cannot_be_proved_by_chunk_or_metadata_alone(self):
        _, _, case, _, observation = values()
        rewrite_bundle(observation, lambda bundle: bundle["document"]["pages"][0].update(blocks=[]))
        with self.assertRaises(ValueError): CHECK.baseline([case], observation)

    def test_chunk_checksum_and_bundle_source_revision_cannot_be_substituted(self):
        _, _, case, _, observation = values()
        observation["documents"][0]["chunks"][0]["text"] += "\r"
        with self.assertRaises(ValueError): CHECK.baseline([case], observation)
        _, _, case, _, observation = values()
        rewrite_bundle(observation, lambda bundle: bundle["document"].update(revisionId="wrong"))
        with self.assertRaises(ValueError): CHECK.baseline([case], observation)

    def test_same_chunk_read_is_reused_for_all_five_annotations_not_just_first_fact(self):
        _, _, case, _, observation = values()
        rows = CHECK.baseline([case], observation)
        transport, captures = FakeHttp(), []
        probe = MODULE.FactProbe(transport, lambda name, value: captures.append((name,value)), maximum_calls=12, deadline_monotonic=100, clock=lambda:0)
        self.assertTrue(probe.execute([case], rows))
        self.assertEqual((5, 1, 8), (len(probe.results), len(probe.controls), probe.calls))
        self.assertEqual(1, sum(route.startswith("/documents/context?") for route, *_ in transport.calls))
        self.assertEqual(4, sum(row["readReused"] for row in probe.results))
        self.assertEqual(5, len({r["factId"] for r in probe.results}))

    def test_stale_hit_is_not_accepted_even_when_its_fact_text_and_read_are_correct(self):
        _, _, case, _, observation = values()
        rows = CHECK.baseline([case], observation)
        probe = MODULE.FactProbe(FakeHttp(stale=True), lambda *_:None, maximum_calls=12, deadline_monotonic=100, clock=lambda:0)
        self.assertFalse(probe.execute([case], rows))
        self.assertTrue(all(not r["currentIdentitiesExact"] for r in probe.results))

    def test_fact_check_requires_baseline_verified_physical_binding(self):
        _, _, case, _, observation = values()
        row = observation["documents"][0]
        cap = next(iter(search_capture(captured()).values()))
        result = CHECK.fact_check(case,row,case["expectations"]["criticalFacts"][0],cap["positive"]["response"],cap["read"])
        self.assertFalse(result["passed"])
        self.assertFalse(result["physicalFactVerifiedInCurrentCanonicalBundle"])

    def test_read_with_an_extra_foreign_chunk_does_not_pass_on_one_correct_item(self):
        _, _, case, _, observation = values()
        row = CHECK.baseline([case], observation)[case["caseId"]]
        cap = next(iter(search_capture(captured()).values()))
        cap["read"]["response"]["items"].append({"chunkId":"99999999-aaaa-bbbb-cccc-dddddddddddd","text":"foreign"})
        result=CHECK.fact_check(case,row,case["expectations"]["criticalFacts"][0],cap["positive"]["response"],cap["read"])
        self.assertFalse(result["searchToReadExact"])
        self.assertFalse(result["passed"])

    def test_nullable_diagnostics_wire_variants_are_healthy_for_source_bound_positive_facts(self):
        _, _, case, _, observation = values()
        row = CHECK.baseline([case], observation)[case["caseId"]]
        for shape in ("omitted", "null", "empty"):
            cap=next(iter(search_capture(captured()).values()))
            metrics=cap["positive"]["response"]["metrics"]
            if shape == "omitted":metrics.pop("degradedRetrievers",None)
            elif shape == "null":metrics.update(degradedRetrievers=None,degradedRetrieverErrors=None)
            else:metrics.update(degradedRetrievers=[],degradedRetrieverErrors={})
            # Round-trip the real JSON serializer shapes, not a truthy helper stub.
            response=json.loads(json.dumps(cap["positive"]["response"]))
            result=CHECK.fact_check(case,row,case["expectations"]["criticalFacts"][0],response,cap["read"])
            with self.subTest(shape=shape):self.assertTrue(result["passed"],result)

    def test_negative_metrics_accept_nullable_empty_diagnostics_like_the_real_serializer(self):
        _, _, case, _, observation = values()
        row = CHECK.baseline([case], observation)[case["caseId"]]
        cap=next(iter(search_capture(captured()).values()))
        del cap["negative"][0]["response"]["metrics"]["degradedRetrievers"]
        result=CHECK.negative_check(case,row,case["expectations"]["criticalFacts"][0],
            cap["positive"]["request"],cap["positive"]["response"],cap["read"],cap["negative"])
        self.assertTrue(result["negativeMetricsExplicitlyNotDegraded"])
        self.assertTrue(result["passed"])

    def test_retriever_errors_are_not_hidden_by_an_empty_or_omitted_retriever_list(self):
        _, _, case, _, observation=values()
        row=CHECK.baseline([case],observation)[case["caseId"]]
        cap=next(iter(search_capture(captured()).values()))
        cap["positive"]["response"]["metrics"]["degradedRetrieverErrors"]={"canonical_sparse_fts":"source_unavailable"}
        self.assertFalse(CHECK.fact_check(case,row,case["expectations"]["criticalFacts"][0],
            cap["positive"]["response"],cap["read"])["passed"])

    def test_active_or_malformed_job_count_prevents_current_publication_baseline(self):
        _, _, case, _, observation = values()
        for count in (1, False, "0"):
            with self.subTest(count=count), self.assertRaises(ValueError):
                CHECK.baseline([case],{**observation,"activeJobs":count})

    def test_dense_nonce_neighbors_remain_valid_only_when_lexical_counts_are_zero(self):
        _, _, case, _, observation = values()
        row = CHECK.baseline([case], observation)[case["caseId"]]
        cap = next(iter(hybrid_nonce_capture(captured()).values()))
        result = CHECK.negative_check(case,row,case["expectations"]["criticalFacts"][0],
            cap["positive"]["request"],cap["positive"]["response"],cap["read"],cap["negative"])
        self.assertTrue(result["passed"])
        cap["negative"][0]["response"]["metrics"]["sparseReturned"] = 1
        self.assertFalse(CHECK.negative_check(case,row,case["expectations"]["criticalFacts"][0],
            cap["positive"]["request"],cap["positive"]["response"],cap["read"],cap["negative"])["passed"])

    def test_deadline_and_http_count_prevent_an_additional_transport_attempt(self):
        called=[]
        transport=lambda *args:called.append(args) or {}
        probe=MODULE.FactProbe(transport,lambda *_:None,maximum_calls=1,deadline_monotonic=100,clock=lambda:0)
        probe.request("/rag/search","POST",{})
        with self.assertRaises(ValueError): probe.request("/rag/search","POST",{})
        self.assertEqual(1,len(called))
        probe=MODULE.FactProbe(transport,lambda *_:None,maximum_calls=2,deadline_monotonic=0,clock=lambda:0)
        with self.assertRaises(ValueError): probe.request("/rag/search","POST",{})
        self.assertEqual(1,len(called))

    def test_shared_deadline_must_be_aware_future_and_at_most_900_seconds(self):
        now=datetime.datetime(2026,10,26,tzinfo=datetime.timezone.utc)
        CHECK.deadline("2026-10-26T00:15:00Z",now)
        for value in ("2026-10-26T00:15:01Z","2026-10-25T23:59:59Z","2026-10-26T00:05:00"):
            with self.subTest(value=value),self.assertRaises((ValueError,TypeError)): CHECK.deadline(value,now)

    def test_publication_or_job_history_drift_changes_final_identity(self):
        observation=values()[-1]
        original=CHECK.observation_identity(observation)
        observation["jobs"].append({"jobId":"00000000-0000-0000-0000-000000009999",
            "docPath": observation["documents"][0]["docPath"], "status":"done"})
        self.assertNotEqual(original,CHECK.observation_identity(observation))


if __name__ == "__main__":
    unittest.main()
