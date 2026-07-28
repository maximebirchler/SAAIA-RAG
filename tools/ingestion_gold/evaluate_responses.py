from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

from .canonical_evaluator import evaluate_canonical_snapshot
from .evaluator import evaluate_case


def evaluate_response_directory(
    manifest_path: Path,
    response_dir: Path,
    canonical_dir: Path | None = None,
    require_canonical: bool = False,
) -> dict[str, Any]:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    resolved_canonical_dir = canonical_dir or response_dir
    results: list[dict[str, Any]] = []
    for case in manifest.get("cases", []):
        case_id = str(case["caseId"])
        response_path = response_dir / f"{case_id}.response.json"
        if not response_path.is_file():
            results.append(
                {
                    "caseId": case_id,
                    "ok": False,
                    "issues": [
                        {
                            "check": "response_file",
                            "message": f"Missing response file {response_path.name}.",
                        }
                    ],
                    "metrics": {},
                }
            )
            continue
        response = json.loads(response_path.read_text(encoding="utf-8"))
        raw_result = evaluate_case(response, case)
        canonical_path = (
            resolved_canonical_dir / f"{case_id}.canonical.json"
        )
        canonical_result: dict[str, Any] | None = None
        if canonical_path.is_file():
            canonical_snapshot = json.loads(
                canonical_path.read_text(encoding="utf-8")
            )
            canonical_result = evaluate_canonical_snapshot(
                canonical_snapshot,
                case,
            )
        elif require_canonical:
            canonical_result = {
                "ok": False,
                "issues": [
                    {
                        "check": "canonical_file",
                        "message": (
                            f"Missing canonical snapshot "
                            f"{canonical_path.name}."
                        ),
                    }
                ],
                "metrics": {},
            }
        probe = response.get("probe")
        effective_result = canonical_result or raw_result
        results.append(
            {
                "caseId": case_id,
                "ok": effective_result["ok"],
                "raw": raw_result,
                **(
                    {"canonical": canonical_result}
                    if canonical_result is not None
                    else {}
                ),
                **({"probe": probe} if isinstance(probe, dict) else {}),
            }
        )
    canonical_results = [
        result["canonical"]
        for result in results
        if isinstance(result.get("canonical"), dict)
    ]
    return {
        "schemaVersion": "ingestion_layout_gold_report_v2",
        "ok": all(result["ok"] for result in results),
        "caseCount": len(results),
        "passedCaseCount": sum(1 for result in results if result["ok"]),
        "rawPassedCaseCount": sum(
            1 for result in results if result.get("raw", {}).get("ok")
        ),
        "canonicalCaseCount": len(canonical_results),
        "canonicalPassedCaseCount": sum(
            1 for result in canonical_results if result.get("ok")
        ),
        "results": results,
    }


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Evaluate previously captured Docling responses."
    )
    parser.add_argument(
        "--manifest",
        default="config/ingestion-layout-gold.v1.json",
    )
    parser.add_argument("--response-dir", required=True)
    parser.add_argument("--canonical-dir")
    parser.add_argument("--require-canonical", action="store_true")
    parser.add_argument("--output")
    args = parser.parse_args()

    report = evaluate_response_directory(
        Path(args.manifest).resolve(),
        Path(args.response_dir).resolve(),
        (
            Path(args.canonical_dir).resolve()
            if args.canonical_dir
            else None
        ),
        args.require_canonical,
    )
    payload = json.dumps(report, ensure_ascii=False, indent=2) + "\n"
    if args.output:
        output_path = Path(args.output).resolve()
        output_path.parent.mkdir(parents=True, exist_ok=True)
        output_path.write_text(payload, encoding="utf-8")
    print(payload, end="")
    return 0 if report["ok"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
