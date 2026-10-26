"""Inspect/stop only the five exact-ID Linux qualification containers; never prune."""
from __future__ import annotations
import argparse
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from linux_owned_runtime import OwnedDocker, assert_owned_path, validate_owner


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--owner", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--execute", action="store_true")
    args = parser.parse_args()
    owner = validate_owner(json.loads(args.owner.read_text(encoding="utf-8-sig")))
    if not args.execute:
        print(json.dumps({"executeRequested": False, "project": owner["project"], "containerCount": len(owner["containers"]),
                          "stopOrder": ["backend", "docling", "tei", "qdrant", "postgres"], "dataOrImagesDeleted": False}))
        return 0
    if sys.platform != "linux":
        raise ValueError("Execute only on the dedicated Linux qualification host.")
    scope = Path(owner["root"])
    for path in (args.owner, args.output):
        assert_owned_path(path, scope)
    if args.output.exists() or not args.output.parent.is_dir():
        raise ValueError("A fresh stop proof in an existing owned directory is required.")
    proof = {"schemaVersion": "saaia-linux-owned-stop.v1", "passed": False}
    try:
        proof.update(OwnedDocker(owner, execute=True).stop())
        proof["passed"] = proof["ownedContainersStopped"] and proof["dockerPublishedPortBindingsAbsent"]
    except Exception as failure:
        proof["failureType"] = type(failure).__name__
    with args.output.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(proof, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    print(json.dumps(proof))
    return 0 if proof["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
