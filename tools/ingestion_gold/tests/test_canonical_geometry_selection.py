from __future__ import annotations

import copy
import unittest

from tools.ingestion_gold.canonical_evaluator import evaluate_canonical_snapshot


def _box(x: float, y: float, width: float = .08) -> dict:
    return {"points": [{"x": x-width/2, "y": y-.01}, {"x": x+width/2, "y": y-.01},
                       {"x": x+width/2, "y": y+.01}, {"x": x-width/2, "y": y+.01}]}


def _block(text: str, polygon: dict | None, block_type: str = "text") -> dict:
    return {"blockId": text, "blockType": block_type, "text": {"canonical": text},
            "polygon": polygon, "provenance": {"attributes": {"semanticDecisionOwner": "llm_client"}}}


def _snapshot(blocks: list[dict], tables: list[dict] | None = None) -> dict:
    return {"schemaVersion": "ingestion_layout_canonical_snapshot_v1", "retrievalChunks": [],
            "document": {"pages": [{"pageNumber": 1, "blocks": blocks, "tables": tables or [], "figures": []}]}}


def _case() -> dict:
    return {"expectations": {"markers": [{"text": "LEFT ALPHA 101"}], "geometry": [
        {"marker": "LEFT ALPHA 101", "centerX": .2, "centerY": .2, "tolerance": .02}]}}


class CanonicalGeometrySelectionTests(unittest.TestCase):
    def test_precise_observed_alternative_is_not_shadowed_by_a_merged_primary_region(self) -> None:
        snapshot = _snapshot([
            _block("LEFT ALPHA 101 and RIGHT ECHO 201 share a merged source region", _box(.5, .2, .8)),
            _block("LEFT ALPHA 101", _box(.2, .2), "native_layout_recovery"),
        ])
        result = evaluate_canonical_snapshot(snapshot, _case())
        self.assertTrue(result["ok"], result["issues"])

    def test_exact_table_cell_anchor_is_not_shadowed_by_unanchored_recovered_text(self) -> None:
        snapshot = _snapshot([_block("LEFT ALPHA 101", None, "native_text_recovery")], [{
            "tableId": "columns-observed-as-table", "polygon": _box(.5, .3, .8),
            "rowCount": 1, "columnCount": 2, "cells": [
                {"cellId": "left", "rowIndex": 0, "columnIndex": 0,
                 "text": {"canonical": "LEFT ALPHA 101"}, "polygon": _box(.2, .2)},
                {"cellId": "right", "rowIndex": 0, "columnIndex": 1,
                 "text": {"canonical": "RIGHT ECHO 201"}, "polygon": _box(.7, .2)},
            ]}])
        result = evaluate_canonical_snapshot(snapshot, _case())
        self.assertTrue(result["ok"], result["issues"])

    def test_geometry_does_not_select_a_coarse_region_because_it_matches_the_expected_center(self) -> None:
        snapshot = _snapshot([
            _block("LEFT ALPHA 101 and many other observations", _box(.2, .2, .8)),
            _block("LEFT ALPHA 101", _box(.7, .2), "native_layout_recovery"),
        ])
        result = evaluate_canonical_snapshot(snapshot, _case())
        self.assertFalse(result["ok"], "Selection must depend on source specificity, never the expected answer.")
        self.assertEqual(["canonical_geometry"], [issue["check"] for issue in result["issues"]])

    def test_unrelated_nearby_geometry_cannot_anchor_a_marker_without_a_polygon(self) -> None:
        snapshot = _snapshot([_block("LEFT ALPHA 101", None), _block("UNRELATED SUBJECT 987", _box(.2, .2))])
        result = evaluate_canonical_snapshot(snapshot, _case())
        self.assertFalse(result["ok"])
        self.assertIn("canonical_geometry", {issue["check"] for issue in result["issues"]})

    def test_geometry_constrained_to_a_page_cannot_borrow_the_same_marker_on_another_page(self) -> None:
        snapshot = _snapshot([_block("LEFT ALPHA 101", _box(.7, .2))])
        other_page = copy.deepcopy(snapshot["document"]["pages"][0])
        other_page["pageNumber"] = 2
        other_page["blocks"] = [_block("LEFT ALPHA 101", _box(.2, .2))]
        snapshot["document"]["pages"].insert(0, other_page)
        case = _case()
        case["expectations"]["geometry"][0]["page"] = 1
        result = evaluate_canonical_snapshot(snapshot, case)
        self.assertFalse(result["ok"])
        self.assertIn("canonical_geometry", {issue["check"] for issue in result["issues"]})

    def test_ordinary_precise_primary_geometry_remains_valid(self) -> None:
        result = evaluate_canonical_snapshot(_snapshot([_block("LEFT ALPHA 101", _box(.2, .2))]), _case())
        self.assertTrue(result["ok"], result["issues"])


if __name__ == "__main__":
    unittest.main()
