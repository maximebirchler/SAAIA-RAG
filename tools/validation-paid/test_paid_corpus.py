"""Offline contracts for real-page seeding; no PDF library or service required."""
import hashlib
import importlib.util
import json
import pathlib
import subprocess
import sys
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("paid_corpus", pathlib.Path(__file__).with_name("prepare_real_corpus.py"))
corpus = importlib.util.module_from_spec(spec)
spec.loader.exec_module(corpus)


class RealCorpusContracts(unittest.TestCase):
    def seed(self, pages=None, **kwargs):
        return corpus.canonical_seed(kwargs.get("run_id", "1" * 32), kwargs.get("filename", "document.pdf"),
                                     b"unchanged source bytes", pages or [(3, "Une vraie page d'origine.")])

    def test_original_bytes_and_page_identity_are_preserved(self):
        sql, manifest = self.seed([(3, "Premier extrait"), (7, "Second extrait")])
        self.assertEqual(hashlib.sha256(b"unchanged source bytes").hexdigest(), manifest["sourceSha256"])
        self.assertEqual([3, 7], [p["pageNumber"] for p in manifest["pages"]])
        self.assertIn("'application/pdf',7,", sql)
        self.assertFalse(manifest["ingestionValidated"])
        self.assertTrue(manifest["vectorIndexIsProtocolSubstitute"])

    def test_source_text_is_not_replaced_with_an_expected_answer(self):
        text = "L'auteur dit : 'un fait'.\\ Ne pas ajouter une interprétation."
        sql, manifest = self.seed([(2, text)])
        self.assertIn(corpus.quote(text), sql)
        self.assertEqual(hashlib.sha256(text.encode()).hexdigest(), manifest["pages"][0]["textSha256"])

    def test_names_and_scope_are_unique_per_run(self):
        _, a = self.seed()
        _, b = self.seed(run_id="2" * 32)
        self.assertNotEqual(a["docId"], b["docId"])
        self.assertNotEqual(a["revisionId"], b["revisionId"])

    def test_duplicate_empty_oversized_and_invalid_pages_are_rejected(self):
        for pages in ([(1, "a"), (1, "b")], [(1, " ")], [(0, "a")], [(1, "x" * 30001)], [(1, "a\0b")]):
            with self.subTest(pages=str(pages)[:30]), self.assertRaises(ValueError):
                self.seed(pages)

    def test_path_injection_is_rejected(self):
        for name in ("../other.pdf", "subdir/other.pdf", "document.sql"):
            with self.subTest(name=name), self.assertRaises(ValueError):
                self.seed(filename=name)

    def test_invalid_owner_cannot_generate_a_seed(self):
        with self.assertRaises(ValueError):
            self.seed(run_id="unowned")

    def test_full_pdf_page_count_is_preserved_for_a_partial_extraction(self):
        _, manifest = corpus.canonical_seed("1" * 32, "original.pdf", b"original", [(5, "real text")], source_page_count=36)
        self.assertEqual(36, manifest["sourcePageCount"])
        self.assertEqual(1, manifest["extractedPageCount"])


@unittest.skipUnless(importlib.util.find_spec("pypdf"), "Requires the explicitly selected local pypdf runtime; never downloads packages")
class RealPdfCliContracts(unittest.TestCase):
    def fixture(self, directory, text=True):
        from pypdf import PdfWriter
        from pypdf.generic import DecodedStreamObject, DictionaryObject, NameObject
        root = pathlib.Path(directory).resolve()
        output = root / "owned"
        docs = output / "runtime" / "documents"
        docs.mkdir(parents=True)
        (output / "environment.json").write_text(json.dumps({"outputDirectory": str(output), "documentsRoot": str(docs), "runId": "1" * 32}), encoding="utf-8")
        writer = PdfWriter()
        for _ in range(3):
            page = writer.add_blank_page(width=595, height=842)
            if text:
                font = writer._add_object(DictionaryObject({NameObject("/Type"): NameObject("/Font"), NameObject("/Subtype"): NameObject("/Type1"), NameObject("/BaseFont"): NameObject("/Helvetica")}))
                page[NameObject("/Resources")] = DictionaryObject({NameObject("/Font"): DictionaryObject({NameObject("/F1"): font})})
                stream = DecodedStreamObject()
                stream.set_data(b"BT /F1 12 Tf 50 750 Td (Actual PDF text for offline extraction.) Tj ET")
                page[NameObject("/Contents")] = writer._add_object(stream)
        pdf = root / "original.pdf"
        with pdf.open("wb") as target:
            writer.write(target)
        return output, pdf

    def invoke(self, output, pdf, digest=None):
        return subprocess.run([sys.executable, "-B", str(pathlib.Path(corpus.__file__)), "--output", str(output), "--pdf", str(pdf),
            "--sha256", digest or hashlib.sha256(pdf.read_bytes()).hexdigest(), "--pages", "2"], capture_output=True, text=True, timeout=20)

    def test_actual_pdf_cli_preserves_original_bytes_and_page_number(self):
        with tempfile.TemporaryDirectory(prefix="saaia-paid-offline-") as directory:
            output, pdf = self.fixture(directory)
            before = pdf.read_bytes()
            result = self.invoke(output, pdf)
            self.assertEqual(0, result.returncode, result.stderr)
            manifest = json.loads((output / "real-corpus-manifest.json").read_text())
            self.assertEqual(3, manifest["sourcePageCount"])
            self.assertEqual(2, manifest["pages"][0]["pageNumber"])
            self.assertEqual(before, (output / "runtime" / "documents" / manifest["docPath"]).read_bytes())
            self.assertIn("Actual PDF text", (output / "runtime" / "seed-real-corpus.sql").read_text())

    def test_pdf_changed_after_freeze_has_no_seed_side_effects(self):
        with tempfile.TemporaryDirectory(prefix="saaia-paid-offline-") as directory:
            output, pdf = self.fixture(directory)
            self.assertNotEqual(0, self.invoke(output, pdf, "0" * 64).returncode)
            self.assertFalse((output / "runtime" / "seed-real-corpus.sql").exists())

    def test_blank_or_ocr_only_page_fails_without_inventing_text(self):
        with tempfile.TemporaryDirectory(prefix="saaia-paid-offline-") as directory:
            output, pdf = self.fixture(directory, text=False)
            self.assertNotEqual(0, self.invoke(output, pdf).returncode)
            self.assertFalse((output / "runtime" / "seed-real-corpus.sql").exists())


if __name__ == "__main__":
    unittest.main()
