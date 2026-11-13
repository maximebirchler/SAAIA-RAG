"""Transport admission and actual bootstrap argv; no Docker, OCR or provider."""
import copy
import json
from pathlib import Path
import unittest
from unittest.mock import patch

import docling_transport_policy as policy
import docling_memory_policy as memory
import invoke_owned_linux_planning_ingestion as ingestion
import test_docling_memory_policy as memory_cases
import test_linux_ingestion_cleanup as cleanup_cases


class DoclingTransportTests(unittest.TestCase):
    def setUp(self):
        self.config = {"DocumentIntelligence": {"MaxResponseBytes": 640 * 1024**2}}

    def test_missing_sidecar_override_rejects_640_mib_signed_backend(self):
        with self.assertRaisesRegex(ValueError, "signed_sidecar_limit_mismatch"):
            policy.validate_observed(self.config, {"Config": {"Env": []}})

    def test_legacy_defaults_agree_and_explicit_limit_agrees(self):
        self.assertTrue(policy.validate_observed({}, {"Config": {"Env": []}})["validated"])
        actual = {"Config": {"Env": [policy.ENVIRONMENT_NAME + "=671088640"]}}
        self.assertEqual(policy.validate_observed(self.config, actual)["observedSidecarMaxResponseBytes"], 671088640)

    def test_malformed_or_ambiguous_limits_fail_closed(self):
        for value in (True, None, "671088640", 671088640.0, 0, 2 * 1024**3 + 1):
            with self.subTest(value=value), self.assertRaisesRegex(ValueError, "signed_limit_invalid"):
                policy.signed_limit({"DocumentIntelligence": {"MaxResponseBytes": value}})
        for value in ("", " 671088640", "+671088640", "671088640.0", "٠١٢٣", "0", "2147483649"):
            with self.subTest(value=value), self.assertRaisesRegex(ValueError, "environment_value_invalid"):
                policy.environment_limit([policy.ENVIRONMENT_NAME + "=" + value])
        with self.assertRaisesRegex(ValueError, "environment_duplicate"):
            policy.environment_limit([policy.ENVIRONMENT_NAME + "=671088640"] * 2)

    def fixture(self):
        case = memory_cases.DoclingMemoryPolicyTests(methodName="runTest")
        case.setUp()
        self.addCleanup(case.doCleanups)
        case.owner["configurationPath"] = str(case.root / "deploy/deployment.config.json")
        case.module.read_json = lambda path: self.config
        return case

    def test_bootstrap_adds_exact_cap_without_changing_quality_resources_or_backend(self):
        case = self.fixture()
        case.bounded()
        before = copy.deepcopy((case.module.ENV, case.module.LIMITS, case.module.REGIONAL_CONFIG))
        original = case.module.container_args(case.root, case.owner, "docling", case.image)
        backend = case.module.container_args(case.root, case.owner, "backend", case.backend_row["imageId"])
        cleanup = case.module._docling_cleanup_identity_verifier
        policy.configure_bootstrap(case.module)
        actual = case.module.container_args(case.root, case.owner, "docling", case.image)
        offset = original.index(case.image)
        self.assertEqual(actual, original[:offset] + ["-e", policy.ENVIRONMENT_NAME + "=671088640"] + original[offset:])
        self.assertEqual(backend, case.module.container_args(case.root, case.owner, "backend", case.backend_row["imageId"]))
        self.assertEqual(before, (case.module.ENV, case.module.LIMITS, case.module.REGIONAL_CONFIG))
        self.assertIs(cleanup, case.module._docling_cleanup_identity_verifier)
        with self.assertRaisesRegex(ValueError, "signed_sidecar_limit_mismatch"):
            case.module.verify_container(case.root, case.owner, case.row, "fake")
        case.actual["Config"]["Env"].append(policy.ENVIRONMENT_NAME + "=671088640")
        self.assertIs(case.actual, case.module.verify_container(case.root, case.owner, case.row, "fake"))
        case.original_verify.side_effect = ValueError("synthetic_ownership_refused")
        with self.assertRaisesRegex(ValueError, "ownership_refused"):
            case.module.verify_container(case.root, case.owner, case.row, "fake")

    def test_existing_conflicting_argument_is_not_silently_replaced(self):
        case = self.fixture()
        case.module.ENV["docling"].append(policy.ENVIRONMENT_NAME + "=536870912")
        policy.configure_bootstrap(case.module)
        with self.assertRaisesRegex(ValueError, "arguments_conflict"):
            case.module.container_args(case.root, case.owner, "docling", case.image)

    def test_mismatch_stops_before_key_http_or_document_staging(self):
        case = cleanup_cases.LinuxIngestionCleanupTests(methodName="runTest")
        case.setUp()
        self.addCleanup(case.doCleanups)
        path = Path(case.owner["configurationPath"])
        value = json.loads(path.read_text())
        value.update(self.config)
        path.write_text(json.dumps(value))
        case.owner["bootstrapPreparation"]["files"]["deploy/deployment.config.json"] = ingestion.digest(path)
        original_read, key_reads = Path.read_text, []
        def guarded_read(path, *args, **kwargs):
            if path == case.root / "private/api-key.txt":
                key_reads.append(path)
            return original_read(path, *args, **kwargs)
        with patch.object(Path, "read_text", guarded_read), self.assertRaisesRegex(ValueError, "real_linux_ingestion_not_accepted"):
            ingestion.run(case.args, case.owner, case.profile, {"cases": []})
        result = json.loads((case.args.output / "result.json").read_text())
        self.assertEqual(result["failureCode"], "docling_transport_signed_sidecar_limit_mismatch")
        self.assertEqual(key_reads, [])
        self.assertEqual(case.request_paths, [])
        self.assertEqual(list((case.root / "documents").iterdir()), [])
        self.assertEqual(result["cleanup"]["jobCancellation"], {"attempted": False})
        case.docker.stop.assert_called_once_with()


if __name__ == "__main__":
    unittest.main()
