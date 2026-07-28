from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

from .evaluator import evaluate_case


def evaluate_response_directory(
    manifest_path: Path,
    response_dir: Path,
) -> dict[str, Any]:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
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
        result = evaluate_case(response, case)
        probe = response.get("probe")
        results.append(
            {
                "caseId": case_id,
                **result,
                **({"probe": probe} if isinstance(probe, dict) else {}),
            }
        )
    return {
        "schemaVersion": "ingestion_layout_gold_report_v1",
        "ok": all(result["ok"] for result in results),
        "caseCount": len(results),
        "passedCaseCount": sum(1 for result in results if result["ok"]),
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
    parser.add_argument("--output")
    args = parser.parse_args()

    report = evaluate_response_directory(
        Path(args.manifest).resolve(),
        Path(args.response_dir).resolve(),
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
