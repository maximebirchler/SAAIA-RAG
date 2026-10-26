"""Contracts only; these synthetic examples do not execute a real PDF ingestion."""
import copy
import unittest

from tools.ingestion_gold.contracts import validate_manifest


def real_manifest():
    return {"schemaVersion": "ingestion_real_acceptance_v1", "cases": [{
        "caseId": "real-source", "sourceSha256": "a" * 64,
        "expectations": {"pageCount": 3, "criticalFacts": [{"id": "f", "page": 2, "text": "Maximum 1,5 bar"}]},
        "canonicalExpectations": {"requireProductionDefaults": True},
        "sourceAnnotations": {"kind": "source_pdf_review", "sourceSha256": "a" * 64,
                              "basis": "Independent physical-page expectations fixed before the ingestion run."}}]}


class RealSourceContractTests(unittest.TestCase):
    def test_real_source_does_not_require_or_claim_a_synthetic_renderer(self):
        manifest = real_manifest()
        self.assertEqual(1, len(validate_manifest(manifest)))
        for mutation in ("renderer", "generator", "wrong_source", "missing_basis", "missing_review"):
            broken = copy.deepcopy(manifest)
            case = broken["cases"][0]
            if mutation == "renderer":
                broken["rendererPins"] = {}
            elif mutation == "generator":
                case["generator"] = "invented"
            elif mutation == "wrong_source":
                case["sourceAnnotations"]["sourceSha256"] = "b" * 64
            elif mutation == "missing_basis":
                case["sourceAnnotations"]["basis"] = " "
            else:
                case.pop("sourceAnnotations")
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                validate_manifest(broken)

    def test_physical_facts_cannot_be_outside_the_document(self):
        for pages, fact_page in ((True, 1), (0, 1), (1, 2), (None, 1)):
            broken = real_manifest()
            case = broken["cases"][0]
            case["expectations"]["pageCount"] = pages
            case["expectations"]["criticalFacts"][0]["page"] = fact_page
            with self.subTest(pages=pages, fact_page=fact_page), self.assertRaises(ValueError):
                validate_manifest(broken)


if __name__ == "__main__":
    unittest.main()
