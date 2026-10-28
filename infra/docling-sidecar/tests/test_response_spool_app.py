"""Real FastAPI/HTTPX ASGI wiring; synthetic upstream, no network/PDF/model work.

Run in the pinned image, whose application dependencies already exist. The
response spool suite also requires the explicit pydantic-core parser dependency.
"""
import asyncio
import importlib.util
import json
import unittest
import weakref
from unittest.mock import patch

AVAILABLE = (all(importlib.util.find_spec(name) for name in ("fastapi", "httpx", "pydantic_core"))
             and (importlib.util.find_spec("multipart") or importlib.util.find_spec("python_multipart")))
if AVAILABLE:
    import httpx
    from saaia_docling import app as APP
    from saaia_docling.response_spool import PayloadDiagnostics, PayloadWorkspace, spool_upstream
    from saaia_docling.text_repair_options import TextRepairOptions
    from saaia_docling.transport_diagnostics import ConversionTransportDiagnostics


@unittest.skipUnless(AVAILABLE, "Run ASGI integration tests in pinned image with existing FastAPI/HTTPX/multipart.")
class ResponseSpoolApplicationTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.workspaces, self.upstream_clients, self.streams = [], [], []
        self.client_type = httpx.AsyncClient
        self.patches = [patch.object(APP, "payload_diagnostics", PayloadDiagnostics()),
                        patch.object(APP, "conversion_transport", ConversionTransportDiagnostics()),
                        patch.object(APP, "conversion_slot", asyncio.Semaphore(1)),
                        patch.object(APP, "REPAIR_ENABLED", False),
                        patch.object(APP, "TEXT_REPAIR", TextRepairOptions(enabled=False)),
                        patch.object(APP, "conversion_execution_metadata", return_value={"fixture": "configuration-only"}),
                        patch.object(APP, "PayloadWorkspace", side_effect=self.workspace)]
        for item in self.patches:
            item.start(); self.addCleanup(item.stop)

    def workspace(self, diagnostics):
        workspace = PayloadWorkspace(diagnostics)
        self.workspaces.append(workspace)
        return workspace

    def streamed_response(self, raw, *, status=200, content_type="application/json", pending=None, headers=None, fail_stream_close=False):
        owner = self

        class Stream(httpx.AsyncByteStream):
            closed = False
            read_blocks = 0

            async def __aiter__(self):
                if pending:
                    pending.set()
                    await asyncio.Event().wait()
                for offset in range(0, len(raw), 8192):
                    self.read_blocks += 1
                    yield raw[offset:offset + 8192]

            async def aclose(self):
                self.closed = True
                if fail_stream_close:
                    raise httpx.ReadError("synthetic stream closure failure")

        class NoBufferedAccess(httpx.Response):
            @property
            def content(self):
                raise AssertionError("Production forwarding must not buffer .content")

            def json(self, **kwargs):
                raise AssertionError("Production forwarding must not call Response.json()")

        stream = Stream(); owner.streams.append(stream)
        return NoBufferedAccess(status, headers={"content-type": content_type, **(headers or {})}, stream=stream)

    async def request(self, handler, *, form=None, fail_client_close=False):
        transport = httpx.MockTransport(handler)
        base_client = self.client_type

        class ClientCloseFailure(base_client):
            async def __aexit__(self, *args):
                await super().__aexit__(*args)
                raise httpx.ReadError("synthetic client closure failure after transport close")

        def upstream_client(*args, **kwargs):
            client_class = ClientCloseFailure if fail_client_close else base_client
            client = client_class(*args, transport=transport, **kwargs)
            self.upstream_clients.append(client)
            return client

        async with self.client_type(transport=httpx.ASGITransport(app=APP.app), base_url="http://wrapper.invalid") as client:
            with patch.object(APP.httpx, "AsyncClient", side_effect=upstream_client):
                return await client.post("/v1/convert/file", files={"files": ("synthetic.pdf", b"no real PDF was converted", "application/pdf")},
                                         data=form or {})

    def assert_clean(self):
        self.assertTrue(self.workspaces)
        self.assertTrue(all(not workspace.root.exists() for workspace in self.workspaces))
        self.assertTrue(all(client.is_closed for client in self.upstream_clients))
        self.assertTrue(all(stream.closed for stream in self.streams))
        self.assertEqual(APP.conversion_transport.snapshot()["active"], 0)
        self.assertFalse(APP.conversion_slot.locked())

    async def test_success_uses_stream_and_exact_starlette_json_bytes(self):
        original = {"status": "success", "errors": [], "document": {"json_content": {
            "texts": [{"text": "é😀"}], "pages": {"1": {"image": {"uri": "data:image/png;base64," + "A" * 1024**2}}}}}}
        raw = json.dumps(original, ensure_ascii=False).encode("utf-8")

        async def handler(request):
            self.assertEqual(request.headers.get("accept-encoding"), "identity")
            self.assertIn(b'filename="synthetic.pdf"', request.content)
            return self.streamed_response(raw)

        response = await self.request(handler)
        expected = {**original, "saaia_execution": {"fixture": "configuration-only"}}
        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.content, APP.JSONResponse(expected).body)
        self.assertEqual(int(response.headers["content-length"]), len(response.content))
        self.assert_clean()

    async def test_non_success_preserves_status_media_type_and_raw_body(self):
        async def handler(request):
            return self.streamed_response(b"not JSON: upstream capacity", status=429, content_type="text/plain")

        response = await self.request(handler)
        self.assertEqual(response.status_code, 429)
        self.assertEqual(response.content, b"not JSON: upstream capacity")
        self.assertTrue(response.headers["content-type"].startswith("text/plain"))
        self.assert_clean()

    async def test_invalid_json_and_failed_serialization_never_emit_http200(self):
        for raw in (b'{"document":', b'{"status":"success"} trailing', b'{"status":"success","bad":"\xff"}',
                    b'{"status":"success","bad":"\\ud800"}', b'{"status":"success","bad":"\\udfff"}',
                    b'{"status":"success","bad":NaN}', b'{"status":"success","bad":Infinity}',
                    b'{"status":"success","bad":-Infinity}', b'{"status":"success","bad":1e400}'):
            async def handler(request):
                return self.streamed_response(raw)

            response = await self.request(handler)
            self.assertEqual(response.status_code, 502)
            self.assert_clean()

    async def test_declared_oversize_is_refused_before_consumption(self):
        async def handler(request):
            return self.streamed_response(b"{}", headers={"content-length": str(APP.MAXIMUM_RESPONSE_BYTES + 1)})

        with self.assertLogs("saaia_docling", level="INFO") as logs:
            response = await self.request(handler)
        self.assertEqual(response.status_code, 502)
        self.assertIn(str(APP.MAXIMUM_RESPONSE_BYTES), response.json()["detail"])
        self.assertTrue(any("stage=upstream_headers" in row and
                            "declared_content_length_bytes=" + str(APP.MAXIMUM_RESPONSE_BYTES + 1) in row for row in logs.output))
        self.assertEqual(sum(stream.read_blocks for stream in self.streams), 0)
        self.assert_clean()

    async def test_actual_overflow_is_refused_even_if_header_lies(self):
        async def bounded(chunks, workspace):
            return await spool_upstream(chunks, workspace, maximum=32)

        async def handler(request):
            return self.streamed_response(b"x" * 64)

        with patch.object(APP, "spool_upstream", side_effect=bounded), self.assertLogs("saaia_docling", level="WARNING") as logs:
            response = await self.request(handler)
        self.assertTrue(any("stage=upstream_download" in row and "declared_content_length_state=absent" in row for row in logs.output))
        self.assertEqual(response.status_code, 502)
        self.assert_clean()

    async def test_upstream_timeout_closes_client_and_cleans_private_workspace(self):
        async def handler(request):
            raise httpx.ReadTimeout("synthetic timeout; no real server")

        response = await self.request(handler)
        self.assertEqual(response.status_code, 502)
        self.assert_clean()
        self.assertEqual(APP.conversion_transport.snapshot()["failed"], 1)

    async def test_stream_closure_failure_is_not_counted_as_success(self):
        async def handler(request):
            return self.streamed_response(b'{"status":"success"}', fail_stream_close=True)

        response = await self.request(handler)
        self.assertEqual(response.status_code, 502)
        self.assert_clean()
        self.assertEqual((APP.conversion_transport.snapshot()["failed"], APP.conversion_transport.snapshot()["success"]), (1, 0))

    async def test_client_closure_failure_is_not_counted_as_success(self):
        async def handler(request):
            return self.streamed_response(b'{"status":"success"}')

        response = await self.request(handler, fail_client_close=True)
        self.assertEqual(response.status_code, 502)
        self.assert_clean()
        self.assertEqual((APP.conversion_transport.snapshot()["failed"], APP.conversion_transport.snapshot()["success"]), (1, 0))

    async def test_canceling_request_closes_stream_client_and_files(self):
        entered = asyncio.Event()

        async def handler(request):
            return self.streamed_response(b"{}", pending=entered)

        task = asyncio.create_task(self.request(handler))
        await entered.wait(); task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        self.assert_clean()
        self.assertFalse(APP.conversion_transport.snapshot()["upstreamModelExecutionCancellationProven"])

    async def test_existing_heading_contract_still_rejects_before_forwarding(self):
        async def handler(request):
            self.fail("Mismatched controls must not reach upstream")

        wrong = "false" if APP.HEADING_HIERARCHY.enabled else "true"
        response = await self.request(handler, form={"saaia_heading_hierarchy_enabled": wrong})
        self.assertEqual(response.status_code, 409)
        self.assertEqual(self.workspaces, [])


    async def test_full_asgi_typed_payload_is_exact_and_releases_tree(self):
        payload = {"status": "success", "big": 2**328 + 17, "negative_big": -(2**328 + 17),
                   "bool": True, "none": None, "float": -0.0, "tiny": 5e-324, "text": "é😀",
                   "geometry": [1.25, -0.0, 595.276, 841.89],
                   "image": "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6pBkAAAAASUVORK5CYII="}
        raw = b"\xef\xbb\xbf" + json.dumps(payload, ensure_ascii=False).encode("utf-8")
        references = []
        real_parse = APP.parse_upstream

        class TrackedTree(dict):
            pass

        def tracked_parse(workspace):
            tree = TrackedTree(real_parse(workspace))
            references.append(weakref.ref(tree))
            return tree

        async def handler(request):
            return self.streamed_response(raw, headers={"content-length": str(len(raw))})

        with patch.object(APP, "parse_upstream", side_effect=tracked_parse):
            response = await self.request(handler)
        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.content, APP.JSONResponse({**payload, "saaia_execution": {"fixture": "configuration-only"}}).body)
        self.assertTrue(references)
        self.assertTrue(all(reference() is None for reference in references))
        self.assertNotIn("error_payload_release", APP.payload_diagnostics.snapshot()["phases"])
        self.assert_clean()

    async def test_serialization_failure_releases_tree_and_does_not_chain_native_exception(self):
        raw = b'{"status":"success","private":"' + b'x' * (2 * 1024**2) + b'","bad":NaN}'
        references, exceptions = [], []
        real_parse, real_exception = APP.parse_upstream, APP.HTTPException

        class TrackedTree(dict):
            pass

        def tracked_parse(workspace):
            tree = TrackedTree(real_parse(workspace))
            references.append(weakref.ref(tree))
            return tree

        def capture_exception(*args, **kwargs):
            error = real_exception(*args, **kwargs)
            exceptions.append(error)
            return error

        async def handler(request):
            return self.streamed_response(raw)

        with patch.object(APP, "parse_upstream", side_effect=tracked_parse), patch.object(APP, "HTTPException", side_effect=capture_exception):
            response = await self.request(handler)
        self.assertEqual(response.status_code, 502)
        self.assertLess(len(response.content), 1024)
        self.assertTrue(references)
        self.assertTrue(all(reference() is None for reference in references))
        self.assertEqual(len(exceptions), 1)
        self.assertIsNone(exceptions[0].__context__)
        self.assertIsNone(exceptions[0].__cause__)
        trace = exceptions[0].__traceback__
        while trace is not None:
            self.assertNotIn(trace.tb_frame.f_code.co_name, ("parse_upstream", "serialize_payload"))
            if trace.tb_frame.f_code.co_name == "_convert_file_owned":
                self.assertIsNone(trace.tb_frame.f_locals.get("payload"))
            trace = trace.tb_next
        release = APP.payload_diagnostics.snapshot()["phases"]["error_payload_release"]
        self.assertEqual(release["started"], 1)
        self.assertEqual(release["completed"], 1)
        self.assertEqual(release["active"], 0)
        self.assertEqual(release["failed"], 0)
        self.assertGreaterEqual(release["lastDurationMs"], 0)
        self.assertIsNone(release["lastBytes"])
        self.assert_clean()

    async def test_native_parse_error_is_translated_without_input_exception_context(self):
        exceptions = []
        real_exception = APP.HTTPException

        def capture_exception(*args, **kwargs):
            error = real_exception(*args, **kwargs)
            exceptions.append(error)
            return error

        async def handler(request):
            return self.streamed_response(b'{"private":"' + b'x' * (2 * 1024**2) + b'", "bad":')

        with patch.object(APP, "HTTPException", side_effect=capture_exception):
            response = await self.request(handler)
        self.assertEqual(response.status_code, 502)
        self.assertEqual(response.json()["detail"], "Docling returned invalid JSON.")
        self.assertEqual(len(exceptions), 1)
        self.assertIsNone(exceptions[0].__context__)
        self.assertIsNone(exceptions[0].__cause__)
        self.assert_clean()

    async def test_untrusted_length_header_is_not_copied_into_logs(self):
        async def handler(request):
            return self.streamed_response(b'{"status":"success"}', headers={"content-length": "private-source-marker"})

        with self.assertLogs("saaia_docling", level="INFO") as logs:
            response = await self.request(handler)
        self.assertEqual(response.status_code, 200)
        text = "\n".join(logs.output)
        self.assertIn("declared_content_length_state=invalid", text)
        self.assertNotIn("private-source-marker", text)
        self.assert_clean()

    async def test_serialized_cap_refusal_has_exact_stage_and_no_partial_success(self):
        from saaia_docling.response_spool import serialize_payload as real_serialize

        async def handler(request):
            return self.streamed_response(b'{"status":"success","value":"' + b'x' * 128 + b'"}')

        def bounded(payload, workspace):
            return real_serialize(payload, workspace, maximum=32)

        with patch.object(APP, "serialize_payload", side_effect=bounded), self.assertLogs("saaia_docling", level="WARNING") as logs:
            response = await self.request(handler)
        self.assertEqual(response.status_code, 502)
        self.assertTrue(any("stage=json_serialize" in row for row in logs.output))
        self.assert_clean()

    async def test_declared_length_normalizes_zeroes_and_bounds_numeric_logging(self):
        raw = b'{"status":"success"}'
        for length, status, state in (("0" * 32 + str(len(raw)), 200, "valid"),
                                      ("9" * 21, 502, "out_of_range")):
            with self.subTest(state=state):
                async def handler(request):
                    return self.streamed_response(raw, headers={"content-length": length})

                with self.assertLogs("saaia_docling", level="INFO") as logs:
                    response = await self.request(handler)
                self.assertEqual(response.status_code, status)
                self.assertTrue(any("declared_content_length_state=" + state in row for row in logs.output))
                if state == "valid":
                    self.assertTrue(any("declared_content_length_bytes=" + str(len(raw)) in row for row in logs.output))
                else:
                    self.assertEqual(self.streams[-1].read_blocks, 0)
                self.assert_clean()


if __name__ == "__main__":
    unittest.main()
