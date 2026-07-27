from __future__ import annotations

from collections import Counter, defaultdict
from copy import deepcopy
from dataclasses import dataclass
from math import isfinite
from statistics import median
from typing import Any, Iterable, Mapping, Sequence


REPAIR_SCHEMA_VERSION = "saaia_table_text_repair_v1"


@dataclass(frozen=True)
class OcrLine:
    text: str
    score: float
    polygon: tuple[tuple[float, float], ...]

    @property
    def left(self) -> float:
        return min(point[0] for point in self.polygon)

    @property
    def top(self) -> float:
        return min(point[1] for point in self.polygon)

    @property
    def right(self) -> float:
        return max(point[0] for point in self.polygon)

    @property
    def bottom(self) -> float:
        return max(point[1] for point in self.polygon)

    @property
    def center_x(self) -> float:
        return (self.left + self.right) / 2

    @property
    def center_y(self) -> float:
        return (self.top + self.bottom) / 2

    @property
    def height(self) -> float:
        return self.bottom - self.top


@dataclass(frozen=True)
class TableRepairResult:
    applied: bool
    reason: str
    table: dict[str, Any]
    missing_coordinates: tuple[tuple[int, int], ...] = ()
    repaired_coordinates: tuple[tuple[int, int], ...] = ()
    added_coordinates: tuple[tuple[int, int], ...] = ()


