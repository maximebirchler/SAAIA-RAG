from __future__ import annotations

import unittest
from pathlib import Path
from tempfile import TemporaryDirectory

from tools.ingestion_gold.evaluate_responses import evaluate_response_directory
from tools.ingestion_gold.evaluator import evaluate_case, reading_order
from tools.ingestion_gold.structural_metrics import summarize_response


def _fixture_response() -> dict:
    return {
        "document": {
            "json_content": {
                "pages": {
                    "1": {
                        "page_no": 1,
                        "size": {"width": 600, "height": 800},
                    }
                },
                "body": {
                    "children": [
                        {"$ref": "#/texts/0"},
                        {"$ref": "#/groups/0"},
                        {"$ref": "#/tables/0"},
                    ]
                },
                "furniture": {"children": []},
                "groups": [
                    {
                        "self_ref": "#/groups/0",
                        "children": [
                            {"$ref": "#/texts/1"},
                            {"$ref": "#/texts/2"},
                        ],
                    }
                ],
                "texts": [
                    {
                        "self_ref": "#/texts/0",
                        "label": "section_header",
                        "text": "GOLD TITLE",
                        "prov": [
                            {
                                "page_no": 1,
                                "bbox": {
                                    "l": 50,
                                    "t": 760,
                                    "r": 300,
                                    "b": 720,
                                    "coord_origin": "BOTTOMLEFT",
                                },
                            }
                        ],
                    },
                    {
                        "self_ref": "#/texts/1",
                        "label": "text",
                        "text": "LEFT ALPHA 101",
                        "prov": [],
                    },
                    {
                        "self_ref": "#/texts/2",
                        "label": "text",
                        "text": "RIGHT ECHO 201",
                        "prov": [],
                    },
                ],
                "pictures": [],
                "key_value_items": [],
                "form_items": [],
                "tables": [
                    {
                        "self_ref": "#/tables/0",
                        "label": "table",
                        "prov": [],
                        "data": {
                            "num_rows": 2,
                            "num_cols": 2,
                            "table_cells": [
                                {
                                    "start_row_offset_idx": 0,
                                    "start_col_offset_idx": 0,
                                    "row_span": 1,
                                    "col_span": 2,
                                    "text": "MATRIX 731",
                                },
                                {
                                    "start_row_offset_idx": 1,
                                    "start_col_offset_idx": 0,
                                    "row_span": 1,
                                    "col_span": 1,
                                    "text": "AX-11",
                                },
                            ],
                        },
                    }
                ],
            }
        }
    }


class EvaluatorTests(unittest.TestCase):
    def test_traverses_groups_in_body_reading_order(self) -> None:
        document = _fixture_response()["document"]["json_content"]
        self.assertEqual(
            [
                "#/texts/0",
                "#/texts/1",
                "#/texts/2",
                "#/tables/0",
            ],
            [item["self_ref"] for item in reading_order(document)],
        )

    def test_evaluates_order_geometry_and_table_structure(self) -> None:
        case = {
            "expectations": {
                "pageCount": 1,
                "markers": [
                    {"text": "GOLD TITLE"},
                    {"text": "LEFT ALPHA 101"},
                    {"text": "RIGHT ECHO 201"},
                ],
                "orderedMarkers": [
                    "GOLD TITLE",
                    "LEFT ALPHA 101",
                    "RIGHT ECHO 201",
                    "MATRIX 731",
                ],
                "minimumLabels": {
                    "section_header": 1,
                    "text": 2,
                    "table": 1,
                },
                "geometry": [
                    {
                        "marker": "GOLD TITLE",
                        "centerX": 0.2917,
                        "centerY": 0.075,
                        "tolerance": 0.01,
                    }
                ],
                "tables": [
                    {
                        "containsText": "MATRIX 731",
                        "rows": 2,
                        "columns": 2,
                        "cells": [
                            {
                                "row": 0,
                                "column": 0,
                                "text": "MATRIX 731",
                                "columnSpan": 2,
                            },
                            {
                                "row": 1,
                                "column": 1,
                                "empty": True,
                            },
                        ],
                    }
                ],
            }
        }
        result = evaluate_case(_fixture_response(), case)
        self.assertTrue(result["ok"], result["issues"])

    def test_reports_reversed_reading_order(self) -> None:
        case = {
            "expectations": {
                "markers": [
                    {"text": "LEFT ALPHA 101"},
                    {"text": "RIGHT ECHO 201"},
                ],
                "orderedMarkers": [
                    "RIGHT ECHO 201",
                    "LEFT ALPHA 101",
                ],
            }
        }
        result = evaluate_case(_fixture_response(), case)
        self.assertFalse(result["ok"])
        self.assertIn(
            "reading_order",
            {issue["check"] for issue in result["issues"]},
        )

    def test_response_directory_reports_missing_case_without_stopping(self) -> None:
        with TemporaryDirectory() as directory:
            root = Path(directory)
            manifest = root / "manifest.json"
            manifest.write_text(
                '{"cases":[{"caseId":"missing","expectations":{}}]}',
                encoding="utf-8",
            )

            report = evaluate_response_directory(manifest, root)

        self.assertFalse(report["ok"])
        self.assertEqual(1, report["caseCount"])
        self.assertEqual(
            "response_file",
            report["results"][0]["issues"][0]["check"],
        )

    def test_structural_metrics_count_spans_and_column_switches(self) -> None:
        response = _fixture_response()
        response["document"]["json_content"]["texts"][1]["prov"] = [
            {
                "page_no": 1,
                "bbox": {
                    "l": 50,
                    "t": 500,
                    "r": 250,
                    "b": 450,
                    "coord_origin": "BOTTOMLEFT",
                },
            }
        ]
        response["document"]["json_content"]["texts"][2]["prov"] = [
            {
                "page_no": 1,
                "bbox": {
                    "l": 350,
                    "t": 500,
                    "r": 550,
                    "b": 450,
                    "coord_origin": "BOTTOMLEFT",
                },
            }
        ]

        metrics = summarize_response(response)

        self.assertEqual(1, metrics["pageCount"])
        self.assertEqual(4, metrics["orderedItemCount"])
        self.assertEqual(1, metrics["tables"][0]["missingCoordinates"])
        self.assertEqual(
            ["L", "R"],
            metrics["pageColumnDiagnostics"][0]["collapsedSideSequence"],
        )


if __name__ == "__main__":
    unittest.main()
