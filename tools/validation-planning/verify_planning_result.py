"""Read-only mechanical qualification of a real planning result and its inputs.

No recipe catalogue, culinary classifier, model call, SQL connection or secret
loader belongs here. A pass is deliberately NOT semantic/product acceptance.
The final reviewer must inspect each named recipe's own PDF body and suitability.
Paid runs have exact application traces/probes, not an independent HTTP proxy.
"""
import argparse
import base64
from collections import Counter
import gzip
import hashlib
import json
import pathlib
import re
import sys
import unicodedata
import uuid

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "validation-wire"))
from verify_wire import extended_windows_path, read_call_probes, require, utf16_length, utf16_slice, verify_projection
from prepare_cases import raw_lines


def sha(value):
    return hashlib.sha256(value).hexdigest()


def read_json(path):
    return json.loads(extended_windows_path(pathlib.Path(path)).read_bytes())


def normal(value, fold_accents=False):
    """Lexical identity/labels only; never decide what constitutes a meal."""
    value = unicodedata.normalize("NFC", value)
    if fold_accents:
        value = "".join(ch for ch in unicodedata.normalize("NFD", value) if not unicodedata.combining(ch))
    return " ".join("".join(ch if ch.isalnum() else " " for ch in value).lower().split())


def contains_name(text, name):
    return bool(normal(name)) and " " + normal(name) + " " in " " + normal(text) + " "


def coordinates(case):
    rows, columns = case["rowLabels"], case["columns"]
    expected_rows = {"one-day-4": 1, "five-day-20": 5}.get(case["kind"])
    require(expected_rows == len(rows) and len(columns) == 4
            and case["answerUnitCount"] == len(rows) * 4
            and case["functionalFiveDayAcceptance"] is (expected_rows == 5)
            and len(set(map(normal, rows))) == len(rows)
            and len(set(map(normal, columns))) == 4,
            "planning_grid_profile_invalid")
    return [{"claimId": f"C{index + 1}", "rowLabel": row, "columnLabel": column}
            for index, (row, column) in enumerate((row, column) for row in rows for column in columns)]


def split_table_row(line):
    """CommonMark pipes: only an odd number of preceding backslashes escapes."""
    parts, part, slashes = [], [], 0
    for character in line.strip():
        if character == "|" and slashes % 2 == 0:
            parts.append("".join(part).strip())
            part = []
        else:
            part.append(character)
        slashes = slashes + 1 if character == "\\" else 0
    parts.append("".join(part).strip())
    if parts and parts[0] == "":
        parts.pop(0)
    if parts and parts[-1] == "":
        parts.pop()
    return parts


def grid_cells(answer, case):
    lines = answer.splitlines()
    wanted = [normal(value, True) for value in case["columns"]]
    headers = [index for index, line in enumerate(lines)
               if len(parts := split_table_row(line)) == 5
               and [normal(value, True) for value in parts[1:]] == wanted]
    require(len(headers) == 1, "planning_grid_header_missing_or_ambiguous")
    start = headers[0]
    require(start + 1 < len(lines) and len(separator := split_table_row(lines[start + 1])) == 5
            and all(re.fullmatch(r":?-{3,}:?", part.replace(" ", "")) for part in separator),
            "planning_grid_separator_invalid")
    rows = []
    for line in lines[start + 2:]:
        if "|" not in line:
            break
        row = split_table_row(line)
        require(len(row) == 5, "planning_grid_row_width_invalid")
        rows.append(row)
    require(len(rows) == len(case["rowLabels"])
            and [normal(row[0], True) for row in rows] == [normal(row, True) for row in case["rowLabels"]],
            "planning_grid_rows_or_order_invalid")
    return [cell for row in rows for cell in row[1:]]


def stable_chunk_id(doc_id, chunk_index):
    # The product ID uses the first sixteen SHA256 bytes as a .NET Guid.
    key = f"{uuid.UUID(doc_id).hex}|retrieval-chunk|v1|{chunk_index}"
    return str(uuid.UUID(bytes_le=hashlib.sha256(key.encode("utf-8")).digest()[:16]))


def decode_bundle(artifact):
    require(artifact["artifactType"] == "canonical_ingestion_bundle"
            and 0 < artifact["byteSize"] <= 512 * 1024 * 1024, "planning_canonical_artifact_invalid")
    data = gzip.decompress(base64.b64decode(artifact["payload"], validate=True))
    require(len(data) == artifact["byteSize"]
            and sha(data) == base64.b64decode(artifact["contentHash"], validate=True).hex(),
            "planning_canonical_bytes_changed")
    return json.loads(data), sha(data)


