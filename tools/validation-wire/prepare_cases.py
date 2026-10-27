"""Freeze documentary expectations before a wire run; reads captured data only."""
import argparse
import base64
import gzip
import hashlib
import json
import pathlib
import re


SOURCE27_SQL_SHA256 = "83875b876e35010e9309b7648794c342734d31b857af2396c15ecc1e20f61219"


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def raw_lines(text):
    # Only CR and LF delimit lines; Python splitlines also splits extra Unicode separators.
    return re.findall(r"[^\r\n]*(?:\r\n|\r|\n)|[^\r\n]+$", text)


def prepare(capture, output):
    raw = capture.read_bytes()
    if sha(raw) != SOURCE27_SQL_SHA256:
        raise ValueError("source27_publication_capture_sha256_differs")
    rows = json.loads(raw)["documents"]
    cases = []
    for path, physical in (("nist-complex.pdf", 40), ("recipe-real-v1.pdf", 5)):
        row, = [row for row in rows if row["docPath"] == path]
        artifact, = row["binaryArtifacts"]
        payload = base64.b64decode(artifact["payloadBase64"])
        data = gzip.decompress(payload)
        if sha(data) != artifact["contentHash"] or len(data) != artifact["byteSize"]:
            raise ValueError("canonical_hash_or_size_differs")
        bundle = json.loads(data)
        page, = [page for page in bundle["document"]["pages"] if page["pageNumber"] == physical]
        observed_blocks = sorted([block for block in page["blocks"] if block["blockType"] in
                  ("native_text_recovery", "native_layout_recovery", "native_table_recovery")],
                  key=lambda item: (item.get("readingOrder", 0), item.get("ordinal", 0), item["blockId"]))
        blocks = [block for block in observed_blocks if len(raw_lines(block.get("text", {}).get("raw", ""))) >= 3]
        if not blocks:
            raise ValueError("three_line_native_observation_required")
        block = sorted(blocks, key=lambda item: (item.get("readingOrder", 0), item.get("ordinal", 0), item["blockId"]))[0]
        primary_page = physical
        light_page = 19 if path == "nist-complex.pdf" else 3
        light_chunks = [chunk for chunk in row["chunks"] if chunk["pageStart"] <= light_page <= chunk["pageEnd"]]
        if len(light_chunks) != 1:
            raise ValueError("light_primary_page_must_have_exactly_one_chunk")
        chunk = max((chunk for chunk in row["chunks"] if chunk["pageStart"] <= primary_page <= chunk["pageEnd"]
                     and len(chunk["text"]) > 40), key=lambda item: len(item["text"]))
        words = re.findall(r"[^\W\d_]{4,}", chunk["text"], re.UNICODE)
        if not words:
            raise ValueError("search_literal_required")
        # Literal prefix stays inside one existing line; no normalized documentary text.
        literal_search = chunk["metadata"].get("literalSearchText") or chunk["text"]
        find = next(line.strip() for line in literal_search.split("\n") if len(line.strip()) >= 8)[:100]
        cases.append({"caseId": "wire-" + path.removesuffix(".pdf"), "docPath": path,
            "docId": row["docId"], "revisionId": row["revisionId"], "sourceSha256": row["sourceSha256"],
            "canonicalSha256": artifact["contentHash"], "manifestSha256": bundle["document"]["manifestSha256"],
            "physicalPage": physical, "primaryPage": primary_page, "initialChunkId": chunk["retrievalChunkId"],
            "lightPrimaryPage": light_page, "lightPrimaryChunkId": light_chunks[0]["retrievalChunkId"],
            "initialChunkTextSha256": chunk["checksumSha256"], "searchQuery": words[0], "findQuery": find,
            "nativeBlockId": block["blockId"], "nativeRawSha256": sha(block["text"]["raw"].encode("utf-8")),
            "nativeInventoryOffset": observed_blocks.index(block),
            "expectedNativeLines": raw_lines(block["text"]["raw"])[:3]})
    result = {"schemaVersion": "saaia-wire-cases-v1", "sourceCaptureSha256": sha(raw),
              "expectationsFrozenBeforeRuntime": True, "cases": cases}
    with output.open("x", encoding="utf-8") as handle:
        json.dump(result, handle, ensure_ascii=False, indent=2)
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--capture", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    prepare(args.capture, args.output)