def repair_sparse_table(
    table: Mapping[str, Any],
    ocr_lines: Sequence[OcrLine],
    *,
    engine_version: str,
    model_id: str,
    duration_ms: int,
    minimum_score: float = 0.5,
) -> TableRepairResult:
    """Repair only mechanically proven gaps in a Docling table grid.

    The function deliberately has no domain vocabulary and performs no semantic
    choice. It accepts a repair only when:

    * the declared grid contains uncovered coordinates and no overlap;
    * every row and column has enough existing geometry to establish a center;
    * OCR supplies text for every uncovered coordinate;
    * any existing cell that absorbed a missing row can be split using geometry
      or exact normalized text composition.

    Otherwise the original table is returned unchanged.
    """

    original = deepcopy(dict(table))
    data = original.get("data")
    if not isinstance(data, dict):
        return _not_applied(original, "table_data_missing")

    row_count = _positive_int(data.get("num_rows"))
    column_count = _positive_int(data.get("num_cols"))
    cells = data.get("table_cells")
    if row_count is None or column_count is None or not isinstance(cells, list):
        return _not_applied(original, "table_grid_invalid")
    if row_count * column_count > 10_000:
        return _not_applied(original, "table_grid_too_large")

    parsed_cells: list[_ParsedCell] = []
    occupancy: dict[tuple[int, int], list[int]] = defaultdict(list)
    for index, value in enumerate(cells):
        parsed = _parse_cell(value, index, row_count, column_count)
        if parsed is None:
            return _not_applied(original, "table_cell_invalid")
        parsed_cells.append(parsed)
        for coordinate in parsed.coordinates:
            occupancy[coordinate].append(index)

    if any(len(indexes) > 1 for indexes in occupancy.values()):
        return _not_applied(original, "overlapping_grid_not_repaired")

    missing = tuple(
        (row, column)
        for row in range(row_count)
        for column in range(column_count)
        if (row, column) not in occupancy
    )
    if not missing:
        return _not_applied(original, "grid_complete")

    row_centers = _axis_centers(parsed_cells, row_count, axis="row")
    column_centers = _axis_centers(parsed_cells, column_count, axis="column")
    if row_centers is None or column_centers is None:
        return _not_applied(original, "grid_centers_unavailable")
    if not _strictly_increasing(row_centers) or not _strictly_increasing(column_centers):
        return _not_applied(original, "grid_centers_not_monotonic")

    usable_lines = [
        line
        for line in ocr_lines
        if line.text.strip()
        and isfinite(line.score)
        and line.score >= minimum_score
        and len(line.polygon) >= 4
    ]
    if not usable_lines:
        return _not_applied(original, "ocr_text_unavailable")

    assigned: dict[tuple[int, int], list[OcrLine]] = defaultdict(list)
    for line in usable_lines:
        row = _nearest_index(line.center_y, row_centers)
        column = _nearest_index(line.center_x, column_centers)
        assigned[(row, column)].append(line)

    if any(not assigned.get(coordinate) for coordinate in missing):
        return _not_applied(original, "missing_cells_not_covered_by_ocr")

    rendered = {
        coordinate: _render_lines(lines)
        for coordinate, lines in assigned.items()
        if lines
    }
    suspects: set[tuple[int, int]] = set()
    cell_by_coordinate = {
        parsed.coordinates[0]: parsed
        for parsed in parsed_cells
        if len(parsed.coordinates) == 1
    }

    for missing_row, missing_column in missing:
        missing_text = rendered[(missing_row, missing_column)]
        for coordinate, parsed in cell_by_coordinate.items():
            row, column = coordinate
            if column != missing_column or row == missing_row:
                continue
            own_text = rendered.get(coordinate, "")
            if not own_text:
                continue
            contains_missing_center = (
                parsed.top <= row_centers[missing_row] <= parsed.bottom
            )
            exact_composition = _is_exact_row_composition(
                parsed.text,
                own_text,
                [
                    rendered[other]
                    for other in missing
                    if other[1] == missing_column
                    and rendered.get(other)
                ],
            )
            if contains_missing_center or exact_composition:
                suspects.add(coordinate)
            elif _contains_normalized(parsed.text, missing_text):
                return _not_applied(original, "absorbed_text_split_is_ambiguous")

    affected = set(missing) | suspects
    if any(not assigned.get(coordinate) for coordinate in affected):
        return _not_applied(original, "affected_cells_not_covered_by_ocr")

    repaired = deepcopy(original)
    repaired_data = repaired["data"]
    repaired_cells = repaired_data["table_cells"]
    original_cell_count = len(repaired_cells)
    repaired_coordinates: list[tuple[int, int]] = []

    for coordinate in sorted(suspects):
        parsed = cell_by_coordinate[coordinate]
        cell = repaired_cells[parsed.index]
        replacement = rendered[coordinate]
        if _normalize(cell.get("text", "")) == _normalize(replacement):
            continue
        cell["saaia_original_text"] = str(cell.get("text", ""))
        cell["text"] = replacement
        cell["bbox"] = _union_bbox(assigned[coordinate])
        cell["saaia_text_repair"] = _cell_repair_metadata(
            assigned[coordinate],
            action="split_absorbed_text",
            engine_version=engine_version,
            model_id=model_id,
        )
        repaired_coordinates.append(coordinate)

    added_coordinates: list[tuple[int, int]] = []
    for coordinate in missing:
        row, column = coordinate
        lines = assigned[coordinate]
        repaired_cells.append(
            {
                "bbox": _union_bbox(lines),
                "row_span": 1,
                "col_span": 1,
                "start_row_offset_idx": row,
                "end_row_offset_idx": row + 1,
                "start_col_offset_idx": column,
                "end_col_offset_idx": column + 1,
                "text": rendered[coordinate],
                "column_header": _majority_role(
                    parsed_cells, row, "column_header"
                ),
                "row_header": _majority_role(parsed_cells, row, "row_header"),
                "row_section": _majority_role(parsed_cells, row, "row_section"),
                # There is no Docling cell to preserve for an uncovered
                # coordinate. The direct OCR observation is therefore the raw
                # text as well as the initial canonical value.
                "saaia_original_text": rendered[coordinate],
                "saaia_text_repair": _cell_repair_metadata(
                    lines,
                    action="add_missing_cell",
                    engine_version=engine_version,
                    model_id=model_id,
                ),
            }
        )
        added_coordinates.append(coordinate)

    repaired_cells.sort(
        key=lambda cell: (
            int(cell.get("start_row_offset_idx", 0)),
            int(cell.get("start_col_offset_idx", 0)),
            int(cell.get("row_span", 1)),
            int(cell.get("col_span", 1)),
        )
    )
    affected_lines = [
        line
        for coordinate in affected
        for line in assigned[coordinate]
    ]
    repaired["saaia_table_repair"] = {
        "schema_version": REPAIR_SCHEMA_VERSION,
        "engine": "RapidOCR",
        "engine_version": engine_version,
        "model_id": model_id,
        "reason": "sparse_grid_with_ocr_coverage",
        "original_cell_count": original_cell_count,
        "final_cell_count": len(repaired_cells),
        "repaired_cell_count": len(repaired_coordinates),
        "added_cell_count": len(added_coordinates),
        "ocr_line_count": len(usable_lines),
        "mean_confidence": round(
            sum(line.score for line in affected_lines) / len(affected_lines),
            6,
        ),
        "duration_ms": max(0, int(duration_ms)),
    }
    return TableRepairResult(
        applied=True,
        reason="sparse_grid_repaired",
        table=repaired,
        missing_coordinates=missing,
        repaired_coordinates=tuple(repaired_coordinates),
        added_coordinates=tuple(added_coordinates),
    )


