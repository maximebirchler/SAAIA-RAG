"""Real bootstrap argv and failure-path admission, without Docker or HTTP."""
import copy
import json
from pathlib import Path, PurePosixPath
import sys
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).parent))
import docling_memory_policy as memory
import new_owned_linux_ingestion as bootstrap
import linux_planning_contracts as contracts
import invoke_owned_linux_planning_ingestion as ingestion
import test_linux_ingestion_cleanup as cleanup_cases


class ExistingMount:
    def __init__(self, value):
        self.value = value
    def exists(self):
        return True
    def __str__(self):
        return self.value


class DoclingMemoryPolicyTests(unittest.TestCase):
    def setUp(self):
        source = Path(__file__).resolve().parents[1] / "validation-ingestion/prepare_owned_linux_bootstrap_v6.py"
        self.module = bootstrap.load_module(source, "23f201c39c4d447979d44ae223a1447d382e997ab396b9bf0f816cd0f37046a0", "docling_memory_public_v6")
        self.module.child = lambda root, relative: ExistingMount(str(root / relative))
        self.root = PurePosixPath("/synthetic-owned-root")
        self.image = "sha256:" + "2" * 64
        self.row = {"role": "docling", "service": "docling", "id": "1" * 64, "imageId": self.image}
        self.backend_row = {"role": "backend", "service": "backend", "id": "3" * 64, "imageId": "sha256:" + "4" * 64}
        self.owner = {"project": "synthetic_owned", "runId": "synthetic", "network": "synthetic_owned_validation",
                      "endpoints": {"backend": "http://127.0.0.1:22521", "docling": "http://127.0.0.1:22522"},
                      "containers": [self.row, self.backend_row], "backendResourcePolicy": contracts.backend_resource_policy(3)}
        self.actual = {"Id": self.row["id"], "Image": self.image, "State": {"Running": True},
                       "HostConfig": {"NanoCpus": 3_000_000_000, "Memory": 7 * 1024**3},
                       "Config": {"Env": list(self.module.ENV["docling"]) + [k + "=" + v for k, v in memory.BOUNDED_ENVIRONMENT.items()]}}
        self.backend = {"Id": self.backend_row["id"], "Image": self.backend_row["imageId"], "State": {"Running": True},
                        "HostConfig": {"NanoCpus": 2_000_000_000, "Memory": 3 * 1024**3, "MemorySwap": 3 * 1024**3}}
        self.original_verify = Mock(side_effect=lambda root, owner, row, docker, **kwargs: self.actual if row["role"] == "docling" else self.backend)
        self.module.verify_container = self.original_verify

    def bounded(self):
        memory.bind_owner(self.owner, "cpu7g-bounded-v1")
        return memory.configure_bootstrap(self.module, "cpu7g-bounded-v1")

    def test_legacy_leaves_owner_origin_functions_argv_and_quality_entirely_unchanged(self):
        original_args, original_verify = self.module.container_args, self.module.verify_container
        before = copy.deepcopy((self.module.ENV, self.module.LIMITS, self.module.REGIONAL_CONFIG, self.owner))
        argv = self.module.container_args(self.root, self.owner, "docling", self.image)
        self.assertIsNone(memory.configure_bootstrap(self.module, "legacy"))
        memory.bind_owner(self.owner, "legacy")
        memory.validate_phase(self.owner, {}, "legacy")
        self.assertIsNone(memory.validate_observed(self.owner, {}))
        self.assertEqual(before, (self.module.ENV, self.module.LIMITS, self.module.REGIONAL_CONFIG, self.owner))
        self.assertEqual(argv, self.module.container_args(self.root, self.owner, "docling", self.image))
        self.assertIs(original_args, self.module.container_args)
        self.assertIs(original_verify, self.module.verify_container)
        self.assertNotIn(memory.POLICY_KEY, self.owner)

    def test_bounded_changes_only_six_docling_env_arguments_and_keeps_quality_resources(self):
        before_env, before_limits, before_quality = copy.deepcopy((self.module.ENV, self.module.LIMITS, self.module.REGIONAL_CONFIG))
        before = self.module.container_args(self.root, self.owner, "docling", self.image)
        backend_before = self.module.container_args(self.root, self.owner, "backend", self.backend_row["imageId"])
        selected = self.bounded()
        after = self.module.container_args(self.root, self.owner, "docling", self.image)
        additions = [part for name, value in memory.BOUNDED_ENVIRONMENT.items() for part in ("-e", name + "=" + value)]
        index = before.index(self.image)
        self.assertEqual(after, before[:index] + additions + before[index:])
        self.assertEqual(before_env, self.module.ENV)
        self.assertEqual(before_limits, self.module.LIMITS)
        self.assertEqual(before_quality, self.module.REGIONAL_CONFIG)
        self.assertEqual(backend_before, self.module.container_args(self.root, self.owner, "backend", self.backend_row["imageId"]))
        self.assertFalse(selected["qualitySettingsChanged"])
        self.assertFalse(selected["wholeDocumentMemoryBoundProven"])

    def test_profile_and_owner_policy_are_closed_and_strictly_typed(self):
        for value in (None, True, 1, "cpu7g", "cpu7g-bounded-v2"):
            with self.subTest(value=value), self.assertRaisesRegex(ValueError, "reviewed_docling_memory_profile_required"):
                memory.policy(value)
        self.bounded()
        for field, value in (("nanoCpus", 3_000_000_000.0), ("memoryBytes", 7.0 * 1024**3),
                             ("qualitySettingsChanged", 0), ("unexpected", True)):
            bad = copy.deepcopy(self.owner); bad[memory.POLICY_KEY][field] = value
            with self.subTest(field=field), self.assertRaisesRegex(ValueError, "owner_policy_invalid"):
                memory.owner_profile(bad)

    def test_each_phase_requires_exact_policy_in_both_owner_and_origin(self):
        selected = self.bounded()
        origin = {memory.POLICY_KEY: copy.deepcopy(selected)}
        memory.validate_phase(self.owner, origin, "cpu7g-bounded-v1")
        for target in ("owner", "origin"):
            a, b = copy.deepcopy(self.owner), copy.deepcopy(origin)
            del (a if target == "owner" else b)[memory.POLICY_KEY]
            with self.subTest(target=target), self.assertRaisesRegex(ValueError, "changed_between_phases"):
                memory.validate_phase(a, b, "cpu7g-bounded-v1")
        with self.assertRaisesRegex(ValueError, "changed_between_phases"):
            memory.validate_phase(self.owner, origin, "legacy")

    def test_exact_observation_does_not_claim_effective_settings_or_fidelity(self):
        self.bounded()
        result = memory.validate_observed(self.owner, self.actual)
        self.assertTrue(result["validated"])
        self.assertEqual(result["observedEnvironment"], memory.BOUNDED_ENVIRONMENT)
        self.assertFalse(result["effectivePipelineSettingsObserved"])
        self.assertFalse(result["semanticFidelityAccepted"])

    def test_missing_wrong_duplicate_or_external_environment_is_rejected(self):
        self.bounded()
        for mode in ("missing", "wrong", "duplicate", "config_file", "bad_type"):
            bad = copy.deepcopy(self.actual)
            rows = bad["Config"]["Env"]
            first = next(i for i, row in enumerate(rows) if row.startswith("DOCLING_SERVE_QUEUE_MAX_SIZE="))
            if mode == "missing": rows.pop(first)
            elif mode == "wrong": rows[first] = "DOCLING_SERVE_QUEUE_MAX_SIZE=100"
            elif mode == "duplicate": rows.append(rows[first])
            elif mode == "config_file": rows.append("DOCLING_SERVE_CONFIG_FILE=")
            else: rows.append(17)
            with self.subTest(mode=mode), self.assertRaises(ValueError):
                memory.validate_observed(self.owner, bad)

    def test_unsupported_worker_engine_or_duplicate_execution_setting_is_rejected(self):
        self.bounded()
        for name, value in (("DOCLING_SERVE_ENG_KIND", "ray"), ("DOCLING_SERVE_ENG_LOC_NUM_WORKERS", "2"),
                            ("DOCLING_SERVE_ENG_LOC_SHARE_MODELS", "false"), ("DOCLING_NUM_THREADS", "8")):
            bad = copy.deepcopy(self.actual)
            bad["Config"]["Env"].append(name + "=" + value)
            with self.subTest(name=name), self.assertRaisesRegex(ValueError, "execution_environment_changed"):
                memory.validate_observed(self.owner, bad)

    def test_container_identity_and_integer_resource_limits_cannot_drift(self):
        self.bounded()
        for field, value in (("Memory", 14 * 1024**3), ("Memory", float(7 * 1024**3)), ("NanoCpus", 1_000_000_000), ("NanoCpus", True)):
            bad = copy.deepcopy(self.actual); bad["HostConfig"][field] = value
            with self.subTest(field=field, value=value), self.assertRaisesRegex(ValueError, "resources_changed"):
                memory.validate_observed(self.owner, bad)
        bad = copy.deepcopy(self.actual); bad["Id"] = "9" * 64
        with self.assertRaisesRegex(ValueError, "identity_changed"):
            memory.validate_observed(self.owner, bad)

    def test_preexisting_override_and_resource_baseline_are_not_silently_rewritten(self):
        self.module.ENV["docling"].append("DOCLING_SERVE_QUEUE_MAX_SIZE=100")
        with self.assertRaisesRegex(ValueError, "already_overridden"):
            memory.configure_bootstrap(self.module, "cpu7g-bounded-v1")
        self.module.ENV["docling"].pop()
        self.module.LIMITS["docling"] = ("3", "14g")
        with self.assertRaisesRegex(ValueError, "baseline_changed"):
            memory.configure_bootstrap(self.module, "cpu7g-bounded-v1")

    def test_profile_composes_with_backend_resources_and_preserves_original_identity_cleanup(self):
        bootstrap.configure_backend_resources(self.module, 3)
        self.bounded()
        self.assertIs(self.actual, self.module.verify_container(self.root, self.owner, self.row, "fake"))
        self.assertIs(self.backend, self.module.verify_container(self.root, self.owner, self.backend_row, "fake"))
        self.backend["HostConfig"]["MemorySwap"] = 6 * 1024**3
        with self.assertRaisesRegex(ValueError, "backend_resource_contract_changed"):
            self.module.verify_container(self.root, self.owner, self.backend_row, "fake")
        self.actual["Config"]["Env"].append("DOCLING_SERVE_QUEUE_MAX_SIZE=99")
        with self.assertRaisesRegex(ValueError, "environment_duplicate"):
            self.module.verify_container(self.root, self.owner, self.row, "fake")
        self.module.read_json = Mock(return_value=self.owner)
        self.module.new_file = Mock()
        docker = Mock()
        def stopped(*arguments, **kwargs):
            target = self.actual if arguments[-1] == self.row["id"] else self.backend
            target["State"]["Running"] = False
        docker.call.side_effect = stopped
        bootstrap.stop_new_partial(self.module, self.root, docker)
        self.assertEqual(docker.call.call_count, 2)
        self.assertFalse(self.actual["State"]["Running"])
        self.assertFalse(self.backend["State"]["Running"])

    def test_new_policy_never_bypasses_original_cleanup_ownership_refusal(self):
        self.bounded()
        self.original_verify.side_effect = ValueError("synthetic_original_ownership_refusal")
        self.module.read_json = Mock(return_value=self.owner)
        docker = Mock()
        with self.assertRaisesRegex(ValueError, "original_ownership_refusal"):
            bootstrap.stop_new_partial(self.module, self.root, docker)
        docker.call.assert_not_called()


