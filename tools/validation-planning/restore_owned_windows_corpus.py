"""Restore a new worker's closed documentary packet into a fresh owned PG16.

No provider, ingestion, vector mutation or auth import. The transaction remains
open until all 32 server JSONL readbacks match the source bytes, including RAW,
compressed canonical artifacts, generated tsvectors and sealed vector manifests.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
import traceback

sys.path.insert(0, str(Path(__file__).parent / "CloneExport"))
from clone_contracts import TABLES, DUMP_TABLES, SESSION_SETTINGS, TENANT, inspect_jsonl, readback_query, require, validate_schema
from export_owned_linux_corpus import SCHEMA_QUERY


def digest(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def read(path):
    return json.loads(path.read_bytes())


def packet_file(root, entry):
    relative = entry["path"]
    require(isinstance(relative, str) and not relative.startswith("/") and "\\" not in relative
            and all(p not in ("", ".", "..") and ":" not in p for p in relative.split("/")), "clone_packet_path_invalid")
    path = root.joinpath(*relative.split("/"))
    require(path.is_file() and path.resolve() == path and path.is_relative_to(root)
            and not any(p.is_symlink() or (getattr(p.lstat(), "st_file_attributes", 0) & 0x400)
                        for p in (path, *path.parents) if p.is_relative_to(root)), "clone_packet_link_or_escape")
    require(digest(path) == entry["sha256"] and ("bytes" not in entry or path.stat().st_size == entry["bytes"]), "clone_packet_bytes_changed")
    return path


def validate_packet(root, metadata_sha):
    require(digest(root / "metadata.json") == metadata_sha, "clone_metadata_changed")
    metadata = read(root / "metadata.json")
    require(metadata["schemaVersion"] == "saaia-owned-documentary-sql-clone.v1" and metadata["complete"] is True
            and metadata["tenantId"] == TENANT and metadata["serverMajor"] == 16 and metadata["encoding"] == "UTF8"
            and metadata["tableOrder"] == list(TABLES) and metadata["dumpTables"] == list(DUMP_TABLES)
            and metadata["sourceSqlWrites"] == metadata["providerCalls"] == 0
            and metadata["generatedColumnComparisonRequired"] is True and metadata["restoreExecuted"] is False
            and metadata["targetReadbackVerified"] is False, "clone_metadata_contract_invalid")
    schema = read(packet_file(root, metadata["schema"]))
    descriptors = validate_schema(schema)
    packet_file(root, metadata["dump"]); packet_file(root, metadata["scope"])
    require(set(metadata["tables"]) == set(TABLES), "clone_table_set_changed")
    for name in TABLES:
        entry = metadata["tables"][name]
        require(entry["columns"] == [v["name"] for v in descriptors[name]["columns"]]
                and entry["primaryKey"] == descriptors[name]["primaryKey"], "clone_table_descriptor_changed")
        measured = inspect_jsonl(packet_file(root, entry), descriptors[name])
        require(all(measured[k] == entry[k] for k in measured), "clone_jsonl_inventory_changed")
    return metadata, schema, descriptors


def exact_schema(source, target):
    # Server build/OS locale are declared separately. Column, migration and PK
    # contracts must match, and generated values are still compared byte-exact.
    require(target["serverMajor"] == 16 and target["encoding"] == "UTF8"
            and source["migrations"] == target["migrations"], "clone_target_schema_version_mismatch")
    require(validate_schema(source) == validate_schema(target), "clone_target_column_contract_mismatch")


def json_recordset(rows):
    # Preserve original JSON literals; conversion to hex is SQL escaping, not a
    # parse/re-serialization of document strings or numeric values.
    return "convert_from(decode('" + (b"[" + b",".join(line.rstrip(b"\n") for line in rows) + b"]").hex() + "','hex'),'UTF8')::jsonb"


def vector_restore_sql(root, metadata):
    plans = packet_file(root, metadata["tables"]["ingestion_vector_plans"]).read_bytes().splitlines(keepends=True)
    points = packet_file(root, metadata["tables"]["ingestion_vector_plan_points"]).read_bytes().splitlines(keepends=True)
    require(len(plans) == 4 and all(json.loads(p)["manifest_sealed"] is True for p in plans), "clone_requires_four_sealed_plans")
    plan_json = json_recordset(plans)
    # The ONLY temporary change is sealed=false, before any point is inserted.
    # The normal seal trigger checks the point count. Original hashes, point
    # identities and all other fields are verified by the final exact readback.
    sql = "INSERT INTO public.ingestion_vector_plans SELECT (jsonb_populate_record(NULL::public.ingestion_vector_plans, jsonb_set(v,'{manifest_sealed}','false'::jsonb))).* FROM jsonb_array_elements(" + plan_json + ") v;\n"
    for offset in range(0, len(points), 64):
        sql += "INSERT INTO public.ingestion_vector_plan_points SELECT * FROM jsonb_populate_recordset(NULL::public.ingestion_vector_plan_points," + json_recordset(points[offset:offset + 64]) + ");\n"
    sql += "UPDATE public.ingestion_vector_plans t SET manifest_sealed=s.manifest_sealed FROM jsonb_populate_recordset(NULL::public.ingestion_vector_plans," + plan_json + ") s WHERE t.plan_id=s.plan_id;\n"
    return sql


def psql_args(owner):
    return [str(Path(owner["postgresBinDirectory"]) / "psql.exe"), "-X", "-A", "-t", "-q", "-w", "-v", "ON_ERROR_STOP=1",
            "-h", "127.0.0.1", "-p", str(owner["pgPort"]), "-U", "saaia_winui", "-d", "postgres"]


def execute(args, metadata, schema, descriptors):
    require(sys.platform == "win32", "owned_clone_target_requires_windows")
    owner = read(args.owner)
    target = args.owner.parent.resolve(); output = args.output.resolve()
    require(owner["schemaVersion"] == "saaia-winui-local-runtime.v1" and owner["state"] == "ready"
            and Path(owner["outputDirectory"]).resolve() == target and output.is_relative_to(target)
            and owner["pgPort"] != 5432 and owner.get("planningCloneOnly") is True, "fresh_owned_clone_environment_required")
    require(not output.exists(), "fresh_clone_evidence_directory_required")
    config = read(target / "runtime/deployment.config.json")
    require(config.get("VectorGc", {}).get("Enabled") is False and config["Ingestion"]["WorkerEnabled"] is False
            and config["AdvancedAnalysis"]["WorkerEnabled"] is False and config["AdvancedAnalysis"]["Provider"] == "disabled", "clone_background_mutations_must_be_disabled")
    require(digest(args.corpus_manifest) == args.corpus_manifest_sha256, "clone_source_manifest_changed")
    corpus = read(args.corpus_manifest)
    require(corpus["origin"] == "real_ingestion_worker" and corpus["observationSha256"] == metadata["workerObservationSha256"], "clone_source_manifest_worker_mismatch")
    by_doc = {d["docId"]: d for d in corpus["documents"]}
    require(len(by_doc) == len(corpus["documents"]) == 4, "clone_pdf_inventory_invalid")
    documents = Path(owner["documentsRoot"]).resolve()
    require(documents == target / "runtime/documents" and not any(documents.iterdir()), "fresh_owned_pdf_root_required")
    pdf_copies = []
    for doc in metadata["documents"]:
        source = by_doc[doc["docId"]]
        require(source["revisionId"] == doc["revisionId"] and source["docPath"] == doc["docPath"]
                and source["sourceSha256"] == doc["sourceSha256"] and re.fullmatch(r"[a-z][a-z0-9-]*\.pdf", doc["docPath"]), "clone_pdf_identity_mismatch")
        original = Path(source["physicalPdfPath"])
        require(digest(original) == doc["sourceSha256"], "clone_original_pdf_bytes_changed")
        pdf_copies.append((original, documents / doc["docPath"], doc["sourceSha256"]))
    output.mkdir()
    secrets = read(target / "runtime/fixture-secrets.json")
    env = os.environ.copy(); env["PGPASSWORD"] = secrets["pgPassword"]
    env["PGCLIENTENCODING"] = "UTF8"; env.pop("PGOPTIONS", None)
    for name in list(env):
        if re.search("OPENAI|RUNPOD|AZURE|SAAIA_ADVANCED|SAAIA_LLM", name, re.I): env.pop(name)
    command = psql_args(owner)
    record = {"schemaVersion": "saaia-owned-documentary-sql-clone-restore.v1", "passed": False,
              "sourceMetadataSha256": args.metadata_sha256, "sourceOwnerRunId": metadata["ownerRunId"],
              "targetOwnerRunId": owner["runId"], "providerCalls": 0, "sourceModified": False,
              "authImported": False, "committed": False, "exactTables": []}

    def query(sql, name):
        result = subprocess.run(command, input=("BEGIN READ ONLY;\n" + SESSION_SETTINGS + sql + ";\nROLLBACK;\n").encode(), env=env,
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=300)
        (output / (name + ".stderr.private.txt")).write_bytes(result.stderr)
        require(result.returncode == 0, "clone_target_read_query_failed")
        (output / (name + ".private.json")).write_bytes(result.stdout)
        return result.stdout

    process = None
    try:
        path = query("SELECT current_setting('data_directory')", "data-directory").decode().strip()
        require(Path(path).resolve() == Path(owner["postgresDataDirectory"]).resolve(), "clone_target_data_directory_mismatch")
        baseline = json.loads(query("SELECT jsonb_build_object('tenant',(SELECT count(*) FROM tenants WHERE tenant_id='" + TENANT + "'::uuid), 'rows',(" + "+".join("(SELECT count(*) FROM public.\"" + table + "\")" for table in TABLES) + "),'advanced',(SELECT count(*) FROM advanced_analysis_jobs))", "empty-baseline"))
        require(baseline == {"tenant": 1, "rows": 0, "advanced": 0}, "clone_target_documentary_store_not_empty")
        actual_schema = json.loads(query(SCHEMA_QUERY.replace("__TABLES__", ",".join("'" + t + "'" for t in TABLES)), "target-schema"))
        exact_schema(schema, actual_schema)
        for original, destination, expected_sha in pdf_copies:
            with original.open("rb") as src, destination.open("xb") as dest: shutil.copyfileobj(src,dest)
            require(digest(destination) == expected_sha, "clone_owned_pdf_copy_changed")
        record["sourcePdfCopies"] = [{"path":str(destination),"originalPath":str(original),"sha256":sha} for original,destination,sha in pdf_copies]
        record["locale"] = {"sourceCollate": schema["lcCollate"], "sourceCtype": schema["lcCtype"], "targetCollate": actual_schema["lcCollate"], "targetCtype": actual_schema["lcCtype"], "generatedValuesStillRequireExactBytes": True}
        restore = Path(owner["postgresBinDirectory"]) / "pg_restore.exe"
        require("16." in subprocess.check_output([str(restore), "--version"]).decode(), "pg_restore16_required")
        sql_file = output / "restore30.private.sql"
        result = subprocess.run([str(restore), "--no-owner", "--no-privileges", "--file", str(sql_file), str(packet_file(args.packet, metadata["dump"]))], stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env, timeout=300)
        (output / "pg-restore.stderr.private.txt").write_bytes(result.stderr)
        require(result.returncode == 0, "pg_restore_sql_generation_failed")
        dump_sql = sql_file.read_bytes()
        readback = output / "tables"; readback.mkdir()
        script = b"BEGIN;\n" + SESSION_SETTINGS.encode() + dump_sql + b"\n" + vector_restore_sql(args.packet, metadata).encode()
        for name in TABLES:
            path = (readback / (name + ".jsonl")).as_posix()
            require("'" not in path and "\n" not in path, "clone_output_filename_invalid")
            script += ("\\o '" + path + "'\n" + readback_query(name, descriptors[name]) + ";\n\\o\n").encode()
        script += b"SELECT 'READY_FOR_CLONE_COMMIT';\n"
        with (output / "restore.stdin.private.sql").open("xb") as stream: stream.write(script)
        stdout = output / "restore.stdout.private.txt"
        with stdout.open("xb") as out, (output / "restore.stderr.private.txt").open("xb") as error:
            process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=out, stderr=error, env=env)
            process.stdin.write(script); process.stdin.flush()
            deadline = time.monotonic() + 600
            while b"READY_FOR_CLONE_COMMIT" not in stdout.read_bytes():
                require(process.poll() is None and time.monotonic() < deadline, "clone_restore_transaction_failed_before_comparison")
                time.sleep(.1)
            for name in TABLES:
                entry = metadata["tables"][name]; actual = readback / (name + ".jsonl")
                # Windows psql writes its record separator as CRLF; remove ONLY
                # that transport byte, never alter escaped JSON document strings.
                raw = actual.read_bytes(); lines = raw.splitlines(keepends=True)
                require(all(v.endswith(b"\r\n") or v.endswith(b"\n") for v in lines), "target_jsonl_separator_invalid")
                normalized = b"".join(v[:-2] + b"\n" if v.endswith(b"\r\n") else v for v in lines)
                if raw != normalized:
                    (readback / (name + ".psql-native.private.jsonl")).write_bytes(raw); actual.write_bytes(normalized)
                measured = inspect_jsonl(actual, descriptors[name])
                require(actual.stat().st_size == entry["bytes"] and all(measured[k] == entry[k] for k in measured), "clone_final_table_bytes_differ_" + name)
                record["exactTables"].append({"name": name, **measured})
            process.stdin.write(b"COMMIT;\n"); process.stdin.close(); process.wait(timeout=60)
            require(process.returncode == 0, "clone_commit_failed")
        record.update(passed=True, committed=True, all32TablesByteExact=True, generatedTsvectorsByteExact=True,
                      temporaryPlanSealOnly=True, pointEndpointsUnchanged=True, winUiRenderingValidated=False)
    except Exception:
        (output / "failure-traceback.private.txt").write_text(traceback.format_exc(), encoding="utf8")
        raise
    finally:
        if process is not None and process.poll() is None:
            try:
                process.stdin.write(b"ROLLBACK;\n"); process.stdin.close(); process.wait(timeout=15)
            except (BrokenPipeError, OSError, subprocess.TimeoutExpired):
                process.kill(); process.wait(timeout=5)
        env.pop("PGPASSWORD", None); secrets = None
        with (output / "result.json").open("x", encoding="utf8") as stream: json.dump(record, stream, indent=2)
    return record


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("owner", "packet", "output", "corpus-manifest"): parser.add_argument("--" + name, required=True, type=Path)
    parser.add_argument("--corpus-manifest-sha256", required=True)
    parser.add_argument("--metadata-sha256", required=True)
    parser.add_argument("--execute", action="store_true")
    args = parser.parse_args(); args.packet = args.packet.resolve()
    metadata, schema, descriptors = validate_packet(args.packet, args.metadata_sha256)
    if not args.execute:
        print(json.dumps({"executed": False, "providerCalls": 0, "sqlCalled": False, "secretsRead": False, "packetValidated": True})); return
    print(json.dumps(execute(args, metadata, schema, descriptors)))


if __name__ == "__main__": main()
