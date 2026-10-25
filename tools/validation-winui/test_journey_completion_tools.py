"""Offline fixtures only: no listener, subprocess, model, UI or database."""
import copy
import json
import unittest

from fixture_http import advanced_answered_content, advanced_fixture_content
from storage_proxy import consume_message_get_delay, message_session_id


SESSION = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
OTHER_SESSION = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"
EVIDENCE_ID = "advanced-evidence-0123456789abcdef0123456789abcdef"


class MessageGetDelayTests(unittest.TestCase):
    def control(self, **overrides):
        result = {"delayGetMessagesSessionId": SESSION,
                  "delayGetMessagesRemaining": 1, "delayGetMessagesSeconds": 12}
        result.update(overrides)
        return result

    def path(self, session=SESSION):
        return f"/chat/sessions/{session}/messages?userId=synthetic&limit=1000"

    def test_only_matching_get_consumes_a_single_bounded_delay(self):
        control = self.control()
        self.assertEqual(12, consume_message_get_delay(control, "GET", self.path()))
        self.assertEqual(0, consume_message_get_delay(control, "GET", self.path()))
        self.assertEqual(0, control["delayGetMessagesRemaining"])

    def test_other_sessions_routes_and_mutations_do_not_consume(self):
        control = self.control()
        for method, path in [("GET", self.path(OTHER_SESSION)), ("POST", self.path()),
                             ("DELETE", f"/chat/sessions/{SESSION}"), ("GET", "/ready"),
                             ("GET", "/chat/sessions?userId=synthetic")]:
            with self.subTest(method=method, path=path):
                self.assertEqual(0, consume_message_get_delay(control, method, path))
                self.assertEqual(1, control["delayGetMessagesRemaining"])

    def test_cdc_alias_requires_one_explicit_uuid(self):
        control = self.control()
        invalid = ["/chat/messages", f"/chat/messages?sessionId={SESSION}&sessionId={OTHER_SESSION}",
                   "/chat/messages?sessionId=not-a-uuid", "/chat/sessions/not-a-uuid/messages"]
        for path in invalid:
            with self.subTest(path=path):
                self.assertIsNone(message_session_id("GET", path))
                self.assertEqual(0, consume_message_get_delay(control, "GET", path))
        self.assertEqual(12, consume_message_get_delay(control, "GET", f"/chat/messages?sessionId={SESSION}"))

    def test_missing_disabled_or_invalid_controls_never_inject(self):
        cases = [{}, self.control(delayGetMessagesSessionId=""),
                 self.control(delayGetMessagesSessionId="invalid"),
                 self.control(delayGetMessagesRemaining=0), self.control(delayGetMessagesSeconds=0),
                 self.control(delayGetMessagesSeconds="invalid")]
        for control in cases:
            with self.subTest(control=control):
                before = copy.deepcopy(control)
                self.assertEqual(0, consume_message_get_delay(control, "GET", self.path()))
                self.assertEqual(before, control)

    def test_duration_and_counter_are_bounded(self):
        control = self.control(delayGetMessagesSeconds=1000, delayGetMessagesRemaining=100_000)
        self.assertEqual(30, consume_message_get_delay(control, "GET", self.path()))
        self.assertEqual(9999, control["delayGetMessagesRemaining"])
        control = self.control(delayGetMessagesSeconds=-10)
        self.assertEqual(0, consume_message_get_delay(control, "GET", self.path()))

    def test_uuid_case_is_normalized_without_changing_unrelated_controls(self):
        control = self.control(delayGetMessagesSessionId=SESSION.upper(), failGetMessagesRemaining=0,
                               failUserMessagesRemaining=3, delayUserMessagesSeconds=8)
        self.assertEqual(12, consume_message_get_delay(control, "GET", self.path(SESSION.upper())))
        self.assertEqual(3, control["failUserMessagesRemaining"])
        self.assertEqual(8, control["delayUserMessagesSeconds"])


