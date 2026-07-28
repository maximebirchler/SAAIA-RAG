from __future__ import annotations

import hashlib
import json
from pathlib import Path
from typing import Callable

from PIL import Image, ImageDraw, ImageFont
from reportlab.lib import colors
from reportlab.lib.enums import TA_CENTER
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib.units import mm
from reportlab.platypus import Paragraph, Table, TableStyle
from reportlab.pdfgen.canvas import Canvas
from reportlab.lib.utils import ImageReader


PAGE_WIDTH, PAGE_HEIGHT = A4


def _canvas(path: Path) -> Canvas:
    return Canvas(
        str(path),
        pagesize=A4,
        pageCompression=0,
        invariant=1,
    )


def _draw_header(canvas: Canvas, title: str) -> None:
    canvas.setFont("Helvetica-Bold", 16)
    canvas.drawString(18 * mm, PAGE_HEIGHT - 20 * mm, title)
    canvas.setStrokeColor(colors.HexColor("#26364A"))
    canvas.setLineWidth(1.2)
    canvas.line(
        18 * mm,
        PAGE_HEIGHT - 23 * mm,
        PAGE_WIDTH - 18 * mm,
        PAGE_HEIGHT - 23 * mm,
    )


def _draw_footer(canvas: Canvas, marker: str) -> None:
    canvas.setFont("Helvetica", 8)
    canvas.setFillColor(colors.HexColor("#566270"))
    canvas.drawCentredString(PAGE_WIDTH / 2, 10 * mm, marker)
    canvas.setFillColor(colors.black)


def build_two_column(path: Path, _: Path) -> None:
    canvas = _canvas(path)
    _draw_header(canvas, "LAYOUT GOLD - TWO COLUMN ORDER")
    left_x = 18 * mm
    right_x = 111 * mm
    top = PAGE_HEIGHT - 34 * mm
    canvas.setFont("Helvetica-Bold", 11)
    canvas.drawString(left_x, top, "LEFT COLUMN")
    canvas.drawString(right_x, top, "RIGHT COLUMN")
    canvas.setFont("Helvetica", 9.5)
    lines = [
        (
            "LEFT ALPHA 101 begins the first independent narrative.",
            "RIGHT ECHO 201 begins only after the left narrative.",
        ),
        (
            "LEFT BRAVO 102 preserves the first column sequence.",
            "RIGHT FOXTROT 202 preserves the second column sequence.",
        ),
        (
            "LEFT CHARLIE 103 remains above the final left item.",
            "RIGHT GOLF 203 remains above the final right item.",
        ),
        (
            "LEFT DELTA 104 closes the complete left column.",
            "RIGHT HOTEL 204 closes the complete right column.",
        ),
    ]
    y = top - 10 * mm
    for left, right in lines:
        canvas.drawString(left_x, y, left)
        canvas.drawString(right_x, y, right)
        y -= 17 * mm
    _draw_footer(canvas, "LAYOUT GOLD FOOTER 001")
    canvas.showPage()
    canvas.save()


def build_three_column(path: Path, _: Path) -> None:
    canvas = _canvas(path)
    _draw_header(canvas, "LAYOUT GOLD - THREE COLUMN ORDER")
    column_x = [18 * mm, 76 * mm, 134 * mm]
    headings = ["COLUMN ONE", "COLUMN TWO", "COLUMN THREE"]
    markers = [
        ["ONE ALPHA 301", "ONE BRAVO 302", "ONE CHARLIE 303", "ONE DELTA 304"],
        ["TWO ECHO 311", "TWO FOXTROT 312", "TWO GOLF 313", "TWO HOTEL 314"],
        ["THREE INDIA 321", "THREE JULIET 322", "THREE KILO 323", "THREE LIMA 324"],
    ]
    top = PAGE_HEIGHT - 34 * mm
    for x, heading, values in zip(column_x, headings, markers):
        canvas.setFont("Helvetica-Bold", 10)
        canvas.drawString(x, top, heading)
        canvas.setFont("Helvetica", 8.5)
        y = top - 10 * mm
        for value in values:
            canvas.drawString(x, y, value)
            y -= 17 * mm
    _draw_footer(canvas, "LAYOUT GOLD FOOTER 004")
    canvas.showPage()
    canvas.save()


