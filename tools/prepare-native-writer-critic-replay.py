#!/usr/bin/env python3
"""Prepare a private Critic fixture from a closed native-read Writer replay."""

from __future__ import annotations

import argparse
import hashlib
import json
from collections import defaultdict
from pathlib import Path


def digest(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def compact(value: object) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--native-input", required=True, type=Path)
    parser.add_argument("--writer-result", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

    native_raw = args.native_input.read_bytes()
    writer_raw = args.writer_result.read_bytes()
    native = json.loads(native_raw.decode("utf-8-sig"))
    writer = json.loads(writer_raw.decode("utf-8-sig"))
    candidate = writer["completion"]
    claims = candidate["claims"]
    if len(claims) != 20:
        raise ValueError("Expected a 20-claim Writer candidate.")

    load = native["handoff"]["load"]
    columns = load["columns"]
    targets: dict[str, set[str]] = defaultdict(set)
    cited_order: list[str] = []
    for claim in claims:
        claim_number = int(claim["claimId"][1:])
        column = columns[(claim_number - 1) % len(columns)]
        for evidence_id in claim["evidenceIds"]:
            targets[evidence_id].add(column)
            if evidence_id not in cited_order:
                cited_order.append(evidence_id)

    found: dict[str, tuple[dict, dict]] = {}
    for event in native["events"]:
        for evidence in event.get("evidence", []):
            evidence_id = evidence["reference"]["evidenceId"]
            found.setdefault(evidence_id, (event, evidence))
    missing = sorted(set(cited_order) - set(found))
    if missing:
        raise ValueError(f"Candidate cites evidence absent from native input: {missing}")

    source_keys: dict[tuple[str, str], str] = {}
    fixture_evidence: list[dict] = []
    for evidence_id in cited_order:
        event, evidence = found[evidence_id]
        reference = evidence["reference"]
        identity = (reference["docId"], reference["revisionId"])
        source_key = source_keys.setdefault(
            identity, f"internal-source-{len(source_keys) + 1}")
        fixture_evidence.append({
            "evidenceId": evidence_id,
            "sourceKey": source_key,
            "retrievedFor": [event["request"].get("query", "")],
            "targetColumns": sorted(targets[evidence_id]),
            "evidenceKind": (
                "content_card" if reference.get("contentCardId")
                else "source_chunk" if reference.get("chunkId")
                else "source_span"
            ),
            "candidateTitle": evidence.get("exactTitle"),
            "candidateTitleIsSourceExact": bool(evidence.get("exactTitle")),
            "content": evidence.get("content", ""),
            "physicalPageStart": reference["pageStart"],
            "physicalPageEnd": reference["pageEnd"],
            "sourceOverview": evidence.get("sourceOverview"),
        })

    planner = {
        "selectionMode": "distinct_named_items",
        "queries": [],
    }
    planner_json = compact(planner)
    candidate_json = compact(candidate)
    fixture = {
        "schema": "saaia.meal-critic-replay.v1",
        "source": {
            "plannerTraceSha256": digest(native_raw),
            "writerTraceSha256": digest(writer_raw),
            "plannerCompletionSha256": digest(planner_json.encode("utf-8")),
            "writerCandidateSha256": digest(candidate_json.encode("utf-8")),
        },
        "requestText": native["handoff"]["requestText"],
        "language": native["handoff"].get("language", "fr"),
        "load": load,
        "plannerCompletionJson": planner_json,
        "writerCandidateJson": candidate_json,
        "evidence": fixture_evidence,
        "evidenceCount": len(fixture_evidence),
        "candidateClaimCount": len(claims),
        "candidateCitedEvidenceCount": len(cited_order),
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
        "fixtureSha256": digest(args.output.read_bytes()),
        "evidenceCount": len(fixture_evidence),
        "candidateClaimCount": len(claims),
    }, indent=2))


if __name__ == "__main__":
    main()
