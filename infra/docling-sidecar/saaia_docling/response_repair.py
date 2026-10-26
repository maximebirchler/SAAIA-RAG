from __future__ import annotations

from copy import deepcopy
import logging
from pathlib import Path
import time
from typing import Any, Protocol

from .response_text_repair import validate_source_region
from .table_repair import REPAIR_SCHEMA_VERSION, repair_sparse_table
from .table_repair_options import TableRepairOptions

LOGGER = logging.getLogger("saaia_docling.table_repair")
MAXIMUM_INSPECTED_TABLES = 1024


class TableRegionEngine(Protocol):
    async def recognize(self, pdf_path, document, table, options, timeout_seconds): ...


async def repair_response_tables(
    pdf_path: str | Path, response: dict[str, Any], engine: TableRegionEngine,
    *, options: TableRepairOptions | None = None, clock=time.monotonic,
) -> list[dict[str, Any]]:
    options = options if options is not None else TableRepairOptions()
    wrapper = response.get("document")
    document = wrapper.get("json_content") if isinstance(wrapper, dict) else None
    tables = document.get("tables") if isinstance(document, dict) else None
    if not isinstance(tables, list):
        return []
    started = clock()
    deadline = started + options.total_timeout_seconds
    summaries = []
    attempted = inspected = 0
    for index, table in enumerate(tables):
        if index >= MAXIMUM_INSPECTED_TABLES or clock() >= deadline:
            summaries.append({"schema_version": REPAIR_SCHEMA_VERSION, "status": "not_applied",
                "reason": "document_time_or_scan_budget_exhausted",
                "uninspected_table_count": len(tables) - index})
            break
        inspected += 1
        if not isinstance(table, dict) or not _has_sparse_grid(table):
            continue
        summary = {"schema_version": REPAIR_SCHEMA_VERSION,
            "table_ref": str(table.get("self_ref", f"table:{index}")),
            "status": "not_applied", "reason": "region_budget_exhausted", "attempted": False}
        completed_observation = None
        if attempted < options.max_regions:
            try:
                validate_source_region(document, table, options, reference_kind="tables")
                remaining = min(options.region_timeout_seconds, deadline - clock())
                if remaining <= 0:
                    raise TimeoutError()
                attempted += 1
                summary["attempted"] = True
                ocr = await engine.recognize(Path(pdf_path), document, table, options, remaining)
                # A late result is never applied, even if a defective adapter returned it.
                if clock() >= deadline:
                    raise TimeoutError()
                result = repair_sparse_table(table, ocr.lines, engine_version=ocr.engine_version,
                    model_id=ocr.model_id, duration_ms=ocr.duration_ms, minimum_score=options.minimum_score)
                if clock() >= deadline:
                    raise TimeoutError()
                summary.update(status="applied" if result.applied else "not_applied", reason=result.reason,
                    missing_coordinates=[list(c) for c in result.missing_coordinates],
                    repaired_coordinates=[list(c) for c in result.repaired_coordinates],
                    added_coordinates=[list(c) for c in result.added_coordinates], ocr_duration_ms=ocr.duration_ms)
                if result.applied:
                    result.table["saaia_table_repair"]["ocr_observation"] = ocr.observation
                else:
                    completed_observation = ocr.observation
                tables[index] = result.table
            except TimeoutError:
                summary["reason"] = "region_or_document_time_budget_exhausted"
            except Exception as error:
                summary["status"] = "error"
                summary["reason"] = str(error) if isinstance(error, ValueError) and str(error) in {
                    "source_geometry_invalid", "source_page_unavailable", "source_page_size_invalid",
                    "rendered_page_pixel_budget_exceeded"} else "regional_engine_unavailable_or_invalid"
                summary["error_type"] = type(error).__name__
                LOGGER.warning("Regional table repair degraded error_type=%s", type(error).__name__)
        if summary["status"] != "applied":
            # Keep a completed but inconclusive reading for private diagnosis.
            # Response summaries remain compact and contain no recognized text.
            attempt = deepcopy(summary)
            if summary["status"] == "not_applied" and completed_observation is not None:
                attempt["ocr_observation"] = deepcopy(completed_observation)
            tables[index]["saaia_table_repair_attempt"] = attempt
        summaries.append(summary)
    elapsed = clock() - started
    response["saaia_table_repairs"] = summaries
    response["saaia_table_repair_summary"] = {"schema_version": REPAIR_SCHEMA_VERSION,
        "inspected_table_count": inspected, "uninspected_table_count": len(tables) - inspected,
        "candidate_count": sum("table_ref" in row for row in summaries), "attempted_count": attempted,
        "applied_count": sum(row["status"] == "applied" for row in summaries),
        "duration_ms": round(elapsed * 1000), "limit_max_regions": options.max_regions,
        "limit_total_timeout_seconds": options.total_timeout_seconds}
    timings = response.setdefault("timings", {})
    if isinstance(timings, dict):
        timings["saaia_table_repair"] = {"scope": "document", "count": 1, "times": [elapsed]}
    return summaries


def _has_sparse_grid(table: dict[str, Any]) -> bool:
    data = table.get("data")
    if not isinstance(data, dict):
        return False
    try:
        rows = int(data["num_rows"])
        columns = int(data["num_cols"])
    except (KeyError, TypeError, ValueError, OverflowError):
        return False
    if rows <= 0 or columns <= 0 or rows * columns > 10_000:
        return False
    cells = data.get("table_cells")
    if not isinstance(cells, list) or len(cells) > 10_000:
        return False
    occupancy: set[tuple[int, int]] = set()
    for cell in cells:
        if not isinstance(cell, dict):
            return False
        try:
            row = int(cell["start_row_offset_idx"])
            column = int(cell["start_col_offset_idx"])
            row_span = int(cell.get("row_span", 1))
            column_span = int(cell.get("col_span", 1))
        except (KeyError, TypeError, ValueError, OverflowError):
            return False
        if not 0 <= row < rows or not 0 <= column < columns or not 1 <= row_span <= rows or not 1 <= column_span <= columns:
            return False
        for current_row in range(row, row + row_span):
            for current_column in range(column, column + column_span):
                coordinate = (current_row, current_column)
                if (
                    current_row < 0
                    or current_row >= rows
                    or current_column < 0
                    or current_column >= columns
                    or coordinate in occupancy
                ):
                    return False
                occupancy.add(coordinate)
    return len(occupancy) < rows * columns