class AdvancedAnsweredFixtureTests(unittest.TestCase):
    def writer(self):
        return {"request": "Contrôle synthétique ORION", "language": "fr",
                "load": {"answerUnitCount": 1, "structuredLayout": False}, "claimCoordinates": [],
                "evidence": [{"evidenceId": EVIDENCE_ID,
                              "content": "Code de validation ORION : 4826. Ce code est fictif."},
                             {"evidenceId": "other-id", "content": "Son code ORION est 9999."}]}

    def body(self, writer=None):
        return {"messages": [{"role": "system", "content": "You are the SAAIA Writer."},
                             {"role": "user", "content": json.dumps(self.writer() if writer is None else writer)}]}

    def test_answer_cites_the_real_supplied_opaque_id_and_one_claim_marker(self):
        result = json.loads(advanced_answered_content(self.body()))
        self.assertEqual("answered", result["outcome"])
        self.assertEqual(1, result["answerText"].count("[C1]"))
        self.assertNotIn(EVIDENCE_ID, result["answerText"])
        self.assertEqual(1, len(result["claims"]))
        self.assertEqual([EVIDENCE_ID], result["claims"][0]["evidenceIds"])
        self.assertEqual("C1", result["claims"][0]["claimId"])
        self.assertNotIn("E1", result["claims"][0]["evidenceIds"])

    def test_missing_homonym_or_truncated_evidence_never_fabricates_a_citation(self):
        for content in ["", "Archive distincte : code ORION 9999.", "Code de validation ORION :"]:
            writer = self.writer()
            writer["evidence"][0]["content"] = content
            with self.subTest(content=content), self.assertRaises(ValueError):
                advanced_answered_content(self.body(writer))

    def test_duplicate_matching_evidence_is_not_chosen_arbitrarily(self):
        writer = self.writer()
        writer["evidence"].append({"evidenceId": "another-version", "content": writer["evidence"][0]["content"]})
        with self.assertRaises(ValueError):
            advanced_answered_content(self.body(writer))

    def test_missing_evidence_identity_is_rejected(self):
        for evidence_id in [None, "", " ", "x" * 129]:
            writer = self.writer()
            writer["evidence"][0]["evidenceId"] = evidence_id
            with self.subTest(evidence_id=evidence_id), self.assertRaises(ValueError):
                advanced_answered_content(self.body(writer))

    def test_unsupported_count_or_structured_coordinates_fail_closed(self):
        cases = [{"answerUnitCount": 2}, {"answerUnitCount": True}, {"structuredLayout": True}]
        for patch in cases:
            writer = self.writer()
            writer["load"].update(patch)
            with self.subTest(patch=patch), self.assertRaises(ValueError):
                advanced_answered_content(self.body(writer))
        writer = self.writer()
        writer["claimCoordinates"] = [{"claimId": "R1C1"}]
        with self.assertRaises(ValueError):
            advanced_answered_content(self.body(writer))

    def test_absent_or_multiple_writer_inputs_are_rejected(self):
        with self.assertRaises(ValueError):
            advanced_answered_content({"messages": []})
        body = self.body()
        body["messages"].append(copy.deepcopy(body["messages"][-1]))
        with self.assertRaises(ValueError):
            advanced_answered_content(body)

    def test_planner_uses_same_query_in_old_and_new_modes(self):
        body = {"messages": [{"role": "system", "content": "You are the research planner for SAAIA."}]}
        for mode in ["advanced", "advanced_answered"]:
            with self.subTest(mode=mode):
                self.assertEqual({"queries": [{"query": "ORION", "topK": 5}]},
                                 json.loads(advanced_fixture_content(body, mode)))

    def test_original_advanced_mode_remains_explicit_insufficiency_without_sources(self):
        result = json.loads(advanced_fixture_content(self.body(), "advanced"))
        self.assertEqual("insufficient_documentation", result["outcome"])
        self.assertEqual([], result["claims"])
        self.assertIn("résultat contrôlé", result["answerText"])


if __name__ == "__main__":
    unittest.main()
