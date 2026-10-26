"""Fail-closed campaign contracts; no service, PDF or model execution."""
from __future__ import annotations

import hashlib
import json
import re
from typing import Any


def validate_manifest(manifest: dict[str, Any]) -> list[dict[str, Any]]:
    if manifest.get("schemaVersion") == "ingestion_mixed_acceptance_v1":
        return _validate_mixed_manifest(manifest)
    if manifest.get("schemaVersion") not in {"ingestion_layout_gold_v1", "ingestion_acceptance_gold_v2", "ingestion_real_acceptance_v1"}:
        raise ValueError("Unsupported gold manifest schema.")
    cases = manifest.get("cases")
    if not isinstance(cases, list) or not cases:
        raise ValueError("A gold campaign must contain at least one case.")
    ids: set[str] = set()
    for case in cases:
        if not isinstance(case, dict):
            raise ValueError("Malformed gold case.")
        case_id = case.get("caseId")
        if not isinstance(case_id, str) or not re.fullmatch(r"[a-z0-9][a-z0-9-]{0,95}", case_id) or case_id in ids:
            raise ValueError("Gold case IDs must be unique safe filenames.")
        ids.add(case_id)
        if not isinstance(case.get("expectations"), dict):
            raise ValueError("Every gold case needs independent expectations.")
        facts = case["expectations"].get("criticalFacts", [])
        if not isinstance(facts, list):
            raise ValueError("Critical facts must be an explicit list.")
        from .critical_facts import critical_fact_issues
        critical_fact_issues(facts, {})  # Validate annotations, not their test outcome.
        if manifest["schemaVersion"] in {"ingestion_acceptance_gold_v2", "ingestion_real_acceptance_v1"}:
            if not facts:
                raise ValueError("Acceptance cases require independent exact, physical-page facts.")
            if not re.fullmatch(r"[a-f0-9]{64}", str(case.get("sourceSha256", ""))):
                raise ValueError("Acceptance cases require an immutable source hash.")
            if not isinstance(case.get("canonicalExpectations"), dict):
                raise ValueError("Acceptance cases require canonical expectations.")
            if case["canonicalExpectations"].get("requireProductionDefaults") is not True:
                raise ValueError("Acceptance cases must use production chunking defaults.")
            page_count = case["expectations"].get("pageCount")
            if type(page_count) is not int or page_count < 1 or any(fact["page"] > page_count for fact in facts):
                raise ValueError("Acceptance facts must lie within the positive physical page count.")
        if manifest["schemaVersion"] == "ingestion_real_acceptance_v1":
            annotations = case.get("sourceAnnotations")
            if (not isinstance(annotations, dict) or annotations.get("kind") != "source_pdf_review"
                    or annotations.get("sourceSha256") != case["sourceSha256"]
                    or not isinstance(annotations.get("basis"), str) or not annotations["basis"].strip()
                    or "generator" in case or "rendererPins" in manifest):
                raise ValueError("Real PDF expectations require a source-bound review, without synthetic renderer claims.")
    if manifest["schemaVersion"] == "ingestion_acceptance_gold_v2":
        pins = manifest.get("rendererPins")
        if not isinstance(pins, dict) or any(not isinstance(pins.get(key), str) or not pins[key]
                for key in ("generatorVersion", "pythonVersion", "reportlabVersion", "pillowVersion")):
            raise ValueError("Acceptance corpus requires renderer version pins.")
        hashes = [pins.get("fontSha256")]
        sources = pins.get("generatorSources")
        if not isinstance(sources, dict) or set(sources) != {"fixtures.py", "fixtures_fr.py", "renderer.py"}:
            raise ValueError("Acceptance corpus requires the complete generator source inventory.")
        hashes.extend(sources.values())
        if any(not isinstance(value, str) or not re.fullmatch(r"[a-f0-9]{64}", value) for value in hashes):
            raise ValueError("Acceptance corpus requires immutable font and generator hashes.")
    return cases


def _canonical_case(case: dict[str, Any]) -> bytes:
    try:
        return json.dumps(case, sort_keys=True, ensure_ascii=False, allow_nan=False,
                          separators=(",", ":")).encode("utf-8")
    except (TypeError, ValueError, UnicodeError):
        raise ValueError("Mixed acceptance cases must be finite UTF8 JSON.") from None


