from __future__ import annotations

import threading
import time
from dataclasses import dataclass
from importlib.metadata import version

from PIL import Image
from rapidocr import RapidOCR

from .pdf_regions import RenderedTableRegion
from .table_repair import OcrLine


MODEL_ID = "PP-OCRv6_det_small+PP-OCRv6_rec_small"


@dataclass(frozen=True)
class OcrRegionResult:
    lines: tuple[OcrLine, ...]
    duration_ms: int
    engine_version: str
    model_id: str = MODEL_ID


class RapidOcrRegionEngine:
    """Lazy, serialized access to the embedded ONNX Runtime OCR models."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._engine: RapidOCR | None = None
        self.engine_version = version("rapidocr")

    def recognize(self, region: RenderedTableRegion) -> OcrRegionResult:
        with self._lock:
            if self._engine is None:
                self._engine = RapidOCR()
            started = time.perf_counter()
            result = self._engine(
                region.image,
                use_cls=False,
                return_word_box=True,
            )
            duration_ms = round((time.perf_counter() - started) * 1_000)
        lines = region.to_page_lines(
            result.boxes if result.boxes is not None else (),
            result.txts if result.txts is not None else (),
            result.scores if result.scores is not None else (),
        )
        return OcrRegionResult(
            lines=tuple(lines),
            duration_ms=duration_ms,
            engine_version=self.engine_version,
        )


def close_region(region: RenderedTableRegion) -> None:
    if isinstance(region.image, Image.Image):
        region.image.close()
