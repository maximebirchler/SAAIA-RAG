"""Source-bound acceptance annotations; imports no renderer or extraction engine."""
from __future__ import annotations

import ast
import hashlib
import json
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[3]


class LayoutSourceContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.manifest = json.loads((ROOT / "config/ingestion-acceptance-gold.v2.json").read_text(encoding="utf-8"))
        cls.cases = {case["caseId"]: case for case in cls.manifest["cases"]}
        cls.generator_bytes = (ROOT / "tools/ingestion_gold/fixtures.py").read_bytes()
        cls.functions = {node.name: node for node in ast.parse(cls.generator_bytes.decode("utf-8")).body
                         if isinstance(node, ast.FunctionDef)}

    def test_annotations_refer_to_the_frozen_generator_not_a_changed_fixture(self) -> None:
        self.assertEqual(self.manifest["rendererPins"]["generatorSources"]["fixtures.py"],
                         hashlib.sha256(self.generator_bytes).hexdigest())

    def test_structured_table_order_contract_preserves_the_literal_source_title(self) -> None:
        function = self.functions["build_structured_table"]
        title = next(node.args[1].value for node in ast.walk(function)
                     if isinstance(node, ast.Call) and isinstance(node.func, ast.Name)
                     and node.func.id == "_draw_header")
        case = self.cases["structured-table-v1"]
        self.assertIn(title, case["expectations"]["orderedMarkers"])
        self.assertIn(title, case["canonicalExpectations"]["primaryOrderedMarkers"])
        self.assertIn(title, [marker["text"] for marker in case["expectations"]["markers"]])

    def test_late_title_narratives_do_not_require_an_undrawn_table(self) -> None:
        function = self.functions["build_late_page_title_columns"]
        table_calls = [node for node in ast.walk(function) if isinstance(node, ast.Call)
                       and isinstance(node.func, ast.Name) and node.func.id == "Table"]
        self.assertEqual([], table_calls, "The frozen generator draws two narratives and a title, not a table.")
        self.assertEqual(0, self.cases["native-late-page-title-v1"]["canonicalExpectations"]
                         ["minimumLabels"].get("table", 0))

    def test_unequal_columns_are_constrained_by_source_regions_and_complete_order(self) -> None:
        case = self.cases["native-unequal-columns-v1"]
        expected = ["LONG LEFT ALPHA 401", "LONG LEFT BRAVO 402", "LONG LEFT CHARLIE 403",
                    "LONG LEFT DELTA 404", "LONG LEFT ECHO 405", "LONG LEFT FOXTROT 406",
                    "SHORT RIGHT GOLF 411", "SHORT RIGHT HOTEL 412", "SHORT RIGHT INDIA 413"]
        self.assertEqual(expected, case["canonicalExpectations"]["alternativeOrderedMarkers"])
        self.assertTrue(set(expected + ["FULL WIDTH NOTE 420"]).issubset(
            {fact["text"] for fact in case["expectations"]["criticalFacts"]}))
        geometry = {item["marker"]: item for item in case["expectations"].get("geometry", [])}
        for marker in (expected[0], expected[6]):
            self.assertIn(marker, geometry)
            self.assertEqual(1, geometry[marker]["page"])
        self.assertLess(geometry[expected[0]]["centerX"], .4)
        self.assertGreater(geometry[expected[6]]["centerX"], .5)

    def test_structured_table_shape_and_empty_merged_cells_remain_mandatory(self) -> None:
        case = self.cases["structured-table-v1"]
        table = case["expectations"]["tables"][0]
        self.assertEqual((5, 4), (table["rows"], table["columns"]))
        self.assertTrue(any(cell.get("columnSpan") == 4 for cell in table["cells"]))
        self.assertTrue(any(cell.get("empty") is True for cell in table["cells"]))
        self.assertEqual(0, case["canonicalExpectations"]["maximumAlternativeBlockCount"])


if __name__ == "__main__":
    unittest.main()
