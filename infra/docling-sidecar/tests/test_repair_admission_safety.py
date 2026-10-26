"""Generic causal safety witnesses: no PDF, OCR model, network, or corpus oracle."""
from copy import deepcopy
import unittest

from saaia_docling.table_repair import OcrLine, repair_sparse_table


def cell(row, column, text, *, bottom=None):
    left, top = column * 40, row * 10
    return {"start_row_offset_idx": row, "start_col_offset_idx": column,
        "row_span": 1, "col_span": 1, "text": text,
        "bbox": {"l": left, "t": top, "r": left + 30,
            "b": top + 10 if bottom is None else bottom, "coord_origin": "TOPLEFT"}}


def sparse(merged="first", *, absorbed_geometry=False):
    return {"self_ref": "#/tables/0", "prov": [{"page_no": 1,
        "bbox": {"l": 0, "t": 0, "r": 600, "b": 600, "coord_origin": "TOPLEFT"}}],
        "data": {"num_rows": 3, "num_cols": 2, "table_cells": [
            cell(0, 0, "Name"), cell(0, 1, "Value"), cell(1, 0, "One"),
            cell(1, 1, merged, bottom=30 if absorbed_geometry else None), cell(2, 0, "Two")]}}


def line(text, x, y):
    return OcrLine(text, .99, ((x - 2, y - 2), (x + 2, y - 2), (x + 2, y + 2), (x - 2, y + 2)))


def repair(table, lines):
    return repair_sparse_table(table, lines, engine_version="causal-test", model_id="no-engine", duration_ms=1)


class RepairAdmissionSafetyTests(unittest.TestCase):
    def test_source_case_of_units_is_not_folded_for_repair_admission(self):
        table = sparse("m M", absorbed_geometry=True)
        result = repair(table, [line("M", 55, 15), line("m", 55, 25)])
        self.assertFalse(result.applied)
        self.assertEqual(table, result.table)

    def test_source_case_of_identifiers_is_not_folded_for_repair_admission(self):
        table = sparse("ab AB", absorbed_geometry=True)
        result = repair(table, [line("AB", 55, 15), line("ab", 55, 25)])
        self.assertFalse(result.applied)
        self.assertEqual(table, result.table)

    def test_bag_of_words_does_not_prove_an_ordered_source_composition(self):
        table = sparse("not approved approved")
        original = deepcopy(table)
        result = repair(table, [line("approved not", 55, 15), line("approved", 55, 25)])
        self.assertFalse(result.applied)
        self.assertEqual(original, result.table)

    def test_absorbed_geometry_does_not_authorize_contradictory_token_order(self):
        table = sparse("not approved approved", absorbed_geometry=True)
        original = deepcopy(table)
        result = repair(table, [line("approved not", 55, 15), line("approved", 55, 25)])
        self.assertFalse(result.applied)
        self.assertEqual(original, result.table)

    def test_far_below_the_observed_grid_cannot_fill_the_last_row(self):
        table = sparse()
        result = repair(table, [line("first", 55, 15), line("not a grid value", 55, 500)])
        self.assertFalse(result.applied)
        self.assertEqual(table, result.table)

    def test_far_right_of_the_observed_grid_cannot_fill_the_last_column(self):
        table = sparse()
        result = repair(table, [line("first", 55, 15), line("not a grid value", 500, 25)])
        self.assertFalse(result.applied)
        self.assertEqual(table, result.table)

    def test_ordered_source_retaining_composition_remains_repairable(self):
        table = sparse("not approved approved", absorbed_geometry=True)
        result = repair(table, [line("not approved", 55, 15), line("approved", 55, 25)])
        self.assertTrue(result.applied)
        self.assertEqual("not approved", next(c for c in result.table["data"]["table_cells"]
            if c["start_row_offset_idx"] == 1 and c["start_col_offset_idx"] == 1)["text"])
        self.assertEqual("not approved approved", table["data"]["table_cells"][3]["text"])

    def test_nearby_observation_can_fill_a_real_grid_hole(self):
        table = sparse()
        result = repair(table, [line("first", 55, 15), line("second", 55, 25)])
        self.assertTrue(result.applied)
        self.assertEqual(((2, 1),), result.added_coordinates)
        self.assertEqual((), result.repaired_coordinates)

    def test_distant_extra_observation_does_not_replace_a_supported_one(self):
        table = sparse()
        result = repair(table, [line("first", 55, 15), line("second", 55, 25), line("outside", 500, 500)])
        self.assertTrue(result.applied)
        target = next(c for c in result.table["data"]["table_cells"]
            if c["start_row_offset_idx"] == 2 and c["start_col_offset_idx"] == 1)
        self.assertEqual("second", target["text"])


if __name__ == "__main__":
    unittest.main()
