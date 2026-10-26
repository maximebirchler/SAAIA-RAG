"""Plan-scoped Qdrant identity, matching IngestionVectorPlan/IdUtil (.NET Guid)."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import uuid


def vector_point_id(canonical_chunk_id: str, plan_fingerprint: str) -> str:
    if not isinstance(plan_fingerprint, str) or not re.fullmatch(r"[a-f0-9]{64}", plan_fingerprint):
        raise ValueError("A full lowercase active vector-plan fingerprint is required.")
    canonical = uuid.UUID(canonical_chunk_id)
    wire = f"qdrant|{canonical.hex}|{plan_fingerprint}".encode("utf-8")
    # C# new Guid(first16Bytes) uses little endian for its first three fields.
    return str(uuid.UUID(bytes_le=hashlib.sha256(wire).digest()[:16]))


def point_identity_manifest(observation: dict) -> dict:
    if observation.get("schemaVersion") != "saaia-owned-worker-observation.v1":
        raise ValueError("Unknown owned observation schema.")
    result = {}
    seen = set()
    for row in observation["documents"]:
        path = row["docPath"]
        if path in result:
            raise ValueError("Duplicate document scope.")
        jobs = [job for job in observation["jobs"] if job["docPath"] == path and job["status"] == "done"]
        plan = row.get("activeVectorPlanFingerprint")
        if len(jobs) != 1 or plan != jobs[0].get("payload", {}).get("resume", {}).get("vectorPlanFingerprint"):
            raise ValueError("Published document plan differs from its completed job.")
        points = []
        for chunk in row["chunks"]:
            canonical = str(uuid.UUID(chunk["retrievalChunkId"]))
            if canonical in seen:
                raise ValueError("Canonical SQL chunk IDs must be unique.")
            seen.add(canonical)
            points.append({"canonicalChunkId": canonical, "pointId": vector_point_id(canonical, plan), "planFingerprint": plan})
        if not points:
            raise ValueError("Published documents require nonempty canonical chunks.")
        result[path] = points
    if not result:
        raise ValueError("An empty point manifest cannot validate publication.")
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--observation", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = point_identity_manifest(json.loads(args.observation.read_text(encoding="utf-8-sig")))
    with args.output.open("x", encoding="utf-8", newline="") as target:
        target.write(json.dumps(result, indent=2) + "\n")
    print(json.dumps({"documentCount": len(result), "pointCount": sum(map(len, result.values())), "serviceCalls": 0}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
