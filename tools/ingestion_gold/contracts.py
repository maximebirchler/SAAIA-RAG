"""Fail-closed campaign contracts; no service, PDF or model execution."""
from __future__ import annotations

import re
from typing import Any


def validate_manifest(manifest: dict[str, Any]) -> list[dict[str, Any]]:
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