def corpus_index(manifest, package, snapshot):
    """Bind the real SQL chunks/cards to the complete frozen product package."""
    require(manifest["schemaVersion"] == "saaia-planning-complete-corpus.v1"
            and manifest["completeDocuments"] is True and manifest["exactSqlReadback"] is True
            and package["schemaVersion"] == "saaia-planning-native-corpus-package.v1"
            and package["boundaries"]["fullDocuments"] is True
            and snapshot["schemaVersion"] == "saaia-planning-corpus-sql-observation.v1",
            "planning_complete_corpus_required")
    documents, chunks, anchors = {}, {}, {}
    require(len(manifest["documents"]) == manifest["documentCount"] == len(package["documents"])
            and manifest["documentCount"] > 0, "planning_corpus_document_count_invalid")
    for source in package["documents"]:
        matching = [doc for doc in manifest["documents"] if doc["docId"] == source["docId"]]
        require(len(matching) == 1 and source["docId"] not in documents, "planning_document_identity_ambiguous")
        doc = matching[0]
        bundle, canonical_hash = decode_bundle(source["artifact"])
        canonical = bundle["document"]
        require(all(doc[key] == source[key] for key in ("docId", "revisionId", "docPath", "sourceSha256", "physicalPdfPath"))
                and doc["completeCanonicalBundle"] is True and doc["canonicalHash"] == canonical_hash
                and canonical["documentId"] == doc["docId"] and canonical["revisionId"] == doc["revisionId"]
                and canonical["source"]["sha256"] == doc["sourceSha256"]
                and canonical["pageCount"] == doc["pageCount"] == source["expectedPageCount"]
                and [page["pageNumber"] for page in canonical["pages"]] == list(range(1, doc["pageCount"] + 1)),
                "planning_manifest_package_identity_changed")
        documents[doc["docId"]] = {**doc, "canonical": canonical, "package": source}
        source_chunks = {stable_chunk_id(doc["docId"], chunk["chunkIndex"]): chunk for chunk in source["retrievalChunks"]}
        published = bundle["sourceAnchors"]
        require(len(published) == doc["sourceAnchorCount"] == doc["retrievalChunkCount"],
                "planning_published_anchor_or_chunk_count_changed")
        for anchor in published:
            chunk_id = anchor["projectionId"]
            require(anchor["projectionType"] == "retrieval_chunk" and chunk_id in source_chunks
                    and anchor["anchorId"] not in anchors and chunk_id not in chunks
                    and anchor["documentId"] == doc["docId"] and anchor["revisionId"] == doc["revisionId"]
                    and anchor["sourceSha256"] == doc["sourceSha256"], "planning_source_anchor_identity_invalid")
            chunk = source_chunks[chunk_id]
            require(sha(chunk["text"].encode("utf-8")) == base64.b64decode(chunk["checksum"], validate=True).hex(),
                    "planning_package_chunk_checksum_changed")
            chunks[chunk_id] = {**chunk, "docId": doc["docId"], "revisionId": doc["revisionId"], "anchorId": anchor["anchorId"]}
            anchors[anchor["anchorId"]] = chunk_id
    require(sum(doc["pageCount"] for doc in documents.values()) == manifest["physicalPageCount"],
            "planning_full_page_count_changed")
    sql_chunks = {item["retrievalChunkId"]: item for item in snapshot["retrievalChunks"]}
    require(len(sql_chunks) == len(snapshot["retrievalChunks"]) and set(sql_chunks) == set(chunks),
            "planning_sql_chunk_inventory_changed")
    changed_transport, crlf_pairs = [], 0
    for chunk_id, expected in chunks.items():
        observed = sql_chunks[chunk_id]
        # The source package precedes the product PostgreSQL storage boundary.
        # Its observed contract converts CRLF to LF. Do not normalize whitespace,
        # lone CR, Unicode, punctuation, units or any other source character.
        stored_text = expected["text"].replace("\r\n", "\n")
        require(all(observed[key] == expected[key] for key in ("revisionId", "chunkIndex", "pageStart", "pageEnd"))
                and observed["text"] == stored_text
                and observed["checksumSha256"] == sha(stored_text.encode("utf-8")),
                "planning_sql_chunk_text_or_identity_changed")
        if stored_text != expected["text"]:
            pairs = expected["text"].count("\r\n")
            crlf_pairs += pairs
            changed_transport.append([chunk_id, sha(expected["text"].encode("utf-8")), sha(stored_text.encode("utf-8")), pairs])
        metadata = observed["metadata"]
        require(metadata.get("extractionQualitySignals", []) == (expected.get("extractionQualitySignals") or [])
                and metadata.get("extractionTextStatus") == expected.get("extractionTextStatus")
                and metadata.get("extractionOcrCandidate") == expected.get("extractionOcrCandidate", False)
                and metadata.get("contentRole") == expected.get("contentRole", "content")
                and metadata.get("chunkType") == expected.get("chunkType"),
                "planning_sql_chunk_quality_changed")
    cards = {item["contentCardId"]: item for item in snapshot["contentCards"]}
    require(len(cards) == len(snapshot["contentCards"]), "planning_sql_card_identity_ambiguous")
    transport = {"allowedTransformation": "CRLF_to_LF_only_at_product_SQL_storage",
                 "comparedChunkCount": len(chunks), "changedChunkCount": len(changed_transport),
                 "normalizedCRLFPairCount": crlf_pairs, "otherCharacterChangesAccepted": False,
                 "changedChunkIdentitiesAndBeforeAfterHashesSha256": sha(json.dumps(sorted(changed_transport), separators=(",", ":")).encode())}
    return {"documents": documents, "chunks": sql_chunks, "packageChunks": chunks, "anchors": anchors, "cards": cards,
            "packageToSqlTextTransport": transport}


