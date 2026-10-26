"""All Git initialization/staging below is confined to new synthetic repositories."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
SCRIPT = HERE.parents[1] / "verify-tested-source-index.py"
spec = importlib.util.spec_from_file_location("index_anchor", SCRIPT)
module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)


class SourceIndexAnchorTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="synthetic-", dir=HERE)
        self.root = Path(self.temp.name).resolve()
        assert self.root.is_relative_to(HERE.resolve())
        self.repo = self.root / "repo"; self.repo.mkdir()
        self.command("init", "--quiet")
        self.command("config", "core.autocrlf", "false")
        self.inventory = self.root / "inventory.json"

    def tearDown(self): self.temp.cleanup()

    def command(self, *args, data=None):
        assert self.repo.resolve().is_relative_to(HERE.resolve())
        assert self.repo.name == "repo" and self.repo.parent.name.startswith("synthetic-")
        env = {k:v for k,v in os.environ.items() if not k.upper().startswith("GIT_")}
        env.update({"GIT_CONFIG_GLOBAL": os.devnull, "GIT_CONFIG_NOSYSTEM": "1"})
        result = subprocess.run(["git", "-C", str(self.repo), *args], input=data,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False, env=env, timeout=20)
        if result.returncode: raise AssertionError("Synthetic Git setup failed: " + str(result.returncode))
        return result.stdout

    def write(self, path, data, stage=True):
        target = self.repo / path; target.parent.mkdir(parents=True, exist_ok=True); target.write_bytes(data)
        if stage: self.command("add", "--", path)
        return target

    def capture(self, paths):
        rows = [{"path":path, "sha256":module.sha((self.repo/path).read_bytes())} for path in paths]
        self.inventory.write_text(json.dumps(rows, ensure_ascii=False), encoding="utf-8")
        return module.sha(json.dumps(rows, ensure_ascii=False, separators=(",", ":")).encode())

    def verify(self, fingerprint, declared=None):
        return module.verify(self.repo, self.inventory, fingerprint, declared)

    def test_exact_binary_bytes_for_all_code_families_and_unicode_path(self):
        paths = ["backend/Value.cs", "tools/calcul_é.py", "tools/Check.ps1", "sql/071.sql"]
        for path in paths: self.write(path, b"SOURCE_LITERAL_SYNTHETIC\r\nsecond line\r\n")
        fingerprint = self.capture(paths); before = (self.repo/".git/index").read_bytes()
        report = self.verify(fingerprint)
        self.assertTrue(report["strictPassed"]); self.assertEqual(4,report["strictExactCount"])
        self.assertEqual(before,(self.repo/".git/index").read_bytes())
        self.assertNotIn("SOURCE_LITERAL_SYNTHETIC",json.dumps(report))
        self.assertEqual(0,report["gitMutationCommandsExecuted"])

    def test_current_eol_change_is_reported_but_never_strict_pass(self):
        path="backend/Value.cs"; self.write(path,b"first\nsecond\n"); fingerprint=self.capture([path])
        self.write(path,b"first\r\nsecond\r\n",stage=False)
        report=self.verify(fingerprint); row=report["files"][0]
        self.assertEqual("EOL_ONLY_PROVEN",row["workingTreeVsTested"])
        self.assertEqual("EXACT",row["indexVsTested"]); self.assertTrue(row["testedFileChangedAfterRun"])
        self.assertFalse(report["strictPassed"])

    def test_staged_eol_change_is_reported_even_if_current_matches_tested(self):
        path="backend/Value.cs"; self.write(path,b"first\nsecond\n"); fingerprint=self.capture([path])
        oid=self.command("hash-object","-w","--stdin",data=b"first\r\nsecond\r\n").decode().strip()
        self.command("update-index","--cacheinfo","100644,"+oid+","+path)
        report=self.verify(fingerprint); row=report["files"][0]
        self.assertEqual("EXACT",row["workingTreeVsTested"]);self.assertEqual("EOL_ONLY_PROVEN",row["indexVsTested"])
        self.assertFalse(report["strictPassed"])

    def test_real_content_difference_cannot_hide_behind_matching_current_and_index(self):
        path="backend/Value.cs";self.write(path,b"limit = 20;\n");fingerprint=self.capture([path])
        self.write(path,b"limit = -20;\n")
        report=self.verify(fingerprint);row=report["files"][0]
        self.assertEqual("DIFFERENT_BYTES_NOT_EXPLAINED_BY_UNIFORM_EOL",row["workingTreeVsTested"])
        self.assertEqual("EXACT",row["indexVsWorkingTree"]);self.assertFalse(report["strictPassed"])

    def test_absent_from_index_is_distinct_from_changed_source(self):
        path="backend/New.cs";self.write(path,b"new source\n",stage=False);fingerprint=self.capture([path])
        report=self.verify(fingerprint);row=report["files"][0]
        self.assertEqual("ABSENT_FROM_INDEX",row["indexState"])
        self.assertFalse(row["testedFileChangedAfterRun"]);self.assertEqual(1,report["absentFromIndex"])

    def test_test_code_changed_after_run_is_explicit(self):
        path="backend/Product.Tests/Checks.cs";self.write(path,b"assert original\n");fingerprint=self.capture([path])
        self.write(path,b"assert modified\n",stage=False)
        report=self.verify(fingerprint);self.assertEqual(1,report["testCodeFilesChangedAfterRun"])
        self.assertTrue(report["files"][0]["testCodeChangedAfterRun"])

    def test_declared_config_and_evidence_are_not_claimed_as_tested_source(self):
        self.write("backend/Value.cs",b"code\n");fingerprint=self.capture(["backend/Value.cs"])
        configs={".gitattributes":b"artifacts/** -text\n","artifacts/proof.json":b'{"passed":true}\n'}
        rows=[]
        for path,data in configs.items():self.write(path,data);rows.append({"path":path,"sha256":module.sha(data)})
        declared=self.root/"declared.json";declared.write_text(json.dumps({"files":rows}))
        report=self.verify(fingerprint,[declared]);self.assertTrue(report["strictPassed"])
        self.assertEqual(1,report["roleCounts"]["configuration"]);self.assertEqual(1,report["roleCounts"]["evidence"])
        for row in report["files"]:
            if not row["inventoryMember"]:
                self.assertEqual("NO_TESTED_BASELINE",row["workingTreeVsTested"])
                self.assertEqual("DECLARED_ONLY_NOT_CLAIMED_TESTED",row["provenance"])

    def test_declared_hash_never_overwrites_conflicting_tested_baseline(self):
        path="backend/Value.cs";self.write(path,b"old\n");fingerprint=self.capture([path])
        declared=self.root/"declared.json";declared.write_text(json.dumps([{"path":path,"sha256":module.sha(b"new\n")}]))
        report=self.verify(fingerprint,[declared]);self.assertTrue(report["files"][0]["declaredBaselineConflictsWithTested"])
        self.assertFalse(report["strictPassed"])

    def test_wrong_inventory_fingerprint_is_rejected(self):
        self.write("backend/Value.cs",b"data");self.capture(["backend/Value.cs"])
        with self.assertRaises(ValueError):self.verify("a"*64)

    def test_inventory_parent_traversal_is_rejected(self):
        rows=[{"path":"../outside.cs","sha256":"a"*64}]
        self.inventory.write_text(json.dumps(rows));fingerprint=module.sha(json.dumps(rows,separators=(",",":")).encode())
        with self.assertRaises(ValueError):self.verify(fingerprint)

    def test_non_utf8_git_blob_is_hashed_without_text_decoding(self):
        path="backend/Value.cs";self.write(path,b"\xff\x00\r\n\xfe");fingerprint=self.capture([path])
        self.assertTrue(self.verify(fingerprint)["strictPassed"])

    def test_unmerged_index_is_not_treated_as_unstaged_file(self):
        path="backend/Value.cs";self.write(path,b"data\n");fingerprint=self.capture([path])
        oid=self.command("rev-parse",":"+path).decode().strip();self.command("update-index","--force-remove",path)
        lines=''.join("100644 "+oid+" "+str(stage)+"\t"+path+"\n" for stage in [1,2,3]).encode()
        self.command("update-index","--index-info",data=lines)
        report=self.verify(fingerprint);self.assertEqual("UNMERGED_INDEX",report["files"][0]["indexState"])
        self.assertEqual(0,report["absentFromIndex"]);self.assertFalse(report["strictPassed"])

    def test_inherited_alternate_index_environment_is_ignored(self):
        self.write("backend/Value.cs",b"data\n");fingerprint=self.capture(["backend/Value.cs"])
        with patch.dict(os.environ,{"GIT_INDEX_FILE":str(self.root/"other-index"),"GIT_DIR":str(self.root/"other-git")}):
            self.assertTrue(self.verify(fingerprint)["strictPassed"])
        self.assertFalse((self.root/"other-index").exists())

    def test_mixed_eol_is_proven_when_exact_tested_peer_bytes_exist_in_index(self):
        path="backend/Value.cs";self.write(path,b"first\r\nsecond\n");fingerprint=self.capture([path])
        self.write(path,b"first\nsecond\n",stage=False)
        report=self.verify(fingerprint)
        self.assertEqual("EOL_ONLY_PROVEN",report["files"][0]["workingTreeVsTested"])
        self.assertFalse(report["strictPassed"])

    def test_concurrent_index_change_is_detected_without_worktree_change(self):
        path="backend/Value.cs";self.write(path,b"old\n");fingerprint=self.capture([path]);original=module.read_blobs
        def change_index(repo,oids):
            result=original(repo,oids)
            oid=self.command("hash-object","-w","--stdin",data=b"new index only\n").decode().strip()
            self.command("update-index","--cacheinfo","100644,"+oid+","+path)
            return result
        with patch.object(module,"read_blobs",side_effect=change_index):report=self.verify(fingerprint)
        self.assertTrue(report["indexChangedDuringVerification"]);self.assertFalse(report["strictPassed"])

    def test_concurrent_worktree_edit_after_first_read_is_detected(self):
        path="backend/Product.Tests/Checks.cs";self.write(path,b"old\n");fingerprint=self.capture([path]);original=module.versus_bytes
        def change_current(current,indexed):
            result=original(current,indexed);self.write(path,b"late change\n",stage=False);return result
        with patch.object(module,"versus_bytes",side_effect=change_current):report=self.verify(fingerprint)
        self.assertEqual(1,report["workingTreeChangedDuringVerificationCount"])
        self.assertEqual(1,report["testCodeFilesChangedAfterRun"]);self.assertFalse(report["strictPassed"])

    def test_missing_current_source_is_explicit_even_if_index_still_matches(self):
        target=self.write("backend/Value.cs",b"data\n");fingerprint=self.capture(["backend/Value.cs"]);target.unlink()
        report=self.verify(fingerprint);row=report["files"][0]
        self.assertEqual("MISSING",row["workingTreeVsTested"]);self.assertEqual("EXACT",row["indexVsTested"])
        self.assertTrue(row["testedFileChangedAfterRun"]);self.assertFalse(report["strictPassed"])


if __name__=="__main__":unittest.main(verbosity=2)
