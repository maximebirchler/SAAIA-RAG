from __future__ import annotations

import argparse
import json
import time
from pathlib import Path

from docling.datamodel.base_models import InputFormat
from docling.datamodel.layout_model_specs import (
    DOCLING_LAYOUT_EGRET_LARGE,
    DOCLING_LAYOUT_EGRET_MEDIUM,
    DOCLING_LAYOUT_EGRET_XLARGE,
    DOCLING_LAYOUT_HERON,
    DOCLING_LAYOUT_HERON_101,
)
from docling.datamodel.pipeline_options import LayoutOptions, PdfPipelineOptions
from docling.document_converter import DocumentConverter, PdfFormatOption


MODEL_SPECS = {
    "heron": DOCLING_LAYOUT_HERON,
    "heron101": DOCLING_LAYOUT_HERON_101,
    "egret-medium": DOCLING_LAYOUT_EGRET_MEDIUM,
    "egret-large": DOCLING_LAYOUT_EGRET_LARGE,
    "egret-xlarge": DOCLING_LAYOUT_EGRET_XLARGE,
}


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Run one official Docling layout model on one PDF."
    )
    parser.add_argument("pdf", nargs="+")
    parser.add_argument("--model", choices=MODEL_SPECS, required=True)
    output_group = parser.add_mutually_exclusive_group(required=True)
    output_group.add_argument(
        "--output",
        help="Response JSON path. Valid only when exactly one PDF is provided.",
    )
    output_group.add_argument(
        "--output-dir",
        help="Directory receiving one <pdf-stem>.response.json file per PDF.",
    )
    parser.add_argument("--ocr", action="store_true")
    parser.add_argument(
        "--force-ocr",
        action="store_true",
        help="Force full-page OCR even when the PDF has a native text layer.",
    )
    args = parser.parse_args()

    options = PdfPipelineOptions()
    options.layout_options = LayoutOptions(model_spec=MODEL_SPECS[args.model])
    options.do_ocr = args.ocr or args.force_ocr
    if args.force_ocr:
        options.ocr_options.force_full_page_ocr = True
    options.do_table_structure = True
    options.table_structure_options.do_cell_matching = True
    options.generate_parsed_pages = True
    hierarchy = getattr(options, "heading_hierarchy_options", None)
    if hierarchy is not None:
        hierarchy.enabled = True
        hierarchy.use_bookmarks = True
        hierarchy.use_numbering = True
        hierarchy.use_style = True
        hierarchy.max_level = 6
        hierarchy.bookmark_match_threshold = 0.8

    converter = DocumentConverter(
        format_options={
            InputFormat.PDF: PdfFormatOption(pipeline_options=options)
        }
    )
    pdf_paths = [Path(value) for value in args.pdf]
    if args.output and len(pdf_paths) != 1:
        parser.error("--output requires exactly one PDF; use --output-dir for a batch.")
    output_dir = Path(args.output_dir) if args.output_dir else None
    if output_dir is not None:
        output_dir.mkdir(parents=True, exist_ok=True)

    for index, pdf_path in enumerate(pdf_paths):
        started = time.perf_counter()
        result = converter.convert(pdf_path)
        duration = time.perf_counter() - started
        payload = {
            "document": {
                "filename": pdf_path.name,
                "json_content": result.document.export_to_dict(),
            },
            "status": str(result.status.value),
            "processing_time": duration,
            "errors": [str(error) for error in result.errors],
            "probe": {
                "layoutModel": args.model,
                "durationSeconds": round(duration, 6),
                "batchIndex": index,
                "warm": index > 0,
            },
        }
        output_path = (
            Path(args.output)
            if args.output
            else output_dir / f"{pdf_path.stem}.response.json"
        )
        output_path.write_text(
            json.dumps(payload, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        print(json.dumps(payload["probe"], ensure_ascii=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