def validate_reference(reference, corpus):
    doc = corpus["documents"].get(reference["docId"])
    require(doc is not None and reference["revisionId"] == doc["revisionId"]
            and reference["docPath"] == doc["docPath"] and reference["sourceHash"] == doc["sourceSha256"]
            and type(reference["pageStart"]) is int and type(reference["pageEnd"]) is int
            and 1 <= reference["pageStart"] <= reference["pageEnd"] <= doc["pageCount"],
            "planning_citation_outside_frozen_pdf")
    return doc


def reference_signature(reference):
    return {key: reference.get(key) for key in ("docId", "revisionId", "docPath", "sourceHash", "pageStart", "pageEnd",
                                                "chunkId", "anchorId", "contentCardId", "nativeObservation", "sourceObservation")}


def primary_source(reference, corpus):
    """Exact resolver surfaces. Unsupported secondary tables are never guessed."""
    doc = validate_reference(reference, corpus)
    require(not reference.get("sourceObservation"), "planning_secondary_table_requires_independent_binding")
    chunk_id = reference.get("chunkId") or corpus["anchors"].get(reference.get("anchorId"))
    if chunk_id:
        chunk = corpus["chunks"].get(chunk_id)
        require(chunk is not None and chunk["revisionId"] == doc["revisionId"]
                and reference["pageStart"] == chunk["pageStart"] and reference["pageEnd"] == chunk["pageEnd"]
                and (not reference.get("anchorId") or corpus["anchors"].get(reference["anchorId"]) == chunk_id),
                "planning_citation_chunk_or_anchor_unknown")
        return chunk["text"], chunk, (doc["revisionId"], "chunk", chunk_id)
    card = corpus["cards"].get(reference.get("contentCardId"))
    require(card is not None and card["docId"] == doc["docId"] and card["revisionId"] == doc["revisionId"]
            and card["pageStart"] == reference["pageStart"] and card["pageEnd"] == reference["pageEnd"],
            "planning_citation_card_unknown")
    evidence = card["metadata"].get("evidence", {})
    indices = [fact.get("value") for fact in evidence.get("facts", [])
               if fact.get("kind") == "canonical_source" and fact.get("label") == "retrieval_chunk_index"]
    candidates = [chunk for chunk in corpus["chunks"].values()
                  if chunk["revisionId"] == doc["revisionId"] and indices and str(chunk["chunkIndex"]) == str(indices[0])
                  and chunk["pageStart"] >= card["pageStart"] and chunk["pageEnd"] <= card["pageEnd"]]
    if evidence.get("schemaVersion") == "canonical_section_anchor_evidence_v1" and len(candidates) == 1:
        chunk = candidates[0]
        return chunk["text"], chunk, (doc["revisionId"], "chunk", chunk["retrievalChunkId"])
    # Fallback cards contain grounded sourceText facts, not their searchText.
    # Validate every such literal against original published chunks and pages.
    parts = list(dict.fromkeys([card["title"].strip()] + [fact["sourceText"].strip()
                           for key in ("facts", "quantityFacts") for fact in evidence.get(key, [])
                           if isinstance(fact.get("sourceText"), str) and fact["sourceText"].strip()]))
    require(len(parts) > 1 and all(any(part in chunk["text"] and chunk["revisionId"] == doc["revisionId"]
                                       and chunk["pageStart"] >= card["pageStart"] and chunk["pageEnd"] <= card["pageEnd"]
                                       for chunk in corpus["chunks"].values()) for part in parts),
            "planning_card_body_not_canonical_grounded")
    return "\n\n".join(parts), None, (doc["revisionId"], "card", card["contentCardId"])


