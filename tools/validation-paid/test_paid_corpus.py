"""Offline transport guards. Synthetic projections do not validate ingestion.

Optional native CLI cases execute only an explicitly supplied precompiled helper;
they never build, restore, start services or call a provider.
"""
import base64
import copy
import hashlib
import importlib.util
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("paid_corpus", pathlib.Path(__file__).with_name("prepare_real_corpus.py"))
corpus = importlib.util.module_from_spec(spec)
spec.loader.exec_module(corpus)
BACKEND = "a" * 64
SOURCE = b"unchanged source bytes"


def digest(text):
    return base64.b64encode(hashlib.sha256(text.encode()).digest()).decode()


def projection(pages=None, filename="document.pdf"):
    """Transport fixture only; these objects are not production projection output."""
    pages = pages if pages is not None else [(3, "Une vraie page d'origine.")]
    result = {"schemaVersion": "saaia-native-paid-corpus-projection.v1", "sourceSha256": hashlib.sha256(SOURCE).hexdigest(),
              "backendAssemblySha256": BACKEND, "docPath": "Corpus/" + filename, "sourcePageCount": 36,
              "projection": "production_native_pdf_selected_pages", "chunking": {"chunkMaxWords": 200, "chunkOverlapWords": 0, "chunkMinWords": 25},
              "boundaries": {"ingestionWorkerExecuted": False, "vectorsIndexed": False},
              "pages": [], "sections": [], "units": [], "retrievalChunks": [], "exactEntries": [], "contextualEntries": []}
    for ordinal, (page, text) in enumerate(pages):
        base = {"pageStart": page, "pageEnd": page, "text": text, "charCount": len(text), "tokenCount": 7, "checksum": digest(text)}
        result["pages"].append({"pageNumber": page, "text": text, "charCount": len(text), "checksum": digest(text)})
        result["sections"].append({"ordinal": ordinal, "title": "Titre de transport", "level": 1, "pageStart": page, "pageEnd": page, "startLine": 0, "endLine": 2})
        result["units"].append(dict(base, ordinal=ordinal, sectionOrdinal=ordinal))
        result["retrievalChunks"].append(dict(base, chunkIndex=ordinal, sectionOrdinal=ordinal, unitOrdinal=ordinal,
                                            sourceUnitOrdinals=[ordinal], chunkType="production_test_transport"))
        # Deliberately unequal to text.lower(): serialization must preserve the
        # extractor's value, not recompute its normalization/candidates.
        normalized = "valeur normalisee fournie par extracteur"
        result["exactEntries"].append(dict(base, entryIndex=ordinal, sectionOrdinal=ordinal, unitOrdinal=ordinal,
                                         normalizedText=normalized, checksum=digest(normalized), kind="verbatim_excerpt"))
        context = text + "\nsource_metadata:\ndocument_name: " + filename
        result["contextualEntries"].append(dict(base, entryIndex=ordinal, sectionOrdinal=ordinal, unitOrdinal=ordinal,
                                              chunkIndex=ordinal, text=context, charCount=len(context), checksum=digest(context)))
    return result


