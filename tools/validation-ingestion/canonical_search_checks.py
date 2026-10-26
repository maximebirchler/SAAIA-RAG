"""Pure source-bound canonical search checks; never executes HTTP or SQL."""
from __future__ import annotations

import re
import uuid

from tools.ingestion_gold.critical_facts import exact_text


def search_fact(case: dict) -> dict:
    """Use an independent physical-source annotation, never a projected chunk."""
    facts = case["expectations"]["criticalFacts"]
    fact = next((item for item in facts if item.get("retrievalRequired", True)), None)
    if fact is None or not exact_text(fact.get("text")) or len(fact["text"]) > 2000:
        raise ValueError("A bounded, independently annotated retrieval fact is required.")
    return fact


def _current_items(items: list, case: dict, row: dict, page: int) -> bool:
    """Dense neighbors still have to be exact current SQL evidence in scope."""
    chunks = {item["retrievalChunkId"]: item for item in row["chunks"]}
    if len({item.get("chunkId") for item in items}) != len(items):
        return False
    for item in items:
        chunk = chunks.get(item.get("chunkId"))
        if (chunk is None or item.get("docId") != row["docId"] or item.get("docPath") != row["docPath"]
                or item.get("revisionId") != row["revisionId"] or item.get("sourceHash") != case["sourceSha256"]
                or item.get("text") != chunk["text"] or item.get("pageStart") != chunk["pageStart"]
                or item.get("pageEnd") != chunk["pageEnd"] or not chunk["pageStart"] <= page <= chunk["pageEnd"]):
            return False
    return True


def _nonce_control(response: dict, case: dict, row: dict, page: int) -> tuple[bool, bool]:
    """No lexical match; the canonical hybrid may return real dense neighbors.

    CanonicalRetrieval executes exact+sparse+dense, with minScore defaulting to
    zero. A random nonce is not a justified empty-nearest-neighbor assertion.
    """
    items, metrics = response.get("items"), response.get("metrics", {})
    if not isinstance(items, list) or not isinstance(metrics, dict):
        return False, False
    lexical_zero = all(type(metrics.get(name)) is int and metrics[name] == 0
                       for name in ("exactMatchReturned", "sparseReturned"))
    dense_current = (_current_items(items, case, row, page)
                     and metrics.get("returned") == metrics.get("denseReturned") == len(items)
                     and metrics.get("retrieversUsed") == (["dense_qdrant"] if items else [])
                     and all(item.get("retriever") == "dense_qdrant" and item.get("exactMatchHit") is not True
                             and item.get("embeddingBasis") != "exact_match_v1"
                             and not str(item.get("embeddingBasis", "")).startswith("source_backed_sparse")
                             for item in items))
    return lexical_zero, dense_current


def check_search_capture(case: dict, row: dict, capture: dict | None) -> dict:
    """Validate actual requests, negative controls and search-to-read identities."""
    fact = search_fact(case)
    passed = {"requestsSourceBound": False, "searchIdentityExact": False,
              "positiveFactFound": False, "negativeControlsPassed": False,
              "searchToReadExact": False, "noDegradedRetrievers": False}
    result = {"passed": False, "factId": fact["id"], "physicalPage": fact["page"],
              "positiveHitCount": 0, "negativeControlCount": 0, **passed}
    if not isinstance(capture, dict):
        return result
    positive = capture.get("positive", {})
    negative = capture.get("negative", [])
    request, response = positive.get("request", {}), positive.get("response", {})
    items = response.get("items")
    if not isinstance(items, list) or not isinstance(negative, list):
        return result
    result["positiveHitCount"] = len(items)
    result["negativeControlCount"] = len(negative)
    requests_ok = (request.get("query") == fact["text"] and request.get("docId") == row["docId"]
                   and request.get("pageStart") == fact["page"] == request.get("pageEnd")
                   and request.get("sourceBackedCanonical") is True
                   and response.get("query") == request["query"])
    controls = {item.get("kind"): item for item in negative if isinstance(item, dict)}
    requests_ok &= len(negative) == 2 and set(controls) == {"absent_query", "absent_document"}
    for kind, item in controls.items():
        req, resp = item.get("request", {}), item.get("response", {})
        requests_ok &= (req.get("sourceBackedCanonical") is True and req.get("pageStart") == fact["page"]
                        and req.get("pageEnd") == fact["page"] and resp.get("query") == req.get("query"))
        if kind == "absent_query":
            requests_ok &= (req.get("docId") == row["docId"]
                            and re.fullmatch(r"saaianomatch[a-f0-9]{32}", str(req.get("query"))) is not None
                            and not any(req["query"] in chunk["text"] for chunk in row["chunks"]))
        elif kind == "absent_document":
            try:
                uuid.UUID(str(req.get("docId")))
            except ValueError:
                requests_ok = False
            requests_ok &= req.get("docId") != row["docId"] and req.get("query") == fact["text"]
    result["requestsSourceBound"] = bool(requests_ok)
    chunks = {item["retrievalChunkId"]: item for item in row["chunks"]}
    identity_ok = bool(items) and _current_items(items, case, row, fact["page"])
    pattern = re.compile(r"(?<!\w)" + re.escape(exact_text(fact["text"])) + r"(?!\w)")
    found = []
    for item in items:
        chunk = chunks.get(item.get("chunkId"))
        if chunk is None:
            continue
        if chunk["pageStart"] <= fact["page"] <= chunk["pageEnd"] and pattern.search(exact_text(item.get("text"))):
            found.append(item["chunkId"])
    result["searchIdentityExact"] = bool(identity_ok)
    result["positiveFactFound"] = bool(found)
    lexical_zero, dense_current = _nonce_control(controls.get("absent_query", {}).get("response", {}), case, row, fact["page"])
    absent_document = controls.get("absent_document", {}).get("response", {})
    document_empty = isinstance(absent_document.get("items"), list) and not absent_document["items"]
    result["absentQueryLexicalMatchesZero"] = lexical_zero
    result["absentQueryDenseNeighborsCurrent"] = bool(dense_current)
    result["absentQueryDenseNeighborCount"] = len(controls.get("absent_query", {}).get("response", {}).get("items", []))
    result["absentDocumentEmpty"] = document_empty
    result["negativeControlsPassed"] = len(controls) == 2 and lexical_zero and dense_current and document_empty
    result["noDegradedRetrievers"] = all(
        isinstance(resp.get("metrics"), dict) and not resp["metrics"].get("degradedRetrievers")
        for resp in [response] + [item.get("response", {}) for item in negative])
    read = capture.get("read", {})
    read_response, read_request = read.get("response", {}), read.get("request", {})
    read_id = read_request.get("chunkId")
    source = read_response.get("document", {})
    read_items = read_response.get("items", [])
    result["searchToReadExact"] = bool(
        read_id in found and read_request.get("docId") == row["docId"]
        and read_response.get("found") is True and read_response.get("anchorFound") is True
        and source.get("docId") == row["docId"] and source.get("revisionId") == row["revisionId"]
        and source.get("sourceHash") == case["sourceSha256"] and isinstance(read_items, list)
        and any(item.get("chunkId") == read_id and item.get("text") == chunks[read_id]["text"]
                for item in read_items))
    result["passed"] = all(result[name] for name in passed)
    return result
