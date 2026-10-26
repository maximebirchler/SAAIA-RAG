from __future__ import annotations

import copy
import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import patch

from tools.ingestion_gold import runner
from tools.ingestion_gold.canonical_evaluator import evaluate_canonical_snapshot
from tools.ingestion_gold.contracts import campaign_report, select_cases, validate_manifest
from tools.ingestion_gold.critical_facts import critical_fact_issues
from tools.ingestion_gold.evaluator import evaluate_case
from tools.ingestion_gold.fixtures import generate_fixtures
from tools.ingestion_gold.renderer import pinned_renderer, renderer_identity


def response(text: str, page: int = 1) -> dict:
    return {"document": {"json_content": {
        "pages": {str(page): {"page_no": page}},
        "body": {"children": [{"$ref": "#/texts/0"}]},
        "texts": [{"self_ref": "#/texts/0", "label": "text", "text": text,
                   "prov": [{"page_no": page}]}], "tables": []}}}


def snapshot(text: str, page: int = 1) -> dict:
    return {"schemaVersion": "ingestion_layout_canonical_snapshot_v1", "sourceSha256": "a" * 64,
            "backendAssemblySha256": "b" * 64, "projectorAssemblySha256": "c" * 64,
            "chunking": {"maxWords": 200, "minWords": 25, "matchesProductionDefaults": True},
            "document": {"pages": [{"pageNumber": page, "blocks": [{
                "blockId": "b", "blockType": "text", "readingOrder": 0, "text": {"raw": text},
            }], "tables": [], "figures": []}]},
            "retrievalChunks": [{"chunkIndex": 0, "pageStart": page, "pageEnd": page,
                                 "text": text, "canonicalBlockIds": ["b"]}]}


def fact_case(text: str, page: int = 1) -> dict:
    return {"sourceSha256": "a" * 64,
            "expectations": {"criticalFacts": [{"id": "fact", "page": page, "text": text}]},
            "canonicalExpectations": {"requireProductionDefaults": True}}


class CriticalFactTests(unittest.TestCase):
    def test_exact_facts_preserve_sign_decimal_negation_and_unicode(self):
        pairs = [("Pression : 1,5 bar.", "Pression : 15 bar."),
                 ("Maximum 1.5 bar", "Maximum 15 bar"),
                 ("Limite : -5 °C.", "Limite : +5 °C."),
                 ("Ne pas ouvrir sous tension.", "Ouvrir sous tension."),
                 ("Capacité : ½ litre.", "Capacité : 1 litre.")]
        for expected, altered in pairs:
            with self.subTest(expected=expected):
                case = fact_case(expected)
                self.assertTrue(evaluate_case(response(expected), case)["ok"])
                self.assertTrue(evaluate_canonical_snapshot(snapshot(expected), case)["ok"])
                self.assertFalse(evaluate_case(response(altered), case)["ok"])
                result = evaluate_canonical_snapshot(snapshot(altered), case)
                self.assertFalse(result["ok"])
                self.assertIn("critical_fact", {item["check"] for item in result["issues"]})

    def test_neighbor_page_cannot_supply_missing_physical_fact(self):
        case = fact_case("Pour 4 opérateurs", 1)
        actual = snapshot("Pour 4 opérateurs", 2)
        actual["retrievalChunks"][0]["pageStart"] = 1  # Whole chunk overlaps both pages.
        result = evaluate_canonical_snapshot(actual, case)
        self.assertFalse(result["ok"])
        self.assertIn("critical_fact", {item["check"] for item in result["issues"]})

    def test_fact_in_source_without_retrieval_is_not_canonical_success(self):
        actual = snapshot("Ne pas ouvrir sous tension.")
        actual["retrievalChunks"][0]["text"] = "Ouvrir sous tension."
        result = evaluate_canonical_snapshot(actual, fact_case("Ne pas ouvrir sous tension."))
        self.assertFalse(result["ok"])
        self.assertIn("critical_retrieval_fact", {item["check"] for item in result["issues"]})

    def test_only_nfc_and_whitespace_variations_are_accepted(self):
        facts = [{"id": "f", "page": 1, "text": "État : OFF"}]
        self.assertEqual([], critical_fact_issues(facts, {1: ["E\u0301tat :\n OFF"]}))
        self.assertNotEqual([], critical_fact_issues(facts, {1: ["Etat : OFF"]}))
        self.assertNotEqual([], critical_fact_issues(facts, {1: ["État : ON"]}))

    def test_malformed_or_duplicate_annotations_fail_closed(self):
        for annotations in ([{"id": "f", "page": True, "text": "value"}],
                            [{"id": "f", "page": 1, "text": ""}],
                            [{"id": "f", "page": 1, "text": "x"}] * 2, ["not-an-object"]):
            with self.subTest(annotations=annotations), self.assertRaises(ValueError):
                critical_fact_issues(annotations, {})

    def test_source_and_loaded_assemblies_and_production_defaults_are_required(self):
        case = fact_case("Reliable source")
        for field, value, issue in (("sourceSha256", "d" * 64, "canonical_source_hash"),
                                    ("backendAssemblySha256", "", "canonical_assembly_identity"),
                                    ("projectorAssemblySha256", None, "canonical_assembly_identity"),
                                    ("chunking", {"matchesProductionDefaults": False}, "canonical_chunking")):
            actual = snapshot("Reliable source")
            actual[field] = value
            with self.subTest(field=field):
                result = evaluate_canonical_snapshot(actual, case)
                self.assertFalse(result["ok"])
                self.assertIn(issue, {item["check"] for item in result["issues"]})