def build_unequal_columns(path: Path, _: Path) -> None:
    canvas = _canvas(path)
    _draw_header(canvas, "LAYOUT GOLD - UNEQUAL COLUMN ORDER")
    left_x = 18 * mm
    right_x = 111 * mm
    top = PAGE_HEIGHT - 34 * mm
    canvas.setFont("Helvetica-Bold", 10)
    canvas.drawString(left_x, top, "LONG LEFT COLUMN")
    canvas.drawString(right_x, top, "SHORT RIGHT COLUMN")
    canvas.setFont("Helvetica", 8.8)
    for index, marker in enumerate(
        [
            "LONG LEFT ALPHA 401",
            "LONG LEFT BRAVO 402",
            "LONG LEFT CHARLIE 403",
            "LONG LEFT DELTA 404",
            "LONG LEFT ECHO 405",
            "LONG LEFT FOXTROT 406",
        ]
    ):
        canvas.drawString(left_x, top - (10 + index * 15) * mm, marker)
    for index, marker in enumerate(
        [
            "SHORT RIGHT GOLF 411",
            "SHORT RIGHT HOTEL 412",
            "SHORT RIGHT INDIA 413",
        ]
    ):
        canvas.drawString(right_x, top - (10 + index * 24) * mm, marker)
    canvas.setFont("Helvetica-Oblique", 9)
    canvas.drawString(
        18 * mm,
        PAGE_HEIGHT - 145 * mm,
        "FULL WIDTH NOTE 420 follows both independent columns.",
    )
    _draw_footer(canvas, "LAYOUT GOLD FOOTER 005")
    canvas.showPage()
    canvas.save()


def build_late_page_title_columns(path: Path, _: Path) -> None:
    canvas = _canvas(path)
    left_x = 18 * mm
    right_x = 111 * mm
    top = PAGE_HEIGHT - 34 * mm

    # Deliberately write both columns into the PDF content stream before the
    # visually superior title. This reproduces a generic parser failure class:
    # visual hierarchy and source-object order disagree.
    canvas.setFont("Helvetica-Bold", 11)
    canvas.drawString(left_x, top, "LEFT SECTION 1001")
    canvas.drawString(right_x, top, "RIGHT SECTION 1011")
    canvas.setFont("Helvetica", 9.5)
    lines = [
        (
            "LEFT ALPHA 1002 begins the complete first narrative.",
            "RIGHT ECHO 1012 begins only after the left narrative.",
        ),
        (
            "LEFT BRAVO 1003 preserves the first column sequence.",
            "RIGHT FOXTROT 1013 preserves the second column sequence.",
        ),
        (
            "LEFT CHARLIE 1004 remains above the final left item.",
            "RIGHT GOLF 1014 remains above the final right item.",
        ),
        (
            "LEFT DELTA 1005 closes the complete left column.",
            "RIGHT HOTEL 1015 closes the complete right column.",
        ),
    ]
    y = top - 10 * mm
    for left, right in lines:
        canvas.drawString(left_x, y, left)
        canvas.drawString(right_x, y, right)
        y -= 17 * mm

    _draw_header(canvas, "LATE PAGE TITLE 1000")
    _draw_footer(canvas, "LAYOUT GOLD FOOTER 007")
    canvas.showPage()
    canvas.save()


def build_columns_and_table(path: Path, _: Path) -> None:
    canvas = _canvas(path)
    _draw_header(canvas, "LAYOUT GOLD - COLUMNS AND TABLE")
    left_x = 18 * mm
    right_x = 111 * mm
    top = PAGE_HEIGHT - 34 * mm
    canvas.setFont("Helvetica-Bold", 10)
    canvas.drawString(left_x, top, "LEFT NARRATIVE")
    canvas.drawString(right_x, top, "RIGHT NARRATIVE")
    canvas.setFont("Helvetica", 8.8)
    left_markers = ["LEFT NOVEMBER 501", "LEFT OSCAR 502", "LEFT PAPA 503"]
    right_markers = ["RIGHT QUEBEC 511", "RIGHT ROMEO 512", "RIGHT SIERRA 513"]
    for index, (left, right) in enumerate(zip(left_markers, right_markers)):
        y = top - (10 + index * 16) * mm
        canvas.drawString(left_x, y, left)
        canvas.drawString(right_x, y, right)

    canvas.setFont("Helvetica", 9)
    canvas.drawString(18 * mm, PAGE_HEIGHT - 100 * mm, "TABLE CAPTION 520")
    data = [
        ["GRID 521", "Metric", "State"],
        ["Row A", "11", "Ready"],
        ["Row B", "22", "Review"],
        ["Row C", "33", "Closed"],
    ]
    table = Table(
        data,
        colWidths=[58 * mm, 45 * mm, 55 * mm],
        rowHeights=[10 * mm] * 4,
    )
    table.setStyle(
        TableStyle(
            [
                ("GRID", (0, 0), (-1, -1), 0.8, colors.HexColor("#2F4358")),
                ("BACKGROUND", (0, 0), (-1, 0), colors.HexColor("#EAF0F6")),
                ("FONTNAME", (0, 0), (-1, 0), "Helvetica-Bold"),
                ("ALIGN", (0, 0), (-1, -1), "CENTER"),
                ("VALIGN", (0, 0), (-1, -1), "MIDDLE"),
            ]
        )
    )
    width, height = table.wrapOn(canvas, PAGE_WIDTH, PAGE_HEIGHT)
    table.drawOn(
        canvas,
        (PAGE_WIDTH - width) / 2,
        PAGE_HEIGHT - 106 * mm - height,
    )
    canvas.setFont("Helvetica", 9)
    canvas.drawString(
        18 * mm,
        PAGE_HEIGHT - 158 * mm,
        "POST GRID NOTE 522 follows the complete table.",
    )
    _draw_footer(canvas, "LAYOUT GOLD FOOTER 006")
    canvas.showPage()
    canvas.save()


