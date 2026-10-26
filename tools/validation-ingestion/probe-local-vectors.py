"""Attested, permuted real E5/Qdrant smoke test; no corpus-wide quality claim."""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import re
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from embedding_attestation import attest_info, validate_expected_profile

MINIMUM_COSINE_MARGIN = 0.001
DIMENSIONS = 768
PERMUTATIONS = ((0, 1, 2), (1, 2, 0), (2, 0, 1))
PASSAGES = (
    "passage: Avant toute intervention de maintenance, isoler l'alimentation électrique et vérifier l'absence de tension.",
    "passage: Pour préparer cette salade, couper les tomates puis ajouter de l'huile et du basilic frais.",
    "passage: Les conditions de prêt de la bibliothèque prévoient un retour des romans après trois semaines.",
)
QUERY = "query: Quelles précautions électriques faut-il prendre avant de réparer la machine ?"


def origin(value: str) -> str:
    parsed = urllib.parse.urlparse(value)
    if (parsed.scheme != "http" or parsed.hostname != "127.0.0.1" or not parsed.port
            or not 1024 <= parsed.port <= 65535 or parsed.port == 5432
            or parsed.username is not None or parsed.password is not None
            or parsed.query or parsed.fragment or parsed.path not in {"", "/"}):
        raise ValueError("Literal uncredentialed loopback HTTP origin required.")
    return value.rstrip("/")


def request(url: str, method: str = "GET", body: dict | None = None) -> dict:
    raw = None if body is None else json.dumps(body, allow_nan=False).encode("utf-8")
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, request, response, code, message, headers, new_url):
            raise ValueError("Local qualification HTTP redirects are forbidden.")
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    with opener.open(urllib.request.Request(url, data=raw, method=method,
            headers={"Content-Type": "application/json"}), timeout=120) as response:
        return json.loads(response.read().decode("utf-8"))


def attest(info: dict, profile: dict | None, embedding: str) -> None:
    attest_info(info, profile, embedding)


def vectors_from(response: dict, model: str, expected_count: int = 4) -> list[list[float]]:
    data = response.get("data")
    if (response.get("model") != model or not isinstance(data, list) or len(data) != expected_count
            or any(not isinstance(row, dict) or type(row.get("index")) is not int for row in data)
            or {row["index"] for row in data} != set(range(expected_count))):
        raise ValueError("Embedding model/count/index contract mismatch.")
    vectors = []
    for row in sorted(data, key=lambda item: item["index"]):
        vector = row.get("embedding")
        if (not isinstance(vector, list) or len(vector) != DIMENSIONS
                or any(type(number) not in (int, float) or not math.isfinite(number) for number in vector)):
            raise ValueError("Embedding shape or finite-number contract mismatch.")
        norm = math.sqrt(sum(number * number for number in vector))
        if not math.isfinite(norm) or norm <= 0 or abs(norm - 1) > 0.001:
            raise ValueError("Expected normalized nonzero E5 vectors.")
        vectors.append([number / norm for number in vector])
    if expected_count > 1 and len({tuple(vector) for vector in vectors[:3]}) < 2:
        raise ValueError("Constant embeddings cannot qualify semantic retrieval.")
    return vectors


def expect_missing(url: str) -> None:
    try:
        request(url)
    except urllib.error.HTTPError as failure:
        if failure.code == 404:
            return
        raise
    raise ValueError("Expected a missing owned collection.")