def verify_quality(item, chunk):
    quality = item.get("sourceQuality")
    if chunk is None:
        require(quality is None or quality.get("semanticAccuracy") == "not_verified", "planning_quality_falsely_approved")
        return
    metadata = chunk["metadata"]
    signals = list(dict.fromkeys(metadata.get("extractionQualitySignals", [])))
    require(quality is not None and quality.get("semanticAccuracy") == "not_verified"
            and quality.get("textStatus") == metadata.get("extractionTextStatus")
            and quality.get("ocrCandidate") == metadata.get("extractionOcrCandidate")
            and quality.get("signals") == signals[:64]
            and quality.get("signalsOmittedCount", 0) == max(0, len(signals) - 64)
            and quality.get("metadataTruncated", False) == (len(signals) > 64)
            and quality.get("metadataMalformed", False) is False,
            "planning_extraction_quality_or_omissions_changed")


def visible_primary(item, reference, corpus):
    full, chunk, identity = primary_source(reference, corpus)
    require(item["physicalPageStart"] == reference["pageStart"] and item["physicalPageEnd"] == reference["pageEnd"],
            "planning_visible_physical_pages_changed")
    content, offset = item["content"], item.get("contentOffset", 0)
    require(type(offset) is int and offset >= 0 and item["originalContentLength"] == utf16_length(full)
            and content == utf16_slice(full, offset, utf16_length(content)), "planning_visible_body_not_canonical_exact")
    length = utf16_length(content)
    truncated = offset > 0 or length < utf16_length(full)
    require(item["contentTruncated"] is truncated
            and item.get("nextContentOffset") == (offset + length if offset + length < utf16_length(full) else None),
            "planning_body_truncation_or_cursor_hidden")
    windows = [content]
    for window in item.get("supportingContentWindows", []):
        text, start = window["content"], window["contentOffset"]
        require(type(start) is int and start >= 0 and text == utf16_slice(full, start, utf16_length(text))
                and window["contentTruncated"] == (start > 0 or utf16_length(text) < utf16_length(full)),
                "planning_supporting_window_not_canonical_exact")
        windows.append(text)
    omitted = item.get("supportingContentWindowsOmittedCount", 0)
    require(type(omitted) is int and omitted >= 0, "planning_supporting_window_omission_invalid")
    verify_quality(item, chunk)
    title = item.get("candidateTitle") or ""
    card = corpus["cards"].get(reference.get("contentCardId"))
    require(not title or (card is not None and title == card["title"]) or contains_name(full, title),
            "planning_visible_candidate_title_not_source_grounded")
    role = item.get("contentRole")
    source_role = chunk["metadata"].get("contentRole") if chunk is not None else role
    # A nonempty literal beyond the observed heading is necessary, never a
    # semantic guarantee. Navigation-only rows are rejected even if numerous.
    heading = normal(item.get("candidateTitle") or "")
    residuals = [normal(text) for text in windows if text.strip()]
    body_candidate = role in ("content", "mixed_navigation_content") and source_role != "navigation" \
        and any(text and text != heading and any(ch.isalpha() for ch in text) for text in residuals)
    return {"identity": identity, "windows": windows, "bodyCandidate": body_candidate,
            "candidateTitle": title, "contentRole": role,
            "contentTruncated": truncated, "supportingWindowsOmitted": omitted,
            "visibleUtf16Characters": sum(utf16_length(text) for text in windows)}


