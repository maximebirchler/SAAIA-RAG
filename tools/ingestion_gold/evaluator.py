from __future__ import annotations

import difflib
import math
import re
from dataclasses import dataclass
from typing import Any, Iterable


@dataclass(frozen=True)
class EvaluationIssue:
    check: str
    message: str


def _normalise(value: object) -> str:
    text = re.sub(r"\s+", " ", str(value or "")).strip().casefold()
    return "".join(character for character in text if character.isalnum() or character == " ")


def _content_items(document: dict[str, Any]) -> list[dict[str, Any]]:
    result: list[dict[str, Any]] = []
    for key in ("texts", "tables", "pictures", "key_value_items", "form_items", "groups"):
        values = document.get(key)
        if isinstance(values, list):
            result.extend(value for value in values if isinstance(value, dict))
    return result


def reading_order(document: dict[str, Any]) -> list[dict[str, Any]]:
    items = _content_items(document)
    by_ref = {
        str(item.get("self_ref")): item
        for item in items
        if str(item.get("self_ref") or "").strip()
    }
    ordered: list[dict[str, Any]] = []
    visited: set[str] = set()
    visiting: set[str] = set()

    def visit(reference: str) -> None:
        if not reference or reference in visited:
            return
        if reference in visiting:
            raise ValueError(f"Cycle in Docling reading order at {reference}.")
        item = by_ref.get(reference)
        if item is None:
            raise ValueError(f"Unresolved Docling reference {reference}.")
        visiting.add(reference)
        if not reference.startswith("#/groups/"):
            ordered.append(item)
        children = item.get("children")
        if isinstance(children, list):
            for child in children:
                if isinstance(child, dict):
                    visit(str(child.get("$ref") or ""))
        visiting.remove(reference)
        visited.add(reference)

    for root_name in ("body", "furniture"):
        root = document.get(root_name)
        if not isinstance(root, dict):
            continue
        children = root.get("children")
        if not isinstance(children, list):
            continue
        for child in children:
            if isinstance(child, dict):
                visit(str(child.get("$ref") or ""))
    for reference in by_ref:
        visit(reference)
    return ordered


def _table_text(item: dict[str, Any]) -> str:
    data = item.get("data")
    if not isinstance(data, dict):
        return ""
    cells = data.get("table_cells")
    if not isinstance(cells, list):
        return ""
    ordered = sorted(
        (cell for cell in cells if isinstance(cell, dict)),
        key=lambda cell: (
            int(cell.get("start_row_offset_idx", 0)),
            int(cell.get("start_col_offset_idx", 0)),
        ),
    )
    return " ".join(str(cell.get("text") or "") for cell in ordered)


def _item_text(item: dict[str, Any]) -> str:
    if item.get("label") == "table" or "data" in item:
        return _table_text(item)
    return str(item.get("text") or item.get("orig") or "")


def _best_item(
    marker: str,
    ordered: Iterable[dict[str, Any]],
) -> tuple[int, float, dict[str, Any] | None]:
    normalised_marker = _normalise(marker)
    best_index = -1
    best_score = 0.0
    best_item: dict[str, Any] | None = None
    for index, item in enumerate(ordered):
        text = _normalise(_item_text(item))
        if not text:
            continue
        if normalised_marker in text:
            return index, 1.0, item
        score = difflib.SequenceMatcher(None, normalised_marker, text).ratio()
        if score > best_score:
            best_index = index
            best_score = score
            best_item = item
    return best_index, best_score, best_item


def _bbox_center(
    item: dict[str, Any],
    pages: dict[str, Any],
) -> tuple[float, float] | None:
    provenance = item.get("prov")
    if not isinstance(provenance, list) or not provenance:
        return None
    first = provenance[0]
    if not isinstance(first, dict) or not isinstance(first.get("bbox"), dict):
        return None
    page_number = int(first.get("page_no", 1))
    page = pages.get(str(page_number)) or pages.get(str(page_number - 1))
    if not isinstance(page, dict) or not isinstance(page.get("size"), dict):
        return None
    width = float(page["size"].get("width", 0))
    height = float(page["size"].get("height", 0))
    if width <= 0 or height <= 0:
        return None
    bbox = first["bbox"]
    left = float(bbox.get("l", 0))
    right = float(bbox.get("r", 0))
    top = float(bbox.get("t", 0))
    bottom = float(bbox.get("b", 0))
    x = ((left + right) / 2) / width
    origin = str(bbox.get("coord_origin") or "").upper()
    raw_y = (top + bottom) / 2
    y = raw_y / height if "TOP" in origin else 1 - (raw_y / height)
    return x, y


