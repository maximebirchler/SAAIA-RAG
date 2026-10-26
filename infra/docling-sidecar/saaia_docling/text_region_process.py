from __future__ import annotations

from dataclasses import asdict
from pathlib import Path
from typing import Any

from .region_process import BoundedRegionEngine
from .table_repair import OcrLine
from .response_text_repair import validate_source_region
from .text_repair import TextOcrObservation
from .text_repair_options import TextRepairOptions


def build_region_request(pdf_path, document, item, options, timeout_seconds, *, reference_kind="texts"):
    validate_source_region(document, item, options, reference_kind=reference_kind)
    page_number = item["prov"][0]["page_no"]
    page = document["pages"].get(str(page_number))
    if not isinstance(page, dict):
        page = next(value for value in document["pages"].values()
                    if isinstance(value, dict) and value.get("page_no") == page_number)
    if not isinstance(item.get("text"), str) or len(item["text"]) > 8_000:
        raise ValueError("Regional OCR request text exceeds its budget.")
    source_region = item["prov"][0]
    return {
        "pdf_path": str(pdf_path),
        "document": {"pages": {str(page_number): {"page_no": page_number,
            "size": {key: page["size"][key] for key in ("width", "height")}}}},
        "item": {"self_ref": item["self_ref"], "text": item["text"], "label": item.get("label"),
                 "prov": [{"page_no": page_number,
                           "bbox": {key: source_region["bbox"][key] for key in ("l", "t", "r", "b", "coord_origin")
                                    if key in source_region["bbox"]}}]},
        "options": asdict(options),
        "timeout_seconds": max(0.1, timeout_seconds - 1),
    }


class BoundedTextRegionEngine(BoundedRegionEngine):
    def __init__(self, shared_slot=None) -> None:
        super().__init__(build_region_request, _decode_text_pair,
            worker_module="saaia_docling.text_region_worker", shared_slot=shared_slot)


def _decode_text_pair(result):
    return _decode_observation(result["rapid"]), _decode_observation(result["secondary"])


def _decode_observation(value: dict[str, Any]) -> TextOcrObservation:
    if not isinstance(value, dict) or not isinstance(value.get("lines"), list):
        raise ValueError("OCR observation schema is invalid.")
    lines = tuple(OcrLine(line["text"], line["confidence"], tuple(tuple(point) for point in line["polygon"])) for line in value["lines"])
    return TextOcrObservation(
        value["engine"], value["engine_version"], value["model_id"], lines,
        value.get("duration_ms", 0), value.get("model_sha256"), value.get("model_artifacts"), value.get("runtime_settings"),
    )
