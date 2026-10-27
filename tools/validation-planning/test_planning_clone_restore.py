"""Pure destination guards: no owned service, PostgreSQL or subprocess execution."""
import copy
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock
import restore_owned_windows_corpus as restore


class CloneRestoreTests(unittest.TestCase):
    def test_json_sql_uses_original_numeric_and_unicode_literals_without_reserializing(self):
        row = b'{"n":1.230000000000000000001,"text":"\xc3\xa9\\nquoted \'word\'"}\n'
        value = restore.json_recordset([row])
        self.assertIn((b"[" + row[:-1] + b"]").hex(), value)
        self.assertNotIn("quoted", value)

    def test_target_requires_identical_generated_columns_and_migrations(self):
        schema = {"serverMajor": 16, "encoding": "UTF8", "migrations": ["001"], "tables": []}
        for name in restore.TABLES:
            schema["tables"].append({"name": name, "primaryKey": ["id"], "columns": [{"name": "id", "generated": "", "type": "uuid"}]})
        target = copy.deepcopy(schema);target["lcCollate"]="another locale"
        restore.exact_schema(schema,target)
        target["tables"][0]["columns"][0]["generated"]="s"
        with self.assertRaisesRegex(ValueError,"column_contract"):
            restore.exact_schema(schema,target)

    def test_packet_paths_reject_escape_and_windows_special_paths(self):
        with tempfile.TemporaryDirectory() as folder:
            for value in ("../outside", "x/../a", "C:/x", "x\\a", "/abs", "x:stream"):
                with self.assertRaisesRegex(ValueError,"path_invalid"):
                    restore.packet_file(Path(folder),{"path":value,"sha256":"0"*64})

    def test_original_recordset_exposes_only_closed_table_names(self):
        with self.assertRaisesRegex(ValueError,"allowlist"):
            restore.readback_query("api_keys",{"name":"api_keys","primaryKey":["id"]})

    def test_vector_protocol_changes_only_seal_and_restores_original_seal(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder).resolve();metadata={"tables":{}}
            for name,rows in (("ingestion_vector_plans",[{"plan_id":str(i),"manifest_sealed":True,"target_endpoint":"http://source"} for i in range(4)]),
                              ("ingestion_vector_plan_points",[{"plan_id":"0","ordinal":0,"point_id":"exact"}])):
                path=root/(name+".jsonl");path.write_text("".join(json.dumps(r)+"\n" for r in rows),encoding="utf8")
                metadata["tables"][name]={"path":path.name,"sha256":restore.digest(path)}
            sql="".join(restore.iter_vector_restore_sql(root,metadata))
            self.assertIn("jsonb_set(v,'{manifest_sealed}','false'::jsonb)",sql)
            self.assertIn("SET manifest_sealed=s.manifest_sealed",sql)
            self.assertNotIn("DISABLE TRIGGER",sql)
            self.assertNotIn("UPDATE public.ingestion_vector_plans t SET target",sql)

    def _restore_without_services(self, folder, *, changed_table=False, commit_timeout=False):
        """Exercise the real transaction coordinator; only PG/process I/O is fake."""
        from types import SimpleNamespace
        root=Path(folder).resolve();target=root/"owner";runtime=target/"runtime";runtime.mkdir(parents=True)
        (runtime/"documents").mkdir();packet=root/"packet";packet.mkdir()
        original=root/"original.pdf";original.write_bytes(b"%PDF-synthetic-not-a-live-document\n")
        sha=restore.digest(original);docs=[{"docId":str(i),"revisionId":str(i),"docPath":f"source-{i}.pdf","sourceSha256":sha,"physicalPdfPath":str(original)} for i in range(4)]
        manifest=root/"manifest.json";manifest.write_text(json.dumps({"origin":"real_ingestion_worker","observationSha256":"1"*64,"documents":docs}),encoding="utf8")
        owner={"schemaVersion":"saaia-winui-local-runtime.v1","state":"ready","outputDirectory":str(target),"pgPort":23456,
               "planningCloneOnly":True,"documentsRoot":str(runtime/"documents"),"postgresDataDirectory":str(runtime/"pg-data"),
               "postgresBinDirectory":str(root/"fake-pg-bin"),"runId":"target"}
        owner_path=target/"environment.json";owner_path.write_text(json.dumps(owner),encoding="utf8")
        (runtime/"deployment.config.json").write_text(json.dumps({"VectorGc":{"Enabled":False},"Ingestion":{"WorkerEnabled":False},"AdvancedAnalysis":{"WorkerEnabled":False,"Provider":"disabled"}}),encoding="utf8")
        (runtime/"fixture-secrets.json").write_text(json.dumps({"pgPassword":"synthetic-test-only"}),encoding="utf8")
        schema={"serverMajor":16,"encoding":"UTF8","migrations":["synthetic"],"lcCollate":"source","lcCtype":"source","tables":[]}
        metadata={"workerObservationSha256":"1"*64,"ownerRunId":"source","documents":docs,"tables":{}}
        rows={};descriptors={}
        for name in restore.TABLES:
            if name=="ingestion_vector_plans":values=[{"plan_id":str(i),"manifest_sealed":True,"text":"é\\r\\n'quoted'"} for i in range(4)];key="plan_id"
            elif name=="ingestion_vector_plan_points":values=[{"point_id":str(i),"plan_id":"0","text":"exact"} for i in range(130)];key="point_id"
            else:values=[{"id":str(i),"text":"é😀\\r\\n'quoted'"+"x"*1000} for i in range(160)];key="id"
            raw=b"".join(json.dumps(v,ensure_ascii=False).encode()+b"\n" for v in values);rows[name]=raw
            path=packet/(name+".jsonl");path.write_bytes(raw)
            descriptor={"name":name,"primaryKey":[key],"columns":[{"name":k,"generated":"","type":"text"} for k in values[0]]}
            descriptors[name]=descriptor;schema["tables"].append(descriptor)
            metadata["tables"][name]={"path":path.name,"bytes":len(raw),"sha256":restore.digest(path),**restore.inspect_jsonl(path,descriptor)}
        dump=packet/"documents.dump";dump.write_bytes(b"PGDMP-synthetic");metadata["dump"]={"path":dump.name,"sha256":restore.digest(dump)}
        copy_sql=(b"COPY public.synthetic (id,text) FROM stdin;\n"+b"1\t\\\\\\n\\r\\t\\.\xc3\xa9\xf0\x9f\x98\x80\n"*100000+b"\\.\n")
        args=SimpleNamespace(owner=owner_path,packet=packet,output=runtime/"strict-worker-clone",corpus_manifest=manifest,
                             corpus_manifest_sha256=restore.digest(manifest),metadata_sha256="2"*64)
        class Pipe(io.BytesIO):
            def __init__(self):super().__init__();self.maximum_write=0;self.snapshot=b""
            def write(self,value):self.maximum_write=max(self.maximum_write,len(value));return super().write(value)
            def close(self):self.snapshot=self.getvalue();super().close()
        pipe=Pipe()
        class Process:
            stdin=pipe;returncode=None
            def poll(self):return self.returncode
            def wait(self,timeout=None):
                if commit_timeout and timeout==60:raise restore.subprocess.TimeoutExpired("synthetic-psql",60)
                self.returncode=0;return 0
            def kill(self):self.returncode=-1
        process=Process()
        def fake_run(command,**kwargs):
            if "--file" in command:
                Path(command[command.index("--file")+1]).write_bytes(copy_sql);data=b""
            else:
                sql=kwargs["input"]
                if b"data_directory" in sql:data=(owner["postgresDataDirectory"]+"\n").encode()
                elif b"'tenant'" in sql:data=b'{"tenant":1,"rows":0,"advanced":0}'
                else:data=json.dumps(schema).encode()
            return SimpleNamespace(returncode=0,stdout=data,stderr=b"")
        def fake_popen(command,**kwargs):
            for name,raw in rows.items():
                if changed_table and name==restore.TABLES[-1]:raw=raw.replace(b'"exact"',b'"other"',1)
                (args.output/"tables"/(name+".jsonl")).write_bytes(raw.replace(b"\n",b"\r\n"))
            kwargs["stdout"].write(b"READY_FOR_CLONE_COMMIT\n");kwargs["stdout"].flush()
            return process
        original_read=Path.read_bytes;original_open=Path.open
        forbidden=[]
        def no_document_read_all(path):
            if path.suffix==".jsonl" or path.name in {"restore30.private.sql","restore.stdin.private.sql"}:
                forbidden.append(str(path));raise AssertionError("whole documentary file read is forbidden")
            return original_read(path)
        class BoundedReader:
            def __init__(self,wrapped):self.wrapped=wrapped
            def __getattr__(self,name):return getattr(self.wrapped,name)
            def __enter__(self):return self
            def __exit__(self,*args):return self.wrapped.__exit__(*args)
            def __iter__(self):return iter(self.wrapped)
            def read(self,size=-1):
                if not 0<size<=65536:raise AssertionError("documentary read must use bounded blocks")
                return self.wrapped.read(size)
        def bounded_open(path,mode="r",*args,**kwargs):
            stream=original_open(path,mode,*args,**kwargs)
            if mode=="rb" and (path.suffix==".jsonl" or path.suffix==".sql"):return BoundedReader(stream)
            return stream
        with mock.patch.object(restore.subprocess,"run",side_effect=fake_run),mock.patch.object(restore.subprocess,"Popen",side_effect=fake_popen), \
             mock.patch.object(restore.subprocess,"check_output",return_value=b"pg_restore (PostgreSQL) 16.4"), \
             mock.patch.object(Path,"read_bytes",no_document_read_all),mock.patch.object(Path,"open",bounded_open):
            if changed_table:
                with self.assertRaisesRegex(ValueError,"clone_final_table_bytes_differ_ingestion_vector_plan_points"):
                    restore.execute(args,metadata,schema,descriptors)
                self.assertTrue(pipe.snapshot.endswith(b"ROLLBACK;\n"));self.assertNotIn(b"COMMIT;\n",pipe.snapshot)
            elif commit_timeout:
                with self.assertRaises(restore.subprocess.TimeoutExpired):restore.execute(args,metadata,schema,descriptors)
                receipt=json.loads((args.output/"result.json").read_text(encoding="utf8"))
                self.assertTrue(receipt["commitRequested"]);self.assertEqual("unknown",receipt["commitOutcome"])
                self.assertFalse(receipt["committed"]);self.assertFalse(receipt["passed"])
                self.assertTrue(receipt["transactionCleanupForcedKill"]);self.assertEqual(32,len(receipt["exactTables"]))
                self.assertNotIn(b"ROLLBACK;\n",pipe.snapshot)
            else:
                result=restore.execute(args,metadata,schema,descriptors)
                self.assertTrue(result["all32TablesByteExact"]);self.assertEqual(32,len(result["exactTables"]))
                self.assertTrue(pipe.snapshot.endswith(b"COMMIT;\n"))
                self.assertLessEqual(pipe.maximum_write,65536)
                with original_open(args.output/"restore.stdin.private.sql","rb") as stream:script=stream.read()
                self.assertEqual(pipe.snapshot,("\\i '"+(args.output/"restore.stdin.private.sql").as_posix()+"'\nCOMMIT;\n").encode())
                self.assertEqual(script[len(b"BEGIN;\n"+restore.SESSION_SETTINGS.encode()):][:len(copy_sql)],copy_sql)
                for name,expected in rows.items():
                    with original_open(args.output/"tables"/(name+".jsonl"),"rb") as stream:self.assertEqual(expected,stream.read())
        self.assertEqual([],forbidden)

    def test_restore_streams_sql_and_all_32_readbacks_without_whole_file_reads(self):
        with tempfile.TemporaryDirectory() as folder:self._restore_without_services(folder)

    def test_streamed_restore_still_rolls_back_on_one_changed_documentary_byte(self):
        with tempfile.TemporaryDirectory() as folder:self._restore_without_services(folder,changed_table=True)

    def test_timeout_after_commit_request_keeps_unknown_outcome_and_writes_receipt(self):
        with tempfile.TemporaryDirectory() as folder:self._restore_without_services(folder,commit_timeout=True)

    def test_jsonl_separator_conversion_preserves_escaped_controls_unicode_and_native_bytes(self):
        expected=b'{"id":"1","text":"\xc3\xa9\xf0\x9f\x98\x80\\r\\n\\\\r\\\\n\\t\\\"quoted\\\"","number":1.230000000000000000001}\n'
        with tempfile.TemporaryDirectory() as folder:
            for size in (1,2,3,7,64,65536):
                with self.subTest(block_bytes=size):
                    path=Path(folder)/f"table-{size}.jsonl";native=expected.replace(b"\n",b"\r\n")*3
                    path.write_bytes(native)
                    with mock.patch.object(restore,"STREAM_BLOCK_BYTES",size):restore.normalize_psql_jsonl(path)
                    self.assertEqual(expected*3,path.read_bytes())
                    self.assertEqual(native,path.with_name(path.stem+".psql-native.private.jsonl").read_bytes())

    def test_normalization_refuses_missing_record_delimiter_and_does_not_repair_data(self):
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/"table.jsonl";path.write_bytes(b'{"id":1}\r')
            with self.assertRaisesRegex(ValueError,"separator_invalid"):restore.normalize_psql_jsonl(path)
            self.assertEqual(b'{"id":1}\r',path.read_bytes())

    def test_lf_only_readback_is_kept_without_unnecessary_native_copy(self):
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/"table.jsonl";raw=b'{"id":"exact","body":"\\r\\n"}\n';path.write_bytes(raw)
            restore.normalize_psql_jsonl(path)
            self.assertEqual(raw,path.read_bytes());self.assertEqual([path],list(Path(folder).iterdir()))

    def test_include_and_output_paths_reject_psql_command_injection(self):
        for suffix in ("quote'file.sql","line\nfile.sql","line\rfile.sql","nul\0file.sql"):
            with self.subTest(path=suffix),self.assertRaisesRegex(ValueError,"filename_invalid"):
                restore.psql_file_literal(Path(suffix))

    def test_ready_marker_can_cross_read_block_boundary_and_timeout_still_refuses(self):
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/"stdout";path.write_bytes(b"noise\nREADY_FOR_CLONE_COMMIT\n")
            process=mock.Mock();process.poll.return_value=None
            with mock.patch.object(restore,"STREAM_BLOCK_BYTES",3):restore.await_restore_ready(process,path)
            path.write_bytes(b"no readiness")
            with self.assertRaisesRegex(ValueError,"before_comparison"):restore.await_restore_ready(process,path,timeout=0)


if __name__ == "__main__":unittest.main()
