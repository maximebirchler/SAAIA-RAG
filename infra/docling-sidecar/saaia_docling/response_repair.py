from __future__ import annotations

import asyncio
import logging
import time
from pathlib import Path
from typing import Any

from .ocr_engine import RapidOcrRegionEngine, close_region
from .pdf_regions import render_table_region
from .table_repair import REPAIR_SCHEMA_VERSION, repair_sparse_table


LOGGER = logging.getLogger("saaia_docling.table_repair")


async def repair_response_tables(
    pdf_path: str | Path,
    response: dict[str, Any],
    engine: RapidOcrRegionEngine,
    *,
    render_scale: float = 2.5,
    padding_points: float = 12.0,
    minimum_score: float = 0.5,
) -> list[dict[str, Any]]:
    document_wrapper = response.get("document")
    if not isinstance(document_wrapper, dict):
        return []
    document = document_wrapper.get("json_content")
    if not isinstance(document, dict):
        return []
    tables = document.get("tables")
    if not isinstance(tables, list):
        return []

    started = time.perf_counter()
    summaries: list[dict[str, Any]] = []
    for index, table in enumerate(tables):
        if not isinstance(table, dict) or not _has_sparse_grid(table):
            continue
        source_ref = str(table.get("self_ref", f"table:{index}"))
        try:
            region = await asyncio.to_thread(
                render_table_region,
                pdf_path,
                document,
                table,
                scale=render_scale,
                padding_points=padding_points,
            )
            try:
                ocr = await asyncio.to_thread(engine.recognize, region)
            finally:
                close_region(region)
            result = repair_sparse_table(
                table,
                ocr.lines,
                engine_version=ocr.engine_version,
                model_id=ocr.model_id,
                duration_ms=ocr.duration_ms,
                minimum_score=minimum_score,
            )
            tables[index] = result.table
            summary = {
                "schema_version": REPAIR_SCHEMA_VERSION,
                "table_ref": source_ref,
                "status": "applied" if result.applied else "not_applied",
                "reason": result.reason,
                "missing_coordinates": [
                    [row, column]
                    for row, column in result.missing_coordinates
                ],
                "repaired_coordinates": [
                    [row, column]
                    for row, column in result.repaired_coordinates
                ],
                "added_coordinates": [
                    [row, column]
                    for row, column in result.added_coordinates
                ],
                "ocr_duration_ms": ocr.duration_ms,
            }
            if not result.applied:
                tables[index]["saaia_table_repair_attempt"] = summary
            summaries.append(summary)
        except Exception as exc:  # fail open: preserve the original Docling table
            LOGGER.exception("Regional table repair failed for %s", source_ref)
            summary = {
                "schema_version": REPAIR_SCHEMA_VERSION,
                "table_ref": source_ref,
                "status": "error",
                "reason": type(exc).__name__,
            }
            table["saaia_table_repair_attempt"] = summary
            summaries.append(summary)

    if summaries:
        elapsed_seconds = time.perf_counter() - started
        response["saaia_table_repairs"] = summaries
        timings = response.setdefault("timings", {})
        if isinstance(timings, dict):
            timings["saaia_table_repair"] = {
                "scope": "document",
                "count": 1,
                "times": [elapsed_seconds],
            }
    return summaries


def _has_sparse_grid(table: dict[str, Any]) -> bool:
    data = table.get("data")
    if not isinstance(data, dict):
        return False
    try:
        rows = int(data["num_rows"])
        columns = int(data["num_cols"])
    except (KeyError, TypeError, ValueError):
        return False
    if rows <= 0 or columns <= 0 or rows * columns > 10_000:
        return False
    cells = data.get("table_cells")
    if not isinstance(cells, list):
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
        except (KeyError, TypeError, ValueError):
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
