"""Table adapter for the same bounded process owner as regional text OCR."""
from dataclasses import dataclass

from .region_process import BoundedRegionEngine
from .response_text_repair import validate_source_region
from .text_region_process import build_region_request, _decode_observation
from .table_observation_options import base_table_options


@dataclass(frozen=True)
class OcrRegionResult:
    lines: tuple
    duration_ms: int
    engine_version: str
    model_id: str
    observation: dict


def build_table_region_request(pdf_path, document, table, options, timeout_seconds):
    # Send only the physical region; no cell content, page pixels or OCR answer.
    item = {"self_ref": str(table.get("self_ref", "table")), "text": "",
            "label": "table", "prov": table.get("prov")}
    validate_source_region(document, item, options, reference_kind="tables")
    request = build_region_request(pdf_path, document, item, options, timeout_seconds, reference_kind="tables")
    request["operation"] = "table_rapid_only"
    request["options"] = base_table_options(options)
    return request


def decode_table_result(result):
    value = result["rapid"]
    observation = _decode_observation(value)
    if observation.engine != "rapidocr":
        raise ValueError("Table OCR engine identity invalid.")
    return OcrRegionResult(observation.lines, observation.duration_ms,
        observation.engine_version, observation.model_id, value)


class BoundedTableRegionEngine(BoundedRegionEngine):
    def __init__(self, shared_slot=None):
        super().__init__(build_table_region_request, decode_table_result,
            worker_module="saaia_docling.text_region_worker", shared_slot=shared_slot)
