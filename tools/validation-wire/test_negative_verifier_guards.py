"""Regressions for zero-POST campaigns that must never pass vacuously."""
import hashlib
import json
import pathlib
import tempfile
import unittest
from unittest.mock import patch

from verify_negative_wire import verify


class NegativeVerifierGuards(unittest.TestCase):
    def check_rejected(self, cases, protocol, reason, *, declared_hash=None):
        with tempfile.TemporaryDirectory() as temporary:
            folder = pathlib.Path(temporary)
            runtime = folder / "runtime"
            runtime.mkdir()
            source = folder / "source.json"
            source.write_bytes(b'{"documents":[]}')
            digest = hashlib.sha256(source.read_bytes()).hexdigest()
            prepared = folder / "cases.json"
            prepared.write_text(json.dumps({"sourceCaptureSha256": declared_hash or digest,
                                            "cases": cases}), encoding="utf-8")
            (folder / "plan.json").write_text(json.dumps({"protocol": protocol}), encoding="utf-8")
            with patch("verify_negative_wire.SOURCE27_SQL_SHA256", digest):
                with self.assertRaisesRegex(ValueError, reason):
                    verify(runtime, prepared, source)

    def test_zero_cases_are_not_successful_source_refusal(self):
        self.check_rejected([], "chat-completions", "negative_cases_empty_or_invalid")

    def test_unknown_protocol_is_not_accepted_without_posts(self):
        self.check_rejected([{"caseId": "one"}], "invented", "negative_protocol_not_supported")

    def test_prepared_cases_must_bind_actual_capture_hash(self):
        self.check_rejected([{"caseId": "one"}], "responses", "negative_cases_source_capture_binding_differs",
                            declared_hash="0" * 64)

    def test_duplicate_cases_cannot_multiply_a_zero_post_success(self):
        self.check_rejected([{"caseId": "one"}, {"caseId": "one"}], "responses", "negative_case_ids_not_unique")


if __name__ == "__main__":
    unittest.main()