@dataclass(frozen=True)
class _ParsedCell:
    index: int
    row: int
    column: int
    row_span: int
    column_span: int
    left: float
    top: float
    right: float
    bottom: float
    text: str
    roles: Mapping[str, bool]

    @property
    def center_x(self) -> float:
        return (self.left + self.right) / 2

    @property
    def center_y(self) -> float:
        return (self.top + self.bottom) / 2

    @property
    def coordinates(self) -> tuple[tuple[int, int], ...]:
        return tuple(
            (row, column)
            for row in range(self.row, self.row + self.row_span)
            for column in range(
                self.column, self.column + self.column_span
            )
        )


def _parse_cell(
    value: Any,
    index: int,
    row_count: int,
    column_count: int,
) -> _ParsedCell | None:
    if not isinstance(value, Mapping):
        return None
    row = _non_negative_int(value.get("start_row_offset_idx"))
    column = _non_negative_int(value.get("start_col_offset_idx"))
    row_span = _positive_int(value.get("row_span", 1))
    column_span = _positive_int(value.get("col_span", 1))
    bbox = _top_left_bbox(value.get("bbox"))
    if (
        row is None
        or column is None
        or row_span is None
        or column_span is None
        or bbox is None
        or row + row_span > row_count
        or column + column_span > column_count
    ):
        return None
    return _ParsedCell(
        index=index,
        row=row,
        column=column,
        row_span=row_span,
        column_span=column_span,
        left=bbox[0],
        top=bbox[1],
        right=bbox[2],
        bottom=bbox[3],
        text=str(value.get("text", "")),
        roles={
            "column_header": bool(value.get("column_header", False)),
            "row_header": bool(value.get("row_header", False)),
            "row_section": bool(value.get("row_section", False)),
        },
    )


def _axis_centers(
    cells: Sequence[_ParsedCell],
    count: int,
    *,
    axis: str,
) -> tuple[float, ...] | None:
    values: dict[int, list[float]] = defaultdict(list)
    for cell in cells:
        if axis == "row" and cell.row_span == 1:
            values[cell.row].append(cell.center_y)
        elif axis == "column" and cell.column_span == 1:
            values[cell.column].append(cell.center_x)
    if any(not values.get(index) for index in range(count)):
        return None
    return tuple(float(median(values[index])) for index in range(count))


def _strictly_increasing(values: Sequence[float]) -> bool:
    return all(left < right for left, right in zip(values, values[1:]))


def _nearest_index(value: float, centers: Sequence[float]) -> int:
    return min(range(len(centers)), key=lambda index: abs(value - centers[index]))


