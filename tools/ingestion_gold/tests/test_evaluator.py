from __future__ import annotations

import unittest
import json
from pathlib import Path
from tempfile import TemporaryDirectory

from tools.ingestion_gold.canonical_evaluator import (
    evaluate_canonical_snapshot,
)
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


def _canonical_snapshot() -> dict:
    def block(
        block_id: str,
        text: str,
        order: int,
        block_type: str = "text",
        *,
        semantic_owner: str | None = None,
    ) -> dict:
        attributes = (
            {"semanticDecisionOwner": semantic_owner}
            if semantic_owner is not None
            else {}
        )
        return {
            "blockId": block_id,
            "blockType": block_type,
            "ordinal": order,
            "readingOrder": order,
            "text": {
                "raw": text,
                "canonical": text,
                "normalized": text.casefold(),
                "retrieval": text,
                "display": text,
                "rawSha256": "a" * 64,
            },
            "polygon": {
                "points": [
                    {"x": 0.1, "y": 0.1 + order * 0.05},
                    {"x": 0.4, "y": 0.1 + order * 0.05},
                    {"x": 0.4, "y": 0.13 + order * 0.05},
                    {"x": 0.1, "y": 0.13 + order * 0.05},
                ]
            },
            "isRepeatedFurniture": False,
            "qualityFlags": [],
            "provenance": {"attributes": attributes},
        }

    primary = [
        block("primary-right", "RIGHT ECHO 201", 0),
        block("primary-left", "LEFT ALPHA 101", 1),
    ]
    alternatives = [
        block(
            "alternative-left",
            "LEFT ALPHA 101",
            2,
            "native_layout_recovery",
            semantic_owner="llm_client",
        ),
        block(
            "alternative-right",
            "RIGHT ECHO 201",
            3,
            "native_layout_recovery",
            semantic_owner="llm_client",
        ),
    ]
    return {
        "schemaVersion": "ingestion_layout_canonical_snapshot_v1",
        "sourceSha256": "b" * 64,
        "document": {
            "pageCount": 1,
            "pages": [
                {
                    "pageNumber": 1,
                    "blocks": [*primary, *alternatives],
                    "tables": [],
                    "figures": [],
                }
            ],
        },
        "reconciliation": {
            "recoveredLayoutPageCount": 1,
            "skippedUndersegmentedLayoutPageCount": 0,
        },
        "retrievalChunks": [
            {
                "chunkIndex": 0,
                "pageStart": 1,
                "pageEnd": 1,
                "text": "RIGHT ECHO 201 LEFT ALPHA 101",
                "chunkType": "docling_canonical_content_v1",
                "canonicalBlockIds": [
                    "primary-right",
                    "primary-left",
                ],
            },
            {
                "chunkIndex": 1,
                "pageStart": 1,
                "pageEnd": 1,
                "text": "LEFT ALPHA 101 RIGHT ECHO 201",
                "chunkType": (
                    "docling_canonical_native_layout_alternative_v1"
                ),
                "canonicalBlockIds": [
                    "alternative-left",
                    "alternative-right",
                ],
            },
        ],
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

    def test_canonical_evaluator_accepts_recovered_alternative_surface(
        self,
    ) -> None:
        case = {
            "expectations": {
                "pageCount": 1,
                "markers": [
                    {"text": "LEFT ALPHA 101"},
                    {"text": "RIGHT ECHO 201"},
                ],
                "minimumLabels": {"text": 2},
            },
            "canonicalExpectations": {
                "alternativeOrderedMarkers": [
                    "LEFT ALPHA 101",
                    "RIGHT ECHO 201",
                ],
                "minimumAlternativeBlockCount": 2,
                "requireAllChunksAnchored": True,
            },
        }

        result = evaluate_canonical_snapshot(_canonical_snapshot(), case)

        self.assertTrue(result["ok"], result["issues"])
        self.assertEqual(
            "native_layout_page_1_1",
            result["metrics"]["selectedReadingOrderSurfaces"][
                "alternative"
            ],
        )
        self.assertEqual(0, result["metrics"]["unanchoredChunkCount"])

    def test_canonical_evaluator_rejects_unanchored_retrieval_chunk(
        self,
    ) -> None:
        snapshot = _canonical_snapshot()
        snapshot["retrievalChunks"][0]["canonicalBlockIds"] = []

        result = evaluate_canonical_snapshot(
            snapshot,
            {"expectations": {}, "canonicalExpectations": {}},
        )

        self.assertFalse(result["ok"])
        self.assertIn(
            "canonical_chunk_identity",
            {issue["check"] for issue in result["issues"]},
        )

    def test_canonical_evaluator_rejects_native_image_without_llm_owner(
        self,
    ) -> None:
        snapshot = _canonical_snapshot()
        snapshot["document"]["pages"][0]["figures"] = [
            {
                "figureId": "native-image",
                "figureType": "embedded_raster",
                "provenance": {
                    "stageId": "native_pdf_image_inventory",
                    "attributes": {},
                },
            }
        ]

        result = evaluate_canonical_snapshot(
            snapshot,
            {"expectations": {}, "canonicalExpectations": {}},
        )

        self.assertFalse(result["ok"])
        self.assertIn(
            "canonical_semantic_owner",
            {issue["check"] for issue in result["issues"]},
        )

    def test_response_directory_reports_raw_and_canonical_layers(
        self,
    ) -> None:
        case = {
            "caseId": "combined",
            "expectations": {
                "markers": [
                    {"text": "LEFT ALPHA 101"},
                    {"text": "RIGHT ECHO 201"},
                ],
            },
            "canonicalExpectations": {
                "alternativeOrderedMarkers": [
                    "LEFT ALPHA 101",
                    "RIGHT ECHO 201",
                ]
            },
        }
        with TemporaryDirectory() as directory:
            root = Path(directory)
            manifest = root / "manifest.json"
            manifest.write_text(
                json.dumps({"cases": [case]}),
                encoding="utf-8",
            )
            (root / "combined.response.json").write_text(
                json.dumps(_fixture_response()),
                encoding="utf-8",
            )
            (root / "combined.canonical.json").write_text(
                json.dumps(_canonical_snapshot()),
                encoding="utf-8",
            )

            report = evaluate_response_directory(
                manifest,
                root,
                require_canonical=True,
            )

        self.assertTrue(report["ok"], report)
        self.assertEqual("ingestion_layout_gold_report_v2", report["schemaVersion"])
        self.assertEqual(1, report["canonicalCaseCount"])
        self.assertIn("raw", report["results"][0])
        self.assertIn("canonical", report["results"][0])

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
