from __future__ import annotations

from copy import deepcopy
import logging
from math import ceil, isfinite
from pathlib import Path
import time
from typing import Any, Protocol

from .text_repair import (
    TEXT_REPAIR_SCHEMA_VERSION, TextOcrObservation, assess_text_damage,
    repair_text_item, source_bounds, _observation_proof,
)
from .text_repair_options import TextRepairOptions


LOGGER = logging.getLogger("saaia_docling.text_repair")


class TextRegionEngine(Protocol):
    async def recognize(
        self, pdf_path: Path, document: dict[str, Any], item: dict[str, Any],
        options: TextRepairOptions, timeout_seconds: float,
    ) -> tuple[TextOcrObservation, TextOcrObservation]: ...


def validate_text_region(document: dict, item: dict, options: TextRepairOptions) -> float:
    provenance = item.get("prov")
    if not isinstance(provenance, list) or len(provenance) != 1 or not isinstance(provenance[0], dict):
        raise ValueError("source_geometry_invalid")
    page_number = provenance[0].get("page_no")
    if type(page_number) is not int or page_number < 1:
        raise ValueError("source_geometry_invalid")
    pages = document.get("pages")
    if not isinstance(pages, dict):
        raise ValueError("source_page_unavailable")
    page = pages.get(str(page_number))
    if not isinstance(page, dict):
        page = next((value for value in pages.values() if isinstance(value, dict) and value.get("page_no") == page_number), None)
    size = page.get("size") if isinstance(page, dict) else None
    if not isinstance(size, dict):
        raise ValueError("source_page_size_invalid")
    try:
        if any(isinstance(size[key], bool) for key in ("width", "height")):
            raise ValueError("source_page_size_invalid")
        width, height = float(size["width"]), float(size["height"])
    except (ValueError, TypeError, KeyError) as exc:
        raise ValueError("source_page_size_invalid") from exc
    if not all(isfinite(value) and value > 0 for value in (width, height)):
        raise ValueError("source_page_size_invalid")
    if ceil(width * options.render_scale) * ceil(height * options.render_scale) > options.max_rendered_pixels:
        raise ValueError("rendered_page_pixel_budget_exceeded")
    bounds = source_bounds(item, page_height=height)
    if bounds is None or bounds[2] > width or bounds[3] > height:
        raise ValueError("source_geometry_invalid")
    return height


def validate_physical_page_size(
    actual_width: float, actual_height: float, declared_width: float,
    declared_height: float, options: TextRepairOptions,
) -> None:
    values = actual_width, actual_height, declared_width, declared_height
    if (not all(not isinstance(value, bool) and isinstance(value, (int, float)) and isfinite(value) and value > 0 for value in values)
            or abs(actual_height - declared_height) > 1 or abs(actual_width - declared_width) > 1
            or ceil(actual_width * options.render_scale) * ceil(actual_height * options.render_scale) > options.max_rendered_pixels):
        raise ValueError("Physical PDF page exceeds or differs from the declared geometry.")


async def repair_response_texts(
    pdf_path: str | Path, response: dict[str, Any], engine: TextRegionEngine,
    *, options: TextRepairOptions, clock=time.monotonic,
) -> list[dict[str, Any]]:
    if not options.enabled:
        return []
    wrapper = response.get("document")
    document = wrapper.get("json_content") if isinstance(wrapper, dict) else None
    items = document.get("texts") if isinstance(document, dict) else None
    if not isinstance(items, list):
        return []
    started = clock()
    deadline = started + options.total_timeout_seconds
    summaries: list[dict[str, Any]] = []
    attempted = 0
    for index, item in enumerate(items):
        if clock() >= deadline:
            summaries.append({"schema_version": TEXT_REPAIR_SCHEMA_VERSION, "status": "not_applied",
                              "reason": "document_time_budget_exhausted", "uninspected_item_count": len(items) - index})
            break
        if not isinstance(item, dict) or item.get("label") in {"formula", "code"}:
            continue
        damage = assess_text_damage(item.get("text"))
        if not damage["candidate"]:
            continue
        summary = {"schema_version": TEXT_REPAIR_SCHEMA_VERSION, "source_ref": item.get("self_ref"),
                   "status": "not_applied", "reason": "region_budget_exhausted", "attempted": False}
        if attempted < options.max_regions:
            try:
                page_height = validate_text_region(document, item, options)
                attempted += 1
                summary["attempted"] = True
                remaining = min(options.region_timeout_seconds, deadline - clock())
                if remaining <= 0:
                    raise TimeoutError()
                rapid, secondary = await engine.recognize(Path(pdf_path), document, item, options, remaining)
                if clock() >= deadline:
                    raise TimeoutError()
                decision = repair_text_item(item, rapid, secondary,
                    minimum_rapid_score=options.minimum_rapid_score,
                    minimum_secondary_score=options.minimum_secondary_score,
                    page_height=page_height)
                summary.update(status="applied" if decision.applied else "not_applied", reason=decision.reason)
                if decision.applied:
                    items[index] = decision.item
                else:
                    # Private conversion provenance keeps both actual readings, including
                    # disagreement. It is never emitted in the content-free log/summary.
                    item["saaia_text_repair_attempt"] = {
                        **summary, "original_text": item["text"], "source_provenance": deepcopy(item.get("prov")),
                        "observations": [_observation_proof(rapid), _observation_proof(secondary)],
                    }
            except TimeoutError:
                summary["reason"] = "region_or_document_time_budget_exhausted"
            except Exception as exc:
                # Do not log OCR text, document paths, subprocess output or exception messages.
                summary["reason"] = str(exc) if isinstance(exc, ValueError) and str(exc) in {
                    "source_geometry_invalid", "source_page_unavailable", "source_page_size_invalid",
                    "rendered_page_pixel_budget_exceeded",
                } else "regional_engine_unavailable_or_invalid"
                LOGGER.warning("Regional text repair degraded error_type=%s", type(exc).__name__)
        if summary["status"] != "applied" and "saaia_text_repair_attempt" not in item:
            item["saaia_text_repair_attempt"] = {
                **summary, "original_text": item["text"], "source_provenance": deepcopy(item.get("prov")),
            }
        summaries.append(summary)
    response["saaia_text_repairs"] = summaries
    response["saaia_text_repair_summary"] = {
        "schema_version": TEXT_REPAIR_SCHEMA_VERSION,
        "candidate_count": sum("source_ref" in item for item in summaries),
        "attempted_count": attempted,
        "applied_count": sum(item["status"] == "applied" for item in summaries),
        "duration_ms": round((clock() - started) * 1_000),
        "limit_max_regions": options.max_regions,
        "limit_total_timeout_seconds": options.total_timeout_seconds,
    }
    return summaries
