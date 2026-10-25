"""Seed real extracted PDF pages into a new owned canonical store.

This does not validate ingestion or vector retrieval. It never manufactures
answer text, recipe roles or expected claims. PDF bytes and extracted pages are
preserved so the paid payload can be checked against the actual source.
"""
import argparse
import hashlib
import json
import math
import pathlib
import re
import uuid


def quote(value):
    return "'" + str(value).replace("'", "''") + "'"


def canonical_seed(run_id, filename, payload, pages, source_page_count=None):
    if not re.fullmatch(r"[a-f0-9]{32}", run_id):
        raise ValueError("Invalid owned run ID")
    if pathlib.PurePath(filename).name != filename or not filename.lower().endswith(".pdf"):
        raise ValueError("A plain PDF filename is required")
    if not pages or len(pages) > 8 or len({n for n, _ in pages}) != len(pages):
        raise ValueError("One to eight distinct real pages are required")
    if any(n < 1 or not text.strip() or len(text) > 30000 or "\0" in text for n, text in pages):
        raise ValueError("Empty, oversized or NUL-containing pages are rejected, never silently truncated")
    if sum(len(text) for _, text in pages) > 120000:
        raise ValueError("Real corpus text exceeds this small campaign's bound")
    tenant = "77777777-7777-4777-8777-777777777777"
    path = "Corpus/" + filename
    source_hash = hashlib.sha256(payload).hexdigest()
    ident = lambda suffix: str(uuid.uuid5(uuid.NAMESPACE_URL, f"saaia-paid:{run_id}:{path}:{suffix}"))
    doc, revision = ident("doc"), ident("revision")
    max_page = max(n for n, _ in pages)
    total_pages = max_page if source_page_count is None else source_page_count
    if total_pages < max_page:
        raise ValueError('Original PDF page count cannot be smaller than a selected page')
    sql = ["BEGIN;", "SET LOCAL standard_conforming_strings=on;"]
    sql.append(f"INSERT INTO documents(tenant_id,doc_id,doc_path,doc_name,category,content_hash,file_size,mime_type,page_count,status,ingestion_version,indexed_version,last_ingested_at) VALUES('{tenant}','{doc}',{quote(path)},{quote(filename)},'Corpus',decode('{source_hash}','hex'),{len(payload)},'application/pdf',{total_pages},'indexed',1,1,now());")
    sql.append(f"INSERT INTO document_revisions(revision_id,tenant_id,doc_id,doc_path,source_hash,source_size,ingestion_version,indexed_version) VALUES('{revision}','{tenant}','{doc}',{quote(path)},decode('{source_hash}','hex'),{len(payload)},1,1);")
    manifest_pages = []
    for ordinal, (page, text) in enumerate(pages):
        checksum = hashlib.sha256(text.encode("utf-8")).hexdigest()
        section, unit, chunk, entry, context = [ident(f"{page}:{kind}") for kind in ("section", "unit", "chunk", "entry", "context")]
        tokens = max(1, math.ceil(len(text) / 4))
        sql.append(f"INSERT INTO document_sections(section_id,tenant_id,revision_id,ordinal,title,page_start,page_end) VALUES('{section}','{tenant}','{revision}',{ordinal},{quote('Page '+str(page))},{page},{page});")
        sql.append(f"INSERT INTO document_units(unit_id,tenant_id,revision_id,section_id,ordinal,page_start,page_end,text_content,char_count,token_count,checksum) VALUES('{unit}','{tenant}','{revision}','{section}',{ordinal},{page},{page},{quote(text)},{len(text)},{tokens},decode('{checksum}','hex'));")
        sql.append(f"INSERT INTO retrieval_chunks(retrieval_chunk_id,tenant_id,revision_id,section_id,unit_id,chunk_index,page_start,page_end,text_content,token_count,checksum) VALUES('{chunk}','{tenant}','{revision}','{section}','{unit}',{ordinal},{page},{page},{quote(text)},{tokens},decode('{checksum}','hex'));")
        sql.append(f"INSERT INTO document_page_index(tenant_id,revision_id,page_number,char_count,checksum) VALUES('{tenant}','{revision}',{page},{len(text)},decode('{checksum}','hex'));")
        sql.append(f"INSERT INTO exact_match_entries(exact_match_entry_id,tenant_id,revision_id,section_id,unit_id,entry_index,page_start,page_end,text_content,normalized_text,char_count,token_count,checksum) VALUES('{entry}','{tenant}','{revision}','{section}','{unit}',{ordinal},{page},{page},{quote(text)},{quote(text.lower())},{len(text)},{tokens},decode('{checksum}','hex'));")
        sql.append(f"INSERT INTO contextual_text_entries(contextual_text_entry_id,tenant_id,revision_id,section_id,unit_id,retrieval_chunk_id,entry_index,page_start,page_end,text_content,char_count,token_count,checksum) VALUES('{context}','{tenant}','{revision}','{section}','{unit}','{chunk}',{ordinal},{page},{page},{quote(text)},{len(text)},{tokens},decode('{checksum}','hex'));")
        manifest_pages.append({"pageNumber": page, "characters": len(text), "textSha256": checksum, "chunkId": chunk})
    sql.append("COMMIT;")
    return "\n".join(sql), {"schemaVersion": "saaia-paid-real-corpus.v1", "docId": doc, "revisionId": revision,
        "docPath": path, "sourceSha256": source_hash, "sourceBytes": len(payload), "sourcePageCount": total_pages,
        "extractedPageCount": len(pages), "pages": manifest_pages,
        "ingestionValidated": False, "extraction": "pypdf text extraction, real original page numbers, no OCR",
        "vectorIndexIsProtocolSubstitute": True, "tokenCountsAreEstimates": True}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--pdf", type=pathlib.Path, required=True)
    parser.add_argument("--sha256", required=True)
    parser.add_argument("--pages", required=True)
    args = parser.parse_args()
    from pypdf import PdfReader
    root = args.output.resolve()
    owner = json.loads((root / "environment.json").read_text(encoding="utf-8-sig"))
    expected_docs = root / "runtime" / "documents"
    if pathlib.Path(owner["outputDirectory"]).resolve() != root or pathlib.Path(owner["documentsRoot"]).resolve() != expected_docs:
        raise ValueError("Not the exact owned document root")
    payload = args.pdf.read_bytes()
    if hashlib.sha256(payload).hexdigest().lower() != args.sha256.lower():
        raise ValueError("Source PDF changed after profile freeze")
    page_numbers = [int(n) for n in args.pages.split(",")]
    reader = PdfReader(args.pdf)
    if any(n < 1 or n > len(reader.pages) for n in page_numbers):
        raise ValueError("Selected PDF page is out of range")
    pages = [(n, reader.pages[n - 1].extract_text() or "") for n in page_numbers]
    sql, manifest = canonical_seed(owner["runId"], args.pdf.name, payload, pages, source_page_count=len(reader.pages))
    target = expected_docs / manifest["docPath"]
    seed = root / "runtime" / "seed-real-corpus.sql"
    private = root / "runtime" / "real-corpus-pages.private.json"
    summary = root / "real-corpus-manifest.json"
    if any(p.exists() for p in (target, seed, private, summary)):
        raise ValueError("Never overwrite corpus or prior evidence")
    target.parent.mkdir(parents=True, exist_ok=True)
    with target.open("xb") as stream:
        stream.write(payload)
    with seed.open("x", encoding="utf-8") as stream:
        stream.write(sql)
    with private.open("x", encoding="utf-8") as stream:
        json.dump([{"pageNumber": n, "text": text} for n, text in pages], stream, ensure_ascii=False, indent=2)
    with summary.open("x", encoding="utf-8") as stream:
        json.dump(manifest, stream, indent=2)
    print(json.dumps({"prepared": True, "pages": len(pages), "realPdf": True, "ingestionValidated": False}))


if __name__ == "__main__":
    main()
