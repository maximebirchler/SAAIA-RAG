"""Generate pinned local PDF sources only; never starts Docling or ingestion."""
from __future__ import annotations

import argparse
import json
from pathlib import Path

from .fixtures import generate_fixtures


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", default="config/ingestion-acceptance-gold.v2.json")
    parser.add_argument("--output-dir", required=True)
    parser.add_argument("--font-path", required=True)
    args = parser.parse_args()
    output = Path(args.output_dir).resolve()
    if output.exists():
        raise FileExistsError("Use a fresh source directory; existing proofs are immutable.")
    entries = generate_fixtures(Path(args.manifest).resolve(), output, Path(args.font_path).resolve())
    proof = {"schemaVersion": "ingestion_gold_source_generation_v1", "sourceCount": len(entries),
             "pageAnnotationsIndependentOfExtraction": True, "serviceCalls": 0, "modelCalls": 0,
             "ingestionWorkerExecuted": False, "sqlPublicationValidated": False, "vectorValidated": False,
             "renderer": entries[0]["renderer"],
             "sources": [{"caseId": entry["case"]["caseId"], "sourceSha256": entry["sourceSha256"],
                          "fileName": entry["path"].name} for entry in entries]}
    (output / "generation-proof.json").write_text(json.dumps(proof, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"sourceCount": len(entries), "rendererVerified": proof["renderer"]["verified"],
                      "serviceCalls": 0, "modelCalls": 0}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
