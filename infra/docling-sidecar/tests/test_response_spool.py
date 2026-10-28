import asyncio
import base64
import gc
import hashlib
import json
from pathlib import Path
import tempfile
import tracemalloc
import unittest

from saaia_docling.response_spool import (
    CHUNK_BYTES, MAXIMUM_RESPONSE_BYTES, OwnedResponseBody, PayloadDiagnostics,
    PayloadWorkspace, ResponseFile, ResponseSizeLimitError,
    ABSOLUTE_MAXIMUM_RESPONSE_BYTES, DEFAULT_RESPONSE_BYTES, MINIMUM_RESPONSE_BYTES,
    RESPONSE_LIMIT_SETTING, parse_upstream, response_byte_limit_from_environment,
    serialize_payload, spool_upstream,
)


async def chunks(*values):
    for value in values:
        yield value


async def receive_forever():
    await asyncio.Event().wait()


def legacy_bytes(value):
    return json.dumps(value, ensure_ascii=False, allow_nan=False, indent=None, separators=(",", ":")).encode("utf-8")


class ResponseByteLimitTests(unittest.TestCase):
    def test_default_and_backend_range_are_preserved(self):
        self.assertEqual(response_byte_limit_from_environment({}), DEFAULT_RESPONSE_BYTES)
        for value in (MINIMUM_RESPONSE_BYTES, DEFAULT_RESPONSE_BYTES, DEFAULT_RESPONSE_BYTES + 1, ABSOLUTE_MAXIMUM_RESPONSE_BYTES):
            self.assertEqual(response_byte_limit_from_environment({RESPONSE_LIMIT_SETTING: str(value)}), value)

    def test_invalid_values_are_not_coerced_or_clamped(self):
        for value in (None, True, 536870912, "", " 536870912", "536870912 ", "+536870912", "01", "-1", "1e8", "1.5", "٠١", "0",
                      str(MINIMUM_RESPONSE_BYTES - 1), str(ABSOLUTE_MAXIMUM_RESPONSE_BYTES + 1), "9" * 100):
            with self.subTest(value=value), self.assertRaises(ValueError):
                response_byte_limit_from_environment({RESPONSE_LIMIT_SETTING: value})

    def test_only_the_public_limit_setting_is_read(self):
        self.assertEqual(response_byte_limit_from_environment({"PRIVATE_SECRET": "not used"}), DEFAULT_RESPONSE_BYTES)


class ResponseSpoolTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.diagnostics = PayloadDiagnostics()
        self.workspace = PayloadWorkspace(self.diagnostics)
        self.addCleanup(self.workspace.close)

    async def make_body(self, value=None):
        data = b'{"answer":"source"}' if value is None else value
        receipt = await spool_upstream(chunks(data), self.workspace)
        return OwnedResponseBody(self.workspace, receipt)

    async def test_upstream_stream_receipt_and_json_remain_exact(self):
        value = {"accent": "é", "astral": "😀", "base64": base64.b64encode(bytes(range(256)) * 4096).decode()}
        original = legacy_bytes(value)
        receipt = await spool_upstream(chunks(original[:100], original[100:]), self.workspace)
        self.assertEqual(receipt.bytes, len(original))
        self.assertEqual(receipt.sha256, hashlib.sha256(original).hexdigest())
        self.assertEqual(parse_upstream(self.workspace), value)
        self.assertFalse(self.workspace.upstream.exists(), "Parsed upstream file/page cache must be released early")
        serialized = serialize_payload(value, self.workspace)
        self.assertEqual(serialized.path.read_bytes(), original)
        self.assertEqual(serialized.sha256, receipt.sha256)

    async def test_upstream_rejects_overflow_without_accepting_truncation(self):
        with self.assertRaises(ResponseSizeLimitError):
            await spool_upstream(chunks(b"1234", b"56789"), self.workspace, maximum=8)
        self.assertEqual(self.workspace.upstream.read_bytes(), b"1234")
        phase = self.diagnostics.snapshot()["phases"]["upstream_download"]
        self.assertEqual((phase["active"], phase["failed"], phase["completed"]), (0, 1, 0))

    async def test_upstream_cancel_closes_file_before_owned_directory_cleanup(self):
        started = asyncio.Event()

        async def pending():
            yield b"first"
            started.set()
            await asyncio.Event().wait()

        task = asyncio.create_task(spool_upstream(pending(), self.workspace))
        await started.wait(); task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        self.workspace.close()
        self.assertFalse(self.workspace.root.exists())
        self.assertEqual(self.diagnostics.snapshot()["phases"]["upstream_download"]["canceled"], 1)

    async def test_malformed_json_has_no_ready_body(self):
        await spool_upstream(chunks(b'{"document":'), self.workspace)
        with self.assertRaises(json.JSONDecodeError):
            parse_upstream(self.workspace)
        self.assertFalse(self.workspace.serialized.exists())

    async def test_serialization_failure_is_before_any_response_is_ready(self):
        for value in ({"bad": float("nan")}, {"bad": "\ud800"}):
            workspace = PayloadWorkspace(self.diagnostics)
            try:
                with self.assertRaises((ValueError, UnicodeError)):
                    serialize_payload(value, workspace)
            finally:
                root = workspace.root; workspace.close()
            self.assertFalse(root.exists())

    async def test_serialization_byte_ceiling_counts_utf8_not_characters(self):
        with self.assertRaises(ResponseSizeLimitError):
            serialize_payload({"é": "😀"}, self.workspace, maximum=12)
        self.assertLessEqual(self.workspace.serialized.stat().st_size, 12)
        self.assertEqual(self.diagnostics.snapshot()["phases"]["json_serialize"]["failed"], 1)

    async def test_global_response_ceiling_cannot_be_relaxed_by_helper(self):
        with self.assertRaises(ValueError):
            serialize_payload({}, self.workspace, maximum=MAXIMUM_RESPONSE_BYTES + 1)
        with self.assertRaises(ValueError):
            await spool_upstream(chunks(b""), self.workspace, maximum=True)

    async def test_empty_non_success_upstream_body_is_preserved(self):
        body = await self.make_body(b"")
        messages = []

        async def send(message):
            messages.append(message)

        await body.send({}, receive_forever, send, status_code=503, headers=[(b"content-length", b"0")])
        self.assertEqual(messages, [{"type": "http.response.start", "status": 503, "headers": [(b"content-length", b"0")]},
                                    {"type": "http.response.body", "body": b"", "more_body": False}])
        self.assertFalse(self.workspace.root.exists())
        self.assertEqual(self.diagnostics.snapshot()["phases"]["upstream_download"]["lastBytes"], 0)

    async def test_normal_asgi_returns_exact_bytes_in_bounded_chunks_then_cleans(self):
        original = b"x" * (CHUNK_BYTES * 3 + 5)
        body = await self.make_body(original)
        messages = []

        async def send(message):
            messages.append(message)

        await body.send({}, receive_forever, send, status_code=418, headers=[(b"content-type", b"application/json")])
        pieces = [message["body"] for message in messages if message["type"] == "http.response.body"]
        self.assertEqual(b"".join(pieces), original)
        self.assertLessEqual(max(map(len, pieces)), CHUNK_BYTES)
        self.assertEqual(messages[0]["status"], 418)
        self.assertFalse(messages[-1]["more_body"])
        self.assertFalse(self.workspace.root.exists())
        self.assertEqual(self.diagnostics.snapshot()["phases"]["wire_send"]["lastBytes"], len(original))

    async def test_send_error_before_headers_or_midbody_always_closes_and_cleans(self):
        for fail_at in (1, 3):
            workspace = PayloadWorkspace(self.diagnostics)
            receipt = await spool_upstream(chunks(b"x" * (CHUNK_BYTES + 1)), workspace)
            body = OwnedResponseBody(workspace, receipt)
            calls = 0

            async def send(message):
                nonlocal calls
                calls += 1
                if calls == fail_at:
                    raise OSError("private source detail must not reach diagnostics")

            with self.assertRaises(OSError):
                await body.send({}, receive_forever, send, status_code=200, headers=[])
            self.assertFalse(workspace.root.exists())
        snapshot = self.diagnostics.snapshot()
        self.assertEqual(snapshot["phases"]["wire_send"]["failed"], 2)
        self.assertNotIn("private source detail", str(snapshot))

    async def test_client_disconnect_cancels_sender_and_cleans_without_faking_completion(self):
        body = await self.make_body()
        headers = asyncio.Event()

        async def send(message):
            if message["type"] == "http.response.start":
                headers.set()
            await asyncio.Event().wait()

        async def receive():
            await headers.wait()
            return {"type": "http.disconnect"}

        await body.send({}, receive, send, status_code=200, headers=[])
        self.assertFalse(self.workspace.root.exists())
        phase = self.diagnostics.snapshot()["phases"]["wire_send"]
        self.assertEqual((phase["canceled"], phase["completed"], phase["active"]), (1, 0, 0))

    async def test_asgi_task_cancellation_reaps_child_tasks_and_cleans(self):
        body = await self.make_body()
        entered = asyncio.Event()

        async def send(message):
            entered.set(); await asyncio.Event().wait()

        task = asyncio.create_task(body.send({}, receive_forever, send, status_code=200, headers=[]))
        await entered.wait(); task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        self.assertFalse(self.workspace.root.exists())
        self.assertEqual(self.diagnostics.snapshot()["phases"]["wire_send"]["active"], 0)

    async def test_receive_failure_cleans_and_reports_failure(self):
        body = await self.make_body()

        async def send(message):
            await asyncio.Event().wait()

        async def receive():
            raise OSError("disconnected transport")

        with self.assertRaises(OSError):
            await body.send({}, receive, send, status_code=200, headers=[])
        self.assertFalse(self.workspace.root.exists())

    async def test_response_abandoned_before_asgi_call_is_finalized(self):
        body = await self.make_body()
        root = self.workspace.root
        del body
        gc.collect()
        self.assertFalse(root.exists())
        self.assertEqual(self.diagnostics.snapshot()["phases"]["cleanup"]["completed"], 1)

    async def test_foreign_file_is_not_accepted_or_deleted(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "do-not-delete.json"; path.write_bytes(b"{}")
            with self.assertRaises(ValueError):
                OwnedResponseBody(self.workspace, ResponseFile(path, 2, "unused"))
            self.assertTrue(path.exists())

    async def test_large_base64_unicode_avoids_the_full_unicode_serialization_peak(self):
        # Actual production serializer, with a synthetic 8 MiB payload. The JSON
        # wire bytes must stay exact; reduced peak cannot come from lost assets.
        payload = {"text": "é😀", "images": ["A" * 1024**2 + str(n) for n in range(8)]}
        tracemalloc.start()
        try:
            legacy = legacy_bytes(payload)
            legacy_peak = tracemalloc.get_traced_memory()[1]
            expected_sha = hashlib.sha256(legacy).hexdigest()
            del legacy
            tracemalloc.stop(); tracemalloc.start()
            receipt = serialize_payload(payload, self.workspace)
            spool_peak = tracemalloc.get_traced_memory()[1]
        finally:
            tracemalloc.stop()
        self.assertEqual(receipt.sha256, expected_sha)
        self.assertLess(spool_peak, legacy_peak // 4, (spool_peak, legacy_peak))


if __name__ == "__main__":
    unittest.main()
