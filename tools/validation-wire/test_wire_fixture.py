import json
import os
import pathlib
import tempfile
import unittest
from fixture_wire import current_prompt, native_inventory, response_envelope, scripted_reply, strict_call, resume_native_read
from prepare_cases import raw_lines
from verify_wire import utf16_length, utf16_slice, verify_projection, read_call_probes, resolve_native_inventory_reference, extended_windows_path


class WireFixturePureTests(unittest.TestCase):
    def native_resume_fixture(self):
        view = {"sourceKey": "S1", "blockId": "B1", "rawTextSha256": "a" * 64, "physicalPage": 5,
                "offset": 1, "limit": 2, "returnedLineCount": 1, "nextOffset": 2, "status": "partial_model_budget"}
        prompt = {"evidence": [{"sourceKey": "S1", "nativeObservation": {"reference": {
            "blockId": "B1", "rawTextSha256": "a" * 64}}}], "alternativeNativeReadings": {"views": [view]}}
        keys = ("sourceKey", "pageStart", "pageEnd", "topK", "nativeObservationView", "nativeObservationBlockId",
                "nativeObservationRawSha256", "nativeObservationOffset", "nativeObservationLimit")
        body = {"tools": [{"function": {"name": "read_source", "parameters": {
            "properties": {key: {} for key in keys}, "required": list(keys)}}}]}
        return body, prompt, {"physicalPage": 5, "nativeBlockId": "B1", "expectedNativeLines": ["A", "B", "C"]}

    def test_native_resume_uses_current_observed_cursor_and_remaining_target(self):
        body, prompt, scenario = self.native_resume_fixture()
        args = json.loads(resume_native_read(body, prompt, scenario, 7)["arguments"])
        self.assertEqual((2, 1), (args["nativeObservationOffset"], args["nativeObservationLimit"]))

    def test_native_resume_never_invents_missing_cursor(self):
        body, prompt, scenario = self.native_resume_fixture()
        del prompt["alternativeNativeReadings"]["views"][0]["nextOffset"]
        with self.assertRaisesRegex(ValueError, "observed_partial_native_cursor_required"):
            resume_native_read(body, prompt, scenario, 7)

    def test_native_resume_refuses_cursor_skipping_a_visible_window_gap(self):
        body, prompt, scenario = self.native_resume_fixture()
        prompt["alternativeNativeReadings"]["views"][0]["nextOffset"] = 3
        with self.assertRaisesRegex(ValueError, "observed_partial_native_cursor_required"):
            resume_native_read(body, prompt, scenario, 7)

    @unittest.skipUnless(os.name == "nt", "Windows long-path boundary")
    def test_existing_trace_over_260_characters_is_read_without_changing_windows_policy(self):
        with tempfile.TemporaryDirectory() as folder:
            normal = pathlib.Path(folder) / ("x" * 230 + ".json")
            self.assertGreater(len(str(normal)), 260)
            extended = extended_windows_path(normal)
            try:
                extended.write_bytes(b'{"requestSha256":"literal"}')
                self.assertEqual(b'{"requestSha256":"literal"}', extended_windows_path(normal).read_bytes())
            finally:
                extended.unlink(missing_ok=True)

    def native_inventory_reference_case(self):
        case = {"docId": "doc", "revisionId": "revision", "sourceSha256": "a" * 64, "physicalPage": 5}
        descriptor = {"docId": "doc", "revisionId": "revision", "sourceSha256": "a" * 64, "physicalPage": 5}
        inventory = {"availability": "observed", "reason": None, "observationCount": 2, "offset": 1,
                     "nextOffset": None, "observations": [descriptor]}
        reference = dict(case, sourceKey="S1", offset=1,
                         inventoryReference="current_user.alternativeNativeReadings.inventories[exact_source_page_offset].inventory")
        prompt = {"alternativeNativeReadings": {"inventories": [{"sourceKey": "S1", "physicalPage": 5, "inventory": inventory}]}}
        return reference, prompt, case

    def test_native_inventory_reference_resolves_complete_current_target(self):
        reference, prompt, case = self.native_inventory_reference_case()
        self.assertIs(prompt["alternativeNativeReadings"]["inventories"][0]["inventory"],
                      resolve_native_inventory_reference(reference, prompt, case))

    def test_native_inventory_reference_cannot_point_at_old_cursor(self):
        reference, prompt, case = self.native_inventory_reference_case()
        reference["offset"] = 0
        with self.assertRaisesRegex(ValueError, "current_target_not_unique"):
            resolve_native_inventory_reference(reference, prompt, case)

    def test_native_inventory_reference_cannot_drop_or_replace_current_descriptors(self):
        reference, prompt, case = self.native_inventory_reference_case()
        prompt["alternativeNativeReadings"]["inventories"][0]["inventory"]["observations"][0]["sourceSha256"] = "b" * 64
        with self.assertRaisesRegex(ValueError, "descriptor_identity_changed"):
            resolve_native_inventory_reference(reference, prompt, case)

    def test_cr_lf_only_line_boundaries_keep_literal_terminators(self):
        self.assertEqual(["A\r\n", "B\r", "C\n", "D\u2028E"], raw_lines("A\r\nB\rC\nD\u2028E"))

    def test_final_terminator_does_not_invent_empty_line(self):
        self.assertEqual(["A\n"], raw_lines("A\n"))
        self.assertEqual([], raw_lines(""))

    def test_utf16_window_does_not_confuse_unicode_scalars(self):
        self.assertEqual(5, utf16_length("A😀BC"))
        self.assertEqual("😀", utf16_slice("A😀BC", 1, 2))

    def test_split_surrogate_is_rejected(self):
        with self.assertRaises(UnicodeDecodeError):
            utf16_slice("A😀BC", 1, 1)

    def test_current_prompt_requires_one_current_user_evidence_payload(self):
        body = {"messages": [{"role": "user", "content": '{"evidence":[]}'}]}
        self.assertEqual({"evidence": []}, current_prompt(body))
        body["messages"].append(body["messages"][0])
        with self.assertRaises(ValueError):
            current_prompt(body)

    def test_current_prompt_supports_responses_input(self):
        self.assertEqual({"evidence": []}, current_prompt({"input": [{"role": "user", "content": '{"evidence":[]}'}]}))

    def test_unoffered_operation_is_never_fabricated(self):
        with self.assertRaisesRegex(ValueError, "not_currently_offered"):
            strict_call("read_source", {}, {"tools": []}, 1)

    def test_unoffered_field_is_rejected(self):
        body = {"tools": [{"function": {"name": "read_source", "parameters": {"properties": {}, "required": []}}}]}
        with self.assertRaisesRegex(ValueError, "not_offered"):
            strict_call("read_source", {"invented": 1}, body, 1)

    def test_strict_nullable_fields_and_unicode_arguments_preserved(self):
        body = {"tools": [{"function": {"name": "read_source", "parameters": {
            "properties": {"sourceKey": {}, "optional": {}}, "required": ["sourceKey", "optional"]}}}]}
        call = strict_call("read_source", {"sourceKey": "é😀"}, body, 7)
        self.assertEqual({"sourceKey": "é😀", "optional": None}, json.loads(call["arguments"]))

    def test_descriptor_must_be_currently_observed(self):
        with self.assertRaisesRegex(ValueError, "not_visible"):
            native_inventory({"evidence": []}, {"physicalPage": 1, "nativeBlockId": "unseen"})

    def test_second_window_can_use_current_raw_reference(self):
        prompt = {"evidence": [{"sourceKey": "S1", "nativeObservation": {
            "reference": {"blockId": "B1", "rawTextSha256": "a" * 64}}}]}
        key, descriptor = native_inventory(prompt, {"physicalPage": 1, "nativeBlockId": "B1"})
        self.assertEqual("S1", key)
        self.assertEqual("B1", descriptor["blockId"])

    def test_responses_call_envelope_keeps_opaque_reasoning_and_linked_id(self):
        result = response_envelope({"kind": "tool", "call": {"id": "C1", "name": "read_source", "arguments": "{}"}}, True, 4)
        self.assertEqual("opaque-fixture-reasoning-4", result["output"][0]["encrypted_content"])
        self.assertEqual("C1", result["output"][1]["call_id"])

    def test_chat_call_envelope_keeps_linked_id(self):
        result = response_envelope({"kind": "tool", "call": {"id": "C1", "name": "read_source", "arguments": "{}"}}, False, 4)
        self.assertEqual("C1", result["choices"][0]["message"]["tool_calls"][0]["id"])

    def test_chat_output_limit_contains_parseable_json_but_length_marker(self):
        text = '{"outcome":"answered","answerText":"literal","claims":[]}'
        result = response_envelope({"kind": "answer", "text": text, "outputLimit": True}, False, 4)
        self.assertEqual("length", result["choices"][0]["finish_reason"])
        self.assertEqual("answered", json.loads(result["choices"][0]["message"]["content"])["outcome"])

    def test_responses_output_limit_contains_text_but_incomplete_marker(self):
        result = response_envelope({"kind": "answer", "text": '{"outcome":"answered"}', "outputLimit": True}, True, 4)
        self.assertEqual("incomplete", result["status"])
        self.assertEqual("max_output_tokens", result["incomplete_details"]["reason"])
        self.assertEqual('{"outcome":"answered"}', result["output"][0]["content"][0]["text"])

    def test_terminal_cannot_be_generated_without_received_native_evidence(self):
        body = {"messages": [{"role": "user", "content": json.dumps({"evidence": [{"sourceKey": "S1", "content": "primary"}]})}]}
        with self.assertRaisesRegex(ValueError, "native_line_required"):
            scripted_reply(body, {"scenario": {"physicalPage": 1}}, 8, 9)

    def test_nondocumentary_projection_must_not_invent_zero_counts(self):
        verify_projection({"evidenceProjection": {"status": "not_applicable"}})
        with self.assertRaisesRegex(ValueError, "fabricated"):
            verify_projection({"evidenceProjection": {"status": "not_applicable", "canonicalProjection": {}}})

    def test_visible_projection_count_is_checked_against_actual_collection(self):
        payload = {"evidence": [], "evidenceProjection": {"status": "observed",
            "canonicalProjection": {"inputCount": 1, "projectedCount": 0, "omittedByPromptBudgetCount": 1},
            "currentCall": {"collection": "evidence", "visibleCount": 1, "fromCanonicalProjectionCount": 0,
                "outsideCanonicalProjectionCount": 1, "canonicalProjectedNotSelectedCount": 0,
                "truncatedContentCount": 0, "supportingWindowsOmittedCount": 0}, "instruction": "Not corpus absence."}}
        with self.assertRaisesRegex(ValueError, "visible_collection"):
            verify_projection(payload)

    def test_probe_parser_preserves_oem_log_but_reads_ascii_json_subrecord(self):
        with tempfile.TemporaryDirectory() as folder:
            path = pathlib.Path(folder) / "stdout.log"
            raw = b'OEM prefix \x82 operation=llm.call outcome=success measurements={"call_id":"C1"}\r\n'
            path.write_bytes(raw)
            self.assertEqual([{"call_id": "C1"}], read_call_probes(path))
            self.assertEqual(raw, path.read_bytes())

    def test_probe_parser_does_not_replace_invalid_json_bytes(self):
        with tempfile.TemporaryDirectory() as folder:
            path = pathlib.Path(folder) / "stdout.log"
            path.write_bytes(b'operation=llm.call measurements={"call_id":"\x82"}\n')
            with self.assertRaises(UnicodeDecodeError):
                read_call_probes(path)


if __name__ == "__main__":
    unittest.main()
