import asyncio
import json
import time
from copy import deepcopy
from dataclasses import replace
from pathlib import Path
import unittest
from unittest.mock import patch

from saaia_docling.response_repair import repair_response_tables, _has_sparse_grid
from saaia_docling.table_region_process import BoundedTableRegionEngine, OcrRegionResult, build_table_region_request
from saaia_docling.table_repair_options import TableRepairOptions
from saaia_docling.text_region_process import BoundedTextRegionEngine
from saaia_docling.region_process import RegionProcessGate
from saaia_docling.table_repair import repair_sparse_table
from saaia_docling.text_repair import TextOcrObservation, _observation_proof
from test_table_repair import _reference_table, _reference_ocr_lines
from test_text_region_process import FakeProcess


def response(count=1):
    tables = []
    for index in range(count):
        table = _reference_table()
        table.update(self_ref=f"#/tables/{index}", prov=[{"page_no": 1,
            "bbox": {"l": 0, "t": 0, "r": 100, "b": 30, "coord_origin": "TOPLEFT"}}])
        tables.append(table)
    return {"document": {"json_content": {"pages": {"1": {"page_no": 1,
        "size": {"width": 600, "height": 800}}}, "tables": tables}}}


class FakeEngine:
    def __init__(self, *, error=None, advance=None):
        self.calls, self.error, self.advance = [], error, advance

    async def recognize(self, pdf_path, document, table, options, timeout_seconds):
        self.calls.append((table["self_ref"], timeout_seconds))
        if self.advance:
            self.advance()
        if self.error:
            raise self.error
        return OcrRegionResult(tuple(_reference_ocr_lines()), 17, "test-version", "test-model",
            {"engine": "rapidocr", "model_artifacts": {"model.onnx": "a" * 64}})