def visible_native(item, reference, corpus):
    doc = validate_reference(reference, corpus)
    line = reference["nativeObservation"]
    pages = [page for page in doc["canonical"]["pages"] if page["pageNumber"] == line["physicalPage"]]
    blocks = [block for page in pages for block in page["blocks"] if block["blockId"] == line["blockId"]
              and block["blockType"] in ("native_text_recovery", "native_layout_recovery", "native_table_recovery")]
    require(len(pages) == len(blocks) == 1, "planning_native_locator_unknown")
    block, page = blocks[0], pages[0]
    text = block["text"]["raw"]
    lines = raw_lines(text)
    ordinal = line["lineOrdinal"]
    require(type(ordinal) is int and 0 <= ordinal < len(lines), "planning_native_line_ordinal_invalid")
    literal = lines[ordinal]
    start, end = sum(len(value) for value in lines[:ordinal]), sum(len(value) for value in lines[:ordinal + 1])
    identity = [doc["docId"], doc["revisionId"], doc["sourceSha256"], doc["canonical"]["manifestSha256"],
                page["pageNumber"], block["blockId"], sha(text.encode("utf-8")), ordinal, start, end, sha(literal.encode("utf-8"))]
    native = item.get("nativeObservation", {})
    signals = list(dict.fromkeys(page.get("qualityFlags", []) + block.get("qualityFlags", [])
                                 + ["canonical_native_audit_literal_not_primary"]))
    require(line["lineId"] == "native_line_" + sha(json.dumps(identity, separators=(",", ":")).encode("utf-8"))
            and line["rawTextSha256"] == identity[6] and line["lineTextSha256"] == identity[-1]
            and line["unicodeScalarStart"] == start and line["unicodeScalarEndExclusive"] == end
            and item["content"] == literal and item["contentTruncated"] is False
            and item["originalContentLength"] == utf16_length(literal)
            and reference["pageStart"] == reference["pageEnd"] == item["physicalPageStart"] == item["physicalPageEnd"] == page["pageNumber"]
            and native.get("reference") == line and native.get("role") == "secondary_native_audit"
            and native.get("semanticAccuracy") == "not_verified" and native.get("sourceFidelityApproved") is False
            and native.get("qualitySignals") == signals,
            "planning_native_literal_identity_or_quality_changed")
    quality = item.get("sourceQuality", {})
    require(quality.get("semanticAccuracy") == "not_verified" and quality.get("signals") == signals[:64]
            and quality.get("signalsOmittedCount", 0) == max(0, len(signals) - 64)
            and quality.get("metadataTruncated", False) == (len(signals) > 64)
            and quality.get("metadataMalformed", False) is False, "planning_native_quality_changed")
    return {"identity": (doc["revisionId"], "native", line["lineId"]), "windows": [literal], "bodyCandidate": False,
            "candidateTitle": "", "contentRole": "secondary_native_audit", "contentTruncated": False,
            "supportingWindowsOmitted": 0, "visibleUtf16Characters": utf16_length(literal),
            "nativeBlock": (doc["revisionId"], line["blockId"], line["rawTextSha256"]),
            "nativeOrdinal": ordinal, "nativePhysicalPage": page["pageNumber"]}


def current_payload(body):
    payloads = []
    for message in body.get("input", body.get("messages", [])):
        if message.get("role") == "user" and isinstance(message.get("content"), str):
            try:
                payload = json.loads(message["content"])
            except ValueError:
                continue
            if isinstance(payload, dict) and isinstance(payload.get("evidence"), list):
                payloads.append(payload)
    require(len(payloads) == 1, "planning_current_documentary_prompt_not_unique")
    return payloads[0]


def unwrap_completion(text):
    text = text.strip()
    if text.startswith("```"):
        text = re.sub(r"\A```(?:json)?\s*", "", text)
        text = re.sub(r"\s*```\Z", "", text)
    try:
        result = json.loads(text)
        return result if isinstance(result, dict) else None
    except ValueError:
        return None


def answer_signature(value):
    return {"outcome": value.get("outcome"), "answerText": value.get("answerText"),
            "claims": [{key: claim.get(key) for key in ("claimId", "selectedItem", "text", "evidenceIds")}
                       for claim in value.get("claims", [])]}


