"""Register and attest real Linux TEI/Qdrant accessed by owned SSH forwards.

Registration and default attestation make no network request or secret read.
Explicit --execute attests only the declared loopback dependencies and local
synthetic PostgreSQL. No provider request or source SQL/vector mutation exists.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import re
import subprocess
import sys
import urllib.request
import uuid

import planning_worker_observation as adapter
import restore_owned_windows_corpus as clone
from embedding_attestation import attest_info

require = clone.require


def pin(path):
    return {"path": str(path.resolve()), "sha256": clone.digest(path)}


def checked(entry):
    path = Path(entry["path"])
    require(path.is_absolute() and clone.digest(path) == entry["sha256"], "rag_pinned_input_changed")
    return path


def source_forwardings(owner):
    endpoints = owner.get("endpoints", {})
    require(set(endpoints) == {"backend", "docling", "tei", "qdrant"}, "rag_owner_endpoints_required")
    ports = {}
    for role, url in endpoints.items():
        match = re.fullmatch(r"http://127\.0\.0\.1:([0-9]{1,5})", url) if isinstance(url, str) else None
        require(match is not None, "rag_owner_loopback_origin_required")
        port = int(match[1])
        require(1024 <= port <= 65535 and port != 5432, "rag_owner_qualification_port_required")
        ports[role] = port
    require(len(set(ports.values())) == 4, "rag_owner_distinct_ports_required")
    return [{"localHost": "127.0.0.1", "localPort": local, "remoteHost": "127.0.0.1", "remotePort": ports[role]}
            for local, role in ((22531, "tei"), (22532, "qdrant"))]


def validate_forwarding(value, owner):
    root = owner["root"]
    require(value["schemaVersion"] == "saaia-owned-ssh-forward.v1" and value["sourceOwnerRoot"] == root
            and value["hostTarget"] == "maxime@saaia-server", "rag_tunnel_owner_mismatch")
    expected = source_forwardings(owner)
    require(value["forwardings"] == expected and all(type(row["localPort"]) is int and type(row["remotePort"]) is int
            for row in value["forwardings"]), "rag_tunnel_forwardings_changed")
    process = value["process"]
    require(type(process["pid"]) is int and process["pid"] > 0 and Path(process["path"]).name.casefold() == "ssh.exe"
            and re.fullmatch("[a-f0-9]{64}", process["commandLineSha256"]), "rag_tunnel_process_invalid")


def register(args):
    manifest = clone.read(args.manifest)
    transfer = adapter.CaptureMap(args.capture_map)
    _, observation, _ = adapter.load_worker_corpus(manifest, transfer)
    metadata, _, _ = clone.validate_packet(args.packet.resolve(), args.metadata_sha256)
    owner = clone.read(args.source_owner); root = owner["root"]
    require(metadata["workerObservationSha256"] == manifest["observationSha256"]
            and metadata["ownerRunId"] == observation["ownerRunId"] == owner["runId"]
            and metadata["sourceOwnerSha256"] == clone.digest(args.source_owner), "rag_clone_worker_identity_mismatch")
    tunnel = clone.read(args.tunnel); validate_forwarding(tunnel, owner)
    embedding_entry = observation["embeddingProfileEvidence"]
    embedding = transfer.resolve(embedding_entry["path"], embedding_entry["sha256"])
    profile = clone.read(embedding)
    qdrant = next(c for c in owner["containers"] if c["role"] == "qdrant")
    collection = "ingestion_" + uuid.UUID(owner["runId"]).hex
    require(owner["qdrantCollection"] == collection and profile["dimensions"] == 768, "rag_current_collection_or_dimension_changed")
    plans = [json.loads(line) for line in clone.packet_file(args.packet.resolve(), metadata["tables"]["ingestion_vector_plans"]).read_bytes().splitlines()]
    require(len(plans) == 4 and all(p["target_collection"] == collection and p["target_revision"] == qdrant["imageId"] for p in plans), "rag_plan_target_identity_mismatch")
    rag = {**profile["Rag"], "EmbeddingsBaseUrl": "http://127.0.0.1:22531", "QdrantBaseUrl": "http://127.0.0.1:22532",
           "QdrantCollection": collection, "QdrantRuntimeRevision": qdrant["imageId"], "EnableRerank": False, "RequireQdrantAuthInProd": False}
    value = {"schemaVersion": "saaia-planning-owned-rag-runtime.v1", "origin": "real_ingestion_worker", "sourceOwnerRoot": root,
             "sourceOwnerRunId": owner["runId"], "sourceOwnerEvidence": pin(args.source_owner), "tunnelEvidence": pin(args.tunnel),
             "corpusManifest": pin(args.manifest), "captureMap": pin(args.capture_map), "cloneSourceMetadata": pin(args.packet / "metadata.json"),
             "embeddingProfileEvidence": pin(embedding), "Rag": rag, "expectedQdrantPointCount": metadata["tables"]["ingestion_vector_plan_points"]["rowCount"],
             "boundaries": {"providerCalls": 0, "dependencyLiveAttestationExecuted": False, "sourceSqlModified": False, "sourceVectorsModified": False}}
    with args.output.open("x", encoding="utf8") as out: json.dump(value, out, ensure_ascii=False, indent=2)
    return {"registered": True, "profile": pin(args.output), "providerCalls": 0, "dependencyRequests": 0}


def validate_profile(value):
    require(value["schemaVersion"] == "saaia-planning-owned-rag-runtime.v1" and value["origin"] == "real_ingestion_worker", "rag_profile_schema_invalid")
    for name in ("sourceOwnerEvidence", "tunnelEvidence", "corpusManifest", "captureMap", "cloneSourceMetadata", "embeddingProfileEvidence"): checked(value[name])
    owner = clone.read(checked(value["sourceOwnerEvidence"]))
    require(owner["root"] == value["sourceOwnerRoot"] and owner["runId"] == value["sourceOwnerRunId"], "rag_profile_owner_changed")
    validate_forwarding(clone.read(checked(value["tunnelEvidence"])), owner)
    embedding = clone.read(checked(value["embeddingProfileEvidence"]))
    manifest = clone.read(checked(value["corpusManifest"]))
    metadata = clone.read(checked(value["cloneSourceMetadata"]))
    require(manifest["origin"] == "real_ingestion_worker" and manifest["observationSha256"] == metadata["workerObservationSha256"]
            and metadata["ownerRunId"] == value["sourceOwnerRunId"]
            and metadata["sourceOwnerSha256"] == value["sourceOwnerEvidence"]["sha256"]
            and metadata["tables"]["ingestion_vector_plan_points"]["rowCount"] == value["expectedQdrantPointCount"], "rag_profile_publication_binding_changed")
    qdrant = next(c for c in owner["containers"] if c["role"] == "qdrant")
    expected = {**embedding["Rag"], "EmbeddingsBaseUrl": "http://127.0.0.1:22531", "QdrantBaseUrl": "http://127.0.0.1:22532",
                "QdrantCollection": owner["qdrantCollection"], "QdrantRuntimeRevision": qdrant["imageId"], "EnableRerank": False, "RequireQdrantAuthInProd": False}
    require(value["Rag"] == expected and type(value["expectedQdrantPointCount"]) is int and value["expectedQdrantPointCount"] > 0, "rag_profile_override_changed")
    return embedding


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args): raise ValueError("rag_dependency_redirect_forbidden")


def check_points(expected, rows):
    require(len(rows) == len(expected) and len({row["id"] for row in rows}) == len(rows), "rag_live_point_count_or_duplicates")
    by_id = {str(uuid.UUID(row["id"])): row for row in rows}
    for point in expected:
        row = by_id.get(point["point_id"])
        require(row is not None and row["payload"]["chunk_id"] == point["canonical_chunk_id"]
                and row["payload"]["ingestion_plan_fingerprint"] == point["plan_fingerprint"], "rag_live_chunk_plan_binding_changed")
        vector = row["vector"]
        require(isinstance(vector,list) and len(vector) == 768
                and all(type(n) in (int,float) and math.isfinite(n) for n in vector), "rag_live_vector_dimension_or_nonfinite")


def attest(args, value, embedding):
    owner = clone.read(args.owner); target = args.owner.parent.resolve()
    require(owner["planningCloneOnly"] is True and owner["state"] == "ready" and owner["pgPort"] != 5432
            and Path(owner["outputDirectory"]).resolve() == target
            and args.output.resolve() == target/"runtime/real-rag-attestation", "rag_owned_clone_runtime_required")
    restored = clone.read(target / "runtime/strict-worker-clone/result.json")
    require(restored["passed"] is True and restored["committed"] is True and restored["all32TablesByteExact"] is True
            and restored["sourceMetadataSha256"] == value["cloneSourceMetadata"]["sha256"]
            and restored["targetOwnerRunId"] == owner["runId"], "rag_exact_clone_receipt_required")
    metadata_path = checked(value["cloneSourceMetadata"]); packet = metadata_path.parent
    metadata, _, descriptors = clone.validate_packet(packet, value["cloneSourceMetadata"]["sha256"])
    require(not args.output.exists(), "fresh_rag_attestation_output_required"); args.output.mkdir()
    private = clone.read(target / "runtime/fixture-secrets.json")
    env = os.environ.copy();env["PGPASSWORD"] = private["pgPassword"];env["PGCLIENTENCODING"] = "UTF8";env.pop("PGOPTIONS",None)
    for name in ("documents", "document_revisions", "retrieval_chunks", "ingestion_vector_plans", "ingestion_vector_plan_points"):
        sql = "BEGIN READ ONLY;\n" + clone.SESSION_SETTINGS + clone.readback_query(name, descriptors[name]) + ";\nROLLBACK;\n"
        result = subprocess.run(clone.psql_args(owner), input=sql.encode(), stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env, timeout=300)
        (args.output / (name + ".stderr.private.txt")).write_bytes(result.stderr)
        raw = result.stdout.replace(b"\r\n", b"\n")  # JSONL separators only; JSON strings contain escaped CR/LF.
        (args.output / (name + ".jsonl")).write_bytes(raw)
        require(result.returncode == 0 and hashlib.sha256(raw).hexdigest() == metadata["tables"][name]["sha256"], "rag_current_sql_differs_from_strict_clone_" + name)
    env.pop("PGPASSWORD",None);private=None
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    calls = 0
    def request(url, body=None):
        nonlocal calls
        require(url.startswith(("http://127.0.0.1:22531/", "http://127.0.0.1:22532/")), "rag_exact_loopback_dependency_required")
        req = urllib.request.Request(url, data=None if body is None else json.dumps(body).encode(), headers={"Content-Type":"application/json"})
        with opener.open(req,timeout=60) as response:
            raw=response.read(64*1024*1024+1); require(len(raw)<=64*1024*1024,"rag_dependency_response_bound")
        calls+=1;(args.output / (f"response-{calls:04d}.private.json")).write_bytes(raw);return json.loads(raw)
    info=request(value["Rag"]["EmbeddingsBaseUrl"]+"/info")
    attest_info(info,embedding,embedding["Rag"]["EmbeddingsBaseUrl"])
    collection=value["Rag"]["QdrantCollection"];base=value["Rag"]["QdrantBaseUrl"]+"/collections/"+collection
    actual=request(base)["result"]
    require(actual["config"]["params"]["vectors"]["size"]==768 and actual["points_count"]==value["expectedQdrantPointCount"],"rag_live_collection_dimension_or_count_changed")
    plans={row["plan_id"]:row for row in [json.loads(line) for line in clone.packet_file(packet,metadata["tables"]["ingestion_vector_plans"]).read_bytes().splitlines()]}
    points=[{**json.loads(line),"plan_fingerprint":plans[json.loads(line)["plan_id"]]["plan_fingerprint"]} for line in clone.packet_file(packet,metadata["tables"]["ingestion_vector_plan_points"]).read_bytes().splitlines()]
    for offset in range(0,len(points),64):
        batch=points[offset:offset+64]
        rows=request(base+"/points",{"ids":[p["point_id"] for p in batch],"with_payload":["chunk_id","ingestion_plan_fingerprint"],"with_vector":True})["result"]
        check_points(batch,rows)
    record={"schemaVersion":"saaia-planning-real-rag-attestation.v1","passed":True,"realRagProfileSha256":clone.digest(args.profile),
            "sourceOwnerRunId":value["sourceOwnerRunId"],"targetOwnerRunId":owner["runId"],"dependencyRequests":calls,"providerRequests":0,
            "sourceSqlWrites":0,"sourceVectorWrites":0,"currentSqlRowsByteExact":True,"realPointChunkPlanBindings":len(points),"nativeTeiInfoExact":True,
            "tunnelEvidence":value["tunnelEvidence"],"liveTunnelProcessVerificationRequiredAtCaller":True,
            "tunnelProcessVerifiedByThisPythonCommand":False}
    with (args.output/"result.json").open("x",encoding="utf8") as out:json.dump(record,out,indent=2)
    return record


def main():
    parser=argparse.ArgumentParser(description=__doc__);commands=parser.add_subparsers(dest="command",required=True)
    register_parser=commands.add_parser("register")
    for name in ("manifest","capture-map","packet","source-owner","tunnel","output"):register_parser.add_argument("--"+name,required=True,type=Path)
    register_parser.add_argument("--metadata-sha256",required=True)
    check=commands.add_parser("attest")
    for name in ("profile","owner","output"):check.add_argument("--"+name,required=True,type=Path)
    check.add_argument("--execute",action="store_true")
    args=parser.parse_args()
    if args.command=="register":result=register(args)
    else:
        value=clone.read(args.profile);embedding=validate_profile(value)
        result=attest(args,value,embedding) if args.execute else {"executed":False,"dependencyRequests":0,"providerRequests":0,"sqlCalled":False,"secretRead":False}
    print(json.dumps(result))


if __name__=="__main__":main()