class RealCorpusTransportContracts(unittest.TestCase):
    def seed(self, value=None, **kwargs):
        return corpus.canonical_seed(kwargs.get("run_id", "1" * 32), kwargs.get("filename", "document.pdf"),
                                     SOURCE, value if value is not None else projection(), kwargs.get("backend", BACKEND))

    def test_original_hash_full_page_count_and_selected_pages_preserved(self):
        sql, manifest = self.seed(projection([(3, "Premier extrait"), (7, "Second extrait")]))
        self.assertEqual(hashlib.sha256(SOURCE).hexdigest(), manifest["sourceSha256"])
        self.assertEqual([3, 7], [p["pageNumber"] for p in manifest["pages"]])
        self.assertIn("'application/pdf',36,", sql)
        self.assertFalse(manifest["ingestionValidated"])
        self.assertTrue(manifest["tokenCountsFromProduction"])
        self.assertTrue(manifest["tokenCountsAreEstimates"])
        self.assertEqual("production_whitespace_word_counts_not_model_tokenizer", manifest["tokenCountSemantics"])
        self.assertTrue(manifest["vectorIndexIsProtocolSubstitute"])

    def test_extractor_candidates_context_and_token_counts_are_transported_not_recomputed(self):
        p = projection()
        sql, manifest = self.seed(p)
        self.assertIn(corpus.quote(p["exactEntries"][0]["normalizedText"]), sql)
        self.assertIn(corpus.quote(p["contextualEntries"][0]["text"]), sql)
        self.assertIn('"sourceUnitOrdinals":[0]', sql)
        self.assertIn(',7,decode(', sql)
        self.assertEqual(1, manifest["projectionCounts"]["exactEntries"])

    def test_source_text_and_quotes_are_not_replaced_with_an_expected_answer(self):
        text = "L'auteur dit : 'un fait'.\\ Pas d'interpretation ajoutee."
        sql, manifest = self.seed(projection([(2, text)]))
        self.assertIn(corpus.quote(text), sql)
        self.assertEqual(hashlib.sha256(text.encode()).hexdigest(), manifest["pages"][0]["textSha256"])

    def test_unique_identity_per_owned_run(self):
        _, a = self.seed()
        _, b = self.seed(run_id="2" * 32)
        self.assertNotEqual(a["docId"], b["docId"])

    def test_frozen_source_backend_path_and_projection_schema_are_enforced(self):
        for key, value in (("sourceSha256", "0" * 64), ("backendAssemblySha256", "0" * 64),
                           ("docPath", "Other/document.pdf"), ("schemaVersion", "old-page-only-seed")):
            p = projection()
            p[key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.seed(p)

    def test_modified_projected_text_or_checksum_is_rejected(self):
        for group, key in (("pages", "text"), ("units", "text"), ("retrievalChunks", "text"),
                           ("exactEntries", "normalizedText"), ("contextualEntries", "text")):
            p = projection()
            p[group][0][key] += " modified"
            with self.subTest(group=group), self.assertRaises(ValueError):
                self.seed(p)

    def test_duplicate_empty_oversized_invalid_and_nul_pages_are_rejected(self):
        for pages in ([], [(1, "a"), (1, "b")], [(1, " ")], [(0, "a")], [(1, "x" * 30001)], [(1, "a\0b")]):
            with self.subTest(length=len(pages)), self.assertRaises(ValueError):
                self.seed(projection(pages))

    def test_missing_context_duplicate_ordinals_and_dangling_links_are_rejected(self):
        cases = []
        p = projection(); p["contextualEntries"] = []; cases.append(p)
        p = projection(); p["exactEntries"].append(copy.deepcopy(p["exactEntries"][0])); cases.append(p)
        p = projection(); p["retrievalChunks"][0]["unitOrdinal"] = 99; cases.append(p)
        p = projection(); p["retrievalChunks"][0]["sourceUnitOrdinals"] = [99]; cases.append(p)
        for p in cases:
            with self.subTest(), self.assertRaises(ValueError):
                self.seed(p)

    def test_path_or_owner_injection_is_rejected(self):
        for name in ("../other.pdf", "subdir/other.pdf", "subdir\\other.pdf", "document.sql"):
            with self.subTest(name=name), self.assertRaises(ValueError):
                self.seed(filename=name)
        with self.assertRaises(ValueError):
            self.seed(run_id="unowned")


HELPER = os.environ.get("SAAIA_PAID_PROJECTION_DLL", "")
NATIVE_READY = bool(HELPER and pathlib.Path(HELPER).is_absolute() and pathlib.Path(HELPER).is_file()
                    and importlib.util.find_spec("pypdf"))


@unittest.skipUnless(NATIVE_READY, "Native CLI validation requires explicit precompiled SAAIA_PAID_PROJECTION_DLL and local pypdf only for generating the test PDF")
class NativePdfCliContracts(unittest.TestCase):
    def fixture(self, directory, text=True):
        from pypdf import PdfWriter
        from pypdf.generic import DecodedStreamObject, DictionaryObject, NameObject
        root = pathlib.Path(directory).resolve()
        output = root / "owned"
        docs = output / "runtime" / "documents"
        docs.mkdir(parents=True)
        (output / "environment.json").write_text(json.dumps({"outputDirectory": str(output), "documentsRoot": str(docs), "runId": "1" * 32}), encoding="utf-8")
        writer = PdfWriter()
        for number in range(1, 4):
            page = writer.add_blank_page(width=595, height=842)
            if text:
                font = writer._add_object(DictionaryObject({NameObject("/Type"): NameObject("/Font"), NameObject("/Subtype"): NameObject("/Type1"), NameObject("/BaseFont"): NameObject("/Helvetica")}))
                page[NameObject("/Resources")] = DictionaryObject({NameObject("/Font"): DictionaryObject({NameObject("/F1"): font})})
                stream = DecodedStreamObject()
                content = f"Actual native PDF fixture page {number}. " + "A real paragraph with enough ordinary words to remain searchable in a small native fixture and preserve the source text without adding any expected answer to its canonical projection."
                stream.set_data(("BT /F1 12 Tf 20 750 Td (" + content + ") Tj ET").encode("ascii"))
                page[NameObject("/Contents")] = writer._add_object(stream)
        pdf = root / "original.pdf"
        with pdf.open("wb") as target:
            writer.write(target)
        return output, pdf

    def invoke(self, output, pdf, digest=None, backend=None):
        backend = backend or hashlib.sha256(pathlib.Path(HELPER).with_name("SAAIA.Backend.dll").read_bytes()).hexdigest()
        return subprocess.run([sys.executable, "-B", str(pathlib.Path(corpus.__file__)), "--output", str(output), "--pdf", str(pdf),
            "--sha256", digest or hashlib.sha256(pdf.read_bytes()).hexdigest(), "--pages", "2",
            "--projection-dll", HELPER, "--backend-sha256", backend], capture_output=True, text=True, timeout=100)

    def test_native_extraction_uses_original_bytes_and_physical_page(self):
        with tempfile.TemporaryDirectory(prefix="saaia-native-offline-") as directory:
            output, pdf = self.fixture(directory)
            before = pdf.read_bytes()
            result = self.invoke(output, pdf)
            self.assertEqual(0, result.returncode, result.stderr)
            manifest = json.loads((output / "real-corpus-manifest.json").read_text())
            self.assertEqual(3, manifest["sourcePageCount"])
            self.assertEqual(2, manifest["pages"][0]["pageNumber"])
            self.assertTrue(manifest["productionProjectionReused"])
            self.assertEqual(before, (output / "runtime" / "documents" / manifest["docPath"]).read_bytes())
            # Inspect actual written bytes: text-mode I/O on Windows can turn
            # the extractor's CRLF into CRCRLF while leaving its checksum intact.
            # In-memory canonical_seed checks alone cannot detect that mutation.
            native = json.loads((output / "runtime" / "native-corpus-projection.private.json").read_bytes().decode("utf-8-sig"))
            seed_bytes = (output / "runtime" / "seed-real-corpus.sql").read_bytes()
            for group, field in (("units", "text"), ("retrievalChunks", "text"),
                                 ("exactEntries", "normalizedText"), ("contextualEntries", "text")):
                for item in native[group]:
                    self.assertTrue(corpus.quote(item[field]).encode("utf-8") in seed_bytes,
                                    "Written SQL altered exact production text in " + group)

    def test_pdf_or_backend_changed_after_freeze_has_no_seed(self):
        for kind in ("source", "backend"):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory(prefix="saaia-native-offline-") as directory:
                output, pdf = self.fixture(directory)
                result = self.invoke(output, pdf, digest="0" * 64 if kind == "source" else None,
                                     backend="0" * 64 if kind == "backend" else None)
                self.assertNotEqual(0, result.returncode)
                self.assertFalse((output / "runtime" / "seed-real-corpus.sql").exists())
                self.assertFalse((output / "runtime" / "native-corpus-projection.private.json").exists())

    def test_blank_or_ocr_only_page_fails_without_inventing_text(self):
        with tempfile.TemporaryDirectory(prefix="saaia-native-offline-") as directory:
            output, pdf = self.fixture(directory, text=False)
            self.assertNotEqual(0, self.invoke(output, pdf).returncode)
            self.assertFalse((output / "runtime" / "seed-real-corpus.sql").exists())


if __name__ == "__main__":
    unittest.main()