def evaluate_case(
    response: dict[str, Any],
    case: dict[str, Any],
) -> dict[str, Any]:
    issues: list[EvaluationIssue] = []
    document_wrapper = response.get("document")
    document = (
        document_wrapper.get("json_content")
        if isinstance(document_wrapper, dict)
        else None
    )
    if not isinstance(document, dict):
        return {
            "ok": False,
            "issues": [
                {
                    "check": "document",
                    "message": "Docling response has no document.json_content.",
                }
            ],
            "metrics": {},
        }
    try:
        ordered = reading_order(document)
    except ValueError as error:
        return {
            "ok": False,
            "issues": [{"check": "reading_order_graph", "message": str(error)}],
            "metrics": {},
        }
    expectations = case.get("expectations") or {}
    pages = document.get("pages") if isinstance(document.get("pages"), dict) else {}
    expected_pages = int(expectations.get("pageCount", 0))
    if expected_pages and len(pages) != expected_pages:
        issues.append(
            EvaluationIssue(
                "page_count",
                f"Expected {expected_pages} page(s), got {len(pages)}.",
            )
        )

    marker_results: dict[str, tuple[int, float, dict[str, Any] | None]] = {}
    for marker_spec in expectations.get("markers", []):
        marker = str(marker_spec["text"])
        minimum = float(marker_spec.get("minSimilarity", 1.0))
        result = _best_item(marker, ordered)
        marker_results[marker] = result
        if result[1] < minimum:
            issues.append(
                EvaluationIssue(
                    "text_marker",
                    f"Marker '{marker}' similarity {result[1]:.3f} is below {minimum:.3f}.",
                )
            )

    last_index = -1
    for marker in expectations.get("orderedMarkers", []):
        marker_text = str(marker)
        result = marker_results.get(marker_text) or _best_item(marker_text, ordered)
        if result[0] < 0 or result[1] < 0.8:
            issues.append(
                EvaluationIssue(
                    "reading_order",
                    f"Ordered marker '{marker_text}' was not reliably found.",
                )
            )
            continue
        if result[0] < last_index:
            issues.append(
                EvaluationIssue(
                    "reading_order",
                    f"Marker '{marker_text}' appears before its expected predecessor.",
                )
            )
        last_index = max(last_index, result[0])

    label_counts: dict[str, int] = {}
    for item in ordered:
        label = str(item.get("label") or "unknown")
        label_counts[label] = label_counts.get(label, 0) + 1
    for label, minimum in (expectations.get("minimumLabels") or {}).items():
        actual = label_counts.get(str(label), 0)
        if actual < int(minimum):
            issues.append(
                EvaluationIssue(
                    "label_count",
                    f"Label '{label}' expected at least {minimum}, got {actual}.",
                )
            )

    for geometry in expectations.get("geometry", []):
        marker = str(geometry["marker"])
        result = marker_results.get(marker) or _best_item(marker, ordered)
        center = _bbox_center(result[2], pages) if result[2] is not None else None
        if center is None:
            issues.append(
                EvaluationIssue(
                    "geometry",
                    f"Marker '{marker}' has no usable page bounding box.",
                )
            )
            continue
        expected_x = float(geometry["centerX"])
        expected_y = float(geometry["centerY"])
        tolerance = float(geometry.get("tolerance", 0.12))
        distance = math.dist(center, (expected_x, expected_y))
        if distance > tolerance:
            issues.append(
                EvaluationIssue(
                    "geometry",
                    f"Marker '{marker}' center {center} is {distance:.3f} from expected.",
                )
            )

    tables = [
        item
        for item in ordered
        if item.get("label") == "table" or isinstance(item.get("data"), dict)
    ]
    for expected in expectations.get("tables", []):
        required_text = _normalise(expected.get("containsText"))
        candidates = [
            table
            for table in tables
            if not required_text or required_text in _normalise(_table_text(table))
        ]
        if not candidates:
            issues.append(
                EvaluationIssue(
                    "table",
                    f"No table contains '{expected.get('containsText', '')}'.",
                )
            )
            continue
        table = candidates[0]
        data = table.get("data") or {}
        rows = int(data.get("num_rows", 0))
        columns = int(data.get("num_cols", 0))
        if rows != int(expected["rows"]) or columns != int(expected["columns"]):
            issues.append(
                EvaluationIssue(
                    "table_shape",
                    f"Expected {expected['rows']}x{expected['columns']}, got {rows}x{columns}.",
                )
            )
        cells = data.get("table_cells") if isinstance(data.get("table_cells"), list) else []
        by_coordinate = {
            (
                int(cell.get("start_row_offset_idx", -1)),
                int(cell.get("start_col_offset_idx", -1)),
            ): cell
            for cell in cells
            if isinstance(cell, dict)
        }
        for expected_cell in expected.get("cells", []):
            coordinate = (int(expected_cell["row"]), int(expected_cell["column"]))
            cell = by_coordinate.get(coordinate)
            if cell is None:
                if bool(expected_cell.get("empty", False)):
                    continue
                issues.append(
                    EvaluationIssue(
                        "table_cell",
                        f"Missing cell at {coordinate}.",
                    )
                )
                continue
            expected_text = _normalise(expected_cell.get("text"))
            actual_text = _normalise(cell.get("text"))
            if expected_text and expected_text not in actual_text:
                issues.append(
                    EvaluationIssue(
                        "table_cell",
                        f"Cell {coordinate} does not contain '{expected_cell.get('text')}'.",
                    )
                )
            if bool(expected_cell.get("empty", False)) and actual_text:
                issues.append(
                    EvaluationIssue(
                        "table_cell",
                        f"Cell {coordinate} should be empty, got '{cell.get('text')}'.",
                    )
                )
            for span_name, json_name in (("rowSpan", "row_span"), ("columnSpan", "col_span")):
                if span_name in expected_cell and int(cell.get(json_name, 1)) != int(expected_cell[span_name]):
                    issues.append(
                        EvaluationIssue(
                            "table_span",
                            f"Cell {coordinate} expected {span_name}={expected_cell[span_name]}, "
                            f"got {cell.get(json_name, 1)}.",
                        )
                    )

    return {
        "ok": not issues,
        "issues": [
            {"check": issue.check, "message": issue.message}
            for issue in issues
        ],
        "metrics": {
            "pageCount": len(pages),
            "orderedItemCount": len(ordered),
            "tableCount": len(tables),
            "labelCounts": label_counts,
            "markerSimilarities": {
                marker: round(result[1], 4)
                for marker, result in marker_results.items()
            },
        },
    }
