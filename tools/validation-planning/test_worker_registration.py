"""Pure registration evidence tests: no files, worker, SQL or HTTP client."""
import copy
import json
import unittest
import uuid
from unittest.mock import patch

import planning_worker_observation as adapter


def fixture():
    documents = []
    accepted = []
    for index in range(1, 5):
        case = "original-" + str(index)
        doc_id, job_id = str(uuid.UUID(int=index)), str(uuid.UUID(int=100 + index))
        tenant = str(uuid.UUID(int=200))
        documents.append({"docId": doc_id, "docPath": case + ".pdf", "indexedVersion": 7,
                          "ingestionVersion": 7, "sourceSha256": str(index) * 64,
                          "revisionId": str(uuid.UUID(int=300 + index)),
                          "ingestionJob": {"job_id": job_id, "tenant_id": tenant, "status": "done",
                                           "attempts": 1, "action": "upsert", "doc_path": case + ".pdf",
                                           "payload": {"docId": doc_id, "version": 7}}})
        accepted.append({"caseId": case, "jobId": job_id, "docPath": case + ".pdf", "action": "upsert",
                         "response": {"jobId": job_id, "tenantId": tenant, "docId": doc_id,
                                      "version": 7, "action": "upsert", "docPath": case + ".pdf"}})
    scan = {"mode": "sequential_explicit_enqueue", "scannerInvoked": False, "scanned": 0,
            "enqueued": 4, "hashVerifiedOriginalInputs": 4, "acceptedJobs": accepted,
            "completedCases": [row["caseId"] for row in accepted],
            "order": [row["caseId"] for row in accepted]}
    observation = {"documents": documents, "ownerRunId": str(uuid.UUID(int=500))}
    for key in ("sourceManifestSha256", "backendAssemblySha256", "profileSha256",
                "dependencyManifestSha256", "configurationSha256"):
        observation[key] = "a" * 64
    values = {"backendLog": {"synthetic": True}, "emptyBaseline": {"documents": 0}, "scanResponse": scan,
              "terminalJobs": {"items": [{"jobId": row["jobId"], "status": "done", "attempts": 1,
                                          "action": "upsert", "docPath": row["docPath"]} for row in accepted]},
              "doclingHealth": {"status": "ok", "upstream": True},
              "embeddingInfo": {"offline": True, "dimensions": 768, "model_id": "synthetic-only",
                                "model_revision": "synthetic", "model_file_sha256": "b" * 64},
              "dependencyOwnership": {"synthetic": True}}
    return observation, values


def evaluate(observation, values):
    class CapturedBytes:
        def __init__(self, data):
            self.data = data

        def read_bytes(self):
            return self.data

    captures = {name: CapturedBytes(json.dumps(value).encode("utf-8")) for name, value in values.items()}
    observation = copy.deepcopy(observation)
    observation["workerExecutionEvidence"] = {
        name: {"path": name, "sha256": adapter.planning.sha(captured.read_bytes())}
        for name, captured in captures.items()}
    with patch.object(adapter, "capture_path", side_effect=lambda name, *_: captures[name]), \
            patch.object(adapter.planning, "read_json", side_effect=lambda captured: json.loads(captured.read_bytes())):
        return adapter.verify_execution_evidence(observation)


class WorkerRegistrationTests(unittest.TestCase):
    def test_explicit_enqueue_with_four_real_job_bindings_does_not_claim_scanner_execution(self):
        observation, values = fixture()
        review = evaluate(observation, values)["registrationEvidence"]
        self.assertEqual("sequential_explicit_enqueue", review["mode"])
        self.assertIs(False, review["scannerInvoked"])
        self.assertIs(False, review["scannerExecutionValidated"])
        self.assertEqual((0, 4, 4), (review["scanned"], review["enqueued"], review["acceptedJobsBound"]))

    def test_explicit_enqueue_cannot_skip_or_rebind_an_original_job_or_version(self):
        mutations = {
            "scanner_was_used": lambda s: s.update(scannerInvoked=True),
            "false_scanned_count": lambda s: s.update(scanned=4),
            "boolean_scanned_count": lambda s: s.update(scanned=False),
            "missing_enqueue": lambda s: s.update(enqueued=3),
            "unverified_inputs": lambda s: s.update(hashVerifiedOriginalInputs=3),
            "missing_job": lambda s: s["acceptedJobs"].pop(),
            "duplicate_job": lambda s: s["acceptedJobs"].__setitem__(1, copy.deepcopy(s["acceptedJobs"][0])),
            "wrong_job": lambda s: s["acceptedJobs"][0].update(jobId=str(uuid.UUID(int=999))),
            "wrong_document": lambda s: s["acceptedJobs"][0]["response"].update(docId=str(uuid.UUID(int=999))),
            "wrong_tenant": lambda s: s["acceptedJobs"][0]["response"].update(tenantId=str(uuid.UUID(int=999))),
            "wrong_response_job": lambda s: s["acceptedJobs"][0]["response"].update(jobId=str(uuid.UUID(int=999))),
            "wrong_version": lambda s: s["acceptedJobs"][0]["response"].update(version=8),
            "wrong_action": lambda s: s["acceptedJobs"][0].update(action="delete"),
            "wrong_response_action": lambda s: s["acceptedJobs"][0]["response"].update(action="delete"),
            "wrong_path": lambda s: s["acceptedJobs"][0].update(docPath="another.pdf"),
            "wrong_response_path": lambda s: s["acceptedJobs"][0]["response"].update(docPath="another.pdf"),
            "wrong_case": lambda s: s["acceptedJobs"][0].update(caseId="another"),
            "missing_completion": lambda s: s["completedCases"].pop(),
            "duplicate_completion": lambda s: s["completedCases"].__setitem__(1, s["completedCases"][0]),
        }
        for label, mutate in mutations.items():
            with self.subTest(label=label):
                observation, values = fixture()
                mutate(values["scanResponse"])
                with self.assertRaisesRegex(ValueError, "worker_adapter_explicit_enqueue"):
                    evaluate(observation, values)

    def test_classic_scan_still_requires_all_four_scanned_and_enqueued(self):
        observation, values = fixture()
        values["scanResponse"] = {"scanned": 4, "enqueued": 4}
        review = evaluate(observation, values)["registrationEvidence"]
        self.assertEqual("scan_count_response", review["mode"])
        self.assertIs(False, review["scannerExecutionValidated"])
        for changed in ({"scanned": 0, "enqueued": 4}, {"scanned": 4, "enqueued": 3},
                        {"mode": "unknown", "scanned": 0, "enqueued": 4}):
            with self.subTest(changed=changed):
                values["scanResponse"] = changed
                with self.assertRaisesRegex(ValueError, "worker_adapter_scan_did_not_enqueue_originals"):
                    evaluate(observation, values)


if __name__ == "__main__":
    unittest.main()
