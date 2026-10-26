from __future__ import annotations

import asyncio
from copy import deepcopy
import unittest

from saaia_docling.response_text_repair import repair_response_texts, validate_text_region, validate_physical_page_size
from saaia_docling.text_repair_options import TextRepairOptions
from test_text_repair import ORIGINAL, RECOVERED, item, observation


def response(count=1):
    items = [item() for _ in range(count)]
    for index, current in enumerate(items):
        current["self_ref"] = f"#/texts/{index}"
    return {"status": "success", "document": {"json_content": {
        "pages": {"2": {"page_no": 2, "size": {"width": 600, "height": 800}}}, "texts": items,
    }}}


class FakeEngine:
    def __init__(self, *, error=None, secondary_text=RECOVERED, advance=None):
        self.calls = []
        self.error = error
        self.secondary_text = secondary_text
        self.advance = advance

    async def recognize(self, pdf_path, document, source, options, timeout_seconds):
        self.calls.append((source["self_ref"], timeout_seconds))
        if self.advance:
            self.advance()
        if self.error:
            raise self.error
        return observation(), observation(self.secondary_text, engine="tesseract")


class RegionalTextResponseTests(unittest.IsolatedAsyncioTestCase):
    async def test_wrapper_applies_only_agreed_text_preserves_orig_and_adds_content_free_counts(self):
        payload, engine = response(), FakeEngine()
        summaries = await repair_response_texts("unopened.pdf", payload, engine, options=TextRepairOptions(enabled=True))
        current = payload["document"]["json_content"]["texts"][0]
        self.assertEqual(RECOVERED, current["text"])
        self.assertEqual(ORIGINAL, current["orig"])
        self.assertEqual(1, payload["saaia_text_repair_summary"]["applied_count"])
        self.assertEqual("applied", summaries[0]["status"])
        self.assertNotIn("original_text", summaries[0])
        self.assertEqual(1, len(engine.calls))

    async def test_disagreement_keeps_primary_and_records_both_actual_observations(self):
        payload, engine = response(), FakeEngine(secondary_text=RECOVERED.replace("-20,5", "20,5"))
        summaries = await repair_response_texts("unopened.pdf", payload, engine, options=TextRepairOptions(enabled=True))
        current = payload["document"]["json_content"]["texts"][0]
        self.assertEqual(ORIGINAL, current["text"])
        self.assertEqual("independent_readings_disagree", summaries[0]["reason"])
        self.assertEqual(2, len(current["saaia_text_repair_attempt"]["observations"]))

    async def test_maximum_four_regions_is_enforced_without_touching_later_primary_texts(self):
        payload, engine = response(6), FakeEngine()
        summaries = await repair_response_texts("unopened.pdf", payload, engine, options=TextRepairOptions(enabled=True))
        self.assertEqual(4, len(engine.calls))
        self.assertEqual(6, len(summaries))
        self.assertEqual(4, payload["saaia_text_repair_summary"]["applied_count"])
        self.assertTrue(all(current["text"] == ORIGINAL for current in payload["document"]["json_content"]["texts"][4:]))

    async def test_document_budget_is_shared_and_late_reading_is_not_applied(self):
        now = [0.0]
        engine = FakeEngine(advance=lambda: now.__setitem__(0, now[0] + 60))
        payload = response(3)
        await repair_response_texts("unopened.pdf", payload, engine, options=TextRepairOptions(enabled=True), clock=lambda: now[0])
        self.assertEqual(1, len(engine.calls))
        self.assertTrue(all(current["text"] == ORIGINAL for current in payload["document"]["json_content"]["texts"]))
        self.assertEqual(0, payload["saaia_text_repair_summary"]["applied_count"])
        self.assertEqual(2, payload["saaia_text_repairs"][-1]["uninspected_item_count"])

    async def test_invalid_geometry_and_oversized_page_do_not_launch_engines(self):
        for width in (5_000, float("nan"), True):
            with self.subTest(width=width):
                payload, engine = response(), FakeEngine()
                payload["document"]["json_content"]["pages"]["2"]["size"]["width"] = width
                await repair_response_texts("unopened.pdf", payload, engine, options=TextRepairOptions(enabled=True))
                self.assertEqual([], engine.calls)
                self.assertEqual(ORIGINAL, payload["document"]["json_content"]["texts"][0]["text"])

    async def test_engine_timeout_or_error_preserves_original_and_sanitizes_diagnostics(self):
        for error in (TimeoutError("PRIVATE TEXT"), RuntimeError("PRIVATE /document/path token=secret")):
            with self.subTest(error=type(error).__name__):
                payload, engine = response(), FakeEngine(error=error)
                summaries = await repair_response_texts("unopened.pdf", payload, engine, options=TextRepairOptions(enabled=True))
                self.assertEqual(ORIGINAL, payload["document"]["json_content"]["texts"][0]["text"])
                self.assertNotIn("PRIVATE", str(summaries))
                self.assertNotIn("secret", str(summaries))

    async def test_caller_cancellation_propagates_and_is_not_relabelled_as_success(self):
        payload = response()
        with self.assertRaises(asyncio.CancelledError):
            await repair_response_texts("unopened.pdf", payload, FakeEngine(error=asyncio.CancelledError()), options=TextRepairOptions(enabled=True))
        self.assertEqual(ORIGINAL, payload["document"]["json_content"]["texts"][0]["text"])

    async def test_disabled_feature_has_no_mutation_or_engine_call(self):
        payload, engine = response(), FakeEngine()
        before = deepcopy(payload)
        self.assertEqual([], await repair_response_texts("unopened.pdf", payload, engine, options=TextRepairOptions()))
        self.assertEqual(before, payload)
        self.assertEqual([], engine.calls)

    async def test_formula_and_code_labels_are_not_sent_to_region_ocr(self):
        for label in ("formula", "code"):
            payload, engine = response(), FakeEngine()
            payload["document"]["json_content"]["texts"][0]["label"] = label
            await repair_response_texts("unopened.pdf", payload, engine, options=TextRepairOptions(enabled=True))
            self.assertEqual([], engine.calls)

    async def test_bottom_left_box_above_actual_page_does_not_launch_engines(self):
        payload, engine = response(), FakeEngine()
        source = payload["document"]["json_content"]["texts"][0]
        source["prov"][0]["bbox"].update(t=1000, b=900, coord_origin="BOTTOMLEFT")
        await repair_response_texts("unopened.pdf", payload, engine, options=TextRepairOptions(enabled=True))
        self.assertEqual([], engine.calls)
        self.assertEqual(ORIGINAL, source["text"])


