"""Inspect disclosed holdout results offline without changing the sealed verdict.

Output contains opaque case IDs and counters only. It separates the execution
route from the final response outcome; it does not reassess semantic oracles.
"""

import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path


def execution_route(row):
    source = str(row.get("answerSource") or "").lower()
    if source.startswith("advanced_analysis:server"):
        return "advanced"
    if "advanced_analysis_required" in source:
        return "handoff_only"
    return "local"


def final_outcome(row):
    source = str(row.get("answerSource") or "").lower()
    if source.startswith("advanced_analysis:server"):
        if str(row.get("advancedStatus") or "").lower() != "succeeded":
            return "unknown"
        outcome = str(row.get("advancedResultOutcome") or "").lower()
        return {
            "answered": "answer",
            "clarification_required": "clarification",
            "insufficient_documentation": "insufficiency",
        }.get(outcome, "unknown")
    if "clarification" in source:
        return "clarification"
    if "insufficient_evidence" in source or "missing_document" in source:
        return "insufficiency"
    if "advanced_analysis_required" in source:
        return "capability_required"
    if source.startswith("source_backed_pipeline:") or source.startswith("rag.answer"):
        return "answer"
    return "unknown"


def inspect(payload):
    cases = payload["cases"]
    results = payload["results"]
    expected_ids = [case["id"] for case in cases]
    rows = [item["row"] for item in results]
    observed_ids = [row["id"] for row in rows]
    if len(set(expected_ids)) != len(expected_ids) or len(set(observed_ids)) != len(observed_ids):
        raise ValueError("Duplicate case IDs")
    if set(expected_ids) != set(observed_ids):
        raise ValueError("Incomplete case/result association")
    by_id = {row["id"]: row for row in rows}
    diagnostics = []
    for case in cases:
        row = by_id[case["id"]]
        expected = case["expectedTerminal"]
        expected_final = "answer" if expected in {"local_direct", "advanced"} else expected
        expected_route = {"local_direct": "local", "advanced": "advanced"}.get(expected)
        actual_final = final_outcome(row)
        actual_route = execution_route(row)
        diagnostics.append({
            "id": case["id"],
            "expectedFinalOutcome": expected_final,
            "observedFinalOutcome": actual_final,
            "finalOutcomeMatches": expected_final == actual_final,
            "expectedAnswerRoute": expected_route,
            "observedExecutionRoute": actual_route,
            "answerRouteMatches": None if expected_route is None else expected_route == actual_route,
        })
    decisions = payload["semanticDecisions"]
    fields = ("pass", "terminalPass", "semanticPass", "allVerifiableClaimsSupported",
              "citationsOpenableAndUnsubstituted", "cardinalityPass", "languagePass")
    return {
        "schemaVersion": "saaia-consumed-holdout-diagnostics-v1",
        "purpose": "offline_diagnostics_only_not_an_acceptance_verdict",
        "oracleFairnessAudited": False,
        "candidateCommit": payload["candidateCommit"],
        "executed": len(rows),
        "originalEvaluatorTrueCounts": {field: sum(d.get(field) is True for d in decisions) for field in fields},
        "observedFinalOutcomeCounts": dict(Counter(d["observedFinalOutcome"] for d in diagnostics)),
        "finalOutcomeMatchesDeclaredOracle": sum(d["finalOutcomeMatches"] for d in diagnostics),
        "answerRouteExpectedCount": sum(d["expectedAnswerRoute"] is not None for d in diagnostics),
        "answerRouteMatchesDeclaredOracle": sum(d["answerRouteMatches"] is True for d in diagnostics),
        "cases": diagnostics,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("payload", type=Path, help="Already disclosed private-payload.json")
    parser.add_argument("--sealed-verdict", type=Path, required=True, help="Existing public verdict recorded before disclosure")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.payload.resolve() == args.output.resolve():
        parser.error("Output must not overwrite the input")
    verdict_raw = args.sealed_verdict.read_bytes()
    verdict = json.loads(verdict_raw)
    if verdict.get("bankConsumed") is not True or verdict.get("verdictWrittenBeforeDisclosure") is not True:
        parser.error("Diagnostics require a consumed bank and an existing sealed verdict")
    raw = args.payload.read_bytes()
    result = inspect(json.loads(raw))
    if result["candidateCommit"] != verdict.get("candidateCommit") or result["executed"] != verdict.get("executed"):
        parser.error("Payload and sealed verdict do not describe the same execution")
    result["sealedVerdictSha256"] = hashlib.sha256(verdict_raw).hexdigest().upper()
    result["inputPayloadSha256"] = hashlib.sha256(raw).hexdigest().upper()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(result, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    print(json.dumps({key: value for key, value in result.items() if key != "cases"}, ensure_ascii=False))


if __name__ == "__main__":
    main()
