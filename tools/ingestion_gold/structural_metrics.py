from __future__ import annotations

import argparse
import json
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any

from .evaluator import reading_order


def _first_provenance(item: dict[str, Any]) -> dict[str, Any] | None:
    provenance = item.get("prov")
    if not isinstance(provenance, list) or not provenance:
        return None
    first = provenance[0]
    return first if isinstance(first, dict) else None


def _table_metrics(item: dict[str, Any]) -> dict[str, Any]:
    data = item.get("data")
    if not isinstance(data, dict):
        return {"rows": 0, "columns": 0, "cells": 0, "missingCoordinates": 0}
    rows = int(data.get("num_rows", 0))
    columns = int(data.get("num_cols", 0))
    cells = data.get("table_cells")
    cells = cells if isinstance(cells, list) else []
    covered: set[tuple[int, int]] = set()
    for cell in cells:
        if not isinstance(cell, dict):
            continue
        row = int(cell.get("start_row_offset_idx", -1))
        column = int(cell.get("start_col_offset_idx", -1))
        row_span = max(1, int(cell.get("row_span", 1)))
        column_span = max(1, int(cell.get("col_span", 1)))
        for row_offset in range(row_span):
            for column_offset in range(column_span):
                covered.add((row + row_offset, column + column_offset))
    expected = {
        (row, column)
        for row in range(rows)
        for column in range(columns)
    }
    return {
        "rows": rows,
        "columns": columns,
        "cells": len(cells),
        "coveredCoordinates": len(covered & expected),
        "missingCoordinates": len(expected - covered),
    }


def summarize_response(response: dict[str, Any]) -> dict[str, Any]:
    wrapper = response.get("document")
    document = wrapper.get("json_content") if isinstance(wrapper, dict) else None
    if not isinstance(document, dict):
        raise ValueError("Docling response has no document.json_content.")
    ordered = reading_order(document)
    pages = document.get("pages")
    pages = pages if isinstance(pages, dict) else {}
    page_sizes = {
        int(page_number): (
            float(page.get("size", {}).get("width", 0)),
            float(page.get("size", {}).get("height", 0)),
        )
        for page_number, page in pages.items()
        if isinstance(page, dict)
    }
    label_counts: Counter[str] = Counter()
    character_counts: Counter[str] = Counter()
    missing_provenance = 0
    side_sequences: dict[int, list[str]] = defaultdict(list)
    tables: list[dict[str, Any]] = []
    for item in ordered:
        label = str(item.get("label") or "unknown")
        label_counts[label] += 1
        text = str(item.get("text") or item.get("orig") or "")
        character_counts[label] += len(text)
        if label == "table" or isinstance(item.get("data"), dict):
            tables.append(_table_metrics(item))
        provenance = _first_provenance(item)
        if provenance is None:
            missing_provenance += 1
            continue
        page_number = int(provenance.get("page_no", 1))
        bbox = provenance.get("bbox")
        width = page_sizes.get(page_number, (0.0, 0.0))[0]
        if not isinstance(bbox, dict) or width <= 0:
            continue
        center_x = (float(bbox.get("l", 0)) + float(bbox.get("r", 0))) / (2 * width)
        side = "L" if center_x <= 0.45 else "R" if center_x >= 0.55 else "C"
        if side != "C" and label not in {"page_header", "page_footer"}:
            sequence = side_sequences[page_number]
            if not sequence or sequence[-1] != side:
                sequence.append(side)

    return {
        "status": response.get("status"),
        "processingTimeSeconds": response.get("processing_time"),
        "probe": response.get("probe"),
        "pageCount": len(pages),
        "orderedItemCount": len(ordered),
        "itemsMissingProvenance": missing_provenance,
        "labelCounts": dict(sorted(label_counts.items())),
        "characterCountsByLabel": dict(sorted(character_counts.items())),
        "tables": tables,
        "pageColumnDiagnostics": [
            {
                "page": page_number,
                "collapsedSideSequence": sequence,
                "sideSwitches": max(0, len(sequence) - 1),
            }
            for page_number, sequence in sorted(side_sequences.items())
        ],
    }


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Export privacy-safe structural metrics from Docling responses."
    )
    parser.add_argument("response", nargs="+")
    parser.add_argument("--output")
    args = parser.parse_args()
    summaries = {
        Path(path).name: summarize_response(
            json.loads(Path(path).read_text(encoding="utf-8"))
        )
        for path in args.response
    }
    payload = json.dumps(summaries, ensure_ascii=False, indent=2) + "\n"
    if args.output:
        output_path = Path(args.output).resolve()
        output_path.parent.mkdir(parents=True, exist_ok=True)
        output_path.write_text(payload, encoding="utf-8")
    print(payload, end="")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
