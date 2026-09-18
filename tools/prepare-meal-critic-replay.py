#!/usr/bin/env python3
"""Build a secret-free private fixture for a paid Terra Critic replay.

The source traces stay local.  This script copies only the prior planner JSON,
the terminal Writer candidate, the visible evidence payload, and the request
shape required to reproduce the Critic input.  Authorization headers,
encrypted reasoning, native tool history, and provider envelopes are excluded.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def read_trace(path: Path) -> tuple[dict, bytes]:
    raw = path.read_bytes()
    return json.loads(raw.decode("utf-8-sig")), raw


def compact_json(value: object) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--planner-trace", required=True, type=Path)
    parser.add_argument("--writer-trace", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

    planner_trace, planner_raw = read_trace(args.planner_trace)
    writer_trace, writer_raw = read_trace(args.writer_trace)
    writer_request = json.loads(writer_trace["requestJson"])
    user_messages = [
        item for item in writer_request.get("input", [])
        if item.get("role") == "user" and isinstance(item.get("content"), str)
    ]
    if not user_messages:
        raise ValueError("The Writer trace contains no user payload.")
    prompt = json.loads(user_messages[-1]["content"])
    candidate = json.loads(writer_trace["completionJson"])
    planner = json.loads(planner_trace["completionJson"])
    evidence = prompt.get("evidence")
    if not isinstance(evidence, list) or not evidence:
        raise ValueError("The Writer trace contains no visible evidence.")
    claims = candidate.get("claims")
    if not isinstance(claims, list) or len(claims) != 20:
        raise ValueError("The captured candidate is not the expected 20-claim grid.")
    evidence_ids = {item.get("evidenceId") for item in evidence}
    cited_ids = {
        evidence_id
        for claim in claims
        for evidence_id in claim.get("evidenceIds", [])
    }
    missing = sorted(cited_ids - evidence_ids)
    if missing:
        raise ValueError(f"Candidate cites absent evidence: {missing}")

    planner_json = compact_json(planner)
    candidate_json = compact_json(candidate)
    fixture = {
        "schema": "saaia.meal-critic-replay.v1",
        "source": {
            "plannerTraceSha256": sha256_bytes(planner_raw),
            "writerTraceSha256": sha256_bytes(writer_raw),
            "plannerCompletionSha256": sha256_bytes(planner_json.encode("utf-8")),
            "writerCandidateSha256": sha256_bytes(candidate_json.encode("utf-8")),
        },
        "requestText": prompt["request"],
        "language": prompt.get("language", "fr"),
        "load": prompt["load"],
        "plannerCompletionJson": planner_json,
        "writerCandidateJson": candidate_json,
        "evidence": evidence,
        "evidenceCount": len(evidence),
        "candidateClaimCount": len(claims),
        "candidateCitedEvidenceCount": len(cited_ids),
        "containsAuthorizationHeader": False,
        "containsEncryptedReasoning": False,
        "productStatus": "TESTE_NON_APPROUVE",
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(fixture, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    print(json.dumps({
        "output": str(args.output.resolve()),
        "fixtureSha256": sha256_bytes(args.output.read_bytes()),
        "evidenceCount": len(evidence),
        "candidateClaimCount": len(claims),
        "candidateCitedEvidenceCount": len(cited_ids),
    }, indent=2))


if __name__ == "__main__":
    main()