def evaluate(profile, corpus, submission, api, sql, traces, probes):
    case = profile["case"]
    expected_coordinates = coordinates(case)
    require(api["jobId"] == sql["jobId"] and api["status"] == sql["status"] == "succeeded"
            and api["attemptCount"] == sql["attemptCount"] == 1 and api["result"] == sql["result"],
            "planning_job_not_once_successful_or_sql_differs")
    result = api["result"]
    require(result["outcome"] == "answered", "planning_full_answer_not_returned")
    handoff = submission["handoff"]
    require(handoff == sql["handoff"] and handoff["requestText"] == case["question"]
            and handoff["transferStage"] == "before_retrieval" and handoff["researchState"]["evidenceReferences"] == [],
            "planning_actual_question_or_initial_state_changed")
    expected_load = {"rowLabels": case["rowLabels"], "columns": case["columns"], "rowCount": len(case["rowLabels"]),
                     "columnCount": 4, "answerUnitCount": case["answerUnitCount"], "atomicEvidenceCount": case["answerUnitCount"],
                     "structuredLayout": True, "selectionPolicy": "distinct_structured_layout"}
    require(all(handoff["load"].get(key) == value for key, value in expected_load.items()), "planning_actual_grid_load_changed")
    canonical_hashes = {doc["revisionId"]: doc["canonicalHash"] for doc in corpus["documents"].values()}
    require(len(sql["canonicalHashes"]) == len(canonical_hashes)
            and {value["revisionId"]: value["sha256"] for value in sql["canonicalHashes"]} == canonical_hashes,
            "planning_sql_canonical_generation_changed")
    claims, references = result["claims"], result["evidence"]
    require(len(claims) == len(expected_coordinates)
            and {claim["claimId"] for claim in claims} == {coordinate["claimId"] for coordinate in expected_coordinates},
            "planning_claim_coordinates_incomplete_or_duplicate")
    require(all(isinstance(claim.get("selectedItem"), str) and normal(claim["selectedItem"]) for claim in claims),
            "planning_selected_recipe_identity_missing")
    identities = [normal(claim["selectedItem"]) for claim in claims]
    require(len(set(identities)) == len(identities), "planning_duplicate_recipe_identity")
    require(references and len({reference["evidenceId"] for reference in references}) == len(references),
            "planning_duplicate_source_card_or_id")
    refs = {reference["evidenceId"]: reference for reference in references}
    cited = [identifier for claim in claims for identifier in claim["evidenceIds"]]
    require(all(claim["evidenceIds"] and len(claim["evidenceIds"]) == len(set(claim["evidenceIds"])) for claim in claims)
            and set(cited) == set(refs), "planning_unknown_citation_or_unused_card")
    answer = result["answerText"]
    cells = grid_cells(answer, case)
    claims_by_id = {claim["claimId"]: claim for claim in claims}
    require(Counter(re.findall(r"\[(C\d+)\]", answer)) == Counter(coordinate["claimId"] for coordinate in expected_coordinates)
            and not re.search(r"\[(?:E\d+|advanced-evidence-[^]]+)\]|\b(?:internal-source-\d+|advanced-evidence-[0-9a-f]{16,64})\b", answer),
            "planning_unknown_or_repeated_visible_citation")
    for coordinate, cell in zip(expected_coordinates, cells):
        claim = claims_by_id[coordinate["claimId"]]
        require(cell.strip() and re.findall(r"\[(C\d+)\]", cell) == [claim["claimId"]]
                and contains_name(cell, claim["selectedItem"])
                and contains_name(claim["text"], claim["selectedItem"]),
                "planning_empty_or_misbound_grid_cell")
    require(traces and len(traces) == len(probes) == result["providerCallCount"], "planning_trace_probe_or_call_count_differs")
    require(len({probe["call_id"] for probe in probes}) == len(probes), "planning_call_probe_id_duplicate")
    trace_bindings, probe_bindings, matched = Counter(), Counter(), []
    for trace in traces:
        request_hash = sha(trace["requestJson"].encode("utf-8"))
        require(trace["jobId"] == api["jobId"] and trace["requestSha256"].lower() == request_hash
                and trace["httpAttemptCount"] == 1, "planning_exact_request_or_attempt_identity_changed")
        body = json.loads(trace["requestJson"])
        require(profile["provider"]["apiProtocol"] == "responses" and body.get("model") == profile["provider"]["model"]
                and isinstance(body.get("input"), list) and "messages" not in body
                and body.get("store") is False and body.get("stream") is False
                and body.get("reasoning", {}).get("effort") == profile["provider"]["reasoningEffort"],
                "planning_paid_model_protocol_or_reasoning_changed")
        for message in body["input"]:
            if message.get("role") == "user" and isinstance(message.get("content"), str):
                candidate = unwrap_completion(message["content"])
                if candidate is not None and "evidenceProjection" in candidate:
                    verify_projection(candidate)
        trace_bindings[(trace["jobId"], trace["role"], request_hash, utf16_length(trace["requestJson"]))] += 1
        completion = unwrap_completion(trace["completionJson"])
        if completion is not None and answer_signature(completion) == answer_signature(result):
            envelope = json.loads(trace["responseEnvelopeJson"])
            text = "".join(part["text"] for output in envelope.get("output", []) if output.get("type") == "message"
                           for part in output.get("content", []) if part.get("type") == "output_text")
            require(unwrap_completion(text) == completion and envelope.get("status") == "completed"
                    and not envelope.get("incomplete_details") and trace.get("normalizationError") is None,
                    "planning_final_provider_envelope_differs_or_incomplete")
            matched.append((trace, current_payload(body)))
    for probe in probes:
        require(probe["wire_protocol"] == "responses" and probe["diagnostic_dropped_measurements"] == 0
                and probe["diagnostic_truncated_values"] == 0, "planning_call_diagnostics_incomplete")
        probe_bindings[(probe["job_id"], probe["role"], probe["request_sha256"].lower(), probe["request_characters"])] += 1
    require(trace_bindings == probe_bindings, "planning_trace_not_bound_to_exact_call_probe")
    require(matched, "planning_final_answer_not_observed_in_provider_completion")
    trace, payload = max(matched, key=lambda pair: pair[0]["capturedAtUtc"])
    verify_projection(payload)
    require(payload.get("claimCoordinates") == expected_coordinates
            and payload.get("request") == case["question"]
            and all(payload["load"].get(key) == value for key, value in expected_load.items()),
            "planning_final_model_grid_contract_changed")
    known = {}
    for event in sql["events"]:
        if event["status"] == "succeeded":
            for reference in event["evidence"]:
                if reference.get("evidenceId") in refs:
                    key = reference["evidenceId"]
                    require(reference_signature(reference) == reference_signature(refs[key]), "planning_reader_and_result_citation_differ")
                    known[key] = reference
    require(set(known) == set(refs), "planning_result_citation_not_read_by_real_tools")
    visible = {item["evidenceId"]: item for item in payload["evidence"] if item.get("evidenceId") in refs}
    require(set(visible) == set(refs), "planning_cited_body_missing_from_final_model_input")
    proofs = {}
    for identifier, reference in refs.items():
        proofs[identifier] = (visible_native if reference.get("nativeObservation") else visible_primary)(visible[identifier], reference, corpus)
        if reference.get("nativeObservation"):
            item, line = visible[identifier], reference["nativeObservation"]
            native = item["nativeObservation"]
            views = [view for view in (payload.get("alternativeNativeReadings") or {}).get("views", [])
                     if view.get("sourceKey") == item.get("sourceKey") and view.get("blockId") == line["blockId"]
                     and any(row.get("evidenceId") == identifier for row in view.get("lines", []))]
            require(len(views) == 1 and native["nextLineOffset"] == views[0]["nextOffset"]
                    and native["lineOffset"] == views[0]["offset"] and native["lineLimit"] == views[0]["limit"]
                    and native["viewLineCount"] == views[0]["rawLineCount"], "planning_native_model_resume_cursor_contradicts_view")
    require(len({proof["identity"] for proof in proofs.values()}) == len(proofs), "planning_duplicate_physical_source_card")
    reviews = []
    for coordinate in expected_coordinates:
        claim = claims_by_id[coordinate["claimId"]]
        selected = normal(claim["selectedItem"])
        own = [proofs[identifier] for identifier in claim["evidenceIds"]]
        primary_body = any(proof["bodyCandidate"] and (selected == normal(proof["candidateTitle"])
                    or any(contains_name(window, selected) for window in proof["windows"]))
                    and any(any(ch.isalpha() for ch in normal(window).replace(selected, ""))
                            for window in proof["windows"]) for proof in own)
        native_groups = {}
        for proof in own:
            if "nativeBlock" in proof:
                native_groups.setdefault(proof["nativeBlock"], []).append(proof)
        native_body = False
        for group in native_groups.values():
            group.sort(key=lambda proof: proof["nativeOrdinal"])
            ordinals = [proof["nativeOrdinal"] for proof in group]
            # Several exact consecutive native lines may jointly establish a
            # body candidate. Never join unrelated chunks or an unread gap.
            literal = "".join(proof["windows"][0] for proof in group)
            canonical_body = any(chunk["revisionId"] == group[0]["nativeBlock"][0]
                and chunk["pageStart"] <= group[0]["nativePhysicalPage"] <= chunk["pageEnd"]
                and chunk["metadata"].get("contentRole") in ("content", "mixed_navigation_content")
                and literal in chunk["text"] for chunk in corpus["chunks"].values())
            native_body |= ordinals == list(range(ordinals[0], ordinals[0] + len(group))) and canonical_body \
                and contains_name(literal, selected) and any(ch.isalpha() for ch in normal(literal).replace(selected, ""))
        require(primary_body or native_body, "planning_recipe_has_no_visible_substantive_body_candidate")
        reviews.append({**coordinate, "selectedItemSha256": sha(claim["selectedItem"].encode("utf-8")),
                        "evidenceIds": claim["evidenceIds"], "proofCount": len(own),
                        "visibleUtf16Characters": sum(proof["visibleUtf16Characters"] for proof in own),
                        "explicitlyTruncatedProofCount": sum(proof["contentTruncated"] for proof in own),
                        "supportingWindowsExplicitlyOmitted": sum(proof["supportingWindowsOmitted"] for proof in own)})
    return {"schemaVersion": "saaia-planning-result-mechanical-review.v1", "jobId": api["jobId"],
            "caseKind": case["kind"], "gridCellCount": len(cells), "distinctSelectedRecipeIdentityCount": len(set(identities)),
            "serverReferenceCardCount": len(refs), "exactApplicationRequestAndProbeParity": True,
            "independentExternalHttpCapture": False, "finalModelRequestSha256": trace["requestSha256"].lower(),
            "canonicalBodiesVisibleInFinalModelInput": True, "qualityAndExplicitOmissionsChecked": True,
            "mechanicalPlanningPassed": True, "manualSemanticReviewRequired": True,
            "manualReviewTasks": ["Verify each selected name denotes a distinct real recipe, not an alias, heading or component.",
                "Inspect each cited recipe's OWN body in the physical PDF; check mixed pages and neighbouring recipes.",
                "Check ordinary suitability for its requested meal slot and all factual wording against the visible body.",
                "Check substantive completeness despite declared truncation/OCR/layout warnings and all source-card usefulness.",
                "Require the separately agreed consecutive frozen live passes before functional/product acceptance."],
            "cellsForManualReview": reviews, "clientUiValidated": False, "newIngestionValidated": False,
            "semanticPlanningApproved": False, "functionalFiveDayAccepted": False,
            "productStatus": "TESTE_NON_APPROUVE", "passed": True}


