import contextlib
import copy
import hashlib
import io
import json
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import uuid

sys.path.insert(0, str(Path(__file__).parent))
import clone_contracts as contracts
import export_owned_linux_corpus as exporter


class CloneExportTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(); self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()

    def documents(self):
        return [{"tenantId": contracts.TENANT, "docId": str(uuid.uuid4()), "revisionId": str(uuid.uuid4()),
            "docPath": f"synthetic-{i}.pdf", "sourceSha256": str(i) * 64, "pageCount": pages,
            "status": "indexed", "ingestionVersion": 1, "indexedVersion": 1,
            "activeVectorPlanFingerprint": str(i) * 64, "ingestionJob": {"status": "done"}}
            for i, pages in enumerate((140, 32, 161, 177), 1)]

    def schema(self):
        return {"serverMajor": 16, "encoding": "UTF8", "migrations": ["075_native_raw_page_projection"],
            "tables": [{"name": name, "primaryKey": ["id"], "columns": [{"name": "id"}, {"name": "text"}]}
                       for name in contracts.TABLES]}

    def test_allowlist_has_all_32_tables_and_dump_excludes_only_sealed_pair(self):
        self.assertEqual(len(contracts.TABLES), 32)
        self.assertEqual(len(set(contracts.TABLES)), 32)
        self.assertEqual(set(contracts.TABLES) - set(contracts.DUMP_TABLES), contracts.SPECIAL_TABLES)
        self.assertEqual(len(contracts.DUMP_TABLES), 30)
        self.assertFalse(set(contracts.TABLES) & {"api_keys", "tenants", "advanced_analysis_jobs", "audit_events", "chat_messages"})

    def test_unregistered_or_injected_table_and_snapshot_are_rejected(self):
        for name in ("api_keys", "documents; DELETE FROM documents", 'documents"'):
            with self.subTest(name=name), self.assertRaises(ValueError): contracts.table_name(name)
        for snapshot in ("", "abc", "00000001-00000002-1';COMMIT;", "00000001-00000002-0"):
            with self.subTest(snapshot=snapshot), self.assertRaises(ValueError): contracts.snapshot_transaction(snapshot, "SELECT 1")

    def test_snapshot_queries_are_read_only_with_exact_snapshot_before_other_settings(self):
        sql = contracts.snapshot_transaction("00000001-00000002-1", "SELECT 1")
        self.assertTrue(sql.startswith("BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;\nSET TRANSACTION SNAPSHOT"))
        self.assertIn("client_encoding='UTF8'", sql); self.assertTrue(sql.endswith("ROLLBACK;\n"))

    def test_schema_requires_exact_tables_primary_keys_pg16_and_utf8(self):
        self.assertEqual(set(contracts.validate_schema(self.schema())), set(contracts.TABLES))
        for mutation in ("table", "pk", "version", "encoding", "identifier"):
            changed = self.schema()
            if mutation == "table": changed["tables"].pop()
            if mutation == "pk": changed["tables"][0]["primaryKey"] = []
            if mutation == "version": changed["serverMajor"] = 17
            if mutation == "encoding": changed["encoding"] = "LATIN1"
            if mutation == "identifier": changed["tables"][0]["columns"][0]["name"] = 'id";DELETE'
            with self.subTest(mutation=mutation), self.assertRaises(ValueError): contracts.validate_schema(changed)

    def test_readback_uses_full_row_and_locale_independent_primary_key_order(self):
        query = contracts.readback_query("documents", self.schema()["tables"][0])
        self.assertIn("to_jsonb(t)::text", query); self.assertIn('COLLATE "C"', query)
        self.assertIn("convert_to(to_jsonb(t.\"id\")::text,'UTF8')", query)
        with self.assertRaises(ValueError): contracts.readback_query("documents", {"name": "documents", "primaryKey": ['id"']})

    def test_jsonl_hash_preserves_document_crlf_unicode_and_final_spaces(self):
        target = self.root / "rows.jsonl"
        content = json.dumps({"id": "x", "text": "é😀\r\nexact  "}, ensure_ascii=False).encode() + b"\n"
        target.write_bytes(content)
        result = contracts.inspect_jsonl(target, self.schema()["tables"][0])
        self.assertEqual(result["sha256"], hashlib.sha256(content).hexdigest()); self.assertEqual(result["rowCount"], 1)
        target.write_bytes(content.replace(b"\\r\\n", b"\\n"))
        self.assertNotEqual(result["sha256"], contracts.inspect_jsonl(target, self.schema()["tables"][0])["sha256"])

    def test_jsonl_missing_columns_or_crlf_record_transport_are_refused(self):
        target = self.root / "rows.jsonl"
        for content in (b'{"id":"x"}\n', b'{"id":"x","text":"x"}\r\n', b'{"id":"x","text":"x"}'):
            target.write_bytes(content)
            with self.subTest(content=content), self.assertRaises(ValueError): contracts.inspect_jsonl(target, self.schema()["tables"][0])

    def test_real_worker_observation_requires_fresh_owner_full_originals_and_no_seed(self):
        run_id = str(uuid.uuid4())
        value = {"schemaVersion": "saaia-planning-worker-corpus-observation.v1", "ownerRunId": run_id,
                 "canonicalRowsSeeded": False, "advancedJobCount": 0, "activeJobCount": 0, "documents": self.documents()}
        self.assertEqual(len(contracts.validate_observation(value, run_id)), 4)
        for mutation in ("owner", "seed", "page", "tenant", "status"):
            changed = copy.deepcopy(value)
            if mutation == "owner": changed["ownerRunId"] = str(uuid.uuid4())
            if mutation == "seed": changed["canonicalRowsSeeded"] = True
            if mutation == "page": changed["documents"][0]["pageCount"] -= 1
            if mutation == "tenant": changed["documents"][0]["tenantId"] = str(uuid.uuid4())
            if mutation == "status": changed["documents"][0]["ingestionJob"]["status"] = "running"
            with self.subTest(mutation=mutation), self.assertRaises(ValueError): contracts.validate_observation(changed, run_id)

    def test_scope_rejects_another_tenant_extra_document_and_active_job(self):
        docs = self.documents()
        value = {"activeJobs": 0, "advancedJobs": 0, "documents": docs,
                 "tableTenantCounts": {name: 0 for name in contracts.TABLES if name != "ingestion_vector_plan_points"}}
        contracts.validate_scope(value, docs)
        for mutation in ("tenant", "extra", "active", "source", "deleted"):
            changed = copy.deepcopy(value)
            if mutation == "tenant": changed["tableTenantCounts"]["document_units"] = 1
            if mutation == "extra": changed["documents"].append(copy.deepcopy(docs[0]))
            if mutation == "active": changed["activeJobs"] = 1
            if mutation == "source": changed["documents"][0]["sourceSha256"] = "f" * 64
            if mutation == "deleted": changed["documents"][0]["status"] = "deleted"
            with self.subTest(mutation=mutation), self.assertRaises(ValueError): contracts.validate_scope(changed, docs)

    def test_incomplete_sealed_plan_foreign_point_and_wrong_generation_are_refused(self):
        docs = self.documents(); plans, points, chunks = [], [], {}
        for doc in docs:
            plan_id, chunk = str(uuid.uuid4()), str(uuid.uuid4())
            chunks[chunk] = {"tenant_id": contracts.TENANT, "revision_id": doc["revisionId"]}
            plans.append({"plan_id": plan_id, "tenant_id": contracts.TENANT, "doc_id": doc["docId"], "generation": 1,
                "plan_fingerprint": doc["activeVectorPlanFingerprint"], "manifest_sealed": True, "retired_at": None, "point_count": 1})
            points.append({"plan_id": plan_id, "ordinal": 0,
                           "point_id": contracts.vector_point_id(chunk, doc["activeVectorPlanFingerprint"]), "canonical_chunk_id": chunk})
        contracts.validate_plan_rows(plans, points, docs, chunks)
        for mutation in ("sealed", "count", "ordinal", "foreign", "generation", "chunk", "point_id", "swap_documents"):
            pp, ps = copy.deepcopy(plans), copy.deepcopy(points)
            if mutation == "sealed": pp[0]["manifest_sealed"] = False
            if mutation == "count": pp[0]["point_count"] = 2
            if mutation == "ordinal": ps[0]["ordinal"] = 1
            if mutation == "foreign": ps[0]["plan_id"] = str(uuid.uuid4())
            if mutation == "generation": pp[0]["generation"] = 2
            if mutation == "chunk": ps[0]["canonical_chunk_id"] = str(uuid.uuid4())
            if mutation == "point_id": ps[0]["point_id"] = str(uuid.uuid4())
            if mutation == "swap_documents":
                ps[0]["canonical_chunk_id"], ps[1]["canonical_chunk_id"] = ps[1]["canonical_chunk_id"], ps[0]["canonical_chunk_id"]
                for i in (0, 1):
                    ps[i]["point_id"] = contracts.vector_point_id(ps[i]["canonical_chunk_id"], pp[i]["plan_fingerprint"])
            with self.subTest(mutation=mutation), self.assertRaises(ValueError): contracts.validate_plan_rows(pp, ps, docs, chunks)

    def test_noexecute_does_not_open_observation_or_construct_docker_or_process(self):
        owner_path = self.root / "owner.json"; owner_path.write_text('{"fixture":true}', encoding="utf-8")
        output = self.root / "evidence/output"
        owner = {"root": str(self.root)}
        args = ["export", "--owner", str(owner_path), "--owner-sha256", exporter.digest(owner_path),
                "--worker-observation", str(self.root / "private-do-not-read.json"), "--worker-observation-sha256", "a" * 64,
                "--output", str(output)]
        stdout = io.StringIO()
        with patch.object(sys, "argv", args), patch.object(exporter, "validate_owner", return_value=owner), \
             patch.object(exporter, "OwnedDocker", side_effect=AssertionError("Docker called")), \
             patch.object(exporter.subprocess, "Popen", side_effect=AssertionError("Process called")), contextlib.redirect_stdout(stdout):
            exporter.main()
        self.assertFalse(json.loads(stdout.getvalue())["workerObservationRead"])
        self.assertFalse(output.exists())

    def test_export_cannot_write_inside_document_data_cache_or_an_existing_directory(self):
        valid = self.root / "evidence/new-export"
        self.assertEqual(exporter.export_path(valid, self.root), valid)
        for name in ("documents/new", "data/postgres/new", "cache/new", "private/new", "new"):
            with self.subTest(name=name), self.assertRaises(ValueError): exporter.export_path(self.root / name, self.root)
        valid.mkdir(parents=True)
        with self.assertRaises(ValueError): exporter.export_path(valid, self.root)

    def snapshot_process(self):
        commands = bytearray()
        stdin = SimpleNamespace(closed=False, write=lambda data: commands.extend(data), flush=lambda: None)
        stdin.close = lambda: setattr(stdin, "closed", True)
        process = SimpleNamespace(stdin=stdin, stdout=SimpleNamespace(fileno=lambda: 321, close=lambda: None),
                                  returncode=None, poll=lambda: None)
        process.wait = lambda timeout: setattr(process, "returncode", 0)
        owner = {"containers": [{"role": "postgres", "id": "a" * 64}], "databaseUser": "saaia_qual_fake", "database": "saaia_qual_fake"}
        return process, commands, owner

    def test_snapshot_is_held_until_exit_then_rolled_back_without_source_writes(self):
        process, commands, owner = self.snapshot_process()
        answer = iter(bytes([value]) for value in b"00000001-00000002-1\n")
        with patch.object(exporter.subprocess, "Popen", return_value=process), \
             patch.object(exporter.select, "select", return_value=([process.stdout], [], [])), patch.object(exporter.os, "read", side_effect=lambda *unused: next(answer)):
            with exporter.HeldSnapshot(owner, self.root / "snapshot.log") as snapshot:
                self.assertEqual(snapshot, "00000001-00000002-1")
                self.assertFalse(process.stdin.closed); self.assertNotIn(b"ROLLBACK", commands)
        self.assertTrue(process.stdin.closed); self.assertTrue(commands.endswith(b"ROLLBACK;\n"))
        self.assertNotIn(b"COMMIT", commands); self.assertIn(b"READ ONLY", commands)

    def test_snapshot_timeout_rolls_back_our_connection(self):
        process, commands, owner = self.snapshot_process()
        with patch.object(exporter.subprocess, "Popen", return_value=process), \
             patch.object(exporter.select, "select", return_value=([], [], [])):
            with self.assertRaisesRegex(ValueError, "snapshot_start_timeout"):
                with exporter.HeldSnapshot(owner, self.root / "snapshot.log"):
                    self.fail("Invalid snapshot was admitted")
        self.assertTrue(process.stdin.closed); self.assertTrue(commands.endswith(b"ROLLBACK;\n"))

    def test_malformed_snapshot_never_reaches_pg_dump_or_readback(self):
        process, commands, owner = self.snapshot_process()
        answer = iter(bytes([value]) for value in b"wrong-snapshot\n")
        with patch.object(exporter.subprocess, "Popen", return_value=process), \
             patch.object(exporter.select, "select", return_value=([process.stdout], [], [])), patch.object(exporter.os, "read", side_effect=lambda *unused: next(answer)):
            with self.assertRaisesRegex(ValueError, "snapshot_identity_invalid"):
                with exporter.HeldSnapshot(owner, self.root / "snapshot.log"):
                    self.fail("Malformed snapshot was admitted")
        self.assertTrue(commands.endswith(b"ROLLBACK;\n"))


if __name__ == "__main__":
    unittest.main()
