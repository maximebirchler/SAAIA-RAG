from __future__ import annotations

import unittest
from unittest.mock import patch
from saaia_docling.execution_metadata import conversion_execution_metadata


class ExecutionMetadataTests(unittest.TestCase):
    def test_received_force_and_effective_repair_settings_are_attested(self) -> None:
        result = conversion_execution_metadata(
            [("force_ocr", "true"), ("do_ocr", "false")],
            repair_enabled=False, render_scale=3.0, padding_points=7.0, minimum_score=0.8,
        )
        self.assertTrue(result["force_ocr"])
        self.assertFalse(result["do_ocr"])
        self.assertFalse(result["table_repair_enabled"])
        self.assertEqual(3.0, result["table_repair_scale"])
        self.assertEqual(7.0, result["table_repair_padding_points"])
        self.assertEqual(0.8, result["table_repair_minimum_score"])
        self.assertEqual("observed_configuration", result["status"])
        self.assertNotIn("successful_pages", result)

    def test_unobserved_request_flags_remain_unknown(self) -> None:
        result = conversion_execution_metadata([], repair_enabled=True,
            render_scale=2.5, padding_points=12.0, minimum_score=0.5)
        self.assertIsNone(result["do_ocr"])
        self.assertIsNone(result["force_ocr"])

    def test_versions_come_from_installed_package_metadata(self) -> None:
        with patch("saaia_docling.execution_metadata.metadata.version", side_effect=lambda package: "observed-" + package):
            result = conversion_execution_metadata([], repair_enabled=True,
                render_scale=2.5, padding_points=12.0, minimum_score=0.5)
        self.assertEqual("observed-docling-serve", result["package_versions"]["docling-serve"])
        self.assertEqual("observed-rapidocr", result["package_versions"]["rapidocr"])

    def test_regional_text_settings_attest_configuration_not_successful_ocr(self) -> None:
        settings = {"enabled": True, "max_regions": 4, "secondary_engine": "tesseract"}
        result = conversion_execution_metadata([], repair_enabled=True, render_scale=2.5,
            padding_points=12, minimum_score=0.5, text_repair_settings=settings)
        self.assertEqual(settings, result["text_repair_settings"])
        self.assertNotIn("successful_pages", result)
        self.assertNotIn("engine_version", result["text_repair_settings"])

    def test_table_process_budgets_are_configuration_and_never_success_proof(self) -> None:
        from saaia_docling.table_repair_options import TableRepairOptions
        settings = TableRepairOptions().attestation()
        result = conversion_execution_metadata([], repair_enabled=True, render_scale=2.5,
            padding_points=12, minimum_score=0.5, table_repair_settings=settings)
        self.assertEqual(settings, result["table_repair_settings"])
        self.assertNotIn("successful_pages", result)
        self.assertEqual("per_region_linux_process_group", result["table_repair_settings"]["process_isolation"])
