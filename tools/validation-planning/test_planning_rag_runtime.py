"""Pure real-dependency identity contracts; no service, secret or HTTP access."""
import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
import uuid
from unittest.mock import patch
import planning_rag_runtime as rag


class PlanningRagTests(unittest.TestCase):
    def test_forwardings_cannot_retarget_the_existing_production_server_ports(self):
        root="/home/maxime/saaia-ingestion-qualification/"+str(uuid.uuid4())
        owner={"root":root,"endpoints":{role:"http://127.0.0.1:"+str(port) for role,port in
               (("backend",22521),("docling",22522),("tei",22523),("qdrant",22524))}}
        proof={"schemaVersion":"saaia-owned-ssh-forward.v1","sourceOwnerRoot":root,"hostTarget":"maxime@saaia-server",
               "process":{"pid":42,"path":"C:/Windows/System32/OpenSSH/ssh.exe","commandLineSha256":"a"*64},
               "forwardings":[{"localHost":"127.0.0.1","localPort":l,"remoteHost":"127.0.0.1","remotePort":r} for l,r in ((22531,22523),(22532,22524))]}
        rag.validate_forwarding(proof,owner)
        bad=copy.deepcopy(proof);bad["forwardings"][0]["remotePort"]=6333
        with self.assertRaisesRegex(ValueError,"forwardings_changed"):rag.validate_forwarding(bad,owner)
        bad=copy.deepcopy(proof);bad["forwardings"][0]["localHost"]="0.0.0.0"
        with self.assertRaisesRegex(ValueError,"forwardings_changed"):rag.validate_forwarding(bad,owner)
        wrong_owner=copy.deepcopy(owner);wrong_owner["root"]+="x"
        with self.assertRaisesRegex(ValueError,"owner_mismatch"):rag.validate_forwarding(proof,wrong_owner)
        # A fresh runtime has different remote ports; local listeners stay fixed.
        fresh=copy.deepcopy(owner)
        fresh["endpoints"]={role:"http://127.0.0.1:"+str(port) for role,port in
                            (("backend",22541),("docling",22542),("tei",22543),("qdrant",22544))}
        with self.assertRaisesRegex(ValueError,"forwardings_changed"):rag.validate_forwarding(proof,fresh)
        moved=copy.deepcopy(proof)
        moved["forwardings"][0]["remotePort"]=22543;moved["forwardings"][1]["remotePort"]=22544
        rag.validate_forwarding(moved,fresh)
        with self.assertRaisesRegex(ValueError,"forwardings_changed"):rag.validate_forwarding(moved,owner)

    def test_owner_ports_reject_non_loopback_credentials_paths_duplicates_and_invalid_ranges(self):
        owner={"endpoints":{role:"http://127.0.0.1:"+str(port) for role,port in
               (("backend",22541),("docling",22542),("tei",22543),("qdrant",22544))}}
        for url in ("http://localhost:22543","https://127.0.0.1:22543","http://127.0.0.1:22543/",
                    "http://name@127.0.0.1:22543","http://127.0.0.1:22543?x=1","http://127.0.0.1:22543\n","http://127.0.0.1:65536",
                    "http://127.0.0.1:80","http://127.0.0.1:5432",True):
            with self.subTest(url=url):
                bad=copy.deepcopy(owner);bad["endpoints"]["tei"]=url
                with self.assertRaises(ValueError):rag.source_forwardings(bad)
        bad=copy.deepcopy(owner);bad["endpoints"]["tei"]=bad["endpoints"]["qdrant"]
        with self.assertRaisesRegex(ValueError,"distinct_ports"):rag.source_forwardings(bad)

    def test_live_points_must_match_same_chunk_plan_and_real_finite_768vector(self):
        point=str(uuid.uuid4());chunk=str(uuid.uuid4())
        expected=[{"point_id":point,"canonical_chunk_id":chunk,"plan_fingerprint":"a"*64}]
        row={"id":point,"payload":{"chunk_id":chunk,"ingestion_plan_fingerprint":"a"*64},"vector":[0.5]*768}
        rag.check_points(expected,[row])
        for key,value in (("chunk_id",str(uuid.uuid4())),("ingestion_plan_fingerprint","b"*64)):
            bad=copy.deepcopy(row);bad["payload"][key]=value
            with self.assertRaisesRegex(ValueError,"binding_changed"):rag.check_points(expected,[bad])
        for vector in ([.5]*3,[float('nan')]*768,[True]*768):
            bad=copy.deepcopy(row);bad["vector"]=vector
            with self.assertRaisesRegex(ValueError,"dimension_or_nonfinite"):rag.check_points(expected,[bad])
        with self.assertRaisesRegex(ValueError,"duplicates"):rag.check_points(expected,[row,row])

    def test_pinned_dependency_input_cannot_change_after_registration(self):
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder).resolve()/"profile.json";path.write_bytes(b'{"synthetic":true}')
            entry=rag.pin(path);self.assertEqual(rag.checked(entry),path)
            path.write_bytes(b'{"synthetic":false}')
            with self.assertRaisesRegex(ValueError,"pinned_input_changed"):rag.checked(entry)

    def test_noexecute_attestation_never_runs_sql_or_opens_a_network_socket(self):
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/"profile.json";path.write_bytes(b'{}')
            args=["rag","attest","--profile",str(path),"--owner","never-read","--output","never-created"]
            with patch.object(rag.sys,"argv",args),patch.object(rag,"validate_profile",return_value={}),\
                 patch.object(rag,"attest",side_effect=AssertionError("execution crossed")),patch("builtins.print") as output:
                rag.main()
            receipt=json.loads(output.call_args.args[0]);self.assertFalse(receipt["sqlCalled"]);self.assertFalse(receipt["secretRead"])


if __name__=="__main__":unittest.main()
