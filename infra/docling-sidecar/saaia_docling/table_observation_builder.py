"""Exact source-bound, private regional readings. No cell or text rewrite."""
from copy import deepcopy
import hashlib
import json
import math


MAX_RAW_BYTES = 2_000_000
MAX_LINES = 5_000


class TableObservationLimitError(ValueError):
    def __init__(self, reason, raw_sha=None):
        super().__init__(reason)
        self.raw_sha = raw_sha


def _number(value):
    return type(value) in (int, float) and abs(value) <= 1_000_000 and math.isfinite(value)


def source_binding(document, table, index):
    if table.get("self_ref") != f"#/tables/{index}":
        raise ValueError("table_observation_source_reference_invalid")
    provenance = table.get("prov")
    if not isinstance(provenance, list) or len(provenance) != 1 or not isinstance(provenance[0], dict):
        raise ValueError("table_observation_source_region_invalid")
    page = provenance[0].get("page_no")
    if type(page) is not int or page <= 0:
        raise ValueError("table_observation_source_page_invalid")
    physical = document.get("pages", {}).get(str(page), {})
    size = physical.get("size", {})
    width, height = size.get("width"), size.get("height")
    if not all(_number(v) and v > 0 for v in (width, height)):
        raise ValueError("table_observation_source_page_invalid")
    box = provenance[0].get("bbox", {})
    if not isinstance(box, dict) or not all(_number(box.get(k)) for k in ("l", "t", "r", "b")):
        raise ValueError("table_observation_source_region_invalid")
    left, top, right, bottom = (box[k] for k in ("l", "t", "r", "b"))
    if box.get("coord_origin") == "BOTTOMLEFT":
        top, bottom = height - top, height - bottom
    elif box.get("coord_origin") != "TOPLEFT":
        raise ValueError("table_observation_source_region_invalid")
    if not 0 <= left < right <= width or not 0 <= top < bottom <= height:
        raise ValueError("table_observation_source_region_invalid")
    return {"source_table_ref": table["self_ref"], "page_no": page,
        "bbox": {"l": left, "t": top, "r": right, "b": bottom}, "coordinate_origin": "TOPLEFT",
        "page_width_points": width, "page_height_points": height}


def build_observation(binding, ocr):
    value = deepcopy(ocr.observation)
    if not isinstance(value, dict) or value.get("engine") != "rapidocr" or value.get("coordinate_origin") != "TOPLEFT":
        raise ValueError("table_observation_result_invalid")
    for key in ("engine_version", "model_id"):
        if not isinstance(value.get(key), str) or not 0 < len(value[key]) <= 256:
            raise ValueError("table_observation_engine_identity_invalid")
    lines = value.get("lines")
    if not isinstance(lines, list) or len(lines) != len(ocr.lines):
        raise ValueError("table_observation_line_inventory_invalid")
    if len(lines) > MAX_LINES:
        raise TableObservationLimitError("table_observation_line_limit")
    for line, decoded in zip(lines, ocr.lines):
        if not isinstance(line, dict) or not isinstance(line.get("text"), str):
            raise ValueError("table_observation_line_invalid")
        score, polygon = line.get("confidence"), line.get("polygon")
        if score is not None and (not _number(score) or not 0 <= score <= 1):
            raise ValueError("table_observation_line_invalid")
        if not isinstance(polygon, (list, tuple)) or len(polygon) != 4:
            raise ValueError("table_observation_line_invalid")
        for point in polygon:
            if not isinstance(point, (list, tuple)) or len(point) != 2 or not all(_number(v) for v in point):
                raise ValueError("table_observation_line_invalid")
            if not 0 <= point[0] <= binding["page_width_points"] or not 0 <= point[1] <= binding["page_height_points"]:
                raise ValueError("table_observation_line_outside_page")
        if line["text"] != decoded.text or score != decoded.score or tuple(map(tuple, polygon)) != tuple(decoded.polygon):
            raise ValueError("table_observation_decoded_result_mismatch")
    try:
        raw = json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False)
        encoded = raw.encode("utf-8", errors="strict")
    except (ValueError, UnicodeError, TypeError, OverflowError) as error:
        raise ValueError("table_observation_result_invalid") from error
    raw_sha = hashlib.sha256(encoded).hexdigest()
    if len(encoded) > MAX_RAW_BYTES:
        raise TableObservationLimitError("table_observation_raw_byte_limit", raw_sha)
    return {**binding, "raw_observation_json": raw,
        "raw_observation_sha256": raw_sha}


def tail_priority(table, binding):
    """Inspection priority only, never a claim that text is missing."""
    bottoms = []
    for cell in table.get("data", {}).get("table_cells", []):
        if not isinstance(cell, dict) or not str(cell.get("text", "")).strip():
            continue
        box = cell.get("bbox")
        if not isinstance(box, dict) or not _number(box.get("b")):
            continue
        bottom = box["b"]
        if box.get("coord_origin") == "BOTTOMLEFT":
            bottom = binding["page_height_points"] - bottom
        elif box.get("coord_origin") != "TOPLEFT":
            continue
        bottoms.append(bottom)
    region = binding["bbox"]
    return max(0, region["b"] - max(bottoms)) / (region["b"] - region["t"]) if bottoms else 0
