from __future__ import annotations

from copy import deepcopy
import unittest

from saaia_docling.table_repair import OcrLine
from saaia_docling.text_repair import (
    TEXT_REPAIR_SCHEMA_VERSION,
    TextOcrObservation,
    assess_text_damage,
    repair_text_item,
)


ORIGINAL = "Cr     p x z      maximum allowed value is -20,5 °C."
RECOVERED = "Critical pump maximum allowed value is -20,5 °C."


def item(text: str = ORIGINAL) -> dict:
    return {
        "self_ref": "#/texts/8",
        "label": "text",
        "text": text,
        "orig": text,
        "prov": [{"page_no": 2, "bbox": {
            "l": 20, "t": 100, "r": 400, "b": 140,
            "coord_origin": "TOPLEFT",
        }}],
    }


def observation(
    text: str = RECOVERED, *, engine: str = "rapidocr", score: float = 0.995,
) -> TextOcrObservation:
    return TextOcrObservation(
        engine=engine, engine_version="observed-test-version",
        model_id="test-model" if engine == "rapidocr" else "eng:psm6",
        lines=(OcrLine(text, score, ((20, 100), (400, 100), (400, 140), (20, 140))),),
        duration_ms=12,
        model_sha256="a" * 64,
    )


class RegionalTextRepairTests(unittest.TestCase):
    def test_structural_holes_and_fragmented_prefix_are_candidates(self) -> None:
        damage = assess_text_damage(ORIGINAL)
        self.assertTrue(damage["candidate"])
        self.assertGreaterEqual(damage["isolated_word_count"], 3)

    def test_agreement_repairs_only_text_and_keeps_original_and_pixel_evidence(self) -> None:
        source = item()
        before = deepcopy(source)
        result = repair_text_item(source, observation(), observation(engine="tesseract"))
        self.assertTrue(result.applied)
        self.assertEqual(RECOVERED, result.item["text"])
        self.assertEqual(ORIGINAL, result.item["orig"])
        self.assertEqual(before, source)
        proof = result.item["saaia_text_repair"]
        self.assertEqual(TEXT_REPAIR_SCHEMA_VERSION, proof["schema_version"])
        self.assertEqual(ORIGINAL, proof["original_text"])
        self.assertEqual(source["prov"], proof["source_provenance"])
        self.assertEqual({"rapidocr", "tesseract"}, {x["engine"] for x in proof["observations"]})
        self.assertTrue(all(x["lines"][0]["polygon"] for x in proof["observations"]))

    def test_nfc_and_whitespace_are_the_only_agreement_normalizations(self) -> None:
        source = item("Cr     p x z      température minimale autorisée -20,5 °C.")
        recovered = "Critère principal température minimale autorisée -20,5 °C."
        secondary = "Crite\u0300re principal\n tempe\u0301rature minimale autorisée -20,5 °C."
        result = repair_text_item(source, observation(recovered), observation(secondary, engine="tesseract"))
        self.assertTrue(result.applied)
        self.assertEqual(recovered, result.item["text"])

    def test_healthy_prose_short_labels_formulas_and_codes_are_not_candidates(self) -> None:
        for text in ("The safe operating range is -20,5 °C.", "Security Fan", "F = m a",
                     "A     B C D     X-42", "A B C are valid column labels."):
            with self.subTest(text=text):
                self.assertFalse(assess_text_damage(text)["candidate"])

    def test_non_candidate_is_never_replaced_even_when_engines_agree(self) -> None:
        source = item("Security Fan")
        result = repair_text_item(source, observation("Security Plan"), observation("Security Plan", engine="tesseract"))
        self.assertFalse(result.applied)
        self.assertEqual(source["text"], result.item["text"])

    def test_any_low_confidence_nonfinite_or_empty_observation_is_rejected(self) -> None:
        for rapid_score, secondary_score in ((0.979, 0.99), (0.99, 0.949), (float("nan"), 0.99),
                                             (0.99, float("inf")), (1.1, 0.99), (-0.1, 0.99)):
            with self.subTest(rapid=rapid_score, secondary=secondary_score):
                result = repair_text_item(item(), observation(score=rapid_score), observation(engine="tesseract", score=secondary_score))
                self.assertFalse(result.applied)
                self.assertEqual(ORIGINAL, result.item["text"])
        for empty in ("", "   "):
            self.assertFalse(repair_text_item(item(), observation(empty), observation(empty, engine="tesseract")).applied)

    def test_two_readings_from_same_engine_do_not_count_as_independent_agreement(self) -> None:
        self.assertFalse(repair_text_item(item(), observation(), observation()).applied)

    def test_disagreement_in_values_punctuation_case_or_accents_is_rejected(self) -> None:
        for secondary in (RECOVERED.replace("-20,5", "20,5"), RECOVERED.replace("-20,5", "-20.5"),
                          RECOVERED.replace("Critical", "critical"), RECOVERED.replace("pump", "púmp"),
                          RECOVERED.replace("°C.", "°C")):
            with self.subTest(secondary=secondary):
                self.assertFalse(repair_text_item(item(), observation(), observation(secondary, engine="tesseract")).applied)

    def test_agreeing_engines_cannot_drop_source_values_signs_or_identifiers(self) -> None:
        for old_tail, new_tail in (("value -20,5 °C.", "value 20,5 °C."),
                                   ("value -20,5 °C.", "value -20.5 °C."),
                                   ("value < 5 mm.", "value 5 mm."),
                                   ("reference AB-42 is mandatory.", "reference AB-24 is mandatory."),
                                   ("capacity 1/2 litre.", "capacity 12 litre.")):
            with self.subTest(old_tail=old_tail, new_tail=new_tail):
                original = "Cr     p x z      " + old_tail
                candidate = "Critical pump " + new_tail
                result = repair_text_item(item(original), observation(candidate), observation(candidate, engine="tesseract"))
                self.assertFalse(result.applied)
                self.assertEqual(original, result.item["text"])

    def test_agreement_cannot_drop_short_negation_or_reorder_reliable_words(self) -> None:
        for original, candidate in (
            ("No     p x z      pressure must be applied.", "Correct pressure must be applied."),
            ("Ne     p x z      jamais appliquer la pression.", "Correctement jamais appliquer la pression."),
            ("Cr     p x z      first release pressure then disconnect power.", "Critical pump first disconnect power then release pressure."),
        ):
            with self.subTest(original=original):
                self.assertFalse(repair_text_item(item(original), observation(candidate), observation(candidate, engine="tesseract")).applied)

    def test_invalid_geometry_or_missing_engine_version_is_inconclusive(self) -> None:
        invalid = item()
        invalid["prov"][0]["bbox"]["r"] = 10
        self.assertFalse(repair_text_item(invalid, observation(), observation(engine="tesseract")).applied)
        missing = TextOcrObservation("tesseract", "", "eng:psm6", observation().lines)
        self.assertFalse(repair_text_item(item(), observation(), missing).applied)

    def test_line_boxes_must_belong_to_the_source_region(self) -> None:
        outside = TextOcrObservation("rapidocr", "observed", "model", (
            OcrLine(RECOVERED, 0.999, ((500, 500), (900, 500), (900, 540), (500, 540))),
        ))
        self.assertFalse(repair_text_item(item(), outside, observation(engine="tesseract")).applied)

    def test_bottom_left_source_requires_page_height_and_uses_top_left_observations(self) -> None:
        source = item()
        source["prov"][0]["bbox"].update(t=700, b=660, coord_origin="BOTTOMLEFT")
        self.assertFalse(repair_text_item(source, observation(), observation(engine="tesseract")).applied)
        self.assertTrue(repair_text_item(source, observation(), observation(engine="tesseract"), page_height=800).applied)

    def test_all_short_words_outside_damaged_prefix_are_protected(self) -> None:
        for short in ("a", "I", "de", "à", "no", "is", "zu"):
            with self.subTest(short=short):
                source = item("Cr     p x z      " + short + " maximum allowed value remains unchanged.")
                candidate = "Critical pump maximum allowed value remains unchanged."
                self.assertFalse(repair_text_item(source, observation(candidate), observation(candidate, engine="tesseract")).applied)

    def test_numbers_operators_and_uppercase_identifiers_in_damaged_prefix_are_protected(self) -> None:
        for marker in ("-5", "<", "AB", "1/2", "A_2"):
            with self.subTest(marker=marker):
                source = item(marker + " Cr     p x z      maximum allowed value remains unchanged.")
                candidate = "Critical pump maximum allowed value remains unchanged."
                self.assertFalse(repair_text_item(source, observation(candidate), observation(candidate, engine="tesseract")).applied)

    def test_agreement_cannot_insert_a_negation_into_the_already_reliable_suffix(self) -> None:
        candidate = RECOVERED.replace("maximum allowed", "maximum not allowed")
        result = repair_text_item(item(), observation(candidate), observation(candidate, engine="tesseract"))
        self.assertFalse(result.applied)
        self.assertEqual(ORIGINAL, result.item["text"])

    def test_isolated_n_is_a_fragment_but_explicit_n_apostrophe_remains_protected(self) -> None:
        source = item("Qr  c n         a must remain visible in every situation.")
        candidate = "All operational limits must remain visible in every situation."
        result = repair_text_item(source, observation(candidate), observation(candidate, engine="tesseract"))
        self.assertTrue(result.applied)
        for apostrophe in ("'", "’"):
            source = item("n" + apostrophe + "  c n         a reste obligatoire dans chaque situation.")
            candidate = "Le contrôle reste obligatoire dans chaque situation."
            self.assertFalse(repair_text_item(source, observation(candidate), observation(candidate, engine="tesseract")).applied)

    def test_lowercase_two_letter_prefix_words_cannot_be_discarded_as_fragments(self) -> None:
        for word in ("if", "or", "de", "un"):
            with self.subTest(word=word):
                source = item(word + "     p x z      maximum allowed value is -20,5 °C.")
                self.assertFalse(repair_text_item(source, observation(), observation(engine="tesseract")).applied)


if __name__ == "__main__":
    unittest.main()
