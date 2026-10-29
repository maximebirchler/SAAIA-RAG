"""Opt-in READ ONLY source export from one newly owned Linux ingestion runtime.

No SSH, SQL writes, provider requests, credentials, Docker mutation or imports.
One held PostgreSQL snapshot covers the dump and every exact JSONL readback.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import select
import shutil
import subprocess
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "validation-ingestion"))
from linux_owned_runtime import OwnedDocker, assert_owned_path, validate_owner
from clone_contracts import (DUMP_TABLES, SESSION_SETTINGS, SNAPSHOT_PATTERN, TABLES, TENANT,
    inspect_jsonl, readback_query, require, snapshot_transaction, validate_observation, validate_plan_rows, validate_schema, validate_scope)


def digest(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def new_json(path, value):
    with path.open("x", encoding="utf-8", newline="\n") as target:
        json.dump(value, target, ensure_ascii=False, indent=2, allow_nan=False); target.write("\n")


def export_path(value, root):
    result = assert_owned_path(value, root)
    require(result.parent == root / "evidence" and not result.exists(), "fresh_direct_evidence_subdirectory_required")
    return result


def psql_command(owner):
    postgres = next(row for row in owner["containers"] if row["role"] == "postgres")
    return ["docker", "--host", "unix:///var/run/docker.sock", "exec", "-i", postgres["id"], "psql",
        "-X", "-A", "-t", "-q", "-w", "-v", "ON_ERROR_STOP=1", "--username", owner["databaseUser"], "--dbname", owner["database"]]


class HeldSnapshot:
    def __init__(self, owner, error_path):
        self.owner, self.error_path, self.process, self.errors = owner, error_path, None, None

    def __enter__(self):
        self.errors = self.error_path.open("xb")
        self.process = subprocess.Popen(psql_command(self.owner), stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.errors)
        try:
            self.process.stdin.write(("BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;\n" + SESSION_SETTINGS
                + "SELECT pg_export_snapshot();\n").encode("utf-8")); self.process.stdin.flush()
            deadline, answer = time.monotonic() + 30, bytearray()
            while not answer.endswith(b"\n"):
                remaining = deadline - time.monotonic()
                require(remaining > 0 and select.select([self.process.stdout], [], [], remaining)[0], "snapshot_start_timeout")
                char = os.read(self.process.stdout.fileno(), 1)
                require(char and len(answer) < 128, "snapshot_start_failed")
                answer.extend(char)
            self.snapshot = answer.decode("ascii").strip()
            require(re.fullmatch(SNAPSHOT_PATTERN, self.snapshot), "snapshot_identity_invalid")
            return self.snapshot
        except BaseException:
            self.__exit__(*sys.exc_info()); raise

    def __exit__(self, *unused):
        if self.process:
            try:
                if self.process.poll() is None:
                    self.process.stdin.write(b"ROLLBACK;\n"); self.process.stdin.close()
                    self.process.wait(timeout=15)
            except (BrokenPipeError, subprocess.TimeoutExpired):
                # Terminate only our docker-exec client; source SQL is READ ONLY.
                self.process.terminate()
                try:
                    self.process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    self.process.kill(); self.process.wait(timeout=5)
            finally:
                self.process.stdout.close()
                if not self.process.stdin.closed:
                    self.process.stdin.close()
        if self.errors:
            self.errors.close()
        if self.process and unused[0] is None:
            require(self.process.returncode == 0, "snapshot_transaction_did_not_close_cleanly")


def run_file(command, output, error_path, sql=None, timeout=600):
    with output.open("xb") as target, error_path.open("xb") as errors:
        result = subprocess.run(command, input=None if sql is None else sql.encode("utf-8"), stdout=target, stderr=errors,
                                timeout=timeout, check=False)
    require(result.returncode == 0, "read_only_source_command_failed_inspect_private_stderr")
    require(output.stat().st_size <= 8 * 1024**3, "source_export_file_size_limit")


# Database locale attributes are catalog fields, not PostgreSQL GUC settings.
SCHEMA_QUERY = """SELECT jsonb_build_object(
 'serverMajor',current_setting('server_version_num')::int/10000,'serverVersion',version(),
 'encoding',current_setting('server_encoding'),'lcCollate',db.datcollate,'lcCtype',db.datctype,
 'migrations',(SELECT jsonb_agg(version ORDER BY version COLLATE "C") FROM schema_migrations),
 'tables',(SELECT jsonb_agg(jsonb_build_object('name',c.relname,
  'primaryKey',(SELECT jsonb_agg(a.attname ORDER BY keys.ordinality) FROM pg_index i
    CROSS JOIN LATERAL unnest(i.indkey) WITH ORDINALITY keys(attnum,ordinality)
    JOIN pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=keys.attnum WHERE i.indrelid=c.oid AND i.indisprimary),
  'columns',(SELECT jsonb_agg(jsonb_build_object('name',a.attname,'type',format_type(a.atttypid,a.atttypmod),
    'ordinal',a.attnum,'notNull',a.attnotnull,'generated',a.attgenerated,'identity',a.attidentity,
    'defaultExpression',pg_get_expr(d.adbin,d.adrelid)) ORDER BY a.attnum)
    FROM pg_attribute a LEFT JOIN pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
    WHERE a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped)) ORDER BY c.relname COLLATE "C")
    FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
    WHERE n.nspname='public' AND c.relkind='r' AND c.relname IN (__TABLES__)))
 FROM pg_database db WHERE db.datname=current_database()"""


def scope_query():
    wrong_tenants = ",".join("'" + table + "',(SELECT count(*) FROM public.\"" + table + "\" WHERE tenant_id<>'" + TENANT + "'::uuid)"
        for table in TABLES if table != "ingestion_vector_plan_points")
    return """SELECT jsonb_build_object('activeJobs',(SELECT count(*) FROM ingestion_jobs WHERE status IN ('queued','running','paused')),
 'advancedJobs',(SELECT count(*) FROM advanced_analysis_jobs),'documents',
 (SELECT jsonb_agg(jsonb_build_object('tenantId',d.tenant_id,'docId',d.doc_id,'revisionId',r.revision_id,'docPath',d.doc_path,'status',d.status,
 'sourceSha256',encode(r.source_hash,'hex'),'pageCount',d.page_count,'ingestionVersion',d.ingestion_version,'indexedVersion',d.indexed_version))
 FROM documents d JOIN document_revisions r ON r.tenant_id=d.tenant_id AND r.doc_id=d.doc_id AND r.indexed_version=d.indexed_version),
 'tableTenantCounts',jsonb_build_object(""" + wrong_tenants + "))"


def execute(args, owner):
    root = Path(owner["root"])
    output = export_path(args.output, root)
    observation_path = assert_owned_path(args.worker_observation, root)
    require(digest(observation_path) == args.worker_observation_sha256, "worker_observation_bytes_changed")
    observation = json.loads(observation_path.read_text(encoding="utf-8-sig"))
    docs = validate_observation(observation, owner["runId"])
    docker = OwnedDocker(owner, execute=True); safe_before, _ = docker.inspect_all()
    require(shutil.disk_usage(root).free >= 12 * 1024**3, "insufficient_free_space_for_owned_export")
    os.umask(0o077); output.mkdir(mode=0o700)
    (output / "tables").mkdir(mode=0o700); (output / "errors").mkdir(mode=0o700)
    record = {"schemaVersion": "saaia-owned-linux-documentary-clone-export-result.v1", "passed": False,
              "ownerRunId": owner["runId"], "sqlWrites": 0, "providerCalls": 0, "sourceStopped": False}
    try:
        with HeldSnapshot(owner, output / "errors/snapshot.private.log") as snapshot:
            command = psql_command(owner)
            schema_path = output / "schema.json"
            schema_query = SCHEMA_QUERY.replace("__TABLES__", ",".join("'" + name + "'" for name in TABLES))
            run_file(command, schema_path, output / "errors/schema.private.log", snapshot_transaction(snapshot, schema_query))
            schema = json.loads(schema_path.read_bytes()); descriptors = validate_schema(schema)
            scope_path = output / "scope.private.json"
            run_file(command, scope_path, output / "errors/scope.private.log", snapshot_transaction(snapshot, scope_query()))
            scope = json.loads(scope_path.read_bytes()); validate_scope(scope, docs)
            readbacks = {}
            for name in TABLES:
                target = output / "tables" / (name + ".jsonl")
                run_file(command, target, output / "errors" / (name + ".private.log"),
                         snapshot_transaction(snapshot, readback_query(name, descriptors[name])))
                readbacks[name] = {"path": "tables/" + name + ".jsonl", "columns": [c["name"] for c in descriptors[name]["columns"]],
                    "primaryKey": descriptors[name]["primaryKey"], "bytes": target.stat().st_size,
                    **inspect_jsonl(target, descriptors[name])}
            require(readbacks["documents"]["rowCount"] == readbacks["document_revisions"]["rowCount"] == 4,
                    "export_requires_exactly_four_document_revisions")
            def rows(name):
                with (output / "tables" / (name + ".jsonl")).open("rb") as source:
                    return [json.loads(line) for line in source]
            validate_plan_rows(rows("ingestion_vector_plans"), rows("ingestion_vector_plan_points"), docs,
                               {row["retrieval_chunk_id"]: row for row in rows("retrieval_chunks")})
            postgres = next(row for row in owner["containers"] if row["role"] == "postgres")
            dump_command = ["docker", "--host", "unix:///var/run/docker.sock", "exec", postgres["id"], "pg_dump", "--format=custom",
                "--data-only", "--no-owner", "--no-privileges", "--no-tablespaces", "--no-comments", "--no-security-labels",
                "--snapshot=" + snapshot, "--username=" + owner["databaseUser"], "--dbname=" + owner["database"],
                *["--table=public." + name for name in DUMP_TABLES]]
            dump_path = output / "documents.dump"
            run_file(dump_command, dump_path, output / "errors/pg-dump.private.log")
            with dump_path.open("rb") as source:
                require(dump_path.stat().st_size > 5 and source.read(5) == b"PGDMP", "custom_dump_header_invalid")
            metadata = {"schemaVersion": "saaia-owned-documentary-sql-clone.v1", "complete": True,
                "ownerRunId": owner["runId"], "tenantId": TENANT, "sourceOwnerSha256": args.owner_sha256,
                "workerObservationSha256": args.worker_observation_sha256, "snapshotId": snapshot,
                "serverMajor": schema["serverMajor"], "encoding": "UTF8", "jsonlRecordDelimiter": "LF",
                "rowEncoding": "PostgreSQL to_jsonb(full_row)::text UTF8; deterministic PK UTF8-hex order",
                "tableOrder": list(TABLES), "dumpTables": list(DUMP_TABLES),
                "specialRestoreProtocol": "insert exact plan headers with manifest_sealed=false; exact points; reseal to source state; compare all final bytes",
                "generatedColumnComparisonRequired": True, "restoreExecuted": False, "targetReadbackVerified": False,
                "sourceSqlWrites": 0, "providerCalls": 0,
                "documents": [{key: d[key] for key in ("tenantId", "docId", "revisionId", "docPath", "sourceSha256", "pageCount", "ingestionVersion", "indexedVersion")} for d in docs],
                "schema": {"path": "schema.json", "sha256": digest(schema_path)},
                "dump": {"path": "documents.dump", "sha256": digest(dump_path), "bytes": dump_path.stat().st_size},
                "scope": {"path": "scope.private.json", "sha256": digest(scope_path)}, "tables": readbacks,
                "excluded": ["tenants (target synthetic identity checked independently)", "api_keys", "chat_*", "audit_events",
                    "advanced_analysis_*", "runtime_*", "admin_jobs", "schema_migrations (versions compared only)"]}
        safe_after, _ = docker.inspect_all()
        require(safe_before == safe_after and digest(args.owner) == args.owner_sha256
                and digest(observation_path) == args.worker_observation_sha256, "source_ownership_or_observation_changed")
        new_json(output / "metadata.json", metadata)
        record.update(passed=True, metadataSha256=digest(output / "metadata.json"), tableCount=len(TABLES),
                      dumpTableCount=len(DUMP_TABLES), heldSnapshotCoversAllFiles=True)
    finally:
        new_json(output / "result.json", record)
    return record


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("owner", "worker-observation", "output"):
        parser.add_argument("--" + name, required=True, type=Path)
    for name in ("owner-sha256", "worker-observation-sha256"):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--execute", action="store_true")
    args = parser.parse_args()
    require(all(re.fullmatch(r"[a-f0-9]{64}", value) for value in (args.owner_sha256, args.worker_observation_sha256)), "exact_sha256_pins_required")
    require(digest(args.owner) == args.owner_sha256, "source_owner_bytes_changed")
    owner = validate_owner(json.loads(args.owner.read_text(encoding="utf-8-sig")))
    export_path(args.output, Path(owner["root"]))
    if not args.execute:
        print(json.dumps({"executeRequested": False, "providerCalls": 0, "dockerCalled": False,
            "workerObservationRead": False, "sqlWrites": 0, "tableCount": len(TABLES), "dumpTableCount": len(DUMP_TABLES)}))
        return
    require(sys.platform == "linux", "source_export_requires_linux")
    print(json.dumps(execute(args, owner)))


if __name__ == "__main__":
    main()
