"""Pure destination guards: no owned service, PostgreSQL or subprocess execution."""
import copy
import json
from pathlib import Path
import tempfile
import unittest
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
            sql=restore.vector_restore_sql(root,metadata)
            self.assertIn("jsonb_set(v,'{manifest_sealed}','false'::jsonb)",sql)
            self.assertIn("SET manifest_sealed=s.manifest_sealed",sql)
            self.assertNotIn("DISABLE TRIGGER",sql)
            self.assertNotIn("UPDATE public.ingestion_vector_plans t SET target",sql)


if __name__ == "__main__":unittest.main()