class DoclingMemoryRunnerAdmissionTests(unittest.TestCase):
    def fixture(self):
        case = cleanup_cases.LinuxIngestionCleanupTests(methodName="runTest")
        case.setUp()
        self.addCleanup(case.doCleanups)
        memory.bind_owner(case.owner, "cpu7g-bounded-v1")
        case.args.docling_memory_profile = "cpu7g-bounded-v1"
        origin = case.root / "evidence/planning-bootstrap-origin.json"
        origin.write_text(json.dumps({memory.POLICY_KEY: case.owner[memory.POLICY_KEY]}), encoding="utf-8")
        row = {"role": "docling", "id": "7" * 64, "imageId": "sha256:" + "8" * 64}
        case.owner["containers"].append(row)
        actual = {"Id": row["id"], "Image": row["imageId"],
                  "HostConfig": {"NanoCpus": 3_000_000_000, "Memory": 7 * 1024**3},
                  "Config": {"Env": [name + "=" + value for name, value in
                                     {**memory.BASELINE_EXECUTION_ENVIRONMENT, **memory.BOUNDED_ENVIRONMENT}.items()]}}
        case.docker.inspect_all.return_value[1].append(actual)
        return case, actual

    def test_policy_mismatch_fails_before_synthetic_key_read_or_first_http(self):
        case, actual = self.fixture()
        case.args.docling_memory_profile = "legacy"
        original_read, key_reads = Path.read_text, []
        def guarded_read(path, *args, **kwargs):
            if path == case.root / "private/api-key.txt":
                key_reads.append(path)
                raise AssertionError("synthetic_key_read_before_policy_admission")
            return original_read(path, *args, **kwargs)
        with patch.object(Path, "read_text", guarded_read):
            with self.assertRaisesRegex(ValueError, "real_linux_ingestion_not_accepted"):
                ingestion.run(case.args, case.owner, case.profile, {"cases": []})
        result = json.loads((case.args.output / "result.json").read_text(encoding="utf-8"))
        self.assertEqual(result["failureCode"], "docling_memory_profile_changed_between_phases")
        self.assertEqual(case.request_paths, [])
        self.assertEqual(key_reads, [])
        self.assertEqual(result["cleanup"]["jobCancellation"], {"attempted": False})
        case.docker.stop.assert_called_once_with()

    def test_each_http_admission_rejects_later_environment_drift_and_still_stops(self):
        case, actual = self.fixture()
        original_open = case.open
        def changed_after_ready(request, *, timeout):
            result = original_open(request, timeout=timeout)
            if request.full_url.endswith("/ready"):
                actual["Config"]["Env"].append("DOCLING_SERVE_QUEUE_MAX_SIZE=100")
            return result
        with patch.object(ingestion.urllib.request, "build_opener", return_value=type("Opener", (), {"open": staticmethod(changed_after_ready)})()):
            with self.assertRaisesRegex(ValueError, "real_linux_ingestion_not_accepted"):
                ingestion.run(case.args, case.owner, case.profile, {"cases": []})
        result = json.loads((case.args.output / "result.json").read_text(encoding="utf-8"))
        self.assertEqual(result["failureCode"], "docling_memory_environment_duplicate")
        self.assertEqual(case.request_paths, ["/ready"])
        self.assertTrue(result["doclingMemoryEnvironmentObserved"]["validated"])
        case.docker.stop.assert_called_once_with()


if __name__ == "__main__":
    unittest.main()