def build_structured_table(path: Path, _: Path) -> None:
    canvas = _canvas(path)
    _draw_header(canvas, "LAYOUT GOLD - STRUCTURED TABLE")
    canvas.setFont("Helvetica", 9)
    canvas.drawString(
        18 * mm,
        PAGE_HEIGHT - 31 * mm,
        "TABLE CAPTION MATERIAL LIMITS",
    )
    style = ParagraphStyle(
        "cell",
        fontName="Helvetica",
        fontSize=8,
        leading=9,
        alignment=TA_CENTER,
    )
    data = [
        [Paragraph("<b>OPERATING MATRIX 731</b>", style), "", "", ""],
        ["Material", "Pressure", "Temperature", "Status"],
        ["Alloy AX-11", "16 bar", "120 C", "Approved"],
        ["Polymer PX-22", "8 bar", "-20 to 80 C", ""],
        [
            "Composite CX-33",
            "12 bar",
            Paragraph("45 C continuous<br/>70 C temporary", style),
            "Review",
        ],
    ]
    table = Table(
        data,
        colWidths=[43 * mm, 32 * mm, 53 * mm, 34 * mm],
        rowHeights=[11 * mm, 10 * mm, 11 * mm, 11 * mm, 17 * mm],
    )
    table.setStyle(
        TableStyle(
            [
                ("SPAN", (0, 0), (3, 0)),
                ("GRID", (0, 0), (-1, -1), 0.8, colors.HexColor("#2F4358")),
                ("BACKGROUND", (0, 0), (-1, 0), colors.HexColor("#D7E5F4")),
                ("BACKGROUND", (0, 1), (-1, 1), colors.HexColor("#EAF0F6")),
                ("FONTNAME", (0, 1), (-1, 1), "Helvetica-Bold"),
                ("ALIGN", (0, 0), (-1, -1), "CENTER"),
                ("VALIGN", (0, 0), (-1, -1), "MIDDLE"),
                ("LEFTPADDING", (0, 0), (-1, -1), 4),
                ("RIGHTPADDING", (0, 0), (-1, -1), 4),
            ]
        )
    )
    width, height = table.wrapOn(canvas, PAGE_WIDTH, PAGE_HEIGHT)
    table.drawOn(
        canvas,
        (PAGE_WIDTH - width) / 2,
        PAGE_HEIGHT - 39 * mm - height,
    )
    canvas.setFont("Helvetica", 9)
    canvas.drawString(
        18 * mm,
        PAGE_HEIGHT - 112 * mm,
        "POST TABLE NOTE 732 confirms the matrix boundary.",
    )
    _draw_footer(canvas, "LAYOUT GOLD FOOTER 002")
    canvas.showPage()
    canvas.save()


def _load_image_font(size: int) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    candidates = [
        Path("C:/Windows/Fonts/arial.ttf"),
        Path("C:/Windows/Fonts/segoeui.ttf"),
        Path("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"),
    ]
    for candidate in candidates:
        if candidate.exists():
            return ImageFont.truetype(str(candidate), size=size)
    return ImageFont.load_default()


def _build_embedded_text_image(path: Path) -> None:
    image = Image.new("RGB", (1200, 360), "white")
    draw = ImageDraw.Draw(image)
    title_font = _load_image_font(54)
    body_font = _load_image_font(42)
    draw.rectangle((4, 4, 1195, 355), outline="#2A3F55", width=5)
    draw.text((45, 35), "IMAGE PANEL 880", fill="#102030", font=title_font)
    draw.text(
        (45, 130),
        "IMAGE OCR ALPHA 881 is raster-only evidence.",
        fill="#102030",
        font=body_font,
    )
    draw.text(
        (45, 215),
        "IMAGE OCR BRAVO 882 closes the raster panel.",
        fill="#102030",
        font=body_font,
    )
    image.save(path, format="PNG", optimize=False)


