"""Exact public bootstrap argv and hostile Docker observations; no service calls."""
import ast
import copy
from pathlib import Path, PurePosixPath
import sys
import unittest
from unittest.mock import Mock

sys.path.insert(0, str(Path(__file__).parent))
import new_owned_linux_ingestion as bootstrap
import linux_planning_contracts as contracts
import invoke_owned_linux_planning_ingestion as ingestion


class ExistingMount:
    """Only filesystem existence is mocked when rendering the real argv."""
    def __init__(self, value):
        self.value = value
    def exists(self):
        return True
    def __str__(self):
        return self.value


class BackendResourcePolicyTests(unittest.TestCase):
    def setUp(self):
        source = Path(__file__).resolve().parents[1] / "validation-ingestion/prepare_owned_linux_bootstrap_v6.py"
        self.module = bootstrap.load_module(source, "23f201c39c4d447979d44ae223a1447d382e997ab396b9bf0f816cd0f37046a0", "resource_policy_public_v6")
        self.module.child = lambda root, relative: ExistingMount(str(root / relative))
        self.root = PurePosixPath("/synthetic-owned-root")
        self.image = "sha256:" + "2" * 64
        self.row = {"role": "backend", "service": "backend", "id": "1" * 64, "imageId": self.image}
        self.owner = {"project": "synthetic_owned", "runId": "synthetic", "network": "synthetic_owned_validation",
                      "endpoints": {"backend": "http://127.0.0.1:22521", "docling": "http://127.0.0.1:22522"},
                      "containers": [self.row], "backendResourcePolicy": contracts.backend_resource_policy(3)}
        self.actual = {"Id": self.row["id"], "Image": self.image, "State": {"Running": True},
                       "HostConfig": {"NanoCpus": 2_000_000_000, "Memory": 3 * 1024**3, "MemorySwap": 3 * 1024**3}}
        self.original_verify = Mock(return_value=self.actual)
        self.module.verify_container = self.original_verify

    def test_default_two_gib_and_900_leave_real_bootstrap_constants_and_argv_unchanged(self):
        before_limits, before_env = copy.deepcopy(self.module.LIMITS), copy.deepcopy(self.module.ENV)
        arguments = self.module.container_args(self.root, self.owner, "backend", self.image)
        original_args = self.module.container_args
        timeout = bootstrap.configure_full_document_timeout(self.module, 900)
        policy = bootstrap.configure_backend_resources(self.module, 2)
        self.assertEqual(arguments, self.module.container_args(self.root, self.owner, "backend", self.image))
        self.assertIs(original_args, self.module.container_args)
        self.assertIs(self.original_verify, self.module.verify_container)
        self.assertEqual(before_limits, self.module.LIMITS)
        self.assertEqual(before_env, self.module.ENV)
        self.assertTrue(timeout["default900Unchanged"])
        self.assertEqual(policy["memoryBytes"], 2 * 1024**3)
        self.assertEqual(policy["swapPolicy"], "bootstrap_default_unchanged")
        self.assertNotIn("--memory-swap", arguments)

    def test_three_gib_renders_real_backend_cpu_memory_and_no_swap_before_image_only(self):
        docling_before = self.module.container_args(self.root, self.owner, "docling", self.image)
        limits_before = copy.deepcopy(self.module.LIMITS)
        policy = bootstrap.configure_backend_resources(self.module, 3)
        arguments = self.module.container_args(self.root, self.owner, "backend", self.image)
        self.assertEqual(arguments[arguments.index("--cpus") + 1], "2")
        self.assertEqual(arguments[arguments.index("--memory") + 1], "3g")
        self.assertEqual(arguments[arguments.index("--memory-swap") + 1], "3g")
        self.assertLess(arguments.index("--memory-swap"), arguments.index(self.image))
        self.assertEqual(docling_before, self.module.container_args(self.root, self.owner, "docling", self.image))
        self.assertEqual({**limits_before, "backend": ("2", "3g")}, self.module.LIMITS)
        self.assertEqual(policy, self.owner["backendResourcePolicy"])
        self.assertFalse(policy["deploymentSignatureCoversDockerResources"])

    def test_unreviewed_memory_and_pinned_baseline_changes_are_rejected(self):
        for value in (0, 1, 4, True, "3", 3.0):
            with self.subTest(value=value), self.assertRaisesRegex(ValueError, "reviewed_backend_memory_required"):
                bootstrap.configure_backend_resources(self.module, value)
        self.module.LIMITS["backend"] = ("2", "4g")
        with self.assertRaisesRegex(ValueError, "resource_baseline_changed"):
            bootstrap.configure_backend_resources(self.module, 3)

    def test_owner_policy_cannot_change_rendered_contract(self):
        bootstrap.configure_backend_resources(self.module, 3)
        self.owner["backendResourcePolicy"] = contracts.backend_resource_policy(2)
        with self.assertRaisesRegex(ValueError, "owner_policy_changed"):
            self.module.container_args(self.root, self.owner, "backend", self.image)

    def test_existing_swap_argument_is_not_silently_overridden(self):
        original = self.module.container_args
        self.module.container_args = lambda *args: original(*args) + ["--memory-swap", "6g"]
        bootstrap.configure_backend_resources(self.module, 3)
        with self.assertRaisesRegex(ValueError, "resource_arguments_changed"):
            self.module.container_args(self.root, self.owner, "backend", self.image)

    def test_wrapper_keeps_original_inspection_and_rejects_observed_memory_cpu_or_swap_drift(self):
        bootstrap.configure_backend_resources(self.module, 3)
        self.assertIs(self.actual, self.module.verify_container(self.root, self.owner, self.row, "mock-docker", require_running=False))
        self.original_verify.assert_called_once_with(self.root, self.owner, self.row, "mock-docker", require_running=False)
        for key, value in (("Memory", 2 * 1024**3), ("NanoCpus", 3_000_000_000), ("MemorySwap", 6 * 1024**3),
                           ("MemorySwap", -1), ("MemorySwap", 0), ("MemorySwap", True)):
            bad = copy.deepcopy(self.actual); bad["HostConfig"][key] = value
            self.original_verify.return_value = bad
            with self.subTest(key=key, value=value), self.assertRaisesRegex(ValueError, "observed_backend_resource_contract_changed"):
                self.module.verify_container(self.root, self.owner, self.row, "mock-docker")

    def test_phase_policy_is_exactly_bound_to_both_owner_and_origin(self):
        policy = contracts.backend_resource_policy(3)
        origin = {"backendResourcePolicy": copy.deepcopy(policy)}
        contracts.validate_backend_resource_phase(self.owner, origin, policy)
        for target in ("owner", "origin"):
            owner, other = copy.deepcopy(self.owner), copy.deepcopy(origin)
            (owner if target == "owner" else other)["backendResourcePolicy"] = contracts.backend_resource_policy(2)
            with self.subTest(target=target), self.assertRaisesRegex(ValueError, "changed_between_phases"):
                contracts.validate_backend_resource_phase(owner, other, policy)
        origin["backendResourcePolicy"]["extra"] = True
        with self.assertRaisesRegex(ValueError, "changed_between_phases"):
            contracts.validate_backend_resource_phase(self.owner, origin, policy)

    def test_resource_refusal_does_not_block_cleanup_with_exact_original_ownership_inspection(self):
        bootstrap.configure_backend_resources(self.module, 3)
        self.actual["HostConfig"]["MemorySwap"] = 6 * 1024**3
        with self.assertRaisesRegex(ValueError, "observed_backend_resource_contract_changed"):
            self.module.verify_container(self.root, self.owner, self.row, "mock-docker")
        self.module.read_json = Mock(return_value=self.owner)
        self.module.new_file = Mock()
        docker = Mock()
        def stopped(*arguments, **kwargs):
            self.assertEqual(arguments, ("stop", "--time", "45", self.row["id"]))
            self.actual["State"]["Running"] = False
        docker.call.side_effect = stopped
        bootstrap.stop_new_partial(self.module, self.root, docker)
        docker.call.assert_called_once()
        self.assertFalse(self.actual["State"]["Running"])
        self.module.new_file.assert_called_once()

    def test_wrong_container_identity_and_policy_cannot_qualify_resources(self):
        for key, value in (("Id", "9" * 64), ("Image", "sha256:" + "9" * 64)):
            bad = copy.deepcopy(self.actual); bad[key] = value
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, "observed_backend_resource_contract_changed"):
                contracts.validate_backend_resources(self.owner, bad)
        self.owner["backendResourcePolicy"]["nanoCpus"] = 3_000_000_000
        with self.assertRaisesRegex(ValueError, "backend_resource_policy_invalid"):
            contracts.validate_backend_resources(self.owner, self.actual)

    def test_default_two_gib_inspection_retains_old_swap_policy_but_requires_exact_memory(self):
        self.owner["backendResourcePolicy"] = contracts.backend_resource_policy(2)
        self.actual["HostConfig"].update(Memory=2 * 1024**3, MemorySwap=4 * 1024**3)
        result = contracts.validate_backend_resources(self.owner, self.actual)
        self.assertTrue(result["validated"])
        self.assertFalse(result["deploymentSignatureCoversDockerResources"])
        self.actual["HostConfig"]["Memory"] = 3 * 1024**3
        with self.assertRaisesRegex(ValueError, "observed_backend_resource_contract_changed"):
            contracts.validate_backend_resources(self.owner, self.actual)

    def test_runner_qualifies_policy_and_host_before_key_read_or_first_http_call(self):
        tree = ast.parse(Path(ingestion.__file__).read_text(encoding="utf-8"))
        run = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "run")
        guards = [node.lineno for node in ast.walk(run) if isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                  and isinstance(node.func.value, ast.Name) and node.func.value.id == "helpers"
                  and node.func.attr in ("validate_backend_resource_phase", "validate_backend_resources")]
        key = next(node.lineno for node in ast.walk(run) if isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                   and isinstance(node.func.value, ast.Name) and node.func.value.id == "key_path" and node.func.attr == "read_text")
        api = min(node.lineno for node in ast.walk(run) if isinstance(node, ast.Call) and isinstance(node.func, ast.Name) and node.func.id == "api")
        self.assertEqual(len(guards), 3)  # Initial phase+inspect, and every subsequent API admission.
        self.assertLess(max(guards), key)
        self.assertLess(key, api)


if __name__ == "__main__":
    unittest.main()