def run(embedding: str, qdrant: str, *, expected_profile: dict | None = None) -> dict:
    embedding, qdrant = origin(embedding), origin(qdrant)
    # Validate declarations without learning expected identity from the endpoint.
    if expected_profile is None:
        raise ValueError("Supply the independent embedding profile before making requests.")
    adapter = validate_expected_profile(expected_profile, embedding)
    info = request(embedding + "/info")
    attest(info, expected_profile, embedding)
    model_id = expected_profile["Rag"]["EmbeddingsModel"]
    started = time.monotonic()
    controls = []
    if adapter == "transformers_cpu":
        negative = [({"model": "unknown", "input": "query: test"}, {400}),
                    ({"model": model_id, "input": "missing prefix"}, {400}),
                    ({"model": model_id, "input": "passage: " + "limite " * 700}, {413})]
    else:
        # TEI is a generic embedding engine: it need not enforce SAAIA's E5
        # prefix convention or reject a cosmetic OpenAI model field.
        negative = [({"model": model_id}, {400, 422})]
        oversized = {"model": model_id, "input": "passage: " + "limite " *
                     (expected_profile["expectedInfo"]["max_input_length"] + 188)}
        if expected_profile["truncationPolicy"] == "reject":
            negative.append((oversized, {400, 413, 422}))
        else:
            accepted = request(embedding + "/v1/embeddings", "POST", oversized)
            vectors_from(accepted, model_id, 1)
            controls.append({"kind": "oversized_input_with_declared_auto_truncate", "observedHttpStatus": 200,
                             "truncatedTokenCountMeasured": False})
    for body, expected in negative:
        try:
            request(embedding + "/v1/embeddings", "POST", body)
            raise ValueError("Embedding negative control was unexpectedly accepted.")
        except urllib.error.HTTPError as failure:
            if failure.code not in expected:
                raise
            controls.append({"expectedHttpStatuses": sorted(expected), "observedHttpStatus": failure.code})
    collection = "semantic_diag_" + uuid.uuid4().hex[:12]
    collection_url = qdrant + "/collections/" + collection
    expect_missing(collection_url)
    ids = [str(uuid.uuid4()) for _ in range(3)]
    created = False
    permutation_results = []
    try:
        if request(collection_url, "PUT", {"vectors": {"size": DIMENSIONS, "distance": "Cosine"}}).get("result") is not True:
            raise ValueError("Collection creation was not acknowledged.")
        created = True
        for order in PERMUTATIONS:
            texts = [PASSAGES[index] for index in order] + [QUERY]
            vectors = vectors_from(request(embedding + "/v1/embeddings", "POST", {"model": model_id, "input": texts}), model_id)
            scores = [sum(a*b for a,b in zip(vectors[3], vector)) for vector in vectors[:3]]
            expected_index = order.index(0)
            margin = scores[expected_index] - max(score for index, score in enumerate(scores) if index != expected_index)
            if not math.isfinite(margin) or margin < MINIMUM_COSINE_MARGIN:
                raise ValueError("Semantic ranking margin is below the fixed minimum.")
            points = [{"id": ids[ordinal], "vector": vector, "payload": {"syntheticOrdinal": ordinal}}
                      for ordinal, vector in zip(order, vectors[:3])]
            request(collection_url + "/points?wait=true", "PUT", {"points": points})
            search = request(collection_url + "/points/search", "POST", {"vector": vectors[3], "limit": 3, "with_payload": True})
            matches = search.get("result")
            if (not isinstance(matches, list) or len(matches) != 3 or {match.get("id") for match in matches} != set(ids)
                    or matches[0].get("id") != ids[0]
                    or any(type(match.get("score")) not in (int, float) or not math.isfinite(match["score"]) for match in matches)):
                raise ValueError("Real Qdrant semantic result identity/ranking mismatch.")
            qdrant_margin = matches[0]["score"] - max(match["score"] for match in matches[1:])
            if qdrant_margin < MINIMUM_COSINE_MARGIN:
                raise ValueError("Qdrant semantic ranking margin is below the fixed minimum.")
            permutation_results.append({"passageOrder": list(order), "expectedPassagePosition": expected_index + 1,
                "cosineScoresInPresentedOrder": scores, "cosineMargin": margin, "qdrantMargin": qdrant_margin,
                "expectedPassageRank": 1, "qdrantExpectedPassageRank": 1})
    finally:
        if created:
            if request(collection_url, "DELETE").get("result") is not True:
                raise ValueError("Owned collection deletion was not acknowledged.")
            expect_missing(collection_url)
    return {"schemaVersion": "saaia-real-local-vector-probe.v2", "passed": True,
            "scope": "Frozen native/runtime info and real E5/Qdrant synthetic ranking; container/weight provenance is separate, not ingestion or corpus-wide recall",
            "runtime": info, "embeddingAdapter": adapter, "runtimeAttestationMatchesProfile": True,
            "nativeRuntimeInfoMatchesFrozenProfile": True,
            "containerAndWeightsAttestationRequireOwnedRunner": adapter == "tei_cpu", "embeddingDimensions": DIMENSIONS,
            "minimumCosineMargin": MINIMUM_COSINE_MARGIN, "permutations": permutation_results,
            "negativeControls": controls, "freshCollectionVerifiedBeforeCreate": True, "ownedCollectionDeletedAndAbsent": True,
            "elapsedSeconds": round(time.monotonic() - started, 3), "paidProviderCalls": 0,
            "semanticSmokeValidated": True, "corpusSemanticRecallValidated": False, "ingestionValidated": False}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--embedding-url", required=True)
    parser.add_argument("--qdrant-url", required=True)
    parser.add_argument("--embedding-profile", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists():
        raise FileExistsError("Never overwrite vector evidence.")
    try:
        profile_bytes = args.embedding_profile.read_bytes()
        result = run(origin(args.embedding_url), origin(args.qdrant_url), expected_profile=json.loads(profile_bytes))
        result["expectedProfileFileSha256"] = hashlib.sha256(profile_bytes).hexdigest()
    except Exception as failure:
        result = {"schemaVersion": "saaia-real-local-vector-probe.v2", "passed": False,
                  "failureType": type(failure).__name__, "paidProviderCalls": 0,
                  "scope": "Probe failed; no semantic qualification or cleanup success is claimed."}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("x", encoding="utf-8", newline="") as output:
        output.write(json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False) + "\n")
    print("Local vector probe passed." if result["passed"] else "Local vector probe failed; inspect its new proof.")
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
