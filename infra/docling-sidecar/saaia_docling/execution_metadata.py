from __future__ import annotations

from typing import Iterable
from importlib import metadata


def conversion_execution_metadata(
    fields: Iterable[tuple[str, str]], *, repair_enabled: bool,
    render_scale: float, padding_points: float, minimum_score: float,
) -> dict[str, object]:
    """Attest the received request and this sidecar's effective settings, not OCR page success."""
    received = dict(fields)
    def flag(name: str) -> bool | None:
        value = received.get(name)
        if value is None:
            return None
        return value.strip().casefold() in {"true", "1", "yes", "on"}
    return {
        "status": "observed_configuration",
        "package_versions": observed_package_versions(),
        "do_ocr": flag("do_ocr"),
        "force_ocr": flag("force_ocr"),
        "table_repair_enabled": repair_enabled,
        "table_repair_scale": render_scale,
        "table_repair_padding_points": padding_points,
        "table_repair_minimum_score": minimum_score,
    }


def observed_package_versions() -> dict[str, str | None]:
    observed: dict[str, str | None] = {}
    for package in ("docling-serve", "docling-slim", "docling-parse", "docling-ibm-models", "rapidocr", "onnxruntime", "torch"):
        try:
            observed[package] = metadata.version(package)
        except metadata.PackageNotFoundError:
            observed[package] = None
    return observed
