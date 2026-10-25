"""Serialize production native projections into an owned canonical SQL store.

The precompiled IngestionGold helper supplies real production extraction,
sections, units, chunks, exact candidates and contextual text. Python validates
identity/links and transports rows. This is not worker ingestion, OCR, Docling,
document profile generation, embedding or semantic vector validation.
"""
import argparse
import base64
import hashlib
import json
import pathlib
import re
import subprocess
import uuid


def quote(value):
    return "NULL" if value is None else "'" + str(value).replace("'", "''") + "'"


def number(value):
    return "NULL" if value is None else str(int(value))


def metadata(item, fields, **extra):
    value = {key: item.get(key) for key in fields.split()}
    value.update(extra)
    return quote(json.dumps(value, ensure_ascii=False, separators=(",", ":"))) + "::jsonb"


def checked_checksum(item, text_key="text"):
    text = item[text_key]
    if not isinstance(text, str) or "\0" in text:
        raise ValueError("Invalid production text")
    actual = hashlib.sha256(text.encode("utf-8")).digest()
    if base64.b64decode(item["checksum"], validate=True) != actual:
        raise ValueError("Production text checksum does not match its transport")
    return "decode(" + quote(actual.hex()) + ",'hex')"


def canonical_seed(run_id, filename, payload, projection, expected_backend_sha256):
    if not re.fullmatch(r"[a-f0-9]{32}", run_id):
        raise ValueError("Invalid owned run ID")
    if pathlib.PurePath(filename).name != filename or "\\" in filename or not filename.lower().endswith(".pdf"):
        raise ValueError("A plain PDF filename is required")
    path = "Corpus/" + filename
    source_hash = hashlib.sha256(payload).hexdigest()
    if (projection.get("schemaVersion") != "saaia-native-paid-corpus-projection.v1"
            or projection.get("sourceSha256", "").lower() != source_hash
            or projection.get("backendAssemblySha256", "").lower() != expected_backend_sha256.lower()
            or not re.fullmatch(r"[a-fA-F0-9]{64}", expected_backend_sha256)
            or projection.get("docPath") != path
            or projection.get("projection") != "production_native_pdf_selected_pages"):
        raise ValueError("Production projection/source/backend identity mismatch")
    pages = projection.get("pages", [])
    page_numbers = [p["pageNumber"] for p in pages]
    if (not pages or len(pages) > 8 or len(set(page_numbers)) != len(pages)
            or any(not isinstance(n, int) or n < 1 for n in page_numbers)
            or projection["sourcePageCount"] < max(page_numbers)
            or any(not p["text"].strip() or len(p["text"]) > 30000 for p in pages)
            or sum(len(p["text"]) for p in pages) > 120000):
        raise ValueError("Invalid, empty or oversized production pages")
    for page in pages:
        checked_checksum(page)
    groups = {name: projection.get(name, []) for name in
              ("sections", "units", "retrievalChunks", "exactEntries", "contextualEntries")}
    if not groups["units"] or not groups["retrievalChunks"] or len(groups["contextualEntries"]) != len(groups["retrievalChunks"]):
        raise ValueError("Empty or incomplete production retrieval projection")
    ordinal_key = {"sections": "ordinal", "units": "ordinal", "retrievalChunks": "chunkIndex",
                   "exactEntries": "entryIndex", "contextualEntries": "entryIndex"}
    for name, rows in groups.items():
        ordinals = [row[ordinal_key[name]] for row in rows]
        if len(set(ordinals)) != len(ordinals) or any(not isinstance(n, int) or n < 0 for n in ordinals):
            raise ValueError("Invalid production ordinals")
        for row in rows:
            if row["pageStart"] < min(page_numbers) or row["pageEnd"] > max(page_numbers) or row["pageEnd"] < row["pageStart"]:
                raise ValueError("Production row exceeds selected physical page scope")
    tenant = "77777777-7777-4777-8777-777777777777"
    ident = lambda suffix: str(uuid.uuid5(uuid.NAMESPACE_URL, f"saaia-paid:{run_id}:{path}:{suffix}"))
    doc, revision = ident("doc"), ident("revision")
    section_ids = {r["ordinal"]: ident(f"section:{r['ordinal']}") for r in groups["sections"]}
    unit_ids = {r["ordinal"]: ident(f"unit:{r['ordinal']}") for r in groups["units"]}
    chunk_ids = {r["chunkIndex"]: ident(f"chunk:{r['chunkIndex']}") for r in groups["retrievalChunks"]}

    def linked(mapping, value):
        if value is not None and value not in mapping:
            raise ValueError("Dangling production projection link")
        return quote(mapping.get(value))

    sql = ["BEGIN;", "SET LOCAL standard_conforming_strings=on;"]
    sql.append(f"INSERT INTO documents(tenant_id,doc_id,doc_path,doc_name,category,content_hash,file_size,mime_type,page_count,status,ingestion_version,indexed_version,last_ingested_at) VALUES('{tenant}','{doc}',{quote(path)},{quote(filename)},'Corpus',decode('{source_hash}','hex'),{len(payload)},'application/pdf',{projection['sourcePageCount']},'indexed',1,1,now());")
    sql.append(f"INSERT INTO document_revisions(revision_id,tenant_id,doc_id,doc_path,source_hash,source_size,ingestion_version,indexed_version) VALUES('{revision}','{tenant}','{doc}',{quote(path)},decode('{source_hash}','hex'),{len(payload)},1,1);")
    for r in groups["sections"]:
        sql.append(f"INSERT INTO document_sections(section_id,tenant_id,revision_id,ordinal,title,section_level,page_start,page_end,start_line,end_line) VALUES({quote(section_ids[r['ordinal']])},'{tenant}','{revision}',{r['ordinal']},{quote(r['title'])},{r['level']},{r['pageStart']},{r['pageEnd']},{number(r['startLine'])},{number(r['endLine'])});")
    quality = "offsetStart offsetEnd extractionTextStatus extractionTextSparse extractionOcrCandidate extractionQualitySignals"
    for r in groups["units"]:
        meta = metadata(r, quality + " sectionTitle headingPath headingLevel", inferred=True)
        sql.append(f"INSERT INTO document_units(unit_id,tenant_id,revision_id,section_id,ordinal,page_start,page_end,text_content,char_count,token_count,checksum,metadata) VALUES({quote(unit_ids[r['ordinal']])},'{tenant}','{revision}',{linked(section_ids,r['sectionOrdinal'])},{r['ordinal']},{r['pageStart']},{r['pageEnd']},{quote(r['text'])},{r['charCount']},{r['tokenCount']},{checked_checksum(r)},{meta});")
    chunk_meta = quality + " chunkType contentRole navigationReason originalChunkType navigationScore contentDensityScore sourceUnitOrdinals sourceUnitStartOrdinal sourceUnitEndOrdinal sourceUnitCount chunkComposition canonicalBlockIds canonicalSpanIds canonicalTableCellIds canonicalContextBlockIds sectionTitle headingPath headingLevel"
    for r in groups["retrievalChunks"]:
        if any(n not in unit_ids for n in (r.get("sourceUnitOrdinals") or [])):
            raise ValueError("Dangling source unit ordinal")
        meta = metadata(r, chunk_meta, inferred=True)
        sql.append(f"INSERT INTO retrieval_chunks(retrieval_chunk_id,tenant_id,revision_id,section_id,unit_id,chunk_index,page_start,page_end,text_content,token_count,checksum,metadata) VALUES({quote(chunk_ids[r['chunkIndex']])},'{tenant}','{revision}',{linked(section_ids,r['sectionOrdinal'])},{linked(unit_ids,r['unitOrdinal'])},{r['chunkIndex']},{r['pageStart']},{r['pageEnd']},{quote(r['text'])},{r['tokenCount']},{checked_checksum(r)},{meta});")
    for page in pages:
        sql.append(f"INSERT INTO document_page_index(tenant_id,revision_id,page_number,char_count,checksum) VALUES('{tenant}','{revision}',{page['pageNumber']},{page['charCount']},{checked_checksum(page)});")
    for r in groups["exactEntries"]:
        meta = metadata(r, quality + " kind", inferred=True)
        sql.append(f"INSERT INTO exact_match_entries(exact_match_entry_id,tenant_id,revision_id,section_id,unit_id,entry_index,page_start,page_end,text_content,normalized_text,char_count,token_count,checksum,metadata) VALUES({quote(ident('exact:'+str(r['entryIndex'])))},'{tenant}','{revision}',{linked(section_ids,r['sectionOrdinal'])},{linked(unit_ids,r['unitOrdinal'])},{r['entryIndex']},{r['pageStart']},{r['pageEnd']},{quote(r['text'])},{quote(r['normalizedText'])},{r['charCount']},{r['tokenCount']},{checked_checksum(r,'normalizedText')},{meta});")
    context_meta = "schemaVersion chunkIndex chunkType contentRole navigationReason navigationScore contentDensityScore sectionTitle headingPath sourceUnitOrdinals sourceUnitStartOrdinal sourceUnitEndOrdinal sourceUnitCount chunkComposition includesCurrentUnitContext includesPreviousContext includesNextContext"
    for r in groups["contextualEntries"]:
        meta = metadata(r, context_meta, inferred=True)
        sql.append(f"INSERT INTO contextual_text_entries(contextual_text_entry_id,tenant_id,revision_id,section_id,unit_id,retrieval_chunk_id,entry_index,page_start,page_end,text_content,char_count,token_count,checksum,metadata) VALUES({quote(ident('context:'+str(r['entryIndex'])))},'{tenant}','{revision}',{linked(section_ids,r['sectionOrdinal'])},{linked(unit_ids,r['unitOrdinal'])},{linked(chunk_ids,r['chunkIndex'])},{r['entryIndex']},{r['pageStart']},{r['pageEnd']},{quote(r['text'])},{r['charCount']},{r['tokenCount']},{checked_checksum(r)},{meta});")
    sql.append("COMMIT;")
    manifest_pages = [{"pageNumber": p["pageNumber"], "characters": p["charCount"],
                       "textSha256": hashlib.sha256(p["text"].encode("utf-8")).hexdigest(),
                       "chunkIds": [chunk_ids[c["chunkIndex"]] for c in groups["retrievalChunks"]
                                    if c["pageStart"] <= p["pageNumber"] <= c["pageEnd"]]} for p in pages]
    return "\n".join(sql), {"schemaVersion": "saaia-paid-real-corpus.v2", "docId": doc, "revisionId": revision,
        "docPath": path, "sourceSha256": source_hash, "sourceBytes": len(payload), "sourcePageCount": projection["sourcePageCount"],
        "extractedPageCount": len(pages), "pages": manifest_pages, "ingestionValidated": False,
        "extraction": "production PdfExtractor native PDF; selected pages use production structure/exact/contextual projection",
        "productionProjectionReused": True, "backendAssemblySha256": expected_backend_sha256,
        "projectionCounts": {name: len(rows) for name, rows in groups.items()}, "chunking": projection["chunking"],
        "projectionBoundaries": projection["boundaries"], "vectorIndexIsProtocolSubstitute": True,
        "tokenCountsFromProduction": True, "tokenCountsAreEstimates": True,
        "tokenCountSemantics": "production_whitespace_word_counts_not_model_tokenizer"}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--pdf", type=pathlib.Path, required=True)
    parser.add_argument("--sha256", required=True)
    parser.add_argument("--pages", required=True)
    parser.add_argument("--projection-dll", type=pathlib.Path, required=True)
    parser.add_argument("--backend-sha256", required=True)
    args = parser.parse_args()
    root = args.output.resolve()
    owner = json.loads((root / "environment.json").read_text(encoding="utf-8-sig"))
    expected_docs = root / "runtime" / "documents"
    if pathlib.Path(owner["outputDirectory"]).resolve() != root or pathlib.Path(owner["documentsRoot"]).resolve() != expected_docs:
        raise ValueError("Not the exact owned document root")
    payload = args.pdf.read_bytes()
    if hashlib.sha256(payload).hexdigest().lower() != args.sha256.lower():
        raise ValueError("Source PDF changed after profile freeze")
    if not args.projection_dll.is_absolute() or not args.projection_dll.is_file() or args.projection_dll.suffix.lower() != ".dll":
        raise ValueError("An explicit precompiled production projection DLL is required; no build/run fallback")
    projection_file = root / "runtime" / "native-corpus-projection.private.json"
    projection_stdout = root / "runtime" / "native-corpus-projection.stdout.private.log"
    projection_stderr = root / "runtime" / "native-corpus-projection.stderr.private.log"
    seed = root / "runtime" / "seed-real-corpus.sql"
    private = root / "runtime" / "real-corpus-pages.private.json"
    summary = root / "real-corpus-manifest.json"
    target = expected_docs / "Corpus" / args.pdf.name
    if any(p.exists() for p in (target, projection_file, projection_stdout, projection_stderr, seed, private, summary)):
        raise ValueError("Never overwrite corpus or prior evidence")
    # Already compiled offline helper only: never dotnet run/build/restore.
    completed = subprocess.run(["dotnet", str(args.projection_dll), "--native-paid-corpus", "--pdf", str(args.pdf.resolve()),
        "--output", str(projection_file), "--doc-path", "Corpus/" + args.pdf.name, "--source-sha256", args.sha256,
        "--backend-sha256", args.backend_sha256, "--pages", args.pages], capture_output=True, timeout=90)
    for path, content in ((projection_stdout, completed.stdout), (projection_stderr, completed.stderr)):
        with path.open("xb") as stream:
            stream.write(content)
    if completed.returncode != 0:
        raise ValueError("Production native corpus projection failed; no seed or paid job was created")
    projection = json.loads(projection_file.read_text(encoding="utf-8-sig"))
    requested = [int(n) for n in args.pages.split(",")]
    if sorted(p["pageNumber"] for p in projection["pages"]) != sorted(requested):
        raise ValueError("Production projection changed explicitly selected physical pages")
    sql, manifest = canonical_seed(owner["runId"], args.pdf.name, payload, projection, args.backend_sha256)
    manifest["projectionAssemblySha256"] = hashlib.sha256(args.projection_dll.read_bytes()).hexdigest()
    manifest["projectionSnapshotSha256"] = hashlib.sha256(projection_file.read_bytes()).hexdigest()
    target.parent.mkdir(parents=True, exist_ok=True)
    with target.open("xb") as stream:
        stream.write(payload)
    # The projected strings and their checksums are exact UTF-8 contracts.
    # Windows text-mode newline translation would turn existing CRLF inside
    # SQL literals into CRCRLF while keeping the original checksum column.
    with seed.open("xb") as stream:
        stream.write(sql.encode("utf-8"))
    with private.open("x", encoding="utf-8") as stream:
        json.dump([{"pageNumber": p["pageNumber"], "text": p["text"]} for p in projection["pages"]], stream, ensure_ascii=False, indent=2)
    with summary.open("x", encoding="utf-8") as stream:
        json.dump(manifest, stream, indent=2)
    print(json.dumps({"prepared": True, "pages": len(manifest["pages"]), "realPdf": True,
                      "productionProjectionReused": True, "ingestionValidated": False}))


if __name__ == "__main__":
    main()
