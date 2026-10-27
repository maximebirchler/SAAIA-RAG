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
        proof={"schemaVersion":"saaia-owned-ssh-forward.v1","sourceOwnerRoot":root,"hostTarget":"maxime@saaia-server",
               "process":{"pid":42,"path":"C:/Windows/System32/OpenSSH/ssh.exe","commandLineSha256":"a"*64},
               "forwardings":[{"localHost":"127.0.0.1","localPort":l,"remoteHost":"127.0.0.1","remotePort":r} for l,r in ((22531,22523),(22532,22524))]}
        rag.validate_forwarding(proof,root)
        bad=copy.deepcopy(proof);bad["forwardings"][0]["remotePort"]=6333
        with self.assertRaisesRegex(ValueError,"forwardings_changed"):rag.validate_forwarding(bad,root)
        bad=copy.deepcopy(proof);bad["forwardings"][0]["localHost"]="0.0.0.0"
        with self.assertRaisesRegex(ValueError,"forwardings_changed"):rag.validate_forwarding(bad,root)
        with self.assertRaisesRegex(ValueError,"owner_mismatch"):rag.validate_forwarding(proof,root+"x")

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
