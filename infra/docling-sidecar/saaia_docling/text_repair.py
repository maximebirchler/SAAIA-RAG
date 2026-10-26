from __future__ import annotations

from copy import deepcopy
from dataclasses import dataclass
from hashlib import sha256
from math import isfinite
import re
from typing import Any
import unicodedata

from .table_repair import OcrLine


TEXT_REPAIR_SCHEMA_VERSION = "saaia_text_region_repair_v1"

# Linguistic safety guard, not a business vocabulary or multilingual proof.
# The whole reliable suffix is protected independently of this finite set.
_PREFIX_NEGATIONS = frozenset({
    "no", "not", "nor", "never", "without", "cannot", "can't", "don't",
    "ne", "non", "ni", "pas", "sans", "aucun", "aucune", "jamais",
    "nicht", "nie", "nein", "kein", "keine", "keinen", "ohne",
})
_WORDS = re.compile(r"[^\W\d_]+", re.UNICODE)
_TOKENS = re.compile(r"[^\W_]+(?:['’][^\W_]+)*|[^\s]", re.UNICODE)
_HORIZONTAL_HOLE = re.compile(r"[^\S\r\n]{3,}")


@dataclass(frozen=True)
class TextOcrObservation:
    engine: str
    engine_version: str
    model_id: str
    lines: tuple[OcrLine, ...]
    duration_ms: int = 0
    model_sha256: str | None = None
    model_artifacts: dict[str, str] | None = None
    runtime_settings: dict[str, Any] | None = None


@dataclass(frozen=True)
class TextRepairResult:
    item: dict[str, Any]
    applied: bool
    reason: str


def assess_text_damage(text: str) -> dict[str, Any]:
    """Identify only a fragmented prefix; ordinary short words elsewhere are not noise."""
    no_damage = {"candidate": False, "reason": "no_structural_damage", "isolated_word_count": 0}
    if not isinstance(text, str) or not 20 <= len(text) <= 8_000:
        return no_damage
    words = list(_WORDS.finditer(text))
    prefix = []
    for word in words:
        if prefix and sum(len(value.group()) == 1 and value.group().islower() for value in prefix) >= 3:
            if _HORIZONTAL_HOLE.search(text[prefix[-1].end():word.start()]):
                break
        if len(word.group()) > 2:
            break
        prefix.append(word)
    # A useful lexical suffix must remain. All-uppercase/formula/code fragments
    # are deliberately ineligible, even if two OCR engines propose an expansion.
    isolated = [word for word in prefix if len(word.group()) == 1 and word.group().islower()]
    if len(prefix) < 4 or len(isolated) < 3 or len(prefix) == len(words):
        return no_damage
    end = prefix[-1].end()
    suffix = text[end:]
    if len([word for word in words[len(prefix):] if len(word.group()) >= 3]) < 3:
        return no_damage
    if not _HORIZONTAL_HOLE.search(text[:words[len(prefix)].start()]):
        return no_damage
    return {
        "candidate": True,
        "reason": "fragmented_prefix_with_typographic_hole",
        "isolated_word_count": len(isolated),
        "fragmented_prefix_end": end,
        "reliable_suffix_start": end,
        "reliable_suffix_token_count": len(_tokens(suffix)),
    }


def repair_text_item(
    item: dict[str, Any],
    rapid: TextOcrObservation,
    secondary: TextOcrObservation,
    *,
    minimum_rapid_score: float = 0.98,
    minimum_secondary_score: float = 0.95,
    page_height: float | None = None,
) -> TextRepairResult:
    """Accept a pixel rereading only under independent, strict, source-retaining guards."""
    result = deepcopy(item)
    original = item.get("text")
    damage = assess_text_damage(original)
    if not damage["candidate"]:
        return TextRepairResult(result, False, "no_structural_damage")
    bounds = source_bounds(item, page_height=page_height)
    if bounds is None:
        return TextRepairResult(result, False, "source_geometry_invalid")
    if rapid.engine != "rapidocr" or secondary.engine != "tesseract":
        return TextRepairResult(result, False, "independent_engines_required")
    if secondary.model_id != "eng:psm6":
        return TextRepairResult(result, False, "secondary_engine_mode_not_qualified")
    if not _valid_threshold(minimum_rapid_score) or not _valid_threshold(minimum_secondary_score):
        raise ValueError("OCR confidence thresholds must be finite and between zero and one.")
    for observation, minimum in ((rapid, minimum_rapid_score), (secondary, minimum_secondary_score)):
        if not observation.engine_version or not observation.model_id:
            return TextRepairResult(result, False, "engine_identity_unavailable")
        if not observation.lines or not all(_valid_line(line, minimum, bounds) for line in observation.lines):
            return TextRepairResult(result, False, "observation_confidence_or_geometry_invalid")
    rapid_text = _normalized(" ".join(line.text for line in rapid.lines))
    secondary_text = _normalized(" ".join(line.text for line in secondary.lines))
    if rapid_text != secondary_text:
        return TextRepairResult(result, False, "independent_readings_disagree")
    if not rapid_text or len(rapid_text) > 8_000:
        return TextRepairResult(result, False, "replacement_size_invalid")
    replacement_tokens = _tokens(rapid_text)
    prefix_end = damage["fragmented_prefix_end"]
    suffix_tokens = _tokens(original[prefix_end:])
    # The already readable suffix is exact, contiguous and terminal. Agreement
    # cannot insert a new negation/quantity into an otherwise reliable passage.
    if not suffix_tokens or replacement_tokens[-len(suffix_tokens):] != suffix_tokens:
        return TextRepairResult(result, False, "reliable_source_suffix_not_preserved")
    prefix_tokens = _tokens(original[:prefix_end])
    protected_prefix = [token for index, token in enumerate(prefix_tokens)
        if _protected_prefix_token(token) or (token.casefold() == "n" and index + 1 < len(prefix_tokens)
                                              and prefix_tokens[index + 1] in {"'", "’"})]
    if not _is_subsequence(protected_prefix, replacement_tokens[:-len(suffix_tokens)]):
        return TextRepairResult(result, False, "critical_source_prefix_not_preserved")
    if _normalized(original) == rapid_text:
        return TextRepairResult(result, False, "no_text_change")
    result["text"] = rapid_text
    # item.orig and the original provenance are never overwritten.
    result["saaia_text_repair"] = {
        "schema_version": TEXT_REPAIR_SCHEMA_VERSION,
        "status": "applied",
        "reason": "independent_high_confidence_source_retaining_agreement",
        "source_ref": item.get("self_ref"),
        "original_text": original,
        "original_text_sha256": sha256(original.encode("utf-8")).hexdigest(),
        "replacement_text": rapid_text,
        "replacement_text_sha256": sha256(rapid_text.encode("utf-8")).hexdigest(),
        "source_provenance": deepcopy(item.get("prov")),
        "damage": damage,
        "agreement_normalization": "unicode_nfc_and_whitespace_only",
        "source_preservation": {
            "reliable_suffix_tokens_in_order": True,
            "reliable_suffix_exact_contiguous_terminal": True,
            "critical_prefix_tokens_in_order": True,
            "lowercase_two_letter_prefix_words_preserved": True,
            "linguistic_prefix_negation_guard": "fr_en_de_finite_v1",
            "universal_multilingual_validation": False,
        },
        "observations": [_observation_proof(rapid), _observation_proof(secondary)],
    }
    return TextRepairResult(result, True, "independent_high_confidence_source_retaining_agreement")


