"""Short French source documents. Annotations live separately in the manifest."""
from __future__ import annotations

from pathlib import Path
from PIL import Image, ImageDraw, ImageFont
from reportlab.pdfgen.canvas import Canvas
from reportlab.lib.pagesizes import A4
from reportlab.lib.utils import ImageReader
from reportlab.platypus import Table, TableStyle
from reportlab.lib import colors
from reportlab.pdfbase import pdfmetrics

from .renderer import FONT_NAME, PINNED_FONT

WIDTH, HEIGHT = A4


def _canvas(path: Path) -> Canvas:
    if PINNED_FONT.get() is None:
        raise ValueError("French acceptance fixtures require a pinned Unicode font.")
    return Canvas(str(path), pagesize=A4, pageCompression=0, invariant=1)


def _lines(canvas: Canvas, lines: list[str], *, y: float = 790) -> None:
    canvas.setFont(FONT_NAME, 12)
    for line in lines:
        words = line.split()
        wrapped = ""
        for word in words:
            candidate = (wrapped + " " + word).strip()
            if wrapped and pdfmetrics.stringWidth(candidate, FONT_NAME, 12) > WIDTH - 80:
                canvas.drawString(40, y, wrapped)
                y -= 22
                wrapped = word
            else:
                wrapped = candidate
        canvas.drawString(40, y, wrapped)
        y -= 28


def build_fr_native(path: Path, _: Path) -> None:
    canvas = _canvas(path)
    for page, lines in enumerate([
        ["FICHE FRANÇAISE — CONTRÔLE A", "Préparation : 25 minutes", "Pour 4 opérateurs",
         "Pression maximale : 1,5 bar.", "Température minimale : -5 °C.",
         "Ne pas ouvrir sous tension.", "Capacité nominale : ½ litre.",
         "Mesurer la réponse, conserver le relevé et signer le rapport de contrôle."],
        ["FICHE FRANÇAISE — CONTRÔLE B", "Préparation : 25 minutes", "Pour 6 opérateurs",
         "Pression maximale : 15 bar.", "Température minimale : +5 °C.",
         "Ouvrir uniquement après consignation.", "Surface utile : 2 m².",
         "Comparer le résultat au seuil de cette page, puis archiver la mesure."],
    ], 1):
        _lines(canvas, lines)
        canvas.drawString(40, 35, f"Page {page} sur 2")
        canvas.showPage()
    canvas.save()


def _raster(path: Path, lines: list[str]) -> None:
    font_path = PINNED_FONT.get()
    if font_path is None:
        raise ValueError("Raster generation requires the pinned font.")
    image = Image.new("RGB", (1654, 2339), "white")
    draw = ImageDraw.Draw(image)
    font = ImageFont.truetype(str(font_path), size=42)
    for index, line in enumerate(lines):
        draw.text((100, 150 + index * 170), line, font=font, fill="black")
    image.save(path, format="PNG", optimize=False)


def build_fr_mixed(path: Path, workdir: Path) -> None:
    canvas = _canvas(path)
    _lines(canvas, ["DOSSIER MIXTE — PAGE NATIVE", "Référence atelier : ORION-4826.",
                    "La page suivante est uniquement une image ; elle contient ses propres limites.",
                    "Consigner le numéro de page de chaque observation dans le rapport de mesure."])
    canvas.showPage()
    image = workdir / "fr-mixed-raster.png"
    _raster(image, ["DOSSIER MIXTE — PAGE SCANNÉE", "Pression limite : 2,5 bar.",
                    "Ne pas démarrer sans protection.", "Qualification : AC", "État demandé : OFF"])
    canvas.drawImage(ImageReader(str(image)), 0, 0, WIDTH, HEIGHT)
    canvas.showPage()
    _lines(canvas, ["DOSSIER MIXTE — RETOUR AU NATIF", "Pression limite : 25 bar.",
                    "Qualification : DC", "État demandé : ON",
                    "Cette valeur appartient à la troisième page ; ne pas la reporter sur la page scannée."])
    canvas.showPage()
    canvas.save()


def build_fr_table(path: Path, _: Path) -> None:
    canvas = _canvas(path)
    for number, rows in enumerate([
        [["MATRICE DES LIMITES — PREMIÈRE PAGE", "", ""], ["Module", "Limite", "Observation"],
         ["ALPHA", "-5 °C", "Ne pas ouvrir"], ["BETA", "1,5 bar", ""]],
        [["MATRICE DES LIMITES — SUITE", "", ""], ["Module", "Limite", "Observation"],
         ["GAMMA", "+5 °C", "Après consignation"], ["DELTA", "15 bar", "Contrôle requis"]],
    ], 1):
        _lines(canvas, [f"TABLEAU FRANÇAIS — PAGE {number}"])
        table = Table(rows, colWidths=[175, 110, 210], rowHeights=[45, 40, 55, 55])
        table.setStyle(TableStyle([("FONTNAME", (0, 0), (-1, -1), FONT_NAME),
            ("FONTSIZE", (0, 0), (-1, -1), 11), ("SPAN", (0, 0), (2, 0)),
            ("GRID", (0, 0), (-1, -1), 1, colors.black), ("VALIGN", (0, 0), (-1, -1), "MIDDLE")]))
        table.wrapOn(canvas, WIDTH, HEIGHT)
        table.drawOn(canvas, 40, 480)
        _lines(canvas, ["Vérifier chaque cellule avec son module et sa page avant de reporter les valeurs.",
                        "Une cellule vide ne signifie pas que la valeur de la ligne voisine s'applique."], y=420)
        canvas.showPage()
    canvas.save()


def build_fr_scan(path: Path, workdir: Path) -> None:
    canvas = _canvas(path)
    image = workdir / "fr-full-scan.png"
    _raster(image, ["CONTRÔLE FRANÇAIS SCANNÉ", "Préparation : 40 minutes", "Pour 4 opérateurs",
                    "Pression maximale : 1,5 bar.", "Ne pas ouvrir sous tension.",
                    "Minimum : -5 °C ; maximum : +5 °C."])
    canvas.drawImage(ImageReader(str(image)), 0, 0, WIDTH, HEIGHT)
    canvas.showPage()
    canvas.save()


BUILDERS = {"fr_native_critical": build_fr_native, "fr_mixed_critical": build_fr_mixed,
            "fr_table_critical": build_fr_table, "fr_scan_critical": build_fr_scan}