def verify(profile_path, runtime):
    profile_path, runtime = extended_windows_path(profile_path), extended_windows_path(runtime)
    profile = read_json(profile_path)
    require(profile["schemaVersion"] == "saaia-local-openai-planning-campaign.v1", "planning_profile_required")
    manifest_path = extended_windows_path(pathlib.Path(profile["corpus"]["manifestPath"]))
    require(sha(manifest_path.read_bytes()) == profile["corpus"]["manifestSha256"].lower(), "planning_corpus_manifest_changed")
    manifest = read_json(manifest_path)
    package_path = extended_windows_path(pathlib.Path(manifest["packagePath"]))
    require(sha(package_path.read_bytes()) == manifest["packageSha256"].lower(), "planning_corpus_package_changed")
    for doc in manifest["documents"]:
        pdf = extended_windows_path(pathlib.Path(doc["physicalPdfPath"]))
        with pdf.open("rb") as handle:
            require(hashlib.file_digest(handle, "sha256").hexdigest() == doc["sourceSha256"], "planning_original_pdf_changed")
    files = {name: runtime / "runtime" / filename for name, filename in {
        "submission": "planning-submission.private.json", "api": "planning-terminal.private.json",
        "sql": "planning-sql-job.private.json", "corpusSql": "planning-corpus-sql.private.json"}.items()}
    values = {name: read_json(path) for name, path in files.items()}
    run_path = runtime.parent / "planning-result.json"
    run = read_json(run_path)
    readback = run["corpusSqlReadback"]
    require(run["profileSha256"].lower() == sha(profile_path.read_bytes())
            and run["corpusManifestSha256"].lower() == sha(manifest_path.read_bytes())
            and run["jobId"] == values["api"]["jobId"] and run["lifecycleComplete"] is True
            and run["externalProviderUsed"] is True and run["preflight"] is False
            and extended_windows_path(pathlib.Path(readback["path"])) == files["corpusSql"]
            and readback["sha256"].lower() == sha(files["corpusSql"].read_bytes()),
            "planning_run_lifecycle_or_readback_binding_changed")
    corpus = corpus_index(manifest, read_json(package_path), values["corpusSql"])
    trace_paths = sorted((runtime / "runtime/private-traces").glob("*.json"))
    traces = [read_json(path) for path in trace_paths]
    probes = read_call_probes(runtime / "runtime/paid-backend.stdout.log")
    result = evaluate(profile, corpus, values["submission"], values["api"], values["sql"], traces, probes)
    result["packageToSqlTextTransport"] = corpus["packageToSqlTextTransport"]
    result["inputPins"] = {"profileSha256": sha(profile_path.read_bytes()), "manifestSha256": sha(manifest_path.read_bytes()),
                           "packageSha256": sha(package_path.read_bytes()),
                           **{name + "Sha256": sha(path.read_bytes()) for name, path in files.items()},
                           "executionResultSha256": sha(run_path.read_bytes()),
                           "traceFiles": [{"name": path.name, "sha256": sha(path.read_bytes())} for path in trace_paths],
                           "probeLogSha256": sha((runtime / "runtime/paid-backend.stdout.log").read_bytes())}
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", type=pathlib.Path, required=True)
    parser.add_argument("--runtime", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    result = verify(args.profile, args.runtime)
    with extended_windows_path(args.output).open("x", encoding="utf-8") as handle:
        json.dump(result, handle, indent=2)
    print(json.dumps({"passed": result["passed"], "cells": result["gridCellCount"], "semanticPlanningApproved": False}))
