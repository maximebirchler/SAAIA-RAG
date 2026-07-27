from __future__ import annotations

from dataclasses import dataclass
from math import ceil, floor, isfinite
from pathlib import Path
from typing import Any, Mapping, Sequence

import pypdfium2 as pdfium
from PIL import Image

from .table_repair import OcrLine


@dataclass(frozen=True)
class RenderedTableRegion:
    image: Image.Image
    page_number: int
    crop_left_px: int
    crop_top_px: int
    pixels_per_point_x: float
    pixels_per_point_y: float
    table_left: float
    table_top: float
    table_right: float
    table_bottom: float

    def to_page_lines(
        self,
        boxes: Sequence[Sequence[Sequence[float]]],
        texts: Sequence[str],
        scores: Sequence[float],
    ) -> list[OcrLine]:
        lines: list[OcrLine] = []
        for box, text, score in zip(boxes, texts, scores):
            polygon = tuple(
                (
                    (self.crop_left_px + float(point[0]))
                    / self.pixels_per_point_x,
                    (self.crop_top_px + float(point[1]))
                    / self.pixels_per_point_y,
                )
                for point in box
            )
            line = OcrLine(str(text), float(score), polygon)
            if (
                self.table_left <= line.center_x <= self.table_right
                and self.table_top <= line.center_y <= self.table_bottom
            ):
                lines.append(line)
        return lines


def render_table_region(
    pdf_path: str | Path,
    document: Mapping[str, Any],
    table: Mapping[str, Any],
    *,
    scale: float = 2.5,
    padding_points: float = 12.0,
) -> RenderedTableRegion:
    if not isfinite(scale) or scale < 1 or scale > 8:
        raise ValueError("Render scale must be between 1 and 8.")
    if (
        not isfinite(padding_points)
        or padding_points < 0
        or padding_points > 72
    ):
        raise ValueError("Table padding must be between 0 and 72 points.")

    provenance = table.get("prov")
    if not isinstance(provenance, list) or len(provenance) != 1:
        raise ValueError("A repairable table must have exactly one provenance region.")
    region = provenance[0]
    if not isinstance(region, Mapping):
        raise ValueError("Table provenance is invalid.")
    page_number = _positive_int(region.get("page_no"))
    if page_number is None:
        raise ValueError("Table page number is invalid.")

    pages = document.get("pages")
    if not isinstance(pages, Mapping):
        raise ValueError("Docling pages are unavailable.")
    page = pages.get(str(page_number))
    if not isinstance(page, Mapping):
        page = next(
            (
                value
                for value in pages.values()
                if isinstance(value, Mapping)
                and _positive_int(value.get("page_no")) == page_number
            ),
            None,
        )
    if not isinstance(page, Mapping):
        raise ValueError(f"Docling page {page_number} is unavailable.")
    size = page.get("size")
    if not isinstance(size, Mapping):
        raise ValueError("Docling page size is unavailable.")
    page_width = _positive_float(size.get("width"))
    page_height = _positive_float(size.get("height"))
    if page_width is None or page_height is None:
        raise ValueError("Docling page size is invalid.")

    table_box = _to_top_left_bbox(region.get("bbox"), page_height)
    if table_box is None:
        raise ValueError("Table provenance bounding box is invalid.")
    table_left, table_top, table_right, table_bottom = table_box

    pdf = pdfium.PdfDocument(str(pdf_path))
    try:
        if page_number > len(pdf):
            raise ValueError(f"PDF page {page_number} is unavailable.")
        pdf_page = pdf[page_number - 1]
        try:
            bitmap = pdf_page.render(scale=scale)
            try:
                page_image = bitmap.to_pil()
            finally:
                bitmap.close()
        finally:
            pdf_page.close()
    finally:
        pdf.close()

    pixels_per_point_x = page_image.width / page_width
    pixels_per_point_y = page_image.height / page_height
    crop_left = max(0.0, table_left - padding_points)
    crop_top = max(0.0, table_top - padding_points)
    crop_right = min(page_width, table_right + padding_points)
    crop_bottom = min(page_height, table_bottom + padding_points)
    left_px = max(0, floor(crop_left * pixels_per_point_x))
    top_px = max(0, floor(crop_top * pixels_per_point_y))
    right_px = min(
        page_image.width,
        ceil(crop_right * pixels_per_point_x),
    )
    bottom_px = min(
        page_image.height,
        ceil(crop_bottom * pixels_per_point_y),
    )
    if right_px <= left_px or bottom_px <= top_px:
        raise ValueError("Rendered table region is empty.")

    crop = page_image.crop((left_px, top_px, right_px, bottom_px))
    page_image.close()
    return RenderedTableRegion(
        image=crop,
        page_number=page_number,
        crop_left_px=left_px,
        crop_top_px=top_px,
        pixels_per_point_x=pixels_per_point_x,
        pixels_per_point_y=pixels_per_point_y,
        table_left=table_left,
        table_top=table_top,
        table_right=table_right,
        table_bottom=table_bottom,
    )


def _to_top_left_bbox(
    value: Any,
    page_height: float,
) -> tuple[float, float, float, float] | None:
    if not isinstance(value, Mapping):
        return None
    try:
        left = float(value["l"])
        top = float(value["t"])
        right = float(value["r"])
        bottom = float(value["b"])
    except (KeyError, TypeError, ValueError):
        return None
    if not all(isfinite(number) for number in (left, top, right, bottom)):
        return None
    origin = str(value.get("coord_origin", "TOPLEFT")).upper()
    if origin == "BOTTOMLEFT":
        top, bottom = page_height - top, page_height - bottom
    elif origin != "TOPLEFT":
        return None
    if right <= left or bottom <= top:
        return None
    return left, top, right, bottom


def _positive_int(value: Any) -> int | None:
    if isinstance(value, bool):
        return None
    try:
        parsed = int(value)
    except (TypeError, ValueError):
        return None
    return parsed if parsed > 0 and parsed == value else None


def _positive_float(value: Any) -> float | None:
    try:
        parsed = float(value)
    except (TypeError, ValueError):
        return None
    return parsed if isfinite(parsed) and parsed > 0 else None
