from __future__ import annotations

from typing import Iterable
from importlib import metadata


def conversion_execution_metadata(
    fields: Iterable[tuple[str, str]], *, repair_enabled: bool,
    render_scale: float, padding_points: float, minimum_score: float,
    text_repair_settings: dict[str, object] | None = None,
    table_repair_settings: dict[str, object] | None = None,
) -> dict[str, object]:
    """Attest the received request and this sidecar's effective settings, not OCR page success."""
    received = dict(fields)
    def flag(name: str) -> bool | None:
        value = received.get(name)
        if value is None:
            return None
        return value.strip().casefold() in {"true", "1", "yes", "on"}
    result = {
        "status": "observed_configuration",
        "package_versions": observed_package_versions(),
        "do_ocr": flag("do_ocr"),
        "force_ocr": flag("force_ocr"),
        "table_repair_enabled": repair_enabled,
        "table_repair_scale": render_scale,
        "table_repair_padding_points": padding_points,
        "table_repair_minimum_score": minimum_score,
    }
    if text_repair_settings is not None:
        result["text_repair_settings"] = dict(text_repair_settings)
    if table_repair_settings is not None:
        result["table_repair_settings"] = dict(table_repair_settings)
    return result


def observed_package_versions() -> dict[str, str | None]:
    observed: dict[str, str | None] = {}
    for package in ("docling-serve", "docling-slim", "docling-parse", "docling-ibm-models", "rapidocr", "onnxruntime", "torch"):
        try:
            observed[package] = metadata.version(package)
        except metadata.PackageNotFoundError:
            observed[package] = None
    return observed