def _unique_json_pairs(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Original acceptance manifests cannot contain duplicate JSON keys.")
        result[key] = value
    return result


def _validate_mixed_manifest(manifest: dict[str, Any]) -> list[dict[str, Any]]:
    """Keep original cases and contracts; only execution-level provenance is new.

    SHA checks bind exact embedded original bytes and case membership. Trust in
    these declared origins still requires pinning the execution manifest itself.
    """
    cases, records, bindings = (manifest.get(name) for name in ("cases", "originManifests", "caseOrigins"))
    if (not isinstance(cases, list) or not 1 <= len(cases) <= 1000
            or not isinstance(records, list) or not 1 <= len(records) <= 32
            or not isinstance(bindings, dict) or "rendererPins" in manifest):
        raise ValueError("Mixed acceptance requires bounded cases, original manifests and case provenance.")
    ids = [case.get("caseId") if isinstance(case, dict) else None for case in cases]
    if (any(not isinstance(case_id, str) or not re.fullmatch(r"[a-z0-9][a-z0-9-]{0,95}", case_id) for case_id in ids)
            or len(set(ids)) != len(ids) or set(bindings) != set(ids)):
        raise ValueError("Mixed acceptance needs unique safe cases and exact provenance coverage.")
    origins = {}
    provenance = {"ingestion_acceptance_gold_v2": "synthetic_renderer",
                  "ingestion_real_acceptance_v1": "source_pdf_review"}
    for record in records:
        if not isinstance(record, dict):
            raise ValueError("Malformed original acceptance manifest record.")
        origin_id, schema = record.get("originId"), record.get("schemaVersion")
        text, expected = record.get("manifestUtf8"), record.get("manifestSha256")
        if (not isinstance(origin_id, str) or not re.fullmatch(r"[a-z0-9][a-z0-9-]{0,95}", origin_id)
                or origin_id in origins or not isinstance(schema, str) or schema not in provenance
                or record.get("provenance") != provenance[schema]
                or not isinstance(text, str) or not isinstance(expected, str)
                or not re.fullmatch(r"[a-f0-9]{64}", expected)):
            raise ValueError("Original acceptance schema, provenance and SHA must be explicit and unique.")
        try:
            raw = text.encode("utf-8")
        except UnicodeError:
            raise ValueError("Original acceptance manifest must be exact UTF8.") from None
        if not 1 <= len(raw) <= 2_097_152 or hashlib.sha256(raw).hexdigest() != expected:
            raise ValueError("Original acceptance manifest bytes differ from their declared SHA.")
        try:
            def invalid_constant(value):
                raise ValueError("Nonfinite JSON value: " + value)
            original = json.loads(raw, object_pairs_hook=_unique_json_pairs, parse_constant=invalid_constant)
        except (ValueError, UnicodeError):
            raise ValueError("Original acceptance manifest is invalid JSON.") from None
        if not isinstance(original, dict) or original.get("schemaVersion") != schema:
            raise ValueError("Original acceptance manifest has a mismatched schema.")
        # Reuse the existing strict contract, including original Gold rendererPins.
        validated = validate_manifest(original)
        origins[origin_id] = (expected, {case["caseId"]: case for case in validated})
    used = set()
    for case in cases:
        binding = bindings[case["caseId"]]
        if (not isinstance(binding, dict) or not isinstance(binding.get("originId"), str)
                or binding.get("originId") not in origins
                or binding.get("originCaseId") != case["caseId"]):
            raise ValueError("Mixed acceptance case must name its exact original case and manifest.")
        origin_id = binding["originId"]
        original_sha, original_cases = origins[origin_id]
        original = original_cases.get(binding["originCaseId"])
        if original is None or binding.get("originManifestSha256") != original_sha:
            raise ValueError("Mixed acceptance original case or manifest binding changed.")
        original_bytes, current_bytes = _canonical_case(original), _canonical_case(case)
        if (binding.get("originCaseSha256") != hashlib.sha256(original_bytes).hexdigest()
                or current_bytes != original_bytes):
            raise ValueError("Mixed acceptance cannot modify original sources, expectations or provenance.")
        used.add(origin_id)
    if used != set(origins):
        raise ValueError("Mixed acceptance cannot declare an unused original manifest.")
    return cases


def select_cases(cases: list[dict[str, Any]], requested: list[str]) -> list[dict[str, Any]]:
    if len(requested) != len(set(requested)):
        raise ValueError("A case may be selected only once.")
    missing = set(requested) - {case["caseId"] for case in cases}
    if missing:
        raise ValueError("Unknown case ID(s): " + ", ".join(sorted(missing)))
    selected = [case for case in cases if not requested or case["caseId"] in requested]
    if not selected:
        raise ValueError("A gold campaign may not select zero cases.")
    return selected


def campaign_report(results: list[dict[str, Any]], expected_ids: list[str], *, canonical_required: bool,
                    renderer_pinned: bool) -> dict[str, Any]:
    actual = [result.get("caseId") for result in results]
    complete = bool(expected_ids) and len(expected_ids) == len(set(expected_ids)) and sorted(actual) == sorted(expected_ids)
    canonical = [result.get("canonical") for result in results if isinstance(result.get("canonical"), dict)]
    diagnostic_ok = complete and all(result.get("ok") is True for result in results)
    qualified = (diagnostic_ok and canonical_required is True and renderer_pinned is True
                 and len(canonical) == len(expected_ids)
                 and all(result.get("ok") is True for result in canonical))
    return {
        "schemaVersion": "ingestion_layout_gold_report_v3",
        "ok": qualified,
        "diagnosticOk": diagnostic_ok,
        "qualificationComplete": qualified,
        "qualificationScope": "docling_conversion_and_canonical_projection_only",
        "ingestionWorkerExecuted": False,
        "sqlPublicationValidated": False,
        "vectorValidated": False,
        "expectedCaseCount": len(expected_ids),
        "caseCount": len(results),
        "caseCoverageComplete": complete,
        "canonicalRequired": canonical_required,
        "rendererPinned": renderer_pinned,
        "passedCaseCount": sum(result.get("ok") is True for result in results),
        "rawPassedCaseCount": sum(result.get("raw", {}).get("ok") is True for result in results),
        "canonicalCaseCount": len(canonical),
        "canonicalPassedCaseCount": sum(result.get("ok") is True for result in canonical),
        "results": results,
    }
