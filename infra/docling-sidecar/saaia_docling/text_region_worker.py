"""Private, disposable OCR child. Importing this module never loads an OCR model."""
from __future__ import annotations

from hashlib import sha256
from importlib.metadata import version
import json
from pathlib import Path
import os
import subprocess
import sys
import time


def _hash(path: Path) -> str:
    digest = sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1_048_576), b""):
            digest.update(chunk)
    return digest.hexdigest()


def rapid_model_paths(package_directory: Path) -> dict[str, Path]:
    paths = {
        "Det.model_path": package_directory / "models" / "PP-OCRv6_det_small.onnx",
        "Rec.model_path": package_directory / "models" / "PP-OCRv6_rec_small.onnx",
        "Cls.model_path": package_directory / "models" / "ch_ppocr_mobile_v2.0_cls_mobile.onnx",
    }
    if any(not path.is_file() or path.stat().st_size == 0 for path in paths.values()):
        raise ValueError("Bundled regional OCR models are unavailable.")
    return paths


def validate_rapid_configuration(config, paths: dict[str, Path]) -> None:
    """Fail before model initialization if RapidOCR ignored any explicit local setting."""
    engine = config["EngineConfig"]["onnxruntime"]
    if (type(engine["intra_op_num_threads"]) is not int or engine["intra_op_num_threads"] != 2
            or type(engine["inter_op_num_threads"]) is not int or engine["inter_op_num_threads"] != 1):
        raise ValueError("RapidOCR thread configuration was not applied.")
    for key, expected in paths.items():
        role, field = key.split(".")
        if not config[role][field] or Path(config[role][field]).resolve() != expected.resolve():
            raise ValueError("RapidOCR local model configuration was not applied.")


def observe_rapid_sessions(rapid) -> dict[str, object]:
    """Attest actual ONNX sessions, not only requested configuration keys."""
    for role in ("text_det", "text_cls", "text_rec"):
        session = getattr(rapid, role).session.session
        effective = session.get_session_options()
        if (effective.intra_op_num_threads != 2 or effective.inter_op_num_threads != 1
                or session.get_providers() != ["CPUExecutionProvider"]):
            raise ValueError("RapidOCR actual session configuration is incompatible.")
    return {"onnxruntime_intra_op_num_threads": 2, "onnxruntime_inter_op_num_threads": 1,
            "observed_session_count": 3, "providers": ["CPUExecutionProvider"]}


