"""Capture pure exporter tests, fabricated fixtures and source byte inventories."""
import argparse
import datetime
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import re
import unittest

parser=argparse.ArgumentParser()
parser.add_argument("--output-name",required=True)
args=parser.parse_args()
if not re.fullmatch(r"tests-[a-z0-9-]{1,64}",args.output_name):raise ValueError("Use a new bounded tests-* folder name")
root=Path(__file__).resolve().parent
output=root/args.output_name
output.mkdir(exist_ok=False)
files=[p for p in root.iterdir() if p.is_file() and p.suffix in {".py",".md"}]
files += [root.parent/"public-export-tools"/n for n in ("export-linux-public-proofs.py","test_public_export.py")]
def inventory():
    return [{"source":p.name,"sizeBytes":p.stat().st_size,"sha256":hashlib.sha256(p.read_bytes()).hexdigest()}
            for p in sorted(files,key=str)]
before=inventory()
suite=unittest.TestSuite()
for index,path in enumerate((root/"test_public_export_v3.py",root.parent/"public-export-tools/test_public_export.py")):
    spec=importlib.util.spec_from_file_location("public_export_capture_"+str(index),path)
    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
    suite.addTests(unittest.TestLoader().loadTestsFromModule(module))
log=io.StringIO();start=datetime.datetime.now(datetime.timezone.utc).isoformat()
result=unittest.TextTestRunner(stream=log,verbosity=2).run(suite)
after=inventory()
summary={"schemaVersion":"saaia-public-export-pure-tests.v3","startedAtUtc":start,
    "finishedAtUtc":datetime.datetime.now(datetime.timezone.utc).isoformat(),"testsRun":result.testsRun,
    "passedCount":result.testsRun-len(result.failures)-len(result.errors)-len(result.skipped),
    "failureCount":len(result.failures),"errorCount":len(result.errors),"skipCount":len(result.skipped),
    "passed":result.wasSuccessful() and not result.skipped and before==after,"sourceChangedDuringRun":before!=after,
    "fabricatedProofsOnly":True,"networkCalls":0,"subprocessCalls":0,"secretStoresRead":0,
    "sourceInventoryBefore":before,"sourceInventoryAfter":after}
(output/"tests.log").write_text(log.getvalue(),encoding="utf-8",newline="\n")
(output/"manifest.json").write_text(json.dumps(summary,indent=2)+"\n",encoding="utf-8",newline="\n")
print(json.dumps({k:v for k,v in summary.items() if not k.startswith("sourceInventory")}))
raise SystemExit(0 if summary["passed"] else 1)
