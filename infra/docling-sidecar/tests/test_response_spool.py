import asyncio
import base64
import gc
import hashlib
import json
from pathlib import Path
import tempfile
import struct
import math
import random
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
        with self.assertRaises(ValueError):
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


    async def test_fixed_seed_finite_float_bits_and_big_integer_types_roundtrip(self):
        random_bits = random.Random(0x5AA1A20261028)
        values = [-0.0, 0.0, 5e-324, -5e-324]
        while len(values) < 2052:
            candidate = struct.unpack(">d", random_bits.getrandbits(64).to_bytes(8, "big"))[0]
            if math.isfinite(candidate):
                values.append(candidate)
        integers = [(-1 if offset % 2 else 1) * (2**bits + random_bits.getrandbits(min(bits, 63)))
                    for offset, bits in enumerate((63, 64, 65, 127, 128, 255, 328, 329, 512, 1024))]
        raw = legacy_bytes({"floats": values, "integers": integers})
        expected = json.loads(raw)
        await spool_upstream(chunks(raw), self.workspace)
        actual = parse_upstream(self.workspace)
        for left, right in zip(expected["floats"], actual["floats"], strict=True):
            self.assertIs(type(right), float)
            self.assertEqual(struct.pack(">d", left), struct.pack(">d", right))
        for left, right in zip(expected["integers"], actual["integers"], strict=True):
            self.assertIs(type(right), int)
            self.assertEqual(right, left)
        self.assertEqual(serialize_payload(actual, self.workspace).path.read_bytes(), raw)

    async def test_parser_preserves_exact_types_big_integers_float_bits_and_assets(self):
        png = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6pBkAAAAASUVORK5CYII=")
        value = {"null": None, "bools": [False, True], "integers": [0, -1, 2**63, 2**64, -(2**328 + 17), 2**328 + 17],
                 "floats": [-0.0, 0.0, 0.1, 1.2345678901234567, 5e-324, 1.7976931348623157e308],
                 "text": "é😀", "geometry": {"l": 1.25, "t": -0.0, "r": 595.276, "b": 841.89},
                 "image": {"mimetype": "image/png", "uri": "data:image/png;base64," + base64.b64encode(png).decode("ascii")}}
        raw = legacy_bytes(value)
        await spool_upstream(chunks(raw), self.workspace)
        parsed = parse_upstream(self.workspace)

        def compare(expected, actual):
            self.assertIs(type(actual), type(expected))
            if type(expected) is dict:
                self.assertEqual(list(actual), list(expected))
                for key in expected:
                    compare(expected[key], actual[key])
            elif type(expected) is list:
                self.assertEqual(len(actual), len(expected))
                for left, right in zip(expected, actual):
                    compare(left, right)
            elif type(expected) is float:
                self.assertEqual(struct.pack(">d", actual), struct.pack(">d", expected))
            else:
                self.assertEqual(actual, expected)

        compare(value, parsed)
        self.assertEqual(parsed["integers"][-1].bit_length(), 329)
        self.assertEqual(base64.b64decode(parsed["image"]["uri"].split(",", 1)[1]), png)
        self.assertFalse(self.workspace.upstream.exists())
        receipt = serialize_payload(parsed, self.workspace)
        self.assertEqual(receipt.path.read_bytes(), raw)
        self.assertEqual(receipt.bytes, len(raw))
        self.assertEqual(receipt.sha256, hashlib.sha256(raw).hexdigest())

    async def test_parser_preserves_bom_valid_surrogate_pair_and_duplicate_key_contract(self):
        await spool_upstream(chunks(b'\xef\xbb\xbf{"text":"\\ud83d\\ude00","x":1,"x":2}'), self.workspace)
        parsed = parse_upstream(self.workspace)
        self.assertEqual(parsed, {"text": "😀", "x": 2})
        self.assertEqual(serialize_payload(parsed, self.workspace).path.read_bytes(), legacy_bytes(parsed))

    async def test_parser_rejects_invalid_utf8_utf16_trailing_root_and_partial_json(self):
        for raw in (b'{"x":"\xff"}', b'\xff\xfe{\x00}\x00', b'{"x":', b'{"x":1} {}', b'{"x":1} trailing', b''):
            with self.subTest(raw=raw):
                workspace = PayloadWorkspace(self.diagnostics)
                try:
                    await spool_upstream(chunks(raw), workspace)
                    with self.assertRaises(ValueError):
                        parse_upstream(workspace)
                    self.assertFalse(workspace.serialized.exists())
                finally:
                    workspace.close()
                self.assertFalse(workspace.root.exists())

    async def test_surviving_isolated_surrogates_never_produce_ready_response(self):
        # The original stdlib pipeline also fails at UTF-8 serialization if this
        # value survives duplicate-key resolution. Pydantic rejects it earlier.
        for raw in (b'{"bad":"\\ud800"}', b'{"bad":"\\udfff"}'):
            with self.subTest(raw=raw):
                workspace = PayloadWorkspace(self.diagnostics)
                try:
                    await spool_upstream(chunks(raw), workspace)
                    with self.assertRaises((ValueError, UnicodeError)):
                        serialize_payload(parse_upstream(workspace), workspace)
                finally:
                    workspace.close()
                self.assertFalse(workspace.root.exists())

    async def test_non_finite_tokens_and_float_overflow_fail_before_ready_response(self):
        for token in (b'NaN', b'Infinity', b'-Infinity', b'1e400', b'-1e400'):
            with self.subTest(token=token):
                workspace = PayloadWorkspace(self.diagnostics)
                try:
                    await spool_upstream(chunks(b'{"bad":' + token + b'}'), workspace)
                    with self.assertRaises((ValueError, UnicodeError)):
                        serialize_payload(parse_upstream(workspace), workspace)
                finally:
                    workspace.close()
                self.assertFalse(workspace.root.exists())

    async def test_native_parser_failure_releases_input_buffer_and_closes_file(self):
        raw = b'{"private":"' + b'x' * (2 * 1024**2) + b'", "unfinished":'
        await spool_upstream(chunks(raw), self.workspace)
        failure = None
        try:
            parse_upstream(self.workspace)
        except ValueError as error:
            failure = error
        self.assertIsNotNone(failure)
        self.assertFalse(hasattr(failure, "doc"))
        trace = failure.__traceback__
        parser_frame_seen = False
        while trace is not None:
            if trace.tb_frame.f_code.co_name == "parse_upstream":
                parser_frame_seen = True
                values = trace.tb_frame.f_locals
                self.assertIsNone(values.get("source_bytes"))
                self.assertTrue(values["source"].closed)
                self.assertFalse(any(type(value) is bytes and len(value) >= len(raw) for value in values.values()))
            trace = trace.tb_next
        self.assertTrue(parser_frame_seen)
        self.workspace.close()
        self.assertFalse(self.workspace.root.exists())

    async def test_overwritten_invalid_surrogate_is_explicitly_rejected_by_native_parser(self):
        raw = b'{"x":"\\ud800","x":"ok"}'
        self.assertEqual(legacy_bytes(json.loads(raw)), b'{"x":"ok"}')
        await spool_upstream(chunks(raw), self.workspace)
        with self.assertRaises(ValueError):
            parse_upstream(self.workspace)
        self.assertFalse(self.workspace.serialized.exists())

    async def test_excessive_nesting_is_rejected_without_partial_tree_or_ready_body(self):
        raw = b'{"x":' + b'[' * 512 + b'0' + b']' * 512 + b'}'
        await spool_upstream(chunks(raw), self.workspace)
        with self.assertRaises(ValueError):
            parse_upstream(self.workspace)
        self.assertFalse(self.workspace.serialized.exists())


if __name__ == "__main__":
    unittest.main()
