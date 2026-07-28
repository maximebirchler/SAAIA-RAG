from __future__ import annotations

import difflib
import math
import re
import unicodedata
from dataclasses import dataclass
from typing import Any, Iterable


NATIVE_LAYOUT_BLOCK_TYPE = "native_layout_recovery"
NATIVE_LAYOUT_CHUNK_TYPE = "docling_canonical_native_layout_alternative_v1"


@dataclass(frozen=True)
class CanonicalEvaluationIssue:
    check: str
    message: str


def _normalise(value: Any) -> str:
    text = unicodedata.normalize("NFKC", str(value or ""))
    return " ".join(
        re.sub(r"[^\w]+", " ", text.casefold(), flags=re.UNICODE).split()
    )


def _text_variant(item: dict[str, Any]) -> str:
    text = item.get("text")
    if isinstance(text, dict):
        for name in ("retrieval", "canonical", "display", "raw"):
            value = text.get(name)
            if value:
                return str(value)
        return ""
    return str(text or "")


def _table_text(table: dict[str, Any]) -> str:
    cells = table.get("cells")
    if not isinstance(cells, list):
        return ""
    ordered = sorted(
        (cell for cell in cells if isinstance(cell, dict)),
        key=lambda cell: (
            int(cell.get("rowIndex", 0)),
            int(cell.get("columnIndex", 0)),
        ),
    )
    return " ".join(_text_variant(cell) for cell in ordered)


def _polygon_center(item: dict[str, Any]) -> tuple[float, float] | None:
    polygon = item.get("polygon")
    points = polygon.get("points") if isinstance(polygon, dict) else None
    if not isinstance(points, list) or not points:
        return None
    xs = [
        float(point["x"])
        for point in points
        if isinstance(point, dict) and "x" in point
    ]
    ys = [
        float(point["y"])
        for point in points
        if isinstance(point, dict) and "y" in point
    ]
    if not xs or not ys:
        return None
    return sum(xs) / len(xs), sum(ys) / len(ys)


def _best_item(
    marker: str,
    items: Iterable[dict[str, Any]],
) -> tuple[float, dict[str, Any] | None]:
    normalised_marker = _normalise(marker)
    best_score = 0.0
    best_item: dict[str, Any] | None = None
    for item in items:
        text = _normalise(_text_variant(item))
        if not text:
            continue
        if normalised_marker in text:
            return 1.0, item
        score = difflib.SequenceMatcher(
            None,
            normalised_marker,
            text,
        ).ratio()
        if score > best_score:
            best_score = score
            best_item = item
    return best_score, best_item


def _surface_text(items: Iterable[dict[str, Any]]) -> str:
    return _normalise(" ".join(_text_variant(item) for item in items))


def _ordered_markers_in_surface(
    markers: Iterable[Any],
    surface: str,
) -> tuple[bool, str]:
    cursor = 0
    for raw_marker in markers:
        marker = _normalise(raw_marker)
        if not marker:
            continue
        index = surface.find(marker, cursor)
        if index < 0:
            return False, f"Marker '{raw_marker}' is missing or out of order."
        cursor = index + len(marker)
    return True, ""


def _group_alternative_chunks(
    chunks: Iterable[dict[str, Any]],
) -> list[tuple[str, list[dict[str, Any]]]]:
    by_page: dict[tuple[int, int], list[dict[str, Any]]] = {}
    for chunk in chunks:
        if str(chunk.get("chunkType") or "") != NATIVE_LAYOUT_CHUNK_TYPE:
            continue
        key = (
            int(chunk.get("pageStart", 0)),
            int(chunk.get("pageEnd", 0)),
        )
        by_page.setdefault(key, []).append(chunk)
    return [
        (
            f"native_layout_page_{page_start}_{page_end}",
            sorted(
                page_chunks,
                key=lambda chunk: int(chunk.get("chunkIndex", 0)),
            ),
        )
        for (page_start, page_end), page_chunks in sorted(by_page.items())
    ]


def _check_order(
    issues: list[CanonicalEvaluationIssue],
    check: str,
    markers: list[Any],
    surfaces: list[tuple[str, list[dict[str, Any]]]],
) -> str | None:
    if not markers:
        return None
    failures: list[str] = []
    for surface_name, items in surfaces:
        ok, detail = _ordered_markers_in_surface(
            markers,
            _surface_text(items),
        )
        if ok:
            return surface_name
        failures.append(f"{surface_name}: {detail}")
    issues.append(
        CanonicalEvaluationIssue(
            check,
            "No canonical surface preserves the expected sequence. "
            + " | ".join(failures),
        )
    )
    return None


