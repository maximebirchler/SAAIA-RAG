from __future__ import annotations

from dataclasses import asdict, dataclass
from math import isfinite
import os


@dataclass(frozen=True)
class TableRepairOptions:
    max_regions: int = 8
    total_timeout_seconds: float = 60.0
    region_timeout_seconds: float = 15.0
    render_scale: float = 2.5
    padding_points: float = 12.0
    minimum_score: float = 0.5
    max_rendered_pixels: int = 20_000_000

    def __post_init__(self):
        for field, maximum in (("max_regions", 8), ("max_rendered_pixels", 20_000_000)):
            value = getattr(self, field)
            if type(value) is not int or not 1 <= value <= maximum:
                raise ValueError("Table repair integer budget invalid.")
        for field, low, high in (("total_timeout_seconds", 0.1, 60),
                ("region_timeout_seconds", 0.1, 15), ("render_scale", 1, 8),
                ("padding_points", 0, 72), ("minimum_score", 0, 1)):
            value = getattr(self, field)
            if isinstance(value, bool) or not isinstance(value, (int, float)) or not isfinite(value) or not low <= value <= high:
                raise ValueError("Table repair numeric budget invalid.")

    @classmethod
    def from_environment(cls, environment=None):
        env = os.environ if environment is None else environment
        suffixes = {"render_scale": "SCALE", "padding_points": "PADDING_POINTS",
                    "minimum_score": "MINIMUM_SCORE"}
        values = {}
        for key, value in asdict(cls()).items():
            name = "SAAIA_DOCLING_TABLE_REPAIR_" + suffixes.get(key, key.upper())
            values[key] = type(value)(env.get(name, str(value)))
        return cls(**values)

    def attestation(self):
        return {**asdict(self), "schema_version": "saaia_table_repair_settings_v2",
                "process_isolation": "per_region_linux_process_group",
                "engine": "rapidocr", "secondary_engine": None,
                "model_resolution": "bundled_local_files_only"}
