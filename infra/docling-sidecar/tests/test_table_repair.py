from __future__ import annotations

import unittest

from saaia_docling.table_repair import OcrLine, repair_sparse_table


class TableRepairTests(unittest.TestCase):
    def test_repairs_missing_cells_and_splits_absorbed_neighbor_text(self) -> None:
        table = _reference_table()
        result = repair_sparse_table(
            table,
            _reference_ocr_lines(),
            engine_version="3.9.1",
            model_id="PP-OCRv6",
            duration_ms=727,
        )

        self.assertTrue(result.applied)
        self.assertEqual(((2, 1), (2, 2)), result.missing_coordinates)
        self.assertEqual(((1, 1), (1, 2)), result.repaired_coordinates)
        self.assertEqual(((2, 1), (2, 2)), result.added_coordinates)
        cells = {
            (
                cell["start_row_offset_idx"],
                cell["start_col_offset_idx"],
            ): cell
            for cell in result.table["data"]["table_cells"]
        }
        self.assertEqual(9, len(cells))
        self.assertEqual("Rd 40-6", cells[(1, 1)]["text"])
        self.assertEqual("Rd 40-6 Rd 48-6", cells[(1, 1)]["saaia_original_text"])
        self.assertEqual("Rd 48-6", cells[(2, 1)]["text"])
        self.assertEqual("Rd 48-6", cells[(2, 1)]["saaia_original_text"])
        self.assertEqual("Thread D", cells[(0, 1)]["text"])
        self.assertNotIn("saaia_text_repair", cells[(0, 1)])
        self.assertEqual(
            "saaia_table_text_repair_v1",
            result.table["saaia_table_repair"]["schema_version"],
        )

    def test_keeps_complete_table_unchanged(self) -> None:
        table = _reference_table()
        table["data"]["table_cells"].extend(
            [
                _cell(2, 1, "Rd 48-6", 40, 20, 70, 30),
                _cell(2, 2, "37", 80, 20, 100, 30),
            ]
        )

        result = repair_sparse_table(
            table,
            _reference_ocr_lines(),
            engine_version="3.9.1",
            model_id="PP-OCRv6",
            duration_ms=10,
        )

        self.assertFalse(result.applied)
        self.assertEqual("grid_complete", result.reason)
        self.assertEqual(table, result.table)

    def test_fails_open_when_ocr_does_not_cover_every_gap(self) -> None:
        table = _reference_table()
        incomplete = [
            line
            for line in _reference_ocr_lines()
            if line.text != "Rd 48-6"
        ]

        result = repair_sparse_table(
            table,
            incomplete,
            engine_version="3.9.1",
            model_id="PP-OCRv6",
            duration_ms=10,
        )

        self.assertFalse(result.applied)
        self.assertEqual("missing_cells_not_covered_by_ocr", result.reason)
        self.assertEqual(((2, 1), (2, 2)), result.missing_coordinates)
        self.assertEqual(table, result.table)

    def test_rejects_overlapping_source_grid(self) -> None:
        table = _reference_table()
        table["data"]["table_cells"].append(
            _cell(1, 0, "duplicate", 0, 10, 30, 20)
        )

        result = repair_sparse_table(
            table,
            _reference_ocr_lines(),
            engine_version="3.9.1",
            model_id="PP-OCRv6",
            duration_ms=10,
        )

        self.assertFalse(result.applied)
        self.assertEqual("overlapping_grid_not_repaired", result.reason)


def _reference_table() -> dict:
    return {
        "self_ref": "#/tables/0",
        "data": {
            "num_rows": 3,
            "num_cols": 3,
            "table_cells": [
                _cell(0, 0, "Size", 0, 0, 30, 10, column_header=True),
                _cell(0, 1, "Thread D", 40, 0, 70, 10, column_header=True),
                _cell(0, 2, "(1)", 80, 0, 100, 10, column_header=True),
                _cell(1, 0, "25", 0, 10, 30, 20),
                _cell(1, 1, "Rd 40-6 Rd 48-6", 40, 10, 70, 30),
                _cell(1, 2, "37 37", 80, 10, 100, 30),
                _cell(2, 0, "32", 0, 20, 30, 30),
            ],
        },
    }


def _reference_ocr_lines() -> list[OcrLine]:
    return [
        _line("Size", 15, 5),
        _line("Thread D", 55, 5),
        _line("(1)", 90, 5),
        _line("25", 15, 15),
        _line("Rd 40-6", 55, 15),
        _line("37", 90, 15),
        _line("32", 15, 25),
        _line("Rd 48-6", 55, 25),
        _line("37", 90, 25),
    ]


def _cell(
    row: int,
    column: int,
    text: str,
    left: float,
    top: float,
    right: float,
    bottom: float,
    *,
    column_header: bool = False,
) -> dict:
    return {
        "bbox": {
            "l": left,
            "t": top,
            "r": right,
            "b": bottom,
            "coord_origin": "TOPLEFT",
        },
        "row_span": 1,
        "col_span": 1,
        "start_row_offset_idx": row,
        "start_col_offset_idx": column,
        "text": text,
        "column_header": column_header,
        "row_header": False,
        "row_section": False,
    }


def _line(text: str, center_x: float, center_y: float) -> OcrLine:
    return OcrLine(
        text=text,
        score=0.99,
        polygon=(
            (center_x - 2, center_y - 2),
            (center_x + 2, center_y - 2),
            (center_x + 2, center_y + 2),
            (center_x - 2, center_y + 2),
        ),
    )


if __name__ == "__main__":
    unittest.main()
