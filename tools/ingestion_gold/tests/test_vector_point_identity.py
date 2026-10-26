"""Frozen observations only: does not execute .NET, SQL, Qdrant or a worker."""
from __future__ import annotations

import copy
import importlib.util
from pathlib import Path
import unittest

SCRIPT = Path(__file__).resolve().parents[2] / "validation-ingestion/vector_point_identity.py"
SPEC = importlib.util.spec_from_file_location("saaia_vector_identity", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def observation():
    plan = "a" * 64
    return {"schemaVersion": "saaia-owned-worker-observation.v1", "documents": [{
        "docPath": "synthetic.pdf", "activeVectorPlanFingerprint": plan,
        "chunks": [{"retrievalChunkId": "11111111-2222-3333-4444-555555555555"}]}],
        "jobs": [{"docPath": "synthetic.pdf", "status": "done", "payload": {"resume": {"vectorPlanFingerprint": plan}}}]}


class PlanIdentityTests(unittest.TestCase):
    def test_map_keeps_sql_id_and_plan_scoped_point_separate(self):
        rows = MODULE.point_identity_manifest(observation())["synthetic.pdf"]
        self.assertEqual(rows, [{"canonicalChunkId": "11111111-2222-3333-4444-555555555555",
            "pointId": "e593078c-d8f0-8882-09cc-8a4979a48b33", "planFingerprint": "a" * 64}])

    def test_empty_missing_job_stale_plan_duplicate_and_non_uuid_fail_closed(self):
        for change in ("empty", "missing_job", "stale", "duplicate_scope", "duplicate_chunk", "non_uuid", "null_plan"):
            value = observation()
            if change == "empty": value["documents"] = []
            elif change == "missing_job": value["jobs"] = []
            elif change == "stale": value["documents"][0]["activeVectorPlanFingerprint"] = "b" * 64
            elif change == "duplicate_scope": value["documents"].append(copy.deepcopy(value["documents"][0]))
            elif change == "duplicate_chunk": value["documents"][0]["chunks"] *= 2
            elif change == "non_uuid": value["documents"][0]["chunks"][0]["retrievalChunkId"] = "synthetic-not-a-guid"
            else:
                value["documents"][0]["activeVectorPlanFingerprint"] = None
                value["jobs"][0]["payload"]["resume"]["vectorPlanFingerprint"] = None
            with self.subTest(change=change), self.assertRaises(ValueError):
                MODULE.point_identity_manifest(value)


if __name__ == "__main__":
    unittest.main()
