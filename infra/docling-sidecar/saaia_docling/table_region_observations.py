"""One bounded table-work scheduler for repairs and secondary observations."""
from copy import deepcopy
import hashlib
from pathlib import Path

from .response_text_repair import validate_source_region
from .table_observation_builder import TableObservationLimitError, build_observation, source_binding, tail_priority
from .table_observation_options import observation_settings
from .table_repair import REPAIR_SCHEMA_VERSION

MAX_INSPECTED_TABLES = 1024
SAFE_ERRORS = {"source_geometry_invalid", "source_page_unavailable", "source_page_size_invalid",
    "rendered_page_pixel_budget_exceeded", "table_observation_raw_byte_limit"}


def _source_sha(path, clock, deadline):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        while True:
            if clock() >= deadline:
                raise TimeoutError()
            block = stream.read(1024 * 1024)
            if not block:
                return digest.hexdigest()
            digest.update(block)


async def collect_table_observations(pdf_path, response, engine, options, clock, sparse_detector, repairer):
    started = clock()
    deadline = started + options.total_timeout_seconds
    wrapper = response.get("document")
    document = wrapper.get("json_content") if isinstance(wrapper, dict) else None
    if not isinstance(document, dict):
        return []
    tables = document.get("tables")
    envelope = {"schema_version": "saaia_table_region_observations_v1", "availability": "unavailable",
        "reason": "tables_inventory_unavailable", "source_sha256": None,
        "settings": observation_settings(options), "source_table_count": None, "inspected_table_count": None,
        "uninspected_table_count": None, "attempted_region_count": None,
        "completed_observation_count": None, "attempts": None, "observations": None}
    try:
        envelope["source_sha256"] = _source_sha(pdf_path, clock, deadline)
    except TimeoutError:
        envelope["reason"] = "source_hash_time_budget_exhausted"
        document["saaia_table_region_observations"] = envelope
        return []
    if not isinstance(tables, list):
        document["saaia_table_region_observations"] = envelope
        return []

    attempts, candidates, summaries, observations = [], [], [], []
    inspected = attempted = repair_attempted = 0
    for index, table in enumerate(tables):
        if index >= MAX_INSPECTED_TABLES or clock() >= deadline:
            break
        inspected += 1
        if not isinstance(table, dict) or table.get("self_ref") != f"#/tables/{index}":
            envelope["reason"] = "table_source_inventory_invalid"
            document["saaia_table_region_observations"] = envelope
            return []
        attempt = {"source_table_ref": table["self_ref"], "page_no": None,
            "attempted": False, "status": "refused", "reason": "source_region_invalid",
            "raw_observation_sha256": None}
        attempts.append(attempt)
        try:
            binding = source_binding(document, table, index)
            attempt["page_no"] = binding["page_no"]
            validate_source_region(document, table, options, reference_kind="tables")
            sparse = bool(getattr(options, "repair_enabled", True) and sparse_detector(table))
            attempt.update(status="not_attempted", reason="region_budget_exhausted")
            candidates.append((not sparse, 0 if sparse else -tail_priority(table, binding),
                index, binding, sparse, attempt))
        except ValueError as error:
            attempt["reason"] = str(error) if str(error) in SAFE_ERRORS else "source_region_invalid"

    # Sparse repairs retain their legacy source order as well as priority.
    # Complete tables may use the tail inspection priority only afterwards,
    # within the same remaining attempt and wall-clock budgets.
    for _, _, index, binding, sparse, attempt in sorted(candidates, key=lambda row: row[:3]):
        table = tables[index]
        summary = {"schema_version": REPAIR_SCHEMA_VERSION, "table_ref": table["self_ref"],
            "status": "not_applied", "reason": "region_budget_exhausted", "attempted": False}
        if sparse:
            summaries.append(summary)
        remaining = min(options.region_timeout_seconds, deadline - clock())
        if attempted >= options.max_regions or remaining <= 0:
            attempt["reason"] = "region_budget_exhausted" if remaining > 0 else "document_time_budget_exhausted"
            continue
        original = deepcopy(table)
        completed = None
        try:
            attempted += 1
            attempt["attempted"] = True
            if sparse:
                repair_attempted += 1
                summary["attempted"] = True
            ocr = await engine.recognize(Path(pdf_path), document, table, options, remaining)
            if clock() >= deadline:
                raise TimeoutError()
            completed = build_observation(binding, ocr)
            if clock() >= deadline:
                raise TimeoutError()
            if sparse:
                result = repairer(table, ocr.lines, engine_version=ocr.engine_version,
                    model_id=ocr.model_id, duration_ms=ocr.duration_ms, minimum_score=options.minimum_score)
                if clock() >= deadline:
                    raise TimeoutError()
                summary.update(status="applied" if result.applied else "not_applied", reason=result.reason,
                    missing_coordinates=[list(c) for c in result.missing_coordinates],
                    repaired_coordinates=[list(c) for c in result.repaired_coordinates],
                    added_coordinates=[list(c) for c in result.added_coordinates], ocr_duration_ms=ocr.duration_ms)
                if result.applied:
                    result.table["saaia_table_repair"]["ocr_observation"] = deepcopy(ocr.observation)
                else:
                    result.table["saaia_table_repair_attempt"] = {**deepcopy(summary),
                        "ocr_observation": deepcopy(ocr.observation)}
                tables[index] = result.table
            observations.append(completed)
            attempt.update(status="completed", reason=None,
                raw_observation_sha256=completed["raw_observation_sha256"])
        except TimeoutError:
            tables[index] = original
            attempt.update(status="timeout", reason="region_or_document_time_budget_exhausted")
            summary["reason"] = "region_or_document_time_budget_exhausted"
        except TableObservationLimitError as error:
            tables[index] = original
            attempt.update(status="refused", reason=str(error), raw_observation_sha256=error.raw_sha)
            summary["reason"] = str(error)
        except Exception as error:
            tables[index] = original
            reason = str(error) if isinstance(error, ValueError) and str(error) in SAFE_ERRORS else "regional_engine_unavailable_or_invalid"
            attempt.update(status="error", reason=reason)
            summary.update(status="error", reason=reason, error_type=type(error).__name__)
        if sparse and summary["status"] != "applied" and attempt["status"] != "completed":
            tables[index]["saaia_table_repair_attempt"] = deepcopy(summary)

    uninspected = len(tables) - inspected
    if uninspected:
        summaries.append({"schema_version": REPAIR_SCHEMA_VERSION, "status": "not_applied",
            "reason": "document_time_or_scan_budget_exhausted", "uninspected_table_count": uninspected})
    elapsed = clock() - started
    response["saaia_table_repairs"] = summaries
    response["saaia_table_repair_summary"] = {"schema_version": REPAIR_SCHEMA_VERSION,
        "inspected_table_count": inspected, "uninspected_table_count": uninspected,
        "candidate_count": sum("table_ref" in row for row in summaries), "attempted_count": repair_attempted,
        "applied_count": sum(row["status"] == "applied" for row in summaries),
        "duration_ms": round(elapsed * 1000), "limit_max_regions": options.max_regions,
        "limit_total_timeout_seconds": options.total_timeout_seconds}
    timings = response.setdefault("timings", {})
    if isinstance(timings, dict):
        timings["saaia_table_repair"] = {"scope": "document", "count": 1, "times": [elapsed]}
    complete = uninspected == 0 and all(row["status"] == "completed" for row in attempts)
    envelope.update(availability="observed" if complete else "partial", reason=None if complete else "regional_work_incomplete",
        source_table_count=len(tables), inspected_table_count=inspected, uninspected_table_count=uninspected,
        attempted_region_count=attempted, completed_observation_count=len(observations),
        attempts=attempts, observations=observations)
    document["saaia_table_region_observations"] = envelope
    return summaries