def source_bounds(
    item: dict[str, Any], *, page_height: float | None = None,
) -> tuple[float, float, float, float] | None:
    """Return a TOPLEFT source box; BOTTOMLEFT requires the actual page height."""
    provenance = item.get("prov")
    if not isinstance(item.get("self_ref"), str) or re.fullmatch(r"#/texts/\d+", item["self_ref"]) is None:
        return None
    if not isinstance(provenance, list) or len(provenance) != 1 or not isinstance(provenance[0], dict):
        return None
    region = provenance[0]
    if type(region.get("page_no")) is not int or region["page_no"] < 1:
        return None
    bbox = region.get("bbox")
    if not isinstance(bbox, dict) or bbox.get("coord_origin", "TOPLEFT") not in {"TOPLEFT", "BOTTOMLEFT"}:
        return None
    try:
        if any(isinstance(bbox[key], bool) for key in ("l", "t", "r", "b")):
            return None
        left, top, right, bottom = (float(bbox[key]) for key in ("l", "t", "r", "b"))
    except (ValueError, KeyError, TypeError):
        return None
    if not all(isfinite(value) and value >= 0 for value in (left, top, right, bottom)) or right <= left:
        return None
    if bbox.get("coord_origin", "TOPLEFT") == "BOTTOMLEFT":
        if isinstance(page_height, bool) or not isinstance(page_height, (int, float)) or not isfinite(page_height) or page_height <= 0:
            return None
        top, bottom = page_height - top, page_height - bottom
    if top < 0 or bottom <= top or (page_height is not None and bottom > page_height):
        return None
    return left, top, right, bottom


def _normalized(text: str) -> str:
    return " ".join(unicodedata.normalize("NFC", text).split())


def _tokens(text: str) -> list[str]:
    return _TOKENS.findall(unicodedata.normalize("NFC", text))


def _protected_prefix_token(token: str) -> bool:
    return (
        token.casefold() in _PREFIX_NEGATIONS
        or (len(token) == 2 and token.isalpha() and token.islower())
        or any(character.isnumeric() for character in token)
        or not token.isalpha()
        or token.isupper()
    )


def _is_subsequence(expected: list[str], actual: list[str]) -> bool:
    cursor = iter(actual)
    return all(any(candidate == token for candidate in cursor) for token in expected)


def _valid_threshold(value: float) -> bool:
    return not isinstance(value, bool) and isinstance(value, (int, float)) and isfinite(value) and 0 <= value <= 1


def _valid_line(line: OcrLine, minimum: float, bounds: tuple[float, float, float, float]) -> bool:
    if not isinstance(line.text, str) or not line.text.strip() or not _valid_threshold(line.score) or line.score < minimum:
        return False
    if len(line.polygon) < 4:
        return False
    try:
        if not all(len(point) == 2 and all(not isinstance(value, bool) and isinstance(value, (int, float)) and isfinite(value) for value in point) for point in line.polygon):
            return False
        left, top, right, bottom = bounds
        return left <= line.center_x <= right and top <= line.center_y <= bottom and line.right > line.left and line.height > 0
    except (TypeError, ValueError, AttributeError):
        return False


def _observation_proof(observation: TextOcrObservation) -> dict[str, Any]:
    return {
        "engine": observation.engine,
        "engine_version": observation.engine_version,
        "model_id": observation.model_id,
        "model_sha256": observation.model_sha256,
        "model_artifacts": deepcopy(observation.model_artifacts),
        "runtime_settings": deepcopy(observation.runtime_settings),
        "duration_ms": observation.duration_ms,
        "coordinate_origin": "TOPLEFT",
        "minimum_confidence": min((line.score for line in observation.lines if _valid_threshold(line.score)), default=None),
        "lines": [{"text": line.text, "confidence": line.score if _valid_threshold(line.score) else None, "polygon": [list(point) for point in line.polygon]}
                  for line in observation.lines],
    }