def build_mixed_image_text(path: Path, workdir: Path) -> None:
    image_path = workdir / "mixed-image-panel.png"
    _build_embedded_text_image(image_path)
    canvas = _canvas(path)
    _draw_header(canvas, "LAYOUT GOLD - MIXED IMAGE AND TEXT")
    canvas.setFont("Helvetica", 10)
    canvas.drawString(
        18 * mm,
        PAGE_HEIGHT - 34 * mm,
        "NATIVE INTRO 870 appears before the raster panel.",
    )
    canvas.drawImage(
        ImageReader(str(image_path)),
        20 * mm,
        PAGE_HEIGHT - 113 * mm,
        width=170 * mm,
        height=51 * mm,
        preserveAspectRatio=True,
        mask="auto",
    )
    canvas.setFont("Helvetica-Oblique", 9)
    canvas.drawCentredString(
        PAGE_WIDTH / 2,
        PAGE_HEIGHT - 119 * mm,
        "FIGURE CAPTION 883 describes the raster panel.",
    )
    canvas.setFont("Helvetica", 10)
    canvas.drawString(
        24 * mm,
        PAGE_HEIGHT - 139 * mm,
        "1. LIST ITEM 884 preserves the first list entry.",
    )
    canvas.drawString(
        24 * mm,
        PAGE_HEIGHT - 151 * mm,
        "2. LIST ITEM 885 preserves the second list entry.",
    )
    canvas.drawString(
        18 * mm,
        PAGE_HEIGHT - 174 * mm,
        "NATIVE OUTRO 886 appears after the list.",
    )
    _draw_footer(canvas, "LAYOUT GOLD FOOTER 003")
    canvas.showPage()
    canvas.save()


def _build_scanned_page(path: Path) -> None:
    width, height = 1191, 1684
    image = Image.new("RGB", (width, height), "#F5F3EC")
    draw = ImageDraw.Draw(image)
    heading = _load_image_font(58)
    body = _load_image_font(40)
    draw.text((95, 110), "LAYOUT GOLD FULL PAGE SCAN", fill="#26313A", font=heading)
    lines = [
        "SCAN ENGLISH 910 confirms the first OCR line.",
        "SCAN FRANCAIS 911 confirme la deuxieme ligne.",
        "SCAN DEUTSCH 912 bestaetigt die dritte Zeile.",
        "SCAN ITALIANO 913 conferma la quarta riga.",
        "SCAN FINAL 914 closes the multilingual sequence.",
    ]
    y = 315
    for line in lines:
        draw.text((100, y), line, fill="#454B50", font=body)
        y += 170
    draw.rectangle((90, 270, 1100, 1230), outline="#8C969C", width=4)
    rotated = image.rotate(
        0.65,
        resample=Image.Resampling.BICUBIC,
        expand=False,
        fillcolor="#F5F3EC",
    )
    rotated.save(path, format="PNG", optimize=False)


def build_full_page_scan(path: Path, workdir: Path) -> None:
    image_path = workdir / "full-page-scan.png"
    _build_scanned_page(image_path)
    canvas = _canvas(path)
    canvas.drawImage(
        ImageReader(str(image_path)),
        0,
        0,
        width=PAGE_WIDTH,
        height=PAGE_HEIGHT,
        preserveAspectRatio=False,
        mask="auto",
    )
    canvas.showPage()
    canvas.save()


BUILDERS: dict[str, Callable[[Path, Path], None]] = {
    "two_column_order": build_two_column,
    "three_column_order": build_three_column,
    "unequal_column_order": build_unequal_columns,
    "late_page_title_columns": build_late_page_title_columns,
    "columns_and_table": build_columns_and_table,
    "structured_table": build_structured_table,
    "mixed_image_text": build_mixed_image_text,
    "full_page_scan": build_full_page_scan,
}


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def generate_fixtures(manifest_path: Path, output_dir: Path) -> list[dict]:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if manifest.get("schemaVersion") != "ingestion_layout_gold_v1":
        raise ValueError("Unsupported ingestion layout gold schema.")
    output_dir.mkdir(parents=True, exist_ok=True)
    generated: list[dict] = []
    for case in manifest.get("cases", []):
        case_id = str(case["caseId"])
        builder_name = str(case["generator"])
        builder = BUILDERS.get(builder_name)
        if builder is None:
            raise ValueError(f"Unknown fixture generator: {builder_name}")
        output_path = output_dir / f"{case_id}.pdf"
        builder(output_path, output_dir)
        source_hash = sha256_file(output_path)
        expected_hash = str(case.get("sourceSha256") or "")
        if expected_hash and source_hash != expected_hash:
            raise ValueError(
                f"Fixture {case_id} drifted: expected {expected_hash}, got {source_hash}."
            )
        generated.append(
            {
                "case": case,
                "path": output_path,
                "sourceSha256": source_hash,
            }
        )
    return generated