class CampaignContractTests(unittest.TestCase):
    def test_empty_missing_duplicate_cases_and_unknown_schema_are_rejected(self):
        case = {"caseId": "one", "expectations": {}}
        for manifest in ({"schemaVersion": "ingestion_layout_gold_v1", "cases": []},
                         {"cases": [case]},
                         {"schemaVersion": "ingestion_layout_gold_v1", "cases": [case, case]}):
            with self.subTest(manifest=manifest), self.assertRaises(ValueError):
                validate_manifest(manifest)
        for selected in (["missing"], ["one", "one"]):
            with self.subTest(selected=selected), self.assertRaises(ValueError):
                select_cases([case], selected)

    def test_zero_partial_duplicate_and_nonboolean_results_cannot_pass(self):
        for results, ids in (([], []), ([], ["one"]),
                             ([{"caseId": "one", "ok": True}] * 2, ["one"]),
                             ([{"caseId": "one", "ok": "true"}], ["one"])):
            with self.subTest(results=results):
                self.assertFalse(campaign_report(results, ids, canonical_required=True, renderer_pinned=True)["ok"])

    def test_skipped_canonical_unpinned_or_failed_canonical_cannot_pass(self):
        result = {"caseId": "one", "ok": True, "canonical": {"ok": True}}
        self.assertTrue(campaign_report([result], ["one"], canonical_required=True, renderer_pinned=True)["ok"])
        for canonical_required, renderer_pinned in ((False, True), (True, False)):
            report = campaign_report([result], ["one"], canonical_required=canonical_required,
                                     renderer_pinned=renderer_pinned)
            self.assertTrue(report["diagnosticOk"])
            self.assertFalse(report["qualificationComplete"])
        failed = copy.deepcopy(result)
        failed["canonical"]["ok"] = False
        self.assertFalse(campaign_report([failed], ["one"], canonical_required=True, renderer_pinned=True)["ok"])

    def test_projection_never_claims_worker_sql_or_vector_validation(self):
        report = campaign_report([{"caseId": "one", "ok": True, "canonical": {"ok": True}}],
                                 ["one"], canonical_required=True, renderer_pinned=True)
        self.assertTrue(report["ok"])
        self.assertFalse(report["ingestionWorkerExecuted"])
        self.assertFalse(report["sqlPublicationValidated"])
        self.assertFalse(report["vectorValidated"])

    def test_acceptance_manifest_requires_source_identity(self):
        manifest = {"schemaVersion": "ingestion_acceptance_gold_v2", "rendererPins": {},
                    "cases": [{"caseId": "one", "expectations": {}, "canonicalExpectations": {}}]}
        with self.assertRaises(ValueError):
            validate_manifest(manifest)

    def test_repository_acceptance_manifest_has_complete_independent_contracts(self):
        path = Path(__file__).resolve().parents[3] / "config/ingestion-acceptance-gold.v2.json"
        manifest = json.loads(path.read_text(encoding="utf-8"))
        cases = validate_manifest(manifest)
        self.assertEqual(12, len(cases))
        for field, value in (("criticalFacts", []),):
            broken = copy.deepcopy(manifest)
            broken["cases"][0]["expectations"][field] = value
            with self.assertRaises(ValueError):
                validate_manifest(broken)
        broken = copy.deepcopy(manifest)
        broken["cases"][0]["canonicalExpectations"]["requireProductionDefaults"] = False
        with self.assertRaises(ValueError):
            validate_manifest(broken)
        broken = copy.deepcopy(manifest)
        broken["rendererPins"]["generatorSources"].pop("fixtures_fr.py")
        with self.assertRaises(ValueError):
            validate_manifest(broken)

    def test_unknown_case_and_missing_projector_stop_before_transmission(self):
        with TemporaryDirectory() as directory:
            root = Path(directory)
            manifest = root / "manifest.json"
            manifest.write_text(json.dumps({"schemaVersion": "ingestion_layout_gold_v1",
                "cases": [{"caseId": "one", "expectations": {}}]}), encoding="utf-8")
            args = ["gold", "--manifest", str(manifest), "--base-url", "http://127.0.0.1:59999",
                    "--output", str(root / "new" / "report.json")]
            for extra in (["--case-id", "missing"], []):
                with self.subTest(extra=extra), patch("sys.argv", args + extra), \
                        patch.object(runner, "_post_local") as post, \
                        patch.object(runner, "generate_fixtures") as generate:
                    with self.assertRaises(ValueError):
                        runner.main()
                    post.assert_not_called()
                    generate.assert_not_called()


