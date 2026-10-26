"""Pinned local fixture renderer. No font/model download or silent fallback."""
from __future__ import annotations

from contextlib import contextmanager
from contextvars import ContextVar
import hashlib
from pathlib import Path
import platform
from typing import Any, Iterator

import PIL
import reportlab
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont

GENERATOR_VERSION = "saaia-gold-renderer-2"
FONT_NAME = "SaaiaGoldPinned"
PINNED_FONT: ContextVar[Path | None] = ContextVar("saaia_gold_font", default=None)
SOURCE_FILES = ("fixtures.py", "fixtures_fr.py", "renderer.py")


def renderer_identity(font_path: Path) -> dict[str, Any]:
    if not font_path.is_file():
        raise ValueError("An existing explicit local font file is required; no fallback or download.")
    return {
        "generatorVersion": GENERATOR_VERSION,
        "pythonVersion": platform.python_version(),
        "reportlabVersion": reportlab.Version,
        "pillowVersion": PIL.__version__,
        "fontSha256": hashlib.sha256(font_path.read_bytes()).hexdigest(),
        "generatorSources": {name: hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in SOURCE_FILES},
    }


@contextmanager
def pinned_renderer(pins: dict[str, Any] | None, font_path: Path | None) -> Iterator[dict[str, Any]]:
    if pins is None:
        yield {"verified": False, "reason": "historical_unpinned_renderer"}
        return
    if font_path is None:
        raise ValueError("Pinned acceptance generation requires --font-path.")
    actual = renderer_identity(font_path)
    if actual != pins:
        raise ValueError("Renderer version, font or generator hash drift; use a separately reviewed corpus manifest.")
    pdfmetrics.registerFont(TTFont(FONT_NAME, str(font_path)))
    token = PINNED_FONT.set(font_path)
    try:
        yield {"verified": True, **actual}
    finally:
        PINNED_FONT.reset(token)
