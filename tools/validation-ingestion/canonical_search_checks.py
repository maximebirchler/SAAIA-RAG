"""Pure source-bound canonical search checks; never executes HTTP or SQL."""
from __future__ import annotations

import base64
import gzip
import hashlib
import io
import json
import re
import uuid

from tools.ingestion_gold.critical_facts import exact_text


def retrieval_metrics_healthy(response: dict) -> bool:
    """Honor the real nullable DTO; falsey wrong types are not healthy data.

    Empty diagnostics are serialized as null and normally omitted. Retriever
    names are a JSON array, whereas retriever errors are a JSON object.
    """
    if not isinstance(response, dict) or not isinstance(response.get("metrics"), dict):
        return False
    metrics = response["metrics"]
    retrievers = metrics.get("degradedRetrievers")
    errors = metrics.get("degradedRetrieverErrors")
    return ((retrievers is None or type(retrievers) is list and len(retrievers) == 0)
            and (errors is None or type(errors) is dict and len(errors) == 0))


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


def fact_in_current_item(item: dict, row: dict, fact: dict) -> bool:
    """Match an unchanged API item through preverified SQL row windows only."""
    windows = row.get('_verifiedRetrievalWindows')
    if windows is not None:
        from tools.ingestion_gold.structured_fact_surfaces import fact_in_windows
        return fact_in_windows(fact['text'], windows.get(str(item.get('chunkId')), []))
    pattern = re.compile(r'(?<!\w)' + re.escape(exact_text(fact['text'])) + r'(?!\w)')
    return bool(pattern.search(exact_text(item.get('text', ''))))


def choose_current_fact_hit(case: dict, row: dict, response: dict):
    """Select unchanged current evidence; the caller must still execute its GET.

    This does not fabricate a source read or approve the physical PDF. A regular
    grid needs its real canonical bindings before row literals can select a hit.
    """
    from tools.ingestion_gold.structured_fact_surfaces import has_structured_chunks, build_retrieval_windows
    from tools.ingestion_gold.structured_table_bindings import require
    fact = search_fact(case)
    chunks, items = row.get('chunks'), response.get('items')
    require(isinstance(chunks, list) and chunks and len(chunks) <= 100_000
            and len({c.get('retrievalChunkId') for c in chunks}) == len(chunks), 'search_row_chunk_identity_invalid')
    require(row.get('sourceSha256') == case['sourceSha256'], 'search_row_source_mismatch')
    for chunk in chunks:
        require(isinstance(chunk.get('text'), str)
                and hashlib.sha256(chunk['text'].encode('utf-8')).hexdigest() == chunk.get('checksumSha256'),
                'search_row_chunk_checksum_mismatch')
    verified_row = row
    if has_structured_chunks(chunks):
        document = _decode_search_document(case, row)
        windows, errors = build_retrieval_windows(document, chunks, document_id=row['docId'],
            revision_id=row['revisionId'], source_sha256=case['sourceSha256'])
        # Explicit overlap ledgers are preserved but cannot supply row evidence.
        # A missing regular-grid map or invalid binding must never fall back.
        require(all(e.get('code') == 'fact_surface_explicit_overlap_ledger_not_a_regular_grid'
                    and e.get('state') == 'UNAVAILABLE' for e in errors), 'search_structured_binding_refused')
        verified_row = {**row, '_verifiedRetrievalWindows': windows}
    require(isinstance(items, list) and all(isinstance(item, dict) for item in items), 'search_items_array_required')
    if response.get('query') != fact['text'] or not _current_items(items, case, row, fact['page']):
        return None
    return next((item for item in items if fact_in_current_item(item, verified_row, fact)), None)


def _decode_search_document(case: dict, row: dict) -> dict:
    """One bounded, checksummed source decoder for the pure hit selector."""
    from tools.ingestion_gold.structured_table_bindings import require
    artifacts = row.get('binaryArtifacts', [])
    require(isinstance(artifacts, list) and len(artifacts) == 1, 'search_one_canonical_bundle_required')
    artifact = artifacts[0]
    require(artifact.get('artifactType') == 'canonical_ingestion_bundle' and artifact.get('compression') == 'gzip'
            and type(artifact.get('byteSize')) is int and 0 < artifact['byteSize'] <= 64 * 1024**2
            and type(artifact.get('storedSize')) is int and 0 < artifact['storedSize'] <= 64 * 1024**2
            and isinstance(artifact.get('payloadBase64'), str) and len(artifact['payloadBase64']) <= 90 * 1024**2,
            'search_canonical_bundle_format_or_size')
    # PostgreSQL encode(bytea,'base64') inserts line breaks. Only that wire
    # wrapping is removed; arbitrary ignored characters remain a hard refusal.
    payload = artifact['payloadBase64'].replace('\r', '').replace('\n', '')
    compressed = base64.b64decode(payload, validate=True)
    with gzip.GzipFile(fileobj=io.BytesIO(compressed)) as reader:
        raw = reader.read(64 * 1024**2 + 1)
    require(len(raw) == artifact['byteSize'] and len(compressed) == artifact['storedSize']
            and hashlib.sha256(raw).hexdigest() == artifact.get('contentHash'), 'search_canonical_bundle_checksum')
    bundle = json.loads(raw.decode('utf-8'))
    document = bundle.get('document', {})
    require(bundle.get('schemaVersion') == 'canonical_ingestion_bundle_v1'
            and document.get('documentId') == row['docId'] and document.get('revisionId') == row['revisionId']
            and document.get('source', {}).get('sha256') == case['sourceSha256'], 'search_canonical_bundle_scope')
    return document


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
        if chunk["pageStart"] <= fact["page"] <= chunk["pageEnd"] and fact_in_current_item(item, row, fact):
            found.append(item["chunkId"])
    result["searchIdentityExact"] = bool(identity_ok)
    result["positiveFactFound"] = bool(found)
    result['positiveFactFoundLegacyRawText'] = any(pattern.search(exact_text(item.get('text', ''))) for item in items)
    result['factSurfaceBasis'] = 'verified_source_bound_windows' if '_verifiedRetrievalWindows' in row else 'legacy_raw_text'
    lexical_zero, dense_current = _nonce_control(controls.get("absent_query", {}).get("response", {}), case, row, fact["page"])
    absent_document = controls.get("absent_document", {}).get("response", {})
    document_empty = isinstance(absent_document.get("items"), list) and not absent_document["items"]
    result["absentQueryLexicalMatchesZero"] = lexical_zero
    result["absentQueryDenseNeighborsCurrent"] = bool(dense_current)
    result["absentQueryDenseNeighborCount"] = len(controls.get("absent_query", {}).get("response", {}).get("items", []))
    result["absentDocumentEmpty"] = document_empty
    result["negativeControlsPassed"] = len(controls) == 2 and lexical_zero and dense_current and document_empty
    result["noDegradedRetrievers"] = all(retrieval_metrics_healthy(resp)
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