class TableResponseTests(unittest.IsolatedAsyncioTestCase):
    async def test_complete_zero_scan_is_attested_without_inventing_an_ocr_attempt(self):
        payload, engine = response(0), FakeEngine()
        self.assertEqual([], await repair_response_tables("unopened.pdf", payload, engine))
        self.assertEqual([], engine.calls)
        summary = payload["saaia_table_repair_summary"]
        self.assertEqual(0, summary["inspected_table_count"])
        self.assertEqual(0, summary["uninspected_table_count"])
        self.assertEqual(0, summary["candidate_count"])
        self.assertEqual(0, summary["attempted_count"])

    async def test_success_preserves_original_cells_and_actual_model_observation(self):
        payload, engine = response(), FakeEngine()
        result = await repair_response_tables("unopened.pdf", payload, engine)
        table = payload["document"]["json_content"]["tables"][0]
        self.assertEqual("applied", result[0]["status"])
        self.assertTrue(result[0]["attempted"])
        self.assertEqual(9, len(table["data"]["table_cells"]))
        self.assertEqual("a" * 64, table["saaia_table_repair"]["ocr_observation"]["model_artifacts"]["model.onnx"])
        self.assertEqual(1, payload["saaia_table_repair_summary"]["attempted_count"])

    async def test_inconclusive_completed_ocr_keeps_private_observation_without_leaking_into_summary(self):
        class NoRepairEngine(FakeEngine):
            async def recognize(self, *args):
                result = await super().recognize(*args)
                return replace(result, lines=(), observation={**result.observation,
                    "lines": [{"text": "PRIVATE_OBSERVATION_SENTINEL"}]})

        payload, engine = response(), NoRepairEngine()
        original = deepcopy(payload["document"]["json_content"]["tables"][0]["data"])
        summaries = await repair_response_tables("unopened.pdf", payload, engine)
        table = payload["document"]["json_content"]["tables"][0]
        self.assertEqual("not_applied", summaries[0]["status"])
        self.assertTrue(summaries[0]["attempted"])
        self.assertEqual(original, table["data"])
        observation = table["saaia_table_repair_attempt"]["ocr_observation"]
        self.assertEqual("a" * 64, observation["model_artifacts"]["model.onnx"])
        self.assertEqual("PRIVATE_OBSERVATION_SENTINEL", observation["lines"][0]["text"])
        self.assertNotIn("ocr_observation", summaries[0])
        self.assertNotIn("PRIVATE_OBSERVATION_SENTINEL", json.dumps(payload["saaia_table_repairs"]))
        self.assertNotIn("PRIVATE_OBSERVATION_SENTINEL", json.dumps(payload["saaia_table_repair_summary"]))

    async def test_region_limit_preserves_unattempted_source_and_counts(self):
        payload, engine = response(3), FakeEngine()
        original = deepcopy(payload["document"]["json_content"]["tables"][1]["data"])
        result = await repair_response_tables("unopened.pdf", payload, engine,
            options=replace(TableRepairOptions(), max_regions=1))
        self.assertEqual(1, len(engine.calls))
        self.assertFalse(result[1]["attempted"])
        self.assertEqual(original, payload["document"]["json_content"]["tables"][1]["data"])

    async def test_document_deadline_rejects_late_result_and_does_not_inspect_remaining_tables(self):
        now = [0.0]
        payload = response(3)
        original = deepcopy(payload["document"]["json_content"]["tables"][0]["data"])
        engine = FakeEngine(advance=lambda: now.__setitem__(0, 61.0))
        result = await repair_response_tables("unopened.pdf", payload, engine, clock=lambda: now[0])
        self.assertEqual(1, len(engine.calls))
        self.assertEqual("region_or_document_time_budget_exhausted", result[0]["reason"])
        self.assertEqual(2, result[1]["uninspected_table_count"])
        self.assertEqual(original, payload["document"]["json_content"]["tables"][0]["data"])
        self.assertNotIn("ocr_observation", payload["document"]["json_content"]["tables"][0]["saaia_table_repair_attempt"])

    async def test_synchronous_table_repair_cannot_publish_after_document_deadline(self):
        now = [0.0]
        payload = response()
        original = deepcopy(payload["document"]["json_content"]["tables"][0]["data"])

        def late_repair(*args, **kwargs):
            result = repair_sparse_table(*args, **kwargs)
            self.assertTrue(result.applied)
            now[0] = 61.0
            return result

        with patch("saaia_docling.response_repair.repair_sparse_table", side_effect=late_repair):
            summaries = await repair_response_tables("unopened.pdf", payload, FakeEngine(), clock=lambda: now[0])
        table = payload["document"]["json_content"]["tables"][0]
        self.assertEqual("not_applied", summaries[0]["status"])
        self.assertEqual("region_or_document_time_budget_exhausted", summaries[0]["reason"])
        self.assertEqual(original, table["data"])
        self.assertNotIn("saaia_table_repair", table)
        self.assertNotIn("ocr_observation", table["saaia_table_repair_attempt"])

    async def test_failure_reports_only_error_type_and_never_private_exception_text(self):
        payload = response()
        result = await repair_response_tables("unopened.pdf", payload,
            FakeEngine(error=RuntimeError("PRIVATE_SENTINEL")))
        self.assertEqual("error", result[0]["status"])
        self.assertNotIn("PRIVATE_SENTINEL", str(result))

    async def test_pixel_budget_rejects_before_engine_and_preserves_table(self):
        payload, engine = response(), FakeEngine()
        result = await repair_response_tables("unopened.pdf", payload, engine,
            options=replace(TableRepairOptions(), max_rendered_pixels=10))
        self.assertEqual([], engine.calls)
        self.assertFalse(result[0]["attempted"])
        self.assertEqual("rendered_page_pixel_budget_exceeded", result[0]["reason"])

    async def test_cancellation_is_propagated_without_applying_a_repair(self):
        payload = response()
        original = deepcopy(payload)
        with self.assertRaises(asyncio.CancelledError):
            await repair_response_tables("unopened.pdf", payload, FakeEngine(error=asyncio.CancelledError()))
        self.assertEqual(original, payload)

    def test_minimized_child_request_excludes_cells_and_pixels_and_uses_rapid_only(self):
        document = response()["document"]["json_content"]
        document["pages"]["1"]["image"] = "PRIVATE_PIXELS"
        table = document["tables"][0]
        table["extra"] = "PRIVATE_METADATA"
        request = build_table_region_request(Path("unopened.pdf"), document, table, TableRepairOptions(), 15)
        self.assertEqual("table_rapid_only", request["operation"])
        self.assertNotIn("PRIVATE", str(request))
        self.assertNotIn("table_cells", str(request))

    def test_invalid_grid_spans_and_huge_cell_list_do_not_trigger_render(self):
        table = _reference_table()
        table["data"]["table_cells"][0]["row_span"] = -1
        self.assertFalse(_has_sparse_grid(table))
        table = _reference_table()
        table["data"]["table_cells"] *= 2_000
        self.assertFalse(_has_sparse_grid(table))

    def test_table_options_reject_nonfinite_unbounded_and_boolean_values(self):
        for kwargs in ({"max_regions": 9}, {"region_timeout_seconds": 16},
                {"max_rendered_pixels": True}, {"render_scale": float("nan")}, {"total_timeout_seconds": 61}):
            with self.subTest(kwargs=kwargs), self.assertRaises(ValueError):
                TableRepairOptions(**kwargs)
        options = TableRepairOptions.from_environment({"SAAIA_DOCLING_TABLE_REPAIR_SCALE": "3"})
        self.assertEqual(3, options.render_scale)
        self.assertIsNone(options.attestation()["secondary_engine"])

    async def test_table_and_text_share_one_slot_without_spawning_after_queue_timeout(self):
        slot = RegionProcessGate()
        text, table = BoundedTextRegionEngine(slot), BoundedTableRegionEngine(slot)
        self.assertIs(text._slot, table._slot)
        await slot.acquire()
        with patch("saaia_docling.region_process.asyncio.create_subprocess_exec") as create:
            with self.assertRaises(TimeoutError):
                await table.recognize(Path("unopened.pdf"), {}, {}, TableRepairOptions(), 0.02)
            create.assert_not_called()
        slot.release()

    async def test_poisoned_shared_gate_refuses_a_waiting_other_mode_without_starting_a_child(self):
        slot = RegionProcessGate()
        table, text = BoundedTableRegionEngine(slot), BoundedTextRegionEngine(slot)
        await slot.acquire()
        with patch("saaia_docling.region_process.asyncio.create_subprocess_exec") as create:
            pending = asyncio.create_task(table.recognize(Path("unopened.pdf"), {}, {}, TableRepairOptions(), 1))
            await asyncio.sleep(0)
            slot.poison()
            slot.release()
            with self.assertRaises(RuntimeError):
                await pending
            with self.assertRaises(RuntimeError):
                await text.recognize(Path("unopened.pdf"), {}, {}, TableRepairOptions(), 1)
            create.assert_not_called()
        self.assertEqual(1, slot._value)
        self.assertTrue(table.snapshot()["shared_ocr_gate_poisoned"])

    async def test_table_child_cancel_timeout_and_success_use_owned_group_cleanup(self):
        for mode in ("success", "cancel", "timeout"):
            process, directories = FakeProcess(mode=mode), []
            async def create(*args, **kwargs):
                self.assertTrue(kwargs["start_new_session"])
                request_path, output_path = Path(args[-2]), Path(args[-1])
                directories.append(request_path.parent)
                observation = TextOcrObservation("rapidocr", "test", "test-model", tuple(_reference_ocr_lines()))
                output_path.write_text(json.dumps({"status": "success",
                    "rapid": _observation_proof(observation)}), encoding="utf-8")
                return process
            def kill(pid):
                self.assertEqual(process.pid, pid)
                process.killed = True
            document = response()["document"]["json_content"]
            table = BoundedTableRegionEngine()
            with self.subTest(mode=mode), patch("saaia_docling.region_process._POSIX_PROCESS_GROUPS", True), \
                    patch("saaia_docling.region_process.asyncio.create_subprocess_exec", side_effect=create), \
                    patch("saaia_docling.region_process._kill_owned_group", side_effect=kill), \
                    patch("saaia_docling.region_process._owned_group_exists", return_value=False):
                operation = table.recognize(Path("unopened.pdf"), document, document["tables"][0], TableRepairOptions(), 0.4)
                if mode == "success":
                    self.assertEqual(9, len((await operation).lines))
                else:
                    with self.assertRaises(asyncio.CancelledError if mode == "cancel" else TimeoutError):
                        await operation
            self.assertTrue(process.killed)
            self.assertEqual(0, table.snapshot()["active_or_unconfirmed_processes"])
            self.assertEqual(1, table.snapshot()["groups_confirmed_absent"])
            self.assertTrue(all(not path.exists() for path in directories))

    async def test_slow_result_decoding_cannot_publish_after_region_deadline(self):
        process = FakeProcess()
        document = response()["document"]["json_content"]
        table = BoundedTableRegionEngine()
        def slow_decode(_):
            time.sleep(0.25)
            return "late"
        table._result_decoder = slow_decode
        async def create(*args, **kwargs):
            Path(args[-1]).write_text(json.dumps({"status": "success"}), encoding="utf-8")
            return process
        def kill(_):
            process.killed = True
        with patch("saaia_docling.region_process._POSIX_PROCESS_GROUPS", True), \
                patch("saaia_docling.region_process.asyncio.create_subprocess_exec", side_effect=create), \
                patch("saaia_docling.region_process._kill_owned_group", side_effect=kill), \
                patch("saaia_docling.region_process._owned_group_exists", return_value=False):
            with self.assertRaises(TimeoutError):
                await table.recognize(Path("unopened.pdf"), document, document["tables"][0], TableRepairOptions(), 0.22)
        self.assertTrue(process.killed)
        self.assertEqual(1, table.snapshot()["groups_confirmed_absent"])