def _run(request: dict, directory: Path) -> dict:
    started = time.monotonic()
    deadline = started + min(float(request["timeout_seconds"]), 14.0)

    def remaining() -> float:
        value = deadline - time.monotonic()
        if value <= 0:
            raise TimeoutError()
        return value

    # Runtime imports stay in the isolated process, not the pure policy/tests.
    import pypdfium2 as pdfium
    import rapidocr
    from rapidocr import RapidOCR
    from .pdf_regions import render_table_region
    from .response_text_repair import validate_source_region, validate_physical_page_size
    from .text_repair import TextOcrObservation, _observation_proof
    from .text_repair_options import TextRepairOptions
    from .table_repair_options import TableRepairOptions

    operation = request.get("operation", "text_consensus")
    if operation not in {"text_consensus", "table_rapid_only"}:
        raise ValueError("Regional OCR operation invalid.")
    options = (TableRepairOptions if operation == "table_rapid_only" else TextRepairOptions)(**request["options"])
    document, item = request["document"], request["item"]
    page_height = validate_source_region(document, item, options,
        reference_kind="tables" if operation == "table_rapid_only" else "texts")
    page_no = item["prov"][0]["page_no"]
    # Check physical PDF dimensions before the existing full-page renderer allocates.
    pdf = pdfium.PdfDocument(request["pdf_path"])
    try:
        page = pdf[page_no - 1]
        try:
            width, height = page.get_size()
            declared_width = float(document["pages"][str(page_no)]["size"]["width"])
            validate_physical_page_size(width, height, declared_width, page_height, options)
        finally:
            page.close()
    finally:
        pdf.close()
    remaining()
    paths = rapid_model_paths(Path(rapidocr.__file__).resolve().parent)
    artifacts = {path.name: _hash(path) for path in paths.values()}
    # Absolute existing paths prevent RapidOCR's implicit model download fallback.
    class LocalRapidOCR(RapidOCR):
        def _initialize(self, config):
            validate_rapid_configuration(config, paths)
            super()._initialize(config)

    rapid = LocalRapidOCR(params={**{key: str(path) for key, path in paths.items()},
        "EngineConfig.onnxruntime.intra_op_num_threads": 2,
        "EngineConfig.onnxruntime.inter_op_num_threads": 1})
    runtime_settings = observe_rapid_sessions(rapid)
    remaining()
    region = render_table_region(request["pdf_path"], document, item,
                                 scale=options.render_scale, padding_points=options.padding_points)
    try:
        rapid_started = time.monotonic()
        raw = rapid(region.image, use_cls=False, return_word_box=True)
        rapid_lines = tuple(region.to_page_lines(
            raw.boxes if raw.boxes is not None else (), raw.txts if raw.txts is not None else (),
            raw.scores if raw.scores is not None else ()))
        rapid_observation = TextOcrObservation("rapidocr", version("rapidocr"),
            "PP-OCRv6_det_small+PP-OCRv6_rec_small", rapid_lines,
            round((time.monotonic() - rapid_started) * 1_000), model_artifacts=artifacts,
            runtime_settings=runtime_settings)
        remaining()
        if operation == "table_rapid_only":
            return {"status": "success", "rapid": _observation_proof(rapid_observation)}
        from .secondary_ocr import parse_tesseract_tsv, resolve_tesseract_model, validate_tesseract_languages
        image_path = directory / "region.png"
        region.image.save(image_path, format="PNG")
        environment = {**os.environ, "LC_ALL": "C", "OMP_THREAD_LIMIT": "1"}
        binary = "/usr/bin/tesseract"
        traineddata = resolve_tesseract_model(environment)
        tessdata_arguments = ["--tessdata-dir", str(traineddata.parent)]
        version_result = subprocess.run([binary, "--version"], capture_output=True, text=True,
            encoding="utf-8", errors="strict", check=True, timeout=remaining(), env=environment)
        engine_version = version_result.stdout.splitlines()[0].strip()
        if not engine_version.startswith("tesseract ") or len(engine_version) > 128:
            raise ValueError("Tesseract engine version is unavailable.")
        languages = subprocess.run([binary, *tessdata_arguments, "--list-langs"], capture_output=True, text=True,
            encoding="utf-8", errors="strict", check=True, timeout=remaining(), env=environment)
        validate_tesseract_languages(languages.stdout)
        traineddata_hash = _hash(traineddata)
        secondary_started = time.monotonic()
        secondary = subprocess.run([binary, str(image_path), "stdout", *tessdata_arguments, "-l", "eng", "--psm", "6", "tsv"],
            capture_output=True, text=True, encoding="utf-8", errors="strict", check=True,
            timeout=remaining(), env=environment)
        if len(secondary.stdout) > 2_000_000:
            raise ValueError("Tesseract result exceeds the region budget.")
        secondary_observation = TextOcrObservation("tesseract", engine_version, "eng:psm6",
            parse_tesseract_tsv(secondary.stdout, region),
            round((time.monotonic() - secondary_started) * 1_000), model_sha256=traineddata_hash,
            model_artifacts={"eng.traineddata": traineddata_hash})
        remaining()
        return {"status": "success", "rapid": _observation_proof(rapid_observation),
                "secondary": _observation_proof(secondary_observation)}
    finally:
        region.image.close()


def main() -> int:
    if len(sys.argv) != 3:
        return 2
    request_path, output_path = (Path(value) for value in sys.argv[1:])
    try:
        request = json.loads(request_path.read_text(encoding="utf-8"))
        result = _run(request, request_path.parent)
    except Exception as exc:
        result = {"status": "error", "error_type": type(exc).__name__}
    with output_path.open("x", encoding="utf-8", newline="\n") as output:
        json.dump(result, output, ensure_ascii=False, allow_nan=False)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