def _chunk_has_evidence_identity(chunk: dict[str, Any]) -> bool:
    for name in (
        "canonicalBlockIds",
        "canonicalSpanIds",
        "canonicalTableCellIds",
    ):
        values = chunk.get(name)
        if isinstance(values, list) and any(str(value).strip() for value in values):
            return True
    return False


def evaluate_canonical_snapshot(
    snapshot: dict[str, Any],
    case: dict[str, Any],
) -> dict[str, Any]:
    issues: list[CanonicalEvaluationIssue] = []
    if snapshot.get("schemaVersion") != "ingestion_layout_canonical_snapshot_v1":
        return {
            "ok": False,
            "issues": [
                {
                    "check": "canonical_schema",
                    "message": "Unsupported or missing canonical snapshot schema.",
                }
            ],
            "metrics": {},
        }

    document = snapshot.get("document")
    if not isinstance(document, dict):
        return {
            "ok": False,
            "issues": [
                {
                    "check": "canonical_document",
                    "message": "Canonical snapshot has no document.",
                }
            ],
            "metrics": {},
        }

    pages = document.get("pages")
    if not isinstance(pages, list):
        pages = []
    blocks: list[dict[str, Any]] = []
    tables: list[dict[str, Any]] = []
    figures: list[dict[str, Any]] = []
    for page in pages:
        if not isinstance(page, dict):
            continue
        page_number = int(page.get("pageNumber", 0))
        for block in page.get("blocks") or []:
            if isinstance(block, dict):
                block["_goldPageNumber"] = page_number
                blocks.append(block)
        for table in page.get("tables") or []:
            if isinstance(table, dict):
                table["_goldPageNumber"] = page_number
                tables.append(table)
        for figure in page.get("figures") or []:
            if isinstance(figure, dict):
                figure["_goldPageNumber"] = page_number
                figures.append(figure)

    primary_blocks = sorted(
        (
            block
            for block in blocks
            if str(block.get("blockType") or "") != NATIVE_LAYOUT_BLOCK_TYPE
            and not bool(block.get("isRepeatedFurniture", False))
        ),
        key=lambda block: (
            int(block.get("_goldPageNumber", 0)),
            int(block.get("readingOrder", 0)),
        ),
    )
    alternative_blocks = sorted(
        (
            block
            for block in blocks
            if str(block.get("blockType") or "") == NATIVE_LAYOUT_BLOCK_TYPE
        ),
        key=lambda block: (
            int(block.get("_goldPageNumber", 0)),
            int(block.get("readingOrder", 0)),
        ),
    )
    chunks = [
        chunk
        for chunk in snapshot.get("retrievalChunks") or []
        if isinstance(chunk, dict)
    ]
    primary_chunks = sorted(
        (
            chunk
            for chunk in chunks
            if str(chunk.get("chunkType") or "") != NATIVE_LAYOUT_CHUNK_TYPE
        ),
        key=lambda chunk: int(chunk.get("chunkIndex", 0)),
    )
    alternative_chunk_surfaces = _group_alternative_chunks(chunks)

    expectations = case.get("expectations") or {}
    canonical_expectations = case.get("canonicalExpectations") or {}
    expected_pages = int(expectations.get("pageCount", 0))
    if expected_pages and len(pages) != expected_pages:
        issues.append(
            CanonicalEvaluationIssue(
                "canonical_page_count",
                f"Expected {expected_pages} page(s), got {len(pages)}.",
            )
        )

    searchable_items = [
        *blocks,
        *(
            {
                "text": {
                    "retrieval": _table_text(table),
                },
                "polygon": table.get("polygon"),
                "_goldPageNumber": table.get("_goldPageNumber"),
            }
            for table in tables
        ),
    ]
    marker_results: dict[str, tuple[float, dict[str, Any] | None]] = {}
    for marker_spec in expectations.get("markers", []):
        marker = str(marker_spec["text"])
        minimum = float(marker_spec.get("minSimilarity", 1.0))
        result = _best_item(marker, searchable_items)
        marker_results[marker] = result
        if result[0] < minimum:
            issues.append(
                CanonicalEvaluationIssue(
                    "canonical_text_marker",
                    f"Marker '{marker}' similarity {result[0]:.3f} "
                    f"is below {minimum:.3f}.",
                )
            )

    label_counts: dict[str, int] = {}
    for block in primary_blocks:
        label = str(block.get("blockType") or "unknown")
        label_counts[label] = label_counts.get(label, 0) + 1
    if tables:
        label_counts["table"] = len(tables)
    if figures:
        label_counts["picture"] = len(figures)
    canonical_minimum_labels = canonical_expectations.get(
        "minimumLabels",
        expectations.get("minimumLabels") or {},
    )
    for label, minimum in canonical_minimum_labels.items():
        actual = label_counts.get(str(label), 0)
        if actual < int(minimum):
            issues.append(
                CanonicalEvaluationIssue(
                    "canonical_label_count",
                    f"Label '{label}' expected at least {minimum}, got {actual}.",
                )
            )

    for geometry in expectations.get("geometry", []):
        marker = str(geometry["marker"])
        result = marker_results.get(marker) or _best_item(
            marker,
            searchable_items,
        )
        center = _polygon_center(result[1]) if result[1] is not None else None
        if center is None:
            issues.append(
                CanonicalEvaluationIssue(
                    "canonical_geometry",
                    f"Marker '{marker}' has no usable canonical polygon.",
                )
            )
            continue
        expected_x = float(geometry["centerX"])
        expected_y = float(geometry["centerY"])
        tolerance = float(geometry.get("tolerance", 0.12))
        distance = math.dist(center, (expected_x, expected_y))
        if distance > tolerance:
            issues.append(
                CanonicalEvaluationIssue(
                    "canonical_geometry",
                    f"Marker '{marker}' center {center} is "
                    f"{distance:.3f} from expected.",
                )
            )

    for expected in expectations.get("tables", []):
        required_text = _normalise(expected.get("containsText"))
        candidates = [
            table
            for table in tables
            if not required_text or required_text in _normalise(_table_text(table))
        ]
        if not candidates:
            issues.append(
                CanonicalEvaluationIssue(
                    "canonical_table",
                    f"No canonical table contains "
                    f"'{expected.get('containsText', '')}'.",
                )
            )
            continue
        table = candidates[0]
        rows = int(table.get("rowCount", 0))
        columns = int(table.get("columnCount", 0))
        if rows != int(expected["rows"]) or columns != int(expected["columns"]):
            issues.append(
                CanonicalEvaluationIssue(
                    "canonical_table_shape",
                    f"Expected {expected['rows']}x{expected['columns']}, "
                    f"got {rows}x{columns}.",
                )
            )
        cells = table.get("cells") if isinstance(table.get("cells"), list) else []
        by_coordinate = {
            (
                int(cell.get("rowIndex", -1)),
                int(cell.get("columnIndex", -1)),
            ): cell
            for cell in cells
            if isinstance(cell, dict)
        }
        for expected_cell in expected.get("cells", []):
            coordinate = (
                int(expected_cell["row"]),
                int(expected_cell["column"]),
            )
            cell = by_coordinate.get(coordinate)
            if cell is None:
                if bool(expected_cell.get("empty", False)):
                    continue
                issues.append(
                    CanonicalEvaluationIssue(
                        "canonical_table_cell",
                        f"Missing canonical cell at {coordinate}.",
                    )
                )
                continue
            expected_text = _normalise(expected_cell.get("text"))
            actual_text = _normalise(_text_variant(cell))
            if expected_text and expected_text not in actual_text:
                issues.append(
                    CanonicalEvaluationIssue(
                        "canonical_table_cell",
                        f"Cell {coordinate} does not contain "
                        f"'{expected_cell.get('text')}'.",
                    )
                )
            if bool(expected_cell.get("empty", False)) and actual_text:
                issues.append(
                    CanonicalEvaluationIssue(
                        "canonical_table_cell",
                        f"Cell {coordinate} should be empty, got "
                        f"'{_text_variant(cell)}'.",
                    )
                )
            for expected_name, actual_name in (
                ("rowSpan", "rowSpan"),
                ("columnSpan", "columnSpan"),
            ):
                if expected_name in expected_cell and int(
                    cell.get(actual_name, 1)
                ) != int(expected_cell[expected_name]):
                    issues.append(
                        CanonicalEvaluationIssue(
                            "canonical_table_span",
                            f"Cell {coordinate} expected "
                            f"{expected_name}={expected_cell[expected_name]}, "
                            f"got {cell.get(actual_name, 1)}.",
                        )
                    )

    selected_surfaces: dict[str, str | None] = {}
    selected_surfaces["primary"] = _check_order(
        issues,
        "canonical_primary_reading_order",
        list(canonical_expectations.get("primaryOrderedMarkers") or []),
        [("primary_retrieval", primary_chunks)],
    )
    selected_surfaces["alternative"] = _check_order(
        issues,
        "canonical_alternative_reading_order",
        list(canonical_expectations.get("alternativeOrderedMarkers") or []),
        alternative_chunk_surfaces,
    )
    any_surfaces = [
        ("primary_retrieval", primary_chunks),
        *alternative_chunk_surfaces,
    ]
    selected_surfaces["any"] = _check_order(
        issues,
        "canonical_any_surface_reading_order",
        list(canonical_expectations.get("orderedMarkersAnySurface") or []),
        any_surfaces,
    )

    minimum_alternatives = int(
        canonical_expectations.get("minimumAlternativeBlockCount", 0)
    )
    maximum_alternatives = canonical_expectations.get(
        "maximumAlternativeBlockCount"
    )
    if len(alternative_blocks) < minimum_alternatives:
        issues.append(
            CanonicalEvaluationIssue(
                "canonical_alternative_count",
                f"Expected at least {minimum_alternatives} native layout "
                f"alternative block(s), got {len(alternative_blocks)}.",
            )
        )
    if maximum_alternatives is not None and len(alternative_blocks) > int(
        maximum_alternatives
    ):
        issues.append(
            CanonicalEvaluationIssue(
                "canonical_alternative_count",
                f"Expected at most {maximum_alternatives} native layout "
                f"alternative block(s), got {len(alternative_blocks)}.",
            )
        )

    invalid_alternative_owners = [
        block.get("blockId")
        for block in alternative_blocks
        if (
            (
                block.get("provenance", {}).get("attributes", {})
                if isinstance(block.get("provenance"), dict)
                else {}
            ).get("semanticDecisionOwner")
            != "llm_client"
        )
    ]
    if invalid_alternative_owners:
        issues.append(
            CanonicalEvaluationIssue(
                "canonical_semantic_owner",
                "Native layout alternatives without "
                "semanticDecisionOwner=llm_client: "
                + ", ".join(str(value) for value in invalid_alternative_owners),
            )
        )

    native_inventory_figures = [
        figure
        for figure in figures
        if (
            figure.get("provenance", {}).get("stageId")
            if isinstance(figure.get("provenance"), dict)
            else None
        )
        == "native_pdf_image_inventory"
    ]
    invalid_native_inventory_owners = [
        str(figure.get("figureId") or "<missing>")
        for figure in native_inventory_figures
        if (
            (
                figure.get("provenance", {}).get("attributes", {})
                if isinstance(figure.get("provenance"), dict)
                else {}
            ).get("semanticDecisionOwner")
            != "llm_client"
        )
    ]
    if invalid_native_inventory_owners:
        issues.append(
            CanonicalEvaluationIssue(
                "canonical_semantic_owner",
                "Native PDF image inventory figures without "
                "semanticDecisionOwner=llm_client: "
                + ", ".join(invalid_native_inventory_owners),
            )
        )

    unanchored_chunks = [
        int(chunk.get("chunkIndex", -1))
        for chunk in chunks
        if not _chunk_has_evidence_identity(chunk)
    ]
    if bool(canonical_expectations.get("requireAllChunksAnchored", True)):
        if unanchored_chunks:
            issues.append(
                CanonicalEvaluationIssue(
                    "canonical_chunk_identity",
                    "Published retrieval chunks without canonical evidence IDs: "
                    + ", ".join(str(value) for value in unanchored_chunks),
                )
            )

    invalid_page_chunks = [
        int(chunk.get("chunkIndex", -1))
        for chunk in chunks
        if int(chunk.get("pageStart", 0)) < 1
        or int(chunk.get("pageEnd", 0))
        < int(chunk.get("pageStart", 0))
    ]
    if invalid_page_chunks:
        issues.append(
            CanonicalEvaluationIssue(
                "canonical_chunk_pages",
                "Retrieval chunks with invalid page anchors: "
                + ", ".join(str(value) for value in invalid_page_chunks),
            )
        )

    reconciliation = snapshot.get("reconciliation")
    if not isinstance(reconciliation, dict):
        reconciliation = {}
    image_inventory = snapshot.get("imageInventory")
    if not isinstance(image_inventory, dict):
        image_inventory = {}
    return {
        "ok": not issues,
        "issues": [
            {"check": issue.check, "message": issue.message}
            for issue in issues
        ],
        "metrics": {
            "pageCount": len(pages),
            "primaryBlockCount": len(primary_blocks),
            "alternativeBlockCount": len(alternative_blocks),
            "tableCount": len(tables),
            "figureCount": len(figures),
            "nativeImageCandidateCount": int(
                image_inventory.get("candidateImageCount", 0)
            ),
            "nativeImageAddedFigureCount": int(
                image_inventory.get("addedFigureCount", 0)
            ),
            "retrievalChunkCount": len(chunks),
            "alternativeChunkCount": sum(
                len(items) for _, items in alternative_chunk_surfaces
            ),
            "unanchoredChunkCount": len(unanchored_chunks),
            "labelCounts": label_counts,
            "markerSimilarities": {
                marker: round(result[0], 4)
                for marker, result in marker_results.items()
            },
            "selectedReadingOrderSurfaces": selected_surfaces,
            "recoveredLayoutPageCount": int(
                reconciliation.get("recoveredLayoutPageCount", 0)
            ),
            "skippedUndersegmentedLayoutPageCount": int(
                reconciliation.get("skippedUndersegmentedLayoutPageCount", 0)
            ),
        },
    }
