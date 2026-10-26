from __future__ import annotations

from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest

from saaia_docling.secondary_ocr import parse_tesseract_tsv, resolve_tesseract_model, validate_tesseract_languages
from saaia_docling.table_repair import OcrLine
from saaia_docling.text_region_worker import rapid_model_paths, validate_rapid_configuration, observe_rapid_sessions


HEADER = "level\tleft\ttop\twidth\theight\tconf\ttext\n"


class FakeRegion:
    def to_page_lines(self, boxes, texts, scores):
        return [OcrLine(text, score, tuple(box)) for box, text, score in zip(boxes, texts, scores)]


class SecondaryOcrTests(unittest.TestCase):
    def test_ignored_thread_or_local_model_parameters_are_refused_before_initialization(self):
        paths = {"Det.model_path": Path("det.onnx"), "Cls.model_path": Path("cls.onnx"), "Rec.model_path": Path("rec.onnx")}
        config = {"EngineConfig": {"onnxruntime": {"intra_op_num_threads": 2, "inter_op_num_threads": 1}},
            **{role: {"model_path": str(paths[role + ".model_path"])} for role in ("Det", "Cls", "Rec")}}
        validate_rapid_configuration(config, paths)
        config["EngineConfig"]["onnxruntime"]["intra_op_num_threads"] = -1
        with self.assertRaises(ValueError):
            validate_rapid_configuration(config, paths)
        config["EngineConfig"]["onnxruntime"]["intra_op_num_threads"] = 2
        config["Rec"]["model_path"] = None
        with self.assertRaises(ValueError):
            validate_rapid_configuration(config, paths)

    def test_actual_sessions_must_confirm_cpu_and_bounded_threads_not_only_requested_config(self):
        session = SimpleNamespace(get_session_options=lambda: SimpleNamespace(intra_op_num_threads=2, inter_op_num_threads=1),
                                  get_providers=lambda: ["CPUExecutionProvider"])
        engine = SimpleNamespace(**{role: SimpleNamespace(session=SimpleNamespace(session=session))
                                   for role in ("text_det", "text_cls", "text_rec")})
        self.assertEqual(3, observe_rapid_sessions(engine)["observed_session_count"])
        session.get_session_options = lambda: SimpleNamespace(intra_op_num_threads=0, inter_op_num_threads=1)
        with self.assertRaises(ValueError):
            observe_rapid_sessions(engine)
        session.get_session_options = lambda: SimpleNamespace(intra_op_num_threads=2, inter_op_num_threads=1)
        session.get_providers = lambda: ["CUDAExecutionProvider", "CPUExecutionProvider"]
        with self.assertRaises(ValueError):
            observe_rapid_sessions(engine)
    def test_actual_tesseract_4_language_header_and_newer_path_header_are_supported(self):
        for stdout in ('List of available languages (2):\neng\nosd\n',
                       'List of available languages in "/synthetic/model/path/" (2):\neng\nosd\n'):
            validate_tesseract_languages(stdout)
        for stdout in ('eng\nosd\n', 'List of available languages (1):\nosd\n',
                       'List of available languages (2):\neng\neng\n'):
            with self.subTest(stdout=stdout), self.assertRaises(ValueError):
                validate_tesseract_languages(stdout)

    def test_unique_local_model_is_selected_and_ambiguous_or_missing_packs_are_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            first, second = root / "one", root / "two"
            first.mkdir()
            second.mkdir()
            with self.assertRaises(ValueError):
                resolve_tesseract_model({}, standard_directories=(first, second))
            (first / "eng.traineddata").write_bytes(b"synthetic-engine-data")
            self.assertEqual((first / "eng.traineddata").resolve(), resolve_tesseract_model({}, standard_directories=(first, second)))
            (second / "eng.traineddata").write_bytes(b"different-synthetic-engine-data")
            with self.assertRaises(ValueError):
                resolve_tesseract_model({}, standard_directories=(first, second))
            self.assertEqual((second / "eng.traineddata").resolve(), resolve_tesseract_model(
                {"SAAIA_DOCLING_TEXT_REPAIR_TESSDATA_DIR": str(second)}, standard_directories=(first, second)))
            with self.assertRaises(ValueError):
                resolve_tesseract_model({"TESSDATA_PREFIX": "relative/path"})

    def test_tsv_keeps_each_actual_word_confidence_in_source_order(self):
        rows = HEADER + "1\t0\t0\t100\t100\t-1\t\n5\t2\t3\t12\t10\t99.5\tNever\n5\t17\t3\t16\t10\t95\t-20,5\n"
        result = parse_tesseract_tsv(rows, FakeRegion())
        self.assertEqual(["Never", "-20,5"], [line.text for line in result])
        self.assertEqual([0.995, 0.95], [line.score for line in result])
        self.assertEqual((2, 3), result[0].polygon[0])

    def test_invalid_tsv_confidence_schema_or_geometry_is_not_silently_skipped(self):
        for rows in ("text\nHello\n", HEADER + "5\t2\t3\t12\t10\tnan\tNever\n",
                     HEADER + "5\t2\t3\t0\t10\t99\tNever\n", HEADER + "5\t2\t3\t12\t10\t101\tNever\n"):
            with self.subTest(rows=rows), self.assertRaises(ValueError):
                parse_tesseract_tsv(rows, FakeRegion())

    def test_missing_local_model_refuses_before_any_model_initialization_or_download(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with self.assertRaises(ValueError):
                rapid_model_paths(root)
            (root / "models").mkdir()
            for name in ("PP-OCRv6_det_small.onnx", "PP-OCRv6_rec_small.onnx", "ch_ppocr_mobile_v2.0_cls_mobile.onnx"):
                (root / "models" / name).write_bytes(b"offline-test-placeholder")
            paths = rapid_model_paths(root)
            self.assertEqual({"Det.model_path", "Rec.model_path", "Cls.model_path"}, set(paths))
            self.assertTrue(all(path.is_file() for path in paths.values()))


if __name__ == "__main__":
    unittest.main()
