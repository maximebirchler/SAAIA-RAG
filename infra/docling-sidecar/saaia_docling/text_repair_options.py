from __future__ import annotations

from dataclasses import asdict, dataclass
import math
import os
from typing import Mapping


@dataclass(frozen=True)
class TextRepairOptions:
    enabled: bool = False
    max_regions: int = 4
    total_timeout_seconds: float = 60.0
    region_timeout_seconds: float = 15.0
    render_scale: float = 4.0
    padding_points: float = 2.0
    minimum_rapid_score: float = 0.98
    minimum_secondary_score: float = 0.95
    max_rendered_pixels: int = 20_000_000

    def __post_init__(self) -> None:
        if type(self.enabled) is not bool:
            raise ValueError("Text repair enabled must be boolean.")
        for name, maximum in (("max_regions", 4), ("max_rendered_pixels", 20_000_000)):
            value = getattr(self, name)
            if type(value) is not int or not 1 <= value <= maximum:
                raise ValueError(f"Text repair {name} is out of bounds.")
        for name, minimum, maximum in (
            ("total_timeout_seconds", 0.1, 60), ("region_timeout_seconds", 0.1, 15),
            ("render_scale", 1, 4), ("padding_points", 0, 2),
            ("minimum_rapid_score", 0.98, 1), ("minimum_secondary_score", 0.95, 1),
        ):
            value = getattr(self, name)
            if isinstance(value, bool) or not isinstance(value, (float, int)) or not math.isfinite(value) or not minimum <= value <= maximum:
                raise ValueError(f"Text repair {name} is out of bounds.")

    @classmethod
    def from_environment(cls, environment: Mapping[str, str] | None = None) -> TextRepairOptions:
        env = os.environ if environment is None else environment
        prefix = "SAAIA_DOCLING_TEXT_REPAIR_"
        raw_enabled = env.get(prefix + "ENABLED", "false").strip().casefold()
        if raw_enabled not in {"true", "false", "1", "0"}:
            raise ValueError("Text repair enabled must be true or false.")
        values: dict[str, object] = {"enabled": raw_enabled in {"true", "1"}}
        for field, value in asdict(cls()).items():
            if field != "enabled":
                values[field] = type(value)(env.get(prefix + field.upper(), str(value)))
        return cls(**values)

    def attestation(self) -> dict[str, object]:
        return {
            **asdict(self),
            "schema_version": "saaia_text_repair_settings_v1",
            "secondary_engine": "tesseract",
            "secondary_language": "eng",
            "secondary_page_segmentation_mode": 6,
            "agreement_normalization": "unicode_nfc_and_whitespace_only",
            "linguistic_prefix_negation_guard": "fr_en_de_finite_v1",
            "universal_multilingual_validation": False,
            "process_isolation": "per_region_linux_process_group",
        }
