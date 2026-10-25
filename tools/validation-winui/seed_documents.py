"""Generate tiny synthetic PDFs and canonical SQL; no real corpus is read."""
import argparse
import hashlib
import json
import pathlib
import uuid


def pdf_bytes(pages):
    objects = [b"<< /Type /Catalog /Pages 2 0 R >>", b"", b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"]
    kids = []
    for page in pages:
        page_id = len(objects) + 1
        stream_id = page_id + 1
        kids.append(f"{page_id} 0 R")
        text = page.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")
        stream = f"BT /F1 18 Tf 50 750 Td ({text}) Tj ET".encode("ascii")
        objects.append(f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {stream_id} 0 R >>".encode())
        objects.append(b"<< /Length " + str(len(stream)).encode() + b" >>\nstream\n" + stream + b"\nendstream")
    objects[1] = f"<< /Type /Pages /Count {len(pages)} /Kids [{' '.join(kids)}] >>".encode()
    result = bytearray(b"%PDF-1.4\n")
    offsets = [0]
    for index, obj in enumerate(objects, 1):
        offsets.append(len(result))
        result += f"{index} 0 obj\n".encode() + obj + b"\nendobj\n"
    xref = len(result)
    result += f"xref\n0 {len(objects)+1}\n0000000000 65535 f \n".encode()
    for offset in offsets[1:]:
        result += f"{offset:010} 00000 n \n".encode()
    result += f"trailer << /Size {len(objects)+1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode()
    return bytes(result)


def quoted(value):
    return "'" + str(value).replace("'", "''") + "'"


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    owner = json.loads((args.output / "environment.json").read_text(encoding="utf-8-sig"))
    tenant = "77777777-7777-4777-8777-777777777777"
    documents = [
        ("Validation/Guide-Validation.pdf", ["VALIDATION WINUI - PAGE 1 - SOMMAIRE", "VALIDATION WINUI - PAGE 2 - CODE ORION 4826"],
         ["Guide synthétique de validation WinUI. Sommaire : section code de validation en page 2.",
          "Code de validation ORION : 4826. Ce code est fictif et ne représente aucun secret. Le guide sert uniquement à vérifier lecture, citations et ouverture de la deuxième page depuis le client WinUI."]),
        ("Archive/Guide-Validation.pdf", ["ARCHIVE DISTINCTE - PAGE 1 - CODE ORION 9999"],
         ["Archive synthétique distincte. Son code ORION est 9999. Ce fichier a volontairement le même nom que le guide actif mais un autre chemin, pour valider la résolution exacte et éviter une ouverture ambiguë."])
    ]
    sql = ["BEGIN;"]
    summary = []
    for relative, pdf_pages, contents in documents:
        doc = str(uuid.uuid5(uuid.NAMESPACE_URL, "saaia-winui:" + relative))
        revision = str(uuid.uuid5(uuid.NAMESPACE_URL, "saaia-winui-revision:" + relative))
        category = relative.split("/")[0]
        target = pathlib.Path(owner["documentsRoot"]) / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        if target.exists():
            raise RuntimeError("Refusing to overwrite synthetic fixture document")
        payload = pdf_bytes(pdf_pages)
        target.write_bytes(payload)
        digest = hashlib.sha256(payload).hexdigest()
        sql.append(f"INSERT INTO documents(tenant_id,doc_id,doc_path,doc_name,category,content_hash,file_size,mime_type,page_count,status,ingestion_version,indexed_version,last_ingested_at) VALUES('{tenant}','{doc}',{quoted(relative)},{quoted(target.name)},{quoted(category)},decode('{digest}','hex'),{len(payload)},'application/pdf',{len(contents)},'indexed',1,1,now());")
        sql.append(f"INSERT INTO document_revisions(revision_id,tenant_id,doc_id,doc_path,source_hash,source_size,ingestion_version,indexed_version) VALUES('{revision}','{tenant}','{doc}',{quoted(relative)},decode('{digest}','hex'),{len(payload)},1,1);")
        for i, text in enumerate(contents):
            page = i + 1
            checksum = hashlib.sha256(text.encode()).hexdigest()
            unit, chunk, section, entry, context = [str(uuid.uuid5(uuid.NAMESPACE_URL, f"saaia-winui:{relative}:{i}:{kind}")) for kind in ("unit", "chunk", "section", "entry", "context")]
            title = "Sommaire" if i == 0 and len(contents) > 1 else "Code de validation ORION"
            sql.append(f"INSERT INTO document_sections(section_id,tenant_id,revision_id,ordinal,title,page_start,page_end) VALUES('{section}','{tenant}','{revision}',{i},{quoted(title)},{page},{page});")
            sql.append(f"INSERT INTO document_units(unit_id,tenant_id,revision_id,section_id,ordinal,page_start,page_end,text_content,char_count,token_count,checksum) VALUES('{unit}','{tenant}','{revision}','{section}',{i},{page},{page},{quoted(text)},{len(text)},40,decode('{checksum}','hex'));")
            sql.append(f"INSERT INTO retrieval_chunks(retrieval_chunk_id,tenant_id,revision_id,section_id,unit_id,chunk_index,page_start,page_end,text_content,token_count,checksum) VALUES('{chunk}','{tenant}','{revision}','{section}','{unit}',{i},{page},{page},{quoted(text)},40,decode('{checksum}','hex'));")
            sql.append(f"INSERT INTO document_page_index(tenant_id,revision_id,page_number,char_count,checksum) VALUES('{tenant}','{revision}',{page},{len(text)},decode('{checksum}','hex'));")
            sql.append(f"INSERT INTO exact_match_entries(exact_match_entry_id,tenant_id,revision_id,section_id,unit_id,entry_index,page_start,page_end,text_content,normalized_text,char_count,token_count,checksum) VALUES('{entry}','{tenant}','{revision}','{section}','{unit}',{i},{page},{page},{quoted(text)},{quoted(text.lower())},{len(text)},40,decode('{checksum}','hex'));")
            sql.append(f"INSERT INTO contextual_text_entries(contextual_text_entry_id,tenant_id,revision_id,section_id,unit_id,retrieval_chunk_id,entry_index,page_start,page_end,text_content,char_count,token_count,checksum) VALUES('{context}','{tenant}','{revision}','{section}','{unit}','{chunk}',{i},{page},{page},{quoted(text)},{len(text)},40,decode('{checksum}','hex'));")
        summary.append({"docId": doc, "revisionId": revision, "docPath": relative, "pages": len(contents), "sha256": digest})
    sql.append("COMMIT;")
    (args.output / "runtime" / "seed-documents.sql").write_text("\n".join(sql), encoding="utf-8")
    (args.output / "synthetic-documents.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
