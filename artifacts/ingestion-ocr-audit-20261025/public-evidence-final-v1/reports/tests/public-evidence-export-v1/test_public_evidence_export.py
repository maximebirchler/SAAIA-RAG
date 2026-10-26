"""Synthetic-only regression tests; no real evidence, services or credential store."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import uuid
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
SCRIPT = HERE.parents[1] / "prepare-public-evidence.py"
spec = importlib.util.spec_from_file_location("public_export", SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
NS = module.NS
OPAQUE = "SYNTHETIC_OPAQUE_CREDENTIAL_7DA460"
NAME = 'Fixture.Tests.Export_theory(token: "' + OPAQUE + '", count: 7)'
IDS = [str(uuid.UUID(int=index)) for index in range(1, 6)]


def trx(name=NAME):
    root = ET.Element("{" + NS["t"] + "}TestRun", {"id": IDS[0], "name": "local-user synthetic-run"})
    q = lambda n: "{" + NS["t"] + "}" + n
    results = ET.SubElement(root, q("Results"))
    result = ET.SubElement(results, q("UnitTestResult"), {"executionId": IDS[1], "testId": IDS[2],
        "testName": name, "outcome": "Passed", "duration": "00:00:00.001", "computerName": "SYNTHETIC_MACHINE"})
    ET.SubElement(result, q("Output")).text = "SYNTHETIC_STDOUT sk-proj-" + "A" * 24
    definitions = ET.SubElement(root, q("TestDefinitions"))
    test = ET.SubElement(definitions, q("UnitTest"), {"name": name, "id": IDS[2], "storage": "C:/synthetic/user/Test.dll"})
    ET.SubElement(test, q("Execution"), {"id": IDS[1]})
    ET.SubElement(test, q("TestMethod"), {"className": "Fixture.Tests", "name": "Export_theory", "codeBase": "C:/synthetic/user/Test.dll"})
    ET.SubElement(root, q("SyntheticEnvironment")).text = "SYNTHETIC_ENVIRONMENT_UNEXPECTED"
    ET.SubElement(result, q("SyntheticExtra")).text = "SYNTHETIC_EXTRA_UNEXPECTED"
    return ET.tostring(root, encoding="utf-8")


class PublicEvidenceExportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="synthetic-", dir=HERE)
        self.root = Path(self.temp.name).resolve()
        assert self.root.is_relative_to(HERE.resolve())
        self.repo = self.root / "repo"
        self.campaign = self.repo / module.CAMPAIGN
        self.run = self.campaign / "tests" / "unit"
        self.run.mkdir(parents=True)

    def tearDown(self):
        self.temp.cleanup()

    def proposal(self, project="Backend", inventory="inventory.json"):
        raw = trx(); (self.run / "result.trx").write_bytes(raw)
        inv = b"[]"; inv_path = (self.run / inventory).resolve()
        assert inv_path.is_relative_to(self.campaign.resolve())
        inv_path.parent.mkdir(parents=True, exist_ok=True); inv_path.write_bytes(inv)
        capture = {"inventory": inventory, "fileCount": 0, "sha256": module.sha(inv)}
        after = {"inventory": "inventory-after.json", "fileCount": 0, "sha256": module.sha(inv)}
        (self.run / "inventory-after.json").write_bytes(inv)
        manifest = {"schemaVersion": "saaia-test-campaign-v1", "runId": "synthetic", "finishedAtUtc": "2026-10-26T00:00:00Z",
            "state": "passed", "profile": "Unit", "buildRequested": True, "sourceChangedDuringRun": False,
            "sourceBefore": capture, "sourceAfter": after,
            "environment": {"syntheticSecret": OPAQUE}, "prerequisites": [OPAQUE],
            "results": [{"project": project, "path": "backend/Test.csproj", "status": "passed", "exitCode": 0,
                "counts": {"passed": 1, "failed": 0, "notExecuted": 0, "total": 1}, "assemblies": [],
                "cases": [{"name": NAME, "outcome": "Passed", "duration": "00:00:00.001", "syntheticCapturedOutput": OPAQUE}],
                "trx": "result.trx", "trxSha256": module.sha(raw)}]}
        path = self.run / "manifest.json"; data = module.public_json(manifest); path.write_bytes(data)
        return {"schemaVersion": "saaia-ingestion-public-evidence-proposal.v1", "publicFiles": [],
                "testCampaigns": [{"manifestPath": path.relative_to(self.repo).as_posix(), "sha256": module.sha(data), "label": "unit"}]}

    def test_trx_names_drop_arguments_keep_identity_outcome_and_original_name_hash(self):
        original = trx(); public, metadata = module.project_trx(original)
        self.assertNotIn(OPAQUE.encode(), public)
        self.assertNotIn(b"SYNTHETIC_STDOUT", public)
        root = ET.fromstring(public)
        result = root.find(".//t:UnitTestResult", NS)
        definition = root.find(".//t:UnitTest", NS)
        for item, attr in [(result, "testName"), (definition, "name")]:
            self.assertEqual("Fixture.Tests.Export_theory", item.attrib[attr])
            self.assertEqual(module.sha(NAME.encode()), item.attrib["originalTestNameSha256"])
        self.assertEqual(IDS[1], result.attrib["executionId"])
        self.assertEqual(IDS[2], definition.attrib["id"])
        self.assertEqual({"Passed": 1}, metadata["outcomeCounts"])
        self.assertFalse(metadata["projectionIsOriginalTrx"])

    def test_trx_unknown_payload_nodes_are_not_public(self):
        public, _ = module.project_trx(trx("Fixture.Tests.Export_theory"))
        self.assertNotIn(b"SYNTHETIC_ENVIRONMENT_UNEXPECTED", public)
        self.assertNotIn(b"SYNTHETIC_EXTRA_UNEXPECTED", public)

    def test_export_manifest_cases_drop_arguments_and_unlisted_captured_fields(self):
        output = self.campaign / "public-output"
        proposal = self.proposal()
        report = module.export(self.repo, proposal, output, proposal_sha256="a" * 64)
        for path in output.rglob("*"):
            if path.is_file(): self.assertNotIn(OPAQUE.encode(), path.read_bytes())
        case = json.loads((output / "unit/manifest.public.json").read_text())["results"][0]["cases"][0]
        self.assertEqual("Fixture.Tests.Export_theory", case["name"])
        self.assertEqual(module.sha(NAME.encode()), case["originalTestNameSha256"])
        self.assertNotIn("syntheticCapturedOutput", case)
        selected = next(item for item in report["files"] if item["path"].endswith("Backend.public.trx"))
        self.assertEqual(module.sha(trx()), selected["originalSha256"])

    def test_distinct_theory_arguments_keep_distinct_hashes(self):
        first = ET.fromstring(module.project_trx(trx('Fixture.Tests.Export_theory(value: "FIRST")'))[0]).find(".//t:UnitTestResult", NS)
        second = ET.fromstring(module.project_trx(trx('Fixture.Tests.Export_theory(value: "SECOND")'))[0]).find(".//t:UnitTestResult", NS)
        self.assertEqual(first.attrib["testName"], second.attrib["testName"])
        self.assertNotEqual(first.attrib["originalTestNameSha256"], second.attrib["originalTestNameSha256"])

    def test_report_containing_synthetic_api_key_is_refused(self):
        path = self.campaign / "unsafe.md"; raw = ("synthetic only: sk-proj-" + "X" * 24).encode(); path.write_bytes(raw)
        proposal = {"schemaVersion": "saaia-ingestion-public-evidence-proposal.v1", "testCampaigns": [],
            "publicFiles": [{"path": path.relative_to(self.repo).as_posix(), "sha256": module.sha(raw), "reason": "synthetic"}]}
        with self.assertRaises(ValueError): module.export(self.repo, proposal, self.campaign / "key-rejected", proposal_sha256="a" * 64)
        self.assertFalse((self.campaign / "key-rejected/reports/unsafe.md").exists())

    def test_project_name_traversal_is_refused_before_external_write(self):
        proposal = self.proposal(project="../../escaped")
        with self.assertRaises(ValueError): module.export(self.repo, proposal, self.campaign / "project-rejected", proposal_sha256="a" * 64)
        self.assertFalse((self.campaign / "escaped.public.trx").exists())

    def test_inventory_destination_traversal_is_refused(self):
        proposal = self.proposal(inventory="../../inventory.json")
        existing = self.campaign / "inventory.json"
        original = existing.read_bytes()
        with self.assertRaises(ValueError): module.export(self.repo, proposal, self.campaign / "inventory-rejected", proposal_sha256="a" * 64)
        self.assertEqual(original, existing.read_bytes())

    def test_source_parent_traversal_is_refused(self):
        file = self.campaign / "safe.md"; file.write_text("synthetic")
        with self.assertRaises(ValueError): module.safe_path(self.repo, module.CAMPAIGN + "/tests/../safe.md")

    def test_execution_and_test_list_ids_remain_exact(self):
        original = ET.fromstring(trx())
        q = lambda name: "{" + NS["t"] + "}" + name
        source_result = original.find(".//t:UnitTestResult", NS)
        source_result.set("testListId", IDS[3])
        entries = ET.SubElement(original, q("TestEntries"))
        ET.SubElement(entries, q("TestEntry"), {"testId": IDS[2], "executionId": IDS[1], "testListId": IDS[3]})
        lists = ET.SubElement(original, q("TestLists"))
        ET.SubElement(lists, q("TestList"), {"id": IDS[3], "name": OPAQUE})
        public, _ = module.project_trx(ET.tostring(original))
        root = ET.fromstring(public)
        self.assertEqual(IDS[0], root.attrib["id"])
        self.assertEqual(IDS[1], root.find(".//t:Execution", NS).attrib["id"])
        self.assertEqual(IDS[3], root.find(".//t:TestList", NS).attrib["id"])
        self.assertEqual({"testId": IDS[2], "executionId": IDS[1], "testListId": IDS[3]}, root.find(".//t:TestEntry", NS).attrib)
        self.assertNotIn(OPAQUE.encode(), public)

    def test_key_shaped_test_argument_is_removed_before_public_secret_scan(self):
        raw = trx('Fixture.Tests.Export_theory(apiKey: "sk-proj-' + 'Z' * 24 + '")')
        public, _ = module.project_trx(raw)
        module.scan(public)
        self.assertNotIn(b'sk-proj-', public)

    def test_path_windows_and_parent_forms_are_refused(self):
        for path in ("C:/synthetic/data.json", "../escape.json", "a/../../escape.json", "a\\..\\escape.json", "/absolute.json"):
            with self.subTest(path=path), self.assertRaises(ValueError): module.relative_path(path)

    def test_frozen_hash_change_is_refused(self):
        proposal = self.proposal(); (self.run / "result.trx").write_bytes(b"changed")
        with self.assertRaises(ValueError): module.export(self.repo, proposal, self.campaign / "hash-rejected", proposal_sha256="a" * 64)


if __name__ == "__main__": unittest.main(verbosity=2)