class RendererPinTests(unittest.TestCase):
    def test_dependency_version_drift_is_rejected(self):
        with TemporaryDirectory() as directory:
            font = Path(directory) / "font.ttf"
            font.write_bytes(b"hash-only")
            pins = renderer_identity(font)
            pins["pillowVersion"] = "different-version"
            with self.assertRaises(ValueError), pinned_renderer(pins, font):
                self.fail("A renderer version mismatch must reject entry.")

    def test_missing_font_or_pin_drift_fails_before_pdf_creation(self):
        with TemporaryDirectory() as directory:
            root = Path(directory)
            font = root / "font.ttf"
            font.write_bytes(b"fake-font-used-only-to-test-hashing")
            pins = renderer_identity(font)
            pins["fontSha256"] = "0" * 64
            manifest = root / "manifest.json"
            manifest.write_text(json.dumps({"schemaVersion": "ingestion_acceptance_gold_v2", "rendererPins": pins,
                "cases": [{"caseId": "one", "generator": "two_column_order", "sourceSha256": "a" * 64,
                           "expectations": {"criticalFacts": [{"id": "f", "page": 1, "text": "value"}]},
                           "canonicalExpectations": {"requireProductionDefaults": True}}]}), encoding="utf-8")
            with self.assertRaises(ValueError):
                generate_fixtures(manifest, root / "sources", font)
            self.assertEqual([], list((root / "sources").glob("*.pdf")))
            with self.assertRaises(ValueError), pinned_renderer(pins, None):
                self.fail("A missing pinned font must reject entry.")


if __name__ == "__main__":
    unittest.main()