def _render_lines(lines: Sequence[OcrLine]) -> str:
    heights = [line.height for line in lines if line.height > 0]
    tolerance = max(1.0, (median(heights) if heights else 1.0) * 0.55)
    rows: list[list[OcrLine]] = []
    row_centers: list[float] = []
    for line in sorted(lines, key=lambda item: (item.center_y, item.left)):
        if not rows or abs(line.center_y - row_centers[-1]) > tolerance:
            rows.append([line])
            row_centers.append(line.center_y)
        else:
            rows[-1].append(line)
            row_centers[-1] = sum(item.center_y for item in rows[-1]) / len(
                rows[-1]
            )
    return " ".join(
        line.text.strip()
        for row in rows
        for line in sorted(row, key=lambda item: item.left)
        if line.text.strip()
    )


def _is_exact_row_composition(
    existing: str,
    own: str,
    missing_values: Iterable[str],
) -> bool:
    existing_tokens = Counter(_tokens(existing))
    expected_tokens = Counter(_tokens(own))
    for value in missing_values:
        expected_tokens.update(_tokens(value))
    return bool(existing_tokens) and existing_tokens == expected_tokens


def _contains_normalized(container: str, value: str) -> bool:
    needle = _normalize(value)
    return bool(needle) and needle in _normalize(container)


def _tokens(value: str) -> list[str]:
    return _normalize(value).split()


def _normalize(value: Any) -> str:
    return " ".join(str(value or "").casefold().split())


def _majority_role(
    cells: Sequence[_ParsedCell],
    row: int,
    role: str,
) -> bool:
    row_cells = [cell for cell in cells if cell.row == row]
    if not row_cells:
        return False
    true_count = sum(bool(cell.roles.get(role)) for cell in row_cells)
    return true_count > len(row_cells) / 2


def _cell_repair_metadata(
    lines: Sequence[OcrLine],
    *,
    action: str,
    engine_version: str,
    model_id: str,
) -> dict[str, Any]:
    return {
        "schema_version": REPAIR_SCHEMA_VERSION,
        "action": action,
        "engine": "RapidOCR",
        "engine_version": engine_version,
        "model_id": model_id,
        "confidence": round(
            sum(line.score for line in lines) / len(lines),
            6,
        ),
        "source_boxes": [
            [[round(x, 4), round(y, 4)] for x, y in line.polygon]
            for line in lines
        ],
    }


def _union_bbox(lines: Sequence[OcrLine]) -> dict[str, Any]:
    return {
        "l": min(line.left for line in lines),
        "t": min(line.top for line in lines),
        "r": max(line.right for line in lines),
        "b": max(line.bottom for line in lines),
        "coord_origin": "TOPLEFT",
    }


def _top_left_bbox(value: Any) -> tuple[float, float, float, float] | None:
    if not isinstance(value, Mapping):
        return None
    try:
        left = float(value["l"])
        top = float(value["t"])
        right = float(value["r"])
        bottom = float(value["b"])
    except (KeyError, TypeError, ValueError):
        return None
    if not all(isfinite(number) for number in (left, top, right, bottom)):
        return None
    if str(value.get("coord_origin", "TOPLEFT")).upper() != "TOPLEFT":
        return None
    if right <= left or bottom <= top:
        return None
    return left, top, right, bottom


def _positive_int(value: Any) -> int | None:
    parsed = _non_negative_int(value)
    return parsed if parsed is not None and parsed > 0 else None


def _non_negative_int(value: Any) -> int | None:
    if isinstance(value, bool):
        return None
    try:
        parsed = int(value)
    except (TypeError, ValueError):
        return None
    if parsed < 0 or parsed != value:
        return None
    return parsed


def _not_applied(table: dict[str, Any], reason: str) -> TableRepairResult:
    return TableRepairResult(applied=False, reason=reason, table=table)
