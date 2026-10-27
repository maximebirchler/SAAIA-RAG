"""Pure adverse tests for the planning-result oracle; never calls a provider."""
import base64
import copy
import gzip
import json
import pathlib
import tempfile
import unittest

import verify_planning_result as verifier


def encoded(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def fixture(count=4, extra_proofs=0):
    days = ["lundi", "mardi", "mercredi", "jeudi", "vendredi"][:count // 4]
    columns = ["petit-déjeuner", "déjeuner", "collation", "souper"]
    policy = "distinct_structured_layout" if count == 4 else "structured_layout"
    case = {"kind": "one-day-4" if count == 4 else "five-day-20",
            "question": "Quatre propositions distinctes." if count == 4 else "Un planning documentaire.",
            "rowLabels": days, "columns": columns, "answerUnitCount": count, "functionalFiveDayAcceptance": count == 20,
            "selectionPolicy": policy}
    profile = {"case": case, "provider": {"model": "economical-fixture", "apiProtocol": "responses", "reasoningEffort": "low"}}
    load = {"rowLabels": days, "columns": columns, "rowCount": len(days), "columnCount": 4,
            "answerUnitCount": count, "atomicEvidenceCount": count, "structuredLayout": True,
            "selectionPolicy": policy}
    handoff = {"requestText": case["question"], "transferStage": "before_retrieval", "load": load,
               "researchState": {"evidenceReferences": []}}
    doc_id, revision = "11111111-1111-4111-8111-111111111111", "22222222-2222-4222-8222-222222222222"
    doc = {"docId": doc_id, "revisionId": revision, "docPath": "Cuisine/artificial.pdf", "sourceSha256": "a" * 64,
           "canonicalHash": "c" * 64, "pageCount": count + extra_proofs, "canonical": {"pages": []}}
    corpus = {"documents": {doc_id: doc}, "chunks": {}, "anchors": {}, "cards": {}, "packageChunks": {}}
    claims, references, items = [], [], []
    for index in range(count + extra_proofs):
        recipe = index if index < count else 0
        title = f"Recette {recipe + 1}"
        text = title + "\r\nIngrédients : 100 g de produit.\r\nPréparation : mélanger puis cuire. 😀\r\n"
        chunk_id = verifier.stable_chunk_id(doc_id, index)
        chunk = {"retrievalChunkId": chunk_id, "revisionId": revision, "chunkIndex": index,
                 "pageStart": index + 1, "pageEnd": index + 1, "text": text, "checksumSha256": verifier.sha(text.encode()),
                 "metadata": {"contentRole": "content", "extractionQualitySignals": ["layout_not_verified"],
                              "extractionTextStatus": "native", "extractionOcrCandidate": False}}
        corpus["chunks"][chunk_id] = chunk
        identifier = "proof-" + str(index + 1)
        reference = {"evidenceId": identifier, "docId": doc_id, "revisionId": revision, "docPath": doc["docPath"],
                     "sourceHash": doc["sourceSha256"], "pageStart": index + 1, "pageEnd": index + 1, "chunkId": chunk_id}
        references.append(reference)
        items.append({"evidenceId": identifier, "candidateTitle": title, "content": text, "contentOffset": 0,
                      "originalContentLength": verifier.utf16_length(text), "contentTruncated": False,
                      "physicalPageStart": index + 1, "physicalPageEnd": index + 1, "contentRole": "content",
                      "sourceQuality": {"textStatus": "native", "ocrCandidate": False, "signals": ["layout_not_verified"],
                                        "semanticAccuracy": "not_verified", "metadataMalformed": False,
                                        "metadataTruncated": False, "signalsOmittedCount": 0}})
        if index < count:
            claims.append({"claimId": "C" + str(index + 1), "selectedItem": title, "text": title,
                           "evidenceIds": [identifier]})
        else:
            claims[0]["evidenceIds"].append(identifier)
    answer = "| Jour | " + " | ".join(columns) + " |\n|---|---|---|---|---|\n"
    for day_index, day in enumerate(days):
        answer += "| " + day + " | " + " | ".join(f"Recette {index + 1} [C{index + 1}]"
                       for index in range(day_index * 4, day_index * 4 + 4)) + " |\n"
    result = {"outcome": "answered", "answerText": answer, "claims": claims, "evidence": references, "providerCallCount": 1}
    api = {"jobId": "job-1", "status": "succeeded", "attemptCount": 1, "result": result}
    sql = {**copy.deepcopy(api), "handoff": copy.deepcopy(handoff), "canonicalHashes": [{"revisionId": revision, "sha256": doc["canonicalHash"]}],
           "events": [{"sequence": 1, "status": "succeeded", "evidence": copy.deepcopy(references)}]}
    payload = {"request": case["question"], "load": copy.deepcopy(load), "claimCoordinates": verifier.coordinates(case), "evidence": items}
    value = {"profile": profile, "corpus": corpus, "submission": {"handoff": handoff}, "api": api, "sql": sql,
             "payload": payload, "traces": [], "probes": []}
    rebind(value)
    return value


def rebind(value, sync_result=True, sync_reader=False):
    """Rebind outer envelopes so adverse tests reach the intended deep guard."""
    if sync_result:
        value["sql"]["result"] = copy.deepcopy(value["api"]["result"])
    if sync_reader:
        value["sql"]["events"][0]["evidence"] = copy.deepcopy(value["api"]["result"]["evidence"])
    payload = value["payload"]
    count = len(payload["evidence"])
    payload["evidenceProjection"] = {"status": "observed", "instruction": "Omissions do not establish source absence.",
        "canonicalProjection": {"inputCount": count, "projectedCount": count, "omittedByPromptBudgetCount": 0},
        "currentCall": {"collection": "evidence", "visibleCount": count, "fromCanonicalProjectionCount": count,
                        "outsideCanonicalProjectionCount": 0, "canonicalProjectedNotSelectedCount": 0,
                        "truncatedContentCount": sum(item["contentTruncated"] for item in payload["evidence"]),
                        "supportingWindowsOmittedCount": sum(item.get("supportingContentWindowsOmittedCount", 0) for item in payload["evidence"])}}
    completion = encoded(verifier.answer_signature(value["api"]["result"]))
    body = {"model": value["profile"]["provider"]["model"], "input": [{"role": "user", "content": encoded(payload)}],
            "store": False, "stream": False, "reasoning": {"effort": "low"}}
    request = encoded(body)
    value["traces"] = [{"jobId": "job-1", "role": "writer", "requestJson": request, "requestSha256": verifier.sha(request.encode()),
                        "httpAttemptCount": 1, "capturedAtUtc": "2026-10-27T20:00:00Z", "completionJson": completion,
                        "responseEnvelopeJson": encoded({"status": "completed", "output": [{"type": "message", "content": [
                            {"type": "output_text", "text": completion}]}]})}]
    value["probes"] = [{"job_id": "job-1", "role": "writer", "request_sha256": verifier.sha(request.encode()),
                        "request_characters": verifier.utf16_length(request), "call_id": "call-1", "wire_protocol": "responses",
                        "diagnostic_dropped_measurements": 0, "diagnostic_truncated_values": 0}]


def evaluate(value):
    return verifier.evaluate(*(value[key] for key in ("profile", "corpus", "submission", "api", "sql", "traces", "probes")))


def corpus_fixture():
    value = fixture()
    doc = next(iter(value["corpus"]["documents"].values()))
    chunks, anchors = [], []
    for chunk in value["corpus"]["chunks"].values():
        projected = {key: chunk[key] for key in ("chunkIndex", "pageStart", "pageEnd", "text")}
        projected.update(checksum=base64.b64encode(bytes.fromhex(chunk["checksumSha256"])).decode(),
                         contentRole="content", chunkType="native_fixture", extractionTextStatus="native",
                         extractionOcrCandidate=False, extractionQualitySignals=["layout_not_verified"])
        chunks.append(projected)
        chunk["metadata"]["chunkType"] = "native_fixture"
        chunk["text"] = chunk["text"].replace("\r\n", "\n")
        chunk["checksumSha256"] = verifier.sha(chunk["text"].encode())
        anchors.append({"anchorId": "anchor-" + str(chunk["chunkIndex"]), "projectionId": chunk["retrievalChunkId"],
                        "projectionType": "retrieval_chunk", "documentId": doc["docId"], "revisionId": doc["revisionId"],
                        "sourceSha256": doc["sourceSha256"]})
    canonical = {"documentId": doc["docId"], "revisionId": doc["revisionId"], "source": {"sha256": doc["sourceSha256"]},
                 "pageCount": 4, "pages": [{"pageNumber": index} for index in range(1, 5)]}
    raw = encoded({"document": canonical, "sourceAnchors": anchors}).encode()
    artifact = {"artifactType": "canonical_ingestion_bundle", "byteSize": len(raw),
                "payload": base64.b64encode(gzip.compress(raw)).decode(),
                "contentHash": base64.b64encode(bytes.fromhex(verifier.sha(raw))).decode()}
    manifest_doc = {key: doc[key] for key in ("docId", "revisionId", "docPath", "sourceSha256", "pageCount")}
    manifest_doc.update(physicalPdfPath="synthetic-only.pdf", canonicalHash=verifier.sha(raw), completeCanonicalBundle=True,
                        sourceAnchorCount=4, retrievalChunkCount=4)
    manifest = {"schemaVersion": "saaia-planning-complete-corpus.v1", "completeDocuments": True, "exactSqlReadback": True,
                "documentCount": 1, "physicalPageCount": 4, "documents": [manifest_doc]}
    package = {"schemaVersion": "saaia-planning-native-corpus-package.v1", "boundaries": {"fullDocuments": True},
               "documents": [{"docId": doc["docId"], "revisionId": doc["revisionId"], "docPath": doc["docPath"],
                              "physicalPdfPath": "synthetic-only.pdf", "sourceSha256": doc["sourceSha256"],
                              "expectedPageCount": 4, "artifact": artifact, "retrievalChunks": chunks}]}
    snapshot = {"schemaVersion": "saaia-planning-corpus-sql-observation.v1",
                "retrievalChunks": list(value["corpus"]["chunks"].values()), "contentCards": []}
    return manifest, package, snapshot


def native_selection_fixture():
    value = fixture()
    doc = next(iter(value["corpus"]["documents"].values()))
    source = value["api"]["result"]["evidence"][0]
    literal = value["payload"]["evidence"][0]["content"]
    doc["canonical"].update(manifestSha256="d" * 64, pages=[{"pageNumber": 1, "qualityFlags": [], "blocks": [
        {"blockId": "native-1", "blockType": "native_text_recovery", "text": {"raw": literal}, "qualityFlags": []}]}])
    lines, references, items = verifier.raw_lines(literal), [], []
    for ordinal, line in enumerate(lines):
        start = sum(len(part) for part in lines[:ordinal])
        end = start + len(line)
        identity = [doc["docId"], doc["revisionId"], doc["sourceSha256"], "d" * 64, 1, "native-1",
                    verifier.sha(literal.encode()), ordinal, start, end, verifier.sha(line.encode())]
        locator = {"physicalPage": 1, "blockId": "native-1", "rawTextSha256": identity[6], "lineOrdinal": ordinal,
                   "unicodeScalarStart": start, "unicodeScalarEndExclusive": end, "lineTextSha256": identity[-1],
                   "lineId": "native_line_" + verifier.sha(json.dumps(identity, separators=(",", ":")).encode())}
        reference = {key: val for key, val in source.items() if key != "chunkId"}
        reference.update(evidenceId="native-proof-" + str(ordinal), nativeObservation=locator)
        references.append(reference)
        quality = ["canonical_native_audit_literal_not_primary"]
        items.append({"evidenceId": reference["evidenceId"], "content": line, "contentOffset": 0,
                      "originalContentLength": verifier.utf16_length(line), "contentTruncated": False,
                      "physicalPageStart": 1, "physicalPageEnd": 1, "contentRole": "secondary_native_audit",
                      "sourceKey": "internal-source-1", "nativeObservation": {"reference": locator,
                          "role": "secondary_native_audit", "semanticAccuracy": "not_verified", "sourceFidelityApproved": False,
                          "qualitySignals": quality, "lineOffset": 0, "lineLimit": len(lines),
                          "viewLineCount": len(lines), "nextLineOffset": None},
                      "sourceQuality": {"signals": quality, "semanticAccuracy": "not_verified"}})
    value["api"]["result"]["claims"][0]["evidenceIds"] = [ref["evidenceId"] for ref in references]
    value["api"]["result"]["evidence"] = references + value["api"]["result"]["evidence"][1:]
    value["payload"]["evidence"] = items + value["payload"]["evidence"][1:]
    value["payload"]["alternativeNativeReadings"] = {"views": [{"sourceKey": "internal-source-1", "blockId": "native-1",
        "offset": 0, "limit": len(lines), "rawLineCount": len(lines), "nextOffset": None,
        "lines": [{"evidenceId": ref["evidenceId"]} for ref in references]}]}
    rebind(value, sync_reader=True)
    return value


class PlanningResultMechanicalTests(unittest.TestCase):
    def rejects(self, value, code):
        with self.assertRaisesRegex(ValueError, "^" + code + "$"):
            evaluate(value)

    def test_four_cells_are_mechanical_diagnostic_never_full_or_semantic_acceptance(self):
        result = evaluate(fixture())
        self.assertEqual(4, result["gridCellCount"])
        self.assertTrue(result["mechanicalPlanningPassed"])
        self.assertTrue(result["manualSemanticReviewRequired"])
        self.assertFalse(result["semanticPlanningApproved"])
        self.assertFalse(result["functionalFiveDayAccepted"])
        self.assertFalse(result["clientUiValidated"])
        self.assertFalse(result["independentExternalHttpCapture"])

    def test_twenty_distinct_recipes_and_multiple_proofs_for_one_recipe_are_accepted(self):
        result = evaluate(fixture(20, extra_proofs=2))
        self.assertEqual(20, result["distinctSelectedRecipeIdentityCount"])
        self.assertEqual(22, result["serverReferenceCardCount"])
        self.assertEqual(3, result["cellsForManualReview"][0]["proofCount"])

    def test_three_exact_native_lines_can_jointly_supply_one_canonical_body_candidate(self):
        result = evaluate(native_selection_fixture())
        self.assertEqual(3, result["cellsForManualReview"][0]["proofCount"])
        self.assertEqual(4, result["distinctSelectedRecipeIdentityCount"])

    def test_native_line_quality_locator_and_visible_cursor_are_checked(self):
        value = native_selection_fixture()
        value["payload"]["evidence"][0]["nativeObservation"]["nextLineOffset"] = 2
        rebind(value)
        self.rejects(value, "planning_native_model_resume_cursor_contradicts_view")
        value = native_selection_fixture()
        locator = value["api"]["result"]["evidence"][0]["nativeObservation"]
        locator["rawTextSha256"] = "e" * 64
        value["payload"]["evidence"][0]["nativeObservation"]["reference"] = locator
        rebind(value, sync_reader=True)
        self.rejects(value, "planning_native_literal_identity_or_quality_changed")

    def test_native_toc_cannot_replace_substantive_body_classification(self):
        value = native_selection_fixture()
        next(iter(value["corpus"]["chunks"].values()))["metadata"]["contentRole"] = "navigation"
        self.rejects(value, "planning_recipe_has_no_visible_substantive_body_candidate")

    def test_twenty_recipes_can_share_a_real_body_chunk_without_twenty_source_cards(self):
        value = fixture(20)
        literal = "\n".join(chunk["text"] for chunk in value["corpus"]["chunks"].values())
        chunk = next(iter(value["corpus"]["chunks"].values()))
        chunk["text"] = literal
        item = value["payload"]["evidence"][0]
        item.update(content=literal, originalContentLength=verifier.utf16_length(literal))
        value["payload"]["evidence"] = [item]
        value["api"]["result"]["evidence"] = value["api"]["result"]["evidence"][:1]
        for claim in value["api"]["result"]["claims"]:
            claim["evidenceIds"] = ["proof-1"]
        rebind(value, sync_reader=True)
        result = evaluate(value)
        self.assertEqual(20, result["distinctSelectedRecipeIdentityCount"])
        self.assertEqual(1, result["serverReferenceCardCount"])

    def test_twenty_toc_names_cannot_count_as_twenty_recipe_bodies(self):
        value = fixture(20)
        for item, chunk in zip(value["payload"]["evidence"], value["corpus"]["chunks"].values()):
            text = item["candidateTitle"] + " ........ 12"
            item.update(content=text, originalContentLength=verifier.utf16_length(text), contentRole="navigation")
            chunk["text"] = text
            chunk["metadata"]["contentRole"] = "navigation"
        rebind(value)
        self.rejects(value, "planning_recipe_has_no_visible_substantive_body_candidate")

    def test_title_only_is_rejected_even_with_missing_candidate_title_and_content_role(self):
        value = fixture()
        item = value["payload"]["evidence"][0]
        item.update(content="Recette 1", candidateTitle=None, originalContentLength=9)
        next(iter(value["corpus"]["chunks"].values()))["text"] = "Recette 1"
        rebind(value)
        self.rejects(value, "planning_recipe_has_no_visible_substantive_body_candidate")

    def test_short_recipe_name_does_not_match_a_different_longer_numbered_name(self):
        value = fixture()
        item = value["payload"]["evidence"][0]
        item.update(content=item["content"].replace("Recette 1", "Recette 10"), candidateTitle="Recette 10")
        next(iter(value["corpus"]["chunks"].values()))["text"] = item["content"]
        item["originalContentLength"] = verifier.utf16_length(item["content"])
        rebind(value)
        self.rejects(value, "planning_recipe_has_no_visible_substantive_body_candidate")

    def test_provisional_title_cannot_invent_a_recipe_absent_from_the_source(self):
        value = fixture()
        value["payload"]["evidence"][0]["candidateTitle"] = "Une recette inventée"
        rebind(value)
        self.rejects(value, "planning_visible_candidate_title_not_source_grounded")

    def test_wrong_pdf_hash_is_rejected_even_with_rebound_sql_and_completion(self):
        value = fixture()
        value["api"]["result"]["evidence"][0]["sourceHash"] = "b" * 64
        rebind(value, sync_reader=True)
        self.rejects(value, "planning_citation_outside_frozen_pdf")

    def test_empty_cell_and_wrong_coordinate_are_rejected(self):
        for replacement in ("", "Recette 1 [C2]"):
            with self.subTest(replacement=replacement):
                value = fixture()
                value["api"]["result"]["answerText"] = value["api"]["result"]["answerText"].replace("Recette 1 [C1]", replacement)
                rebind(value)
                self.rejects(value, "planning_unknown_or_repeated_visible_citation")

    def test_exchanging_cells_preserves_counts_but_is_rejected(self):
        value = fixture()
        text = value["api"]["result"]["answerText"]
        value["api"]["result"]["answerText"] = text.replace("Recette 1 [C1]", "TEMP").replace("Recette 2 [C2]", "Recette 1 [C1]").replace("TEMP", "Recette 2 [C2]")
        rebind(value)
        self.rejects(value, "planning_empty_or_misbound_grid_cell")

    def test_new_chunk_id_does_not_make_same_recipe_a_distinct_recipe(self):
        value = fixture(4)
        value["api"]["result"]["claims"][1]["selectedItem"] = "RECETTE--1"
        rebind(value)
        self.rejects(value, "planning_duplicate_recipe_identity")

    def test_historical_grid_reuses_supported_recipe_with_one_source_card_and_reports_cells(self):
        value = fixture(20)
        result = value["api"]["result"]
        reused, original = result["claims"][4], result["claims"][0]
        reused.update(selectedItem=original["selectedItem"], text=original["text"], evidenceIds=original["evidenceIds"][:])
        result["answerText"] = result["answerText"].replace("Recette 5 [C5]", "Recette 1 [C5]")
        result["evidence"] = [row for row in result["evidence"] if row["evidenceId"] != "proof-5"]
        value["payload"]["evidence"] = [row for row in value["payload"]["evidence"] if row["evidenceId"] != "proof-5"]
        rebind(value, sync_reader=True)
        review = evaluate(value)
        self.assertEqual(20, review["gridCellCount"])
        self.assertEqual(19, review["distinctSelectedRecipeIdentityCount"])
        self.assertEqual(19, review["serverReferenceCardCount"])
        self.assertEqual(1, review["repeatedRecipeReuseCount"])
        self.assertEqual(["C1", "C5"], review["repeatedRecipeGroups"][0]["claimIds"])
        self.assertFalse(review["semanticPlanningApproved"])

    def test_missing_or_unknown_selection_policy_cannot_silently_weaken_explicit_case(self):
        for policy in (None, "unrestricted"):
            value = fixture()
            if policy is None:
                del value["profile"]["case"]["selectionPolicy"]
            else:
                value["profile"]["case"]["selectionPolicy"] = policy
            with self.subTest(policy=policy):
                self.rejects(value, "planning_case_selection_policy_invalid")

    def test_repeatable_recipe_name_still_requires_its_own_body_in_that_cells_proofs(self):
        value = fixture(20)
        result = value["api"]["result"]
        result["claims"][4].update(selectedItem="Recette 1", text="Recette 1")
        result["answerText"] = result["answerText"].replace("Recette 5 [C5]", "Recette 1 [C5]")
        # Keep C5 bound to recipe 5's body: repeatability never transfers the
        # evidence of a neighbouring recipe or permits a fabricated identity.
        rebind(value)
        self.rejects(value, "planning_recipe_has_no_visible_substantive_body_candidate")

    def test_case_policy_cannot_be_strengthened_or_weakened_without_a_new_registered_case(self):
        for count, policy in ((20, "distinct_structured_layout"), (4, "structured_layout")):
            value = fixture(count)
            value["profile"]["case"]["selectionPolicy"] = policy
            with self.subTest(count=count):
                self.rejects(value, "planning_case_selection_policy_invalid")

    def test_unknown_claim_evidence_and_unused_cards_are_rejected(self):
        value = fixture()
        value["api"]["result"]["claims"][0]["evidenceIds"] = ["invented"]
        rebind(value)
        self.rejects(value, "planning_unknown_citation_or_unused_card")

    def test_invented_visible_claim_marker_is_rejected(self):
        value = fixture()
        value["api"]["result"]["answerText"] += "\nAutre proposition [C900]."
        rebind(value)
        self.rejects(value, "planning_unknown_or_repeated_visible_citation")

    def test_citation_that_never_reached_real_reader_is_rejected(self):
        value = fixture()
        value["sql"]["events"] = []
        self.rejects(value, "planning_result_citation_not_read_by_real_tools")

    def test_body_missing_from_actual_final_model_message_is_rejected(self):
        value = fixture()
        value["payload"]["evidence"].pop()
        rebind(value)
        self.rejects(value, "planning_cited_body_missing_from_final_model_input")

    def test_changed_body_with_rebound_request_hashes_is_still_rejected(self):
        value = fixture()
        item = value["payload"]["evidence"][0]
        item["content"] = item["content"].replace("100 g", "900 g")
        rebind(value)
        self.rejects(value, "planning_visible_body_not_canonical_exact")

    def test_hidden_truncation_is_rejected_and_explicit_exact_window_is_accepted(self):
        value = fixture()
        item = value["payload"]["evidence"][0]
        item["content"] = item["content"][:65]
        rebind(value)
        self.rejects(value, "planning_body_truncation_or_cursor_hidden")
        item["contentTruncated"] = True
        item["nextContentOffset"] = verifier.utf16_length(item["content"])
        rebind(value)
        result = evaluate(value)
        self.assertEqual(1, result["cellsForManualReview"][0]["explicitlyTruncatedProofCount"])

    def test_quality_warning_removed_after_last_64_is_not_silently_accepted(self):
        value = fixture()
        chunk = next(iter(value["corpus"]["chunks"].values()))
        signals = ["warning_" + str(index) for index in range(65)]
        chunk["metadata"]["extractionQualitySignals"] = signals
        quality = value["payload"]["evidence"][0]["sourceQuality"]
        quality["signals"] = signals[:64]
        rebind(value)
        self.rejects(value, "planning_extraction_quality_or_omissions_changed")
        quality.update(signalsOmittedCount=1, metadataTruncated=True)
        rebind(value)
        self.assertTrue(evaluate(value)["qualityAndExplicitOmissionsChecked"])

    def test_duplicate_physical_card_under_new_evidence_id_is_rejected(self):
        value = fixture(extra_proofs=1)
        first = copy.deepcopy(value["api"]["result"]["evidence"][0])
        first["evidenceId"] = "proof-5"
        value["api"]["result"]["evidence"][-1] = first
        item = copy.deepcopy(value["payload"]["evidence"][0])
        item["evidenceId"] = "proof-5"
        value["payload"]["evidence"][-1] = item
        rebind(value, sync_reader=True)
        self.rejects(value, "planning_duplicate_physical_source_card")

    def test_api_sql_mismatch_and_unanswered_status_are_rejected(self):
        for mutate in (lambda value: value["sql"].update(status="failed"),
                       lambda value: value["sql"]["result"].update(answerText="different")):
            value = fixture()
            mutate(value)
            self.rejects(value, "planning_job_not_once_successful_or_sql_differs")

    def test_trace_byte_tampering_and_wrong_protocol_are_rejected(self):
        value = fixture()
        value["traces"][0]["requestJson"] += " "
        self.rejects(value, "planning_exact_request_or_attempt_identity_changed")
        value = fixture()
        value["probes"][0]["wire_protocol"] = "chat-completions"
        self.rejects(value, "planning_call_diagnostics_incomplete")

    def test_final_provider_response_envelope_cannot_differ_from_stored_completion(self):
        value = fixture()
        envelope = json.loads(value["traces"][0]["responseEnvelopeJson"])
        envelope["output"][0]["content"][0]["text"] = "{}"
        value["traces"][0]["responseEnvelopeJson"] = encoded(envelope)
        self.rejects(value, "planning_final_provider_envelope_differs_or_incomplete")

    def test_card_uses_exact_canonical_body_not_search_text(self):
        value = fixture()
        chunk = next(iter(value["corpus"]["chunks"].values()))
        reference = value["api"]["result"]["evidence"][0]
        reference.pop("chunkId")
        reference["contentCardId"] = "card-1"
        value["corpus"]["cards"]["card-1"] = {"contentCardId": "card-1", "docId": reference["docId"],
            "revisionId": reference["revisionId"], "pageStart": 1, "pageEnd": 1, "title": "Recette 1",
            "searchText": "This search-only text is not evidence.", "metadata": {"evidence": {
                "schemaVersion": "canonical_section_anchor_evidence_v1", "facts": [{"kind": "canonical_source",
                     "label": "retrieval_chunk_index", "value": str(chunk["chunkIndex"])}]}}}
        rebind(value, sync_reader=True)
        self.assertTrue(evaluate(value)["passed"])
        value["payload"]["evidence"][0]["content"] = "This search-only text is not evidence."
        rebind(value)
        self.rejects(value, "planning_visible_body_not_canonical_exact")

    def test_escaped_pipes_do_not_create_extra_grid_cells(self):
        value = fixture()
        value["api"]["result"]["answerText"] = value["api"]["result"]["answerText"].replace("Recette 1 [C1]", r"Recette 1 \| proposition [C1]")
        rebind(value)
        self.assertTrue(evaluate(value)["passed"])


class PlanningCorpusBindingTests(unittest.TestCase):
    def test_complete_file_pipeline_binds_profile_package_pdf_sql_traces_and_run_receipt(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            runtime = root / "owned-environment"
            (runtime / "runtime/private-traces").mkdir(parents=True)
            def write(path, value):
                path.write_text(encoded(value), encoding="utf-8")
                return verifier.sha(path.read_bytes())
            pdf = root / "artificial.pdf"
            pdf.write_bytes(b"%PDF-1.7\nSynthetic unit test identity, not an extraction fixture.\n")
            pdf_hash = verifier.sha(pdf.read_bytes())
            manifest, package, snapshot = corpus_fixture()
            md, pd = manifest["documents"][0], package["documents"][0]
            bundle, _ = verifier.decode_bundle(pd["artifact"])
            bundle["document"]["source"]["sha256"] = pdf_hash
            for anchor in bundle["sourceAnchors"]:
                anchor["sourceSha256"] = pdf_hash
            raw = encoded(bundle).encode()
            pd["artifact"].update(payload=base64.b64encode(gzip.compress(raw)).decode(), byteSize=len(raw),
                                  contentHash=base64.b64encode(bytes.fromhex(verifier.sha(raw))).decode())
            for doc in (md, pd):
                doc.update(physicalPdfPath=str(pdf), sourceSha256=pdf_hash)
            md["canonicalHash"] = verifier.sha(raw)
            package_path, manifest_path = root / "package.json", root / "manifest.json"
            manifest.update(packagePath=str(package_path), packageSha256=write(package_path, package))
            manifest_hash = write(manifest_path, manifest)
            value = fixture()
            for reference in value["api"]["result"]["evidence"]:
                reference["sourceHash"] = pdf_hash
            value["sql"]["canonicalHashes"][0]["sha256"] = md["canonicalHash"]
            value["profile"].update(schemaVersion="saaia-local-openai-planning-campaign.v1",
                corpus={"manifestPath": str(manifest_path), "manifestSha256": manifest_hash})
            rebind(value, sync_reader=True)
            for item in value["payload"]["evidence"]:
                item["content"] = item["content"].replace("\r\n", "\n")
                item["originalContentLength"] = verifier.utf16_length(item["content"])
            rebind(value, sync_reader=True)
            profile_path = root / "profile.json"
            profile_hash = write(profile_path, value["profile"])
            for key, filename in (("submission", "planning-submission.private.json"), ("api", "planning-terminal.private.json"),
                                  ("sql", "planning-sql-job.private.json")):
                write(runtime / "runtime" / filename, value[key])
            readback_path = runtime / "runtime/planning-corpus-sql.private.json"
            readback_hash = write(readback_path, snapshot)
            write(runtime / "runtime/private-traces/trace.json", value["traces"][0])
            (runtime / "runtime/paid-backend.stdout.log").write_text(
                "operation=llm.call measurements=" + encoded(value["probes"][0]) + "\n", encoding="ascii")
            run_path = root / "planning-result.json"
            run = {"profileSha256": profile_hash, "corpusManifestSha256": manifest_hash, "jobId": "job-1",
                   "lifecycleComplete": True, "externalProviderUsed": True, "preflight": False,
                   "corpusSqlReadback": {"path": str(readback_path), "sha256": readback_hash}}
            write(run_path, run)
            result = verifier.verify(profile_path, runtime)
            self.assertTrue(result["passed"])
            self.assertEqual(readback_hash, result["inputPins"]["corpusSqlSha256"])
            self.assertEqual(4, result["packageToSqlTextTransport"]["changedChunkCount"])
            run["corpusSqlReadback"]["sha256"] = "0" * 64
            write(run_path, run)
            with self.assertRaisesRegex(ValueError, "planning_run_lifecycle_or_readback_binding_changed"):
                verifier.verify(profile_path, runtime)

    def test_real_sql_mapping_must_match_full_product_chunk_package_and_anchor_inventory(self):
        manifest, package, snapshot = corpus_fixture()
        index = verifier.corpus_index(manifest, package, snapshot)
        self.assertEqual(4, len(index["chunks"]))
        self.assertEqual(4, len(index["anchors"]))
        self.assertEqual(4, index["packageToSqlTextTransport"]["changedChunkCount"])
        self.assertEqual(12, index["packageToSqlTextTransport"]["normalizedCRLFPairCount"])
        self.assertFalse(index["packageToSqlTextTransport"]["otherCharacterChangesAccepted"])

    def test_crlf_storage_policy_does_not_allow_lone_cr_or_unicode_whitespace_repair(self):
        for replacement in (lambda text: text.replace("\n", "\r"),
                            lambda text: text.replace("Ingrédients", "Ingredients"),
                            lambda text: text.replace("100 g", "100  g")):
            manifest, package, snapshot = corpus_fixture()
            chunk = snapshot["retrievalChunks"][0]
            chunk["text"] = replacement(chunk["text"])
            chunk["checksumSha256"] = verifier.sha(chunk["text"].encode())
            with self.assertRaisesRegex(ValueError, "planning_sql_chunk_text_or_identity_changed"):
                verifier.corpus_index(manifest, package, snapshot)

    def test_changed_sql_literal_is_rejected_even_with_recomputed_local_checksum(self):
        manifest, package, snapshot = corpus_fixture()
        chunk = snapshot["retrievalChunks"][0]
        chunk["text"] = chunk["text"].replace("100 g", "500 g")
        chunk["checksumSha256"] = verifier.sha(chunk["text"].encode())
        with self.assertRaisesRegex(ValueError, "planning_sql_chunk_text_or_identity_changed"):
            verifier.corpus_index(manifest, package, snapshot)

    def test_removing_sql_navigation_or_quality_flags_is_rejected_against_package(self):
        for mutation in (lambda metadata: metadata.update(contentRole="navigation"),
                         lambda metadata: metadata.update(extractionQualitySignals=[])):
            with self.subTest(mutation=mutation):
                manifest, package, snapshot = corpus_fixture()
                mutation(snapshot["retrievalChunks"][0]["metadata"])
                with self.assertRaisesRegex(ValueError, "planning_sql_chunk_quality_changed"):
                    verifier.corpus_index(manifest, package, snapshot)

    def test_missing_page_or_changed_canonical_generation_is_rejected(self):
        manifest, package, snapshot = corpus_fixture()
        manifest["documents"][0]["canonicalHash"] = "f" * 64
        with self.assertRaisesRegex(ValueError, "planning_manifest_package_identity_changed"):
            verifier.corpus_index(manifest, package, snapshot)

    def test_canonical_payload_hash_cannot_be_replaced_by_a_claimed_hash(self):
        raw = b'{"document":{}}'
        artifact = {"artifactType": "canonical_ingestion_bundle", "byteSize": len(raw),
                    "payload": base64.b64encode(gzip.compress(raw)).decode(), "contentHash": base64.b64encode(bytes(32)).decode()}
        with self.assertRaisesRegex(ValueError, "planning_canonical_bytes_changed"):
            verifier.decode_bundle(artifact)

    def test_normalization_is_lexical_not_meal_suitability(self):
        self.assertEqual("petit dejeuner", verifier.normal("Petit-déjeuner", True))
        self.assertEqual(verifier.normal("Crème  aux   fruits"), verifier.normal("CRÈME-aux-fruits"))


if __name__ == "__main__":
    unittest.main()
