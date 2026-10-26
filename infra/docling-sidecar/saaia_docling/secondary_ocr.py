from __future__ import annotations

import csv
from io import StringIO
from math import isfinite
import os
from pathlib import Path
import re
from typing import Any, Mapping, Sequence

from .table_repair import OcrLine


_STANDARD_TESSDATA = (
    Path("/usr/share/tesseract-ocr/4.00/tessdata"),
    Path("/usr/share/tesseract-ocr/5/tessdata"),
    Path("/usr/share/tessdata"),
)


def resolve_tesseract_model(
    environment: Mapping[str, str] | None = None,
    *, standard_directories: Sequence[Path] = _STANDARD_TESSDATA,
) -> Path:
    """Select a single local language pack; the caller must pass its directory to Tesseract."""
    env = os.environ if environment is None else environment
    configured = env.get("SAAIA_DOCLING_TEXT_REPAIR_TESSDATA_DIR") or env.get("TESSDATA_PREFIX")
    if configured:
        root = Path(configured)
        if not root.is_absolute():
            raise ValueError("Tesseract model directory must be absolute.")
        directories = (root, root / "tessdata")
    else:
        directories = standard_directories
    candidates = {(directory / "eng.traineddata").resolve() for directory in directories
                  if (directory / "eng.traineddata").is_file() and (directory / "eng.traineddata").stat().st_size > 0}
    if len(candidates) != 1:
        raise ValueError("A unique local Tesseract English model is required.")
    return next(iter(candidates))


def validate_tesseract_languages(stdout: str) -> None:
    lines = stdout.splitlines()
    if not lines:
        raise ValueError("Tesseract language inventory is unavailable.")
    header = re.fullmatch(r'List of available languages(?: in "[^"\r\n]+")? \((\d+)\):', lines[0].strip())
    languages = [line.strip() for line in lines[1:] if line.strip()]
    if (header is None or int(header.group(1)) != len(languages) or len(languages) != len(set(languages))
            or "eng" not in languages or not all(re.fullmatch(r"[A-Za-z0-9_+\-]+", value) for value in languages)):
        raise ValueError("Tesseract English language inventory is invalid.")


def parse_tesseract_tsv(tsv: str, region: Any) -> tuple[OcrLine, ...]:
    """Keep individual word confidences and TSV reading order, never its empty layout rows."""
    rows = csv.DictReader(StringIO(tsv), delimiter="\t")
    required = {"level", "left", "top", "width", "height", "conf", "text"}
    if not rows.fieldnames or not required.issubset(rows.fieldnames):
        raise ValueError("Tesseract TSV schema is invalid.")
    boxes, texts, scores = [], [], []
    for row in rows:
        if row.get("level") != "5":
            continue
        text = row.get("text", "").strip()
        if not text:
            continue
        try:
            left, top, width, height = (int(row[key]) for key in ("left", "top", "width", "height"))
            confidence = float(row["conf"]) / 100
        except (ValueError, TypeError, KeyError) as exc:
            raise ValueError("Tesseract TSV word is invalid.") from exc
        if left < 0 or top < 0 or width <= 0 or height <= 0 or not isfinite(confidence) or not 0 <= confidence <= 1:
            raise ValueError("Tesseract TSV word geometry or confidence is invalid.")
        boxes.append(((left, top), (left + width, top), (left + width, top + height), (left, top + height)))
        texts.append(text)
        scores.append(confidence)
    return tuple(region.to_page_lines(boxes, texts, scores))