class RegionalTextOptionsTests(unittest.TestCase):
    def test_defaults_are_opt_in_and_attest_real_fixed_engine_contract(self):
        options = TextRepairOptions.from_environment({})
        self.assertFalse(options.enabled)
        self.assertEqual("eng", options.attestation()["secondary_language"])
        self.assertEqual(6, options.attestation()["secondary_page_segmentation_mode"])
        self.assertFalse(options.attestation()["universal_multilingual_validation"])

    def test_environment_cannot_weaken_the_approved_safety_bounds(self):
        for field, value in (("MAX_REGIONS", "5"), ("TOTAL_TIMEOUT_SECONDS", "61"),
                             ("REGION_TIMEOUT_SECONDS", "16"), ("RENDER_SCALE", "5"),
                             ("PADDING_POINTS", "3"), ("MINIMUM_RAPID_SCORE", "0.97"),
                             ("MINIMUM_SECONDARY_SCORE", "0.94"), ("MAX_RENDERED_PIXELS", "20000001"),
                             ("TOTAL_TIMEOUT_SECONDS", "nan"), ("ENABLED", "maybe")):
            with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                TextRepairOptions.from_environment({"SAAIA_DOCLING_TEXT_REPAIR_" + field: value})

    def test_bounds_use_actual_page_height_and_refuse_box_outside_page(self):
        payload = response()
        document = payload["document"]["json_content"]
        source = document["texts"][0]
        self.assertEqual(800, validate_text_region(document, source, TextRepairOptions()))
        source["prov"][0]["bbox"]["r"] = 601
        with self.assertRaises(ValueError):
            validate_text_region(document, source, TextRepairOptions())

    def test_physical_pdf_width_and_height_must_match_declared_geometry_before_render(self):
        options = TextRepairOptions()
        validate_physical_page_size(600, 800, 600, 800, options)
        for width, height in ((300, 800), (600, 400), (-1, 800), (600, float("nan"))):
            with self.subTest(width=width, height=height), self.assertRaises(ValueError):
                validate_physical_page_size(width, height, 600, 800, options)


if __name__ == "__main__":
    unittest.main()
