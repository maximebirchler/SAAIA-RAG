from __future__ import annotations

import asyncio
import json
from pathlib import Path
import unittest
from unittest.mock import patch

from saaia_docling.text_region_process import BoundedTextRegionEngine, build_region_request
from saaia_docling.text_repair import _observation_proof
from saaia_docling.text_repair_options import TextRepairOptions
from test_text_repair import item, observation


DOCUMENT = {"pages": {"2": {"page_no": 2, "size": {"width": 600, "height": 800}}}}


class FakeProcess:
    pid = 123456789

    def __init__(self, *, mode="success"):
        self.mode, self.returncode, self.killed, self.waits = mode, None, False, 0

    async def wait(self):
        self.waits += 1
        if self.killed:
            self.returncode = -9
        elif self.mode == "cancel":
            raise asyncio.CancelledError()
        elif self.mode == "timeout":
            await asyncio.Future()
        else:
            self.returncode = 0
        return self.returncode


class TextRegionProcessTests(unittest.IsolatedAsyncioTestCase):
    def test_child_request_excludes_page_images_other_pages_and_unneeded_item_payload(self):
        document = {"pages": {"2": {"page_no": 2, "size": {"width": 600, "height": 800},
            "image": {"uri": "PRIVATE_LARGE_PAGE_IMAGE" * 10_000}},
            "3": {"page_no": 3, "size": {"width": 600, "height": 800}}}}
        source = item()
        source["unneeded_payload"] = "PRIVATE_ITEM_METADATA" * 10_000
        request = build_region_request(Path("unopened.pdf"), document, source, TextRepairOptions(), 15)
        encoded = json.dumps(request)
        self.assertNotIn("PRIVATE_", encoded)
        self.assertEqual({"2"}, set(request["document"]["pages"]))
        self.assertLessEqual(len(encoded.encode("utf-8")), 65_536)

    async def run_mock(self, mode, kill_error=None):
        process, paths, args_seen = FakeProcess(mode=mode), [], []

        async def create(*args, **kwargs):
            request_path, output_path = Path(args[-2]), Path(args[-1])
            paths.append(request_path.parent)
            args_seen.append((args, kwargs))
            output_path.write_text(json.dumps({"status": "success", "rapid": _observation_proof(observation()),
                "secondary": _observation_proof(observation(engine="tesseract"))}), encoding="utf-8")
            return process

        def kill(pid):
            self.assertEqual(process.pid, pid)
            if kill_error is not None:
                raise kill_error
            process.killed = True

        engine = BoundedTextRegionEngine()
        unconfirmed = (kill_error is not None and not isinstance(kill_error, ProcessLookupError)) or mode == "lingering"
        with patch("saaia_docling.region_process._POSIX_PROCESS_GROUPS", True), \
             patch("saaia_docling.region_process.asyncio.create_subprocess_exec", side_effect=create), \
             patch("saaia_docling.region_process._kill_owned_group", side_effect=kill), \
             patch("saaia_docling.region_process._owned_group_exists", return_value=mode == "lingering"):
            if kill_error is not None and not isinstance(kill_error, ProcessLookupError):
                with self.assertRaises(type(kill_error)):
                    await engine.recognize(Path("unopened.pdf"), DOCUMENT, item(), TextRepairOptions(), 2)
            elif mode == "lingering":
                with self.assertRaises(RuntimeError):
                    await engine.recognize(Path("unopened.pdf"), DOCUMENT, item(), TextRepairOptions(), 0.4)
            elif mode == "success":
                results = await engine.recognize(Path("unopened.pdf"), DOCUMENT, item(), TextRepairOptions(), 2)
                self.assertEqual(("rapidocr", "tesseract"), tuple(result.engine for result in results))
            else:
                error = asyncio.CancelledError if mode == "cancel" else TimeoutError
                with self.assertRaises(error):
                    await engine.recognize(Path("unopened.pdf"), DOCUMENT, item(), TextRepairOptions(), 0.4)
        self.assertEqual(1, engine._slot._value)
        self.assertEqual(1 if unconfirmed else 0, engine.snapshot()["active_or_unconfirmed_processes"])
        self.assertEqual(1 if unconfirmed else 0, engine.snapshot()["cleanup_unconfirmed"])
        self.assertEqual(unconfirmed, engine.snapshot()["shared_ocr_gate_poisoned"])
        self.assertEqual(0 if unconfirmed else 1, engine.snapshot()["groups_confirmed_absent"])
        self.assertEqual(1, engine.snapshot()["processes_started"])
        self.assertEqual(1, engine.snapshot()["processes_reaped"])
        if kill_error is None:
            self.assertTrue(process.killed)
        self.assertTrue(all(not path.exists() for path in paths))
        self.assertTrue(args_seen[0][1]["start_new_session"])
        self.assertEqual("1", args_seen[0][1]["env"]["HF_HUB_OFFLINE"])
        self.assertFalse(args_seen[0][1].get("shell", False))
        if unconfirmed:
            other = BoundedTextRegionEngine(engine._slot)
            with patch("saaia_docling.region_process.asyncio.create_subprocess_exec") as launch:
                with self.assertRaises(RuntimeError):
                    await other.recognize(Path("unopened.pdf"), DOCUMENT, item(), TextRepairOptions(), 2)
                launch.assert_not_called()
        return process

    async def test_signal_permission_or_os_error_cannot_claim_group_cleaned_even_if_leader_exited(self):
        for error in (PermissionError("private sentinel"), OSError("private sentinel")):
            with self.subTest(error=type(error).__name__):
                await self.run_mock("success", kill_error=error)

    async def test_missing_group_is_confirmed_absent_instead_of_being_reported_as_cleanup_error(self):
        await self.run_mock("success", kill_error=ProcessLookupError())

    async def test_reaped_leader_does_not_hide_a_group_that_remains_present(self):
        await self.run_mock("lingering")

    async def test_success_is_private_and_group_cleanup_and_temporary_image_scope_are_closed(self):
        await self.run_mock("success")

    async def test_cancel_kills_and_reaps_only_owned_group_before_releasing_slot(self):
        process = await self.run_mock("cancel")
        self.assertGreaterEqual(process.waits, 2)

    async def test_timeout_kills_and_reaps_only_owned_group_before_releasing_slot(self):
        process = await self.run_mock("timeout")
        self.assertGreaterEqual(process.waits, 2)

    async def test_missing_isolation_is_refused_without_launching_any_process(self):
        engine = BoundedTextRegionEngine()
        with patch("saaia_docling.region_process._POSIX_PROCESS_GROUPS", False), \
             patch("saaia_docling.region_process.asyncio.create_subprocess_exec") as launch:
            with self.assertRaises(RuntimeError):
                await engine.recognize(Path("unopened.pdf"), {}, item(), TextRepairOptions(), 2)
            launch.assert_not_called()
        self.assertEqual(1, engine._slot._value)


if __name__ == "__main__":
    unittest.main()
