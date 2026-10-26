"""Ownership guards use Docker-shaped dictionaries only; never a Docker daemon."""
from __future__ import annotations
import copy
import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[2] / "validation-ingestion/linux_owned_runtime.py"
SPEC = importlib.util.spec_from_file_location("saaia_linux_owned_runtime", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def fixture():
    run = "11111111-2222-4333-8444-555555555555"
    root = "/home/maxime/saaia-ingestion-qualification/" + run
    roles = sorted(MODULE.ROLES)
    owner = {"schemaVersion": "saaia-linux-ingestion-runtime.v1", "runId": run,
             "root": root, "project": "saaiaqual_111111112222", "documentsRoot": root + "/documents",
             "configurationPath": root + "/deploy/deployment.config.json",
             "database": "saaia_qual_db", "databaseUser": "saaia_qual_user",
             "qdrantCollection": "ingestion_11111111222243338444555555555555",
             "endpoints": {role: "http://127.0.0.1:" + str(15100 + i)
                           for i, role in enumerate(roles) if role != "postgres"},
             "containers": [{"role": role, "service": role, "id": str(i + 1) * 64,
                             "imageId": "sha256:" + "a" * 64} for i, role in enumerate(roles)]}
    observed = []
    for record in owner["containers"]:
        role = record["role"]
        ports = {} if role == "postgres" else {"80/tcp": [{"HostIp": "127.0.0.1",
            "HostPort": owner["endpoints"][role].rsplit(":", 1)[1]}]}
        observed.append({"Id": record["id"], "Image": record["imageId"], "State": {"Running": True},
            "Config": {"Env": [], "Labels": {"com.docker.compose.project": owner["project"],
                "com.docker.compose.service": role, MODULE.RUN_LABEL: run}},
            "HostConfig": {"Memory": 1024 ** 3, "NanoCpus": 1000000000, "PortBindings": ports},
            "NetworkSettings": {"Ports": ports, "Networks": {owner["project"] + "_default": {}}},
            "Mounts": [{"Type": "bind", "Source": root + "/data/" + role}]})
        if role == "backend":
            observed[-1]["Config"].update({"Entrypoint": ["dotnet", "SAAIA.Backend.dll"], "Cmd": None})
            observed[-1]["Mounts"] += [{"Type": "bind", "Source": owner["documentsRoot"], "Destination": "/app/documents"},
                {"Type": "bind", "Source": owner["configurationPath"], "Destination": "/app/deployment.config.json"}]
    return owner, observed


class LinuxRuntimeGuardTests(unittest.TestCase):
    def test_model_artifact_mapping_cannot_ignore_a_more_specific_bind(self):
        container = {"Mounts": [{"Type": "bind", "Source": "/owned/cache", "Destination": "/data"},
            {"Type": "bind", "Source": "/owned/other.onnx", "Destination": "/data/snapshot/onnx/model.onnx"}]}
        self.assertEqual("/owned/other.onnx", MODULE.mounted_source(container, "/data/snapshot/onnx/model.onnx"))
        self.assertEqual("/owned/cache/snapshot/config.json", MODULE.mounted_source(container, "/data/snapshot/config.json"))
    def test_network_egress_policy_is_explicit_and_never_inferred_from_a_project_name(self):
        import json
        for explicit_bridge, internal, passed in [(False, True, True), (False, False, False), (True, False, True)]:
            owner, observed = fixture()
            if explicit_bridge: owner["networkPolicy"] = "dedicated_bridge"
            networks = [{"Name": owner["project"] + "_default", "Internal": internal,
                         "Labels": {"com.docker.compose.project": owner["project"]}}]
            runtime = MODULE.OwnedDocker(owner, True)
            with self.subTest(explicit_bridge=explicit_bridge, internal=internal), \
                    patch.object(runtime, "command", side_effect=[json.dumps(observed), json.dumps(networks)]), \
                    patch.object(MODULE, "assert_owned_path", side_effect=lambda path, root: path):
                if not passed:
                    with self.assertRaises(ValueError): runtime.inspect_all()
                else:
                    safe, _ = runtime.inspect_all()
                    self.assertEqual(internal, safe[0]["externalEgressBlockedByDockerInternalNetwork"])

    def test_symlink_path_guard_is_invoked_for_every_actual_bind(self):
        import json
        owner, observed = fixture()
        runtime = MODULE.OwnedDocker(owner, True)
        with patch.object(runtime, "command", return_value=json.dumps(observed)), \
                patch.object(MODULE, "assert_owned_path", side_effect=ValueError("synthetic symlink")) as path_guard:
            with self.assertRaisesRegex(ValueError, "symlink"): runtime.inspect_all()
            path_guard.assert_called_once()

    def test_cpu_scope_rejects_gpu_and_host_devices(self):
        for field in ("Devices", "DeviceRequests"):
            owner, observed = fixture(); observed[0]["HostConfig"][field] = [{"synthetic": "device"}]
            with self.subTest(field=field), self.assertRaises(ValueError):
                MODULE.validate_container(owner, owner["containers"][0], observed[0])

    def test_exact_dedicated_runtime_is_accepted(self):
        owner, observed = fixture()
        MODULE.validate_owner(owner)
        for record, one in zip(owner["containers"], observed):
            self.assertTrue(MODULE.validate_container(owner, record, one)["running"])

    def test_primary_project_paths_and_partial_ids_are_rejected(self):
        for field, value in [("project", "infra"), ("root", "/opt/saaia"),
                             ("root", "/home/maxime/saaia-ingestion-qualification/../other"),
                             ("documentsRoot", "/opt/saaia/documents")]:
            owner, _ = fixture(); owner[field] = value
            with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                MODULE.validate_owner(owner)
        owner, _ = fixture(); owner["containers"][0]["id"] = "1234"
        with self.assertRaises(ValueError): MODULE.validate_owner(owner)

    def test_foreign_labels_image_mounts_networks_and_public_ports_are_rejected(self):
        changes = [lambda x: x["Config"]["Labels"].update({"com.docker.compose.project": "infra"}),
                   lambda x: x.update({"Image": "sha256:" + "b" * 64}),
                   lambda x: x["Mounts"][0].update({"Source": "/opt/saaia/data"}),
                   lambda x: x["Mounts"][0].update({"Type": "volume"}),
                   lambda x: x["NetworkSettings"].update({"Networks": {"infra_default": {}}}),
                   lambda x: next(iter(x["HostConfig"]["PortBindings"].values()))[0].update({"HostIp": "0.0.0.0"}),
                   lambda x: x["HostConfig"].update({"Memory": 0}),
                   lambda x: x["HostConfig"].update({"NanoCpus": 0})]
        for change in changes:
            owner, observed = fixture(); record = owner["containers"][0]; one = observed[0]; change(one)
            with self.subTest(change=changes.index(change)), self.assertRaises(ValueError):
                MODULE.validate_container(owner, record, one)

    def test_provider_key_rejected_without_exposing_its_value(self):
        owner, observed = fixture(); observed[0]["Config"]["Env"] = ["OPENAI_API_KEY=synthetic-not-a-real-key"]
        with self.assertRaisesRegex(ValueError, "credential") as failure:
            MODULE.validate_container(owner, owner["containers"][0], observed[0])
        self.assertNotIn("synthetic-not-a-real-key", str(failure.exception))

    def test_docker_is_unreachable_without_execute(self):
        owner, _ = fixture()
        with patch.object(MODULE.subprocess, "run") as process:
            with self.assertRaises(PermissionError): MODULE.OwnedDocker(owner).inspect_all()
            process.assert_not_called()

    def test_stopped_containers_keep_ownership_checks_using_configured_ports(self):
        owner, observed = fixture(); observed[0]["State"]["Running"] = False
        observed[0]["NetworkSettings"]["Ports"] = {}
        self.assertFalse(MODULE.validate_container(owner, owner["containers"][0], observed[0])["running"])

    def test_stop_prevalidates_all_containers_before_any_mutation(self):
        import json
        owner, observed = fixture(); observed[-1]["Config"]["Labels"][MODULE.RUN_LABEL] = "foreign"
        runtime = MODULE.OwnedDocker(owner, True)
        with patch.object(runtime, "command", return_value=json.dumps(observed)) as command:
            with self.assertRaises(ValueError): runtime.stop()
            self.assertEqual(1, command.call_count)
            self.assertEqual("inspect", command.call_args.args[0][0])
